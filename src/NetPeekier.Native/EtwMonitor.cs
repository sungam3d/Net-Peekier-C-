// ETW per-process byte counters via the NT Kernel Logger.
//
// Replacement for capture.WinDivertBackend's sniff side from the Python
// build. The kernel ETW provider gives us, for every packet, the owning
// PID + size + 5-tuple — exactly what we need to attribute bytes to
// processes and connections, without a kernel driver.
//
// Lifecycle:
//   1. Start() opens the NT Kernel Logger session (singleton — only one
//      per machine — so we defensively stop any orphan first), wires up
//      handlers, and spins a background task to call Source.Process()
//      which blocks pumping events.
//   2. Each event handler adds bytes to two pairs of dictionaries:
//        _pidAcc / _pidTotal     (keyed by PID)
//        _connAcc / _connTotal   (keyed by ConnectionKey)
//      The Acc maps are the unread-since-last-drain delta; Total maps are
//      cumulative since startup.
//   3. NetworkMonitor.Tick calls DrainRates(interval) — we return the
//      Acc maps divided by interval (bytes/sec) and reset them.
//   4. Stop()/Dispose() cancels and disposes the session.
//
// Requires the Microsoft.Diagnostics.Tracing.TraceEvent NuGet package and
// administrator privileges (the kernel session won't open otherwise).
// If either is missing, Start sets Available=false and the NetworkMonitor
// falls back to NetworkInterface.GetIPStatistics() for system-wide totals.

using System.Net;
using System.Runtime.Versioning;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using NetPeekier.Core;

namespace NetPeekier.Native;

[SupportedOSPlatform("windows")]
public sealed class EtwMonitor : IDisposable
{
    /// <summary>True if the kernel session is open and pumping events.</summary>
    public bool Available { get; private set; }

    private readonly ProcessMap _procmap;
    private TraceEventSession? _session;
    private Task? _consumer;

    // Thread sync: events arrive on the consumer thread; drains and totals
    // are read from the NetworkMonitor's tick thread. One lock guards all
    // four dictionaries together so a drain sees a consistent snapshot.
    private readonly object _gate = new();

    // Per-pid byte counters. *Acc dictionaries hold the delta since the
    // previous DrainRates call; *Total holds cumulative since Start.
    private readonly Dictionary<int, (long Up, long Down)> _pidAcc   = new();
    private readonly Dictionary<int, (long Up, long Down)> _pidTotal = new();
    private readonly Dictionary<ConnectionKey, (long Up, long Down)> _connAcc   = new();
    private readonly Dictionary<ConnectionKey, (long Up, long Down)> _connTotal = new();

    public EtwMonitor(ProcessMap procmap) { _procmap = procmap; }

    public void Start()
    {
        if (Available) return;
        try
        {
            // Defensively stop any orphan session from a previous crashed
            // run. The NT Kernel Logger is a machine-wide singleton.
            try
            {
                using var prev = new TraceEventSession(
                    KernelTraceEventParser.KernelSessionName,
                    TraceEventSessionOptions.Attach);
                prev.Stop();
            }
            catch { /* nothing was there; that's fine */ }

            Diag.Log("EtwMonitor.Start: opening NT Kernel Logger session");
            _session = new TraceEventSession(KernelTraceEventParser.KernelSessionName);
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            // Wire up the eight event flavours. Send = outbound (up),
            // Recv = inbound (down). For IPv6 the data class is named
            // differently but the payload shape is the same.
            var k = _session.Source.Kernel;
            k.TcpIpSend      += d => Add(d.ProcessID, d.size, "TCP", d.saddr, d.sport, d.daddr, d.dport, outbound: true);
            k.TcpIpRecv      += d => Add(d.ProcessID, d.size, "TCP", d.daddr, d.dport, d.saddr, d.sport, outbound: false);
            k.TcpIpSendIPV6  += d => Add(d.ProcessID, d.size, "TCP", d.saddr, d.sport, d.daddr, d.dport, outbound: true);
            k.TcpIpRecvIPV6  += d => Add(d.ProcessID, d.size, "TCP", d.daddr, d.dport, d.saddr, d.sport, outbound: false);
            k.UdpIpSend      += d => Add(d.ProcessID, d.size, "UDP", d.saddr, d.sport, d.daddr, d.dport, outbound: true);
            k.UdpIpRecv      += d => Add(d.ProcessID, d.size, "UDP", d.daddr, d.dport, d.saddr, d.sport, outbound: false);
            k.UdpIpSendIPV6  += d => Add(d.ProcessID, d.size, "UDP", d.saddr, d.sport, d.daddr, d.dport, outbound: true);
            k.UdpIpRecvIPV6  += d => Add(d.ProcessID, d.size, "UDP", d.daddr, d.dport, d.saddr, d.sport, outbound: false);

            // Process() blocks; run it on a background thread.
            _consumer = Task.Run(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { Diag.LogException("EtwMonitor.consumer", ex); }
            });

            Available = true;
            Diag.Log("EtwMonitor.Start: session active");
        }
        catch (Exception ex)
        {
            // Most likely cause: not elevated, or another consumer already
            // holds the kernel logger. Either way, fall back to no-ETW mode.
            Diag.LogException("EtwMonitor.Start", ex);
            Available = false;
            try { _session?.Dispose(); } catch { /* ignore */ }
            _session = null;
        }
    }

    public void Stop()
    {
        if (!Available && _session is null) return;
        Diag.Log("EtwMonitor.Stop: tearing down session");
        try { _session?.Stop(); }     catch (Exception ex) { Diag.LogException("EtwMonitor.Stop", ex); }
        try { _session?.Dispose(); }  catch (Exception ex) { Diag.LogException("EtwMonitor.Dispose-session", ex); }
        _session = null;
        Available = false;

        // Source.Process() returns once the session is stopped.
        try { _consumer?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _consumer = null;
    }

    public void Dispose() => Stop();

    // =====================================================================
    // Accumulation.
    // =====================================================================

    /// <summary>
    /// Add one event's bytes to the per-pid + per-connection accumulators.
    /// Hot path — events fire ~thousands/sec on a busy machine, so the
    /// implementation is deliberately allocation-light.
    /// </summary>
    private void Add(
        int pid, int size, string proto,
        IPAddress localIp, int localPort, IPAddress remoteIp, int remotePort,
        bool outbound)
    {
        if (pid <= 0 || size <= 0) return;

        // The "loose" key: same shape as IpHelper's ConnectionKey. Using
        // IPAddress.ToString() everywhere (here and in IpHelper) means the
        // keys match exactly when the GUI joins ETW rates onto the
        // connection table.
        var key = new ConnectionKey(proto, localIp.ToString(), localPort, remoteIp.ToString(), remotePort);

        lock (_gate)
        {
            // Per-PID
            _pidAcc.TryGetValue(pid, out var pa);
            _pidTotal.TryGetValue(pid, out var pt);
            if (outbound) { pa.Up   += size; pt.Up   += size; }
            else          { pa.Down += size; pt.Down += size; }
            _pidAcc[pid]   = pa;
            _pidTotal[pid] = pt;

            // Per-connection
            _connAcc.TryGetValue(key, out var ca);
            _connTotal.TryGetValue(key, out var ct);
            if (outbound) { ca.Up   += size; ct.Up   += size; }
            else          { ca.Down += size; ct.Down += size; }
            _connAcc[key]   = ca;
            _connTotal[key] = ct;
        }
    }

    // =====================================================================
    // Reader API used by NetworkMonitor.Tick.
    // =====================================================================

    /// <summary>
    /// Snapshot the accumulators as rates (bytes/sec) and reset them. Cum-
    /// ulative totals are NOT reset; those keep growing for the "total"
    /// columns in the GUI.
    /// </summary>
    public (IReadOnlyDictionary<int, (double Up, double Down)> PidRates,
            IReadOnlyDictionary<ConnectionKey, (double Up, double Down)> ConnRates)
        DrainRates(double intervalSeconds)
    {
        if (intervalSeconds <= 0) intervalSeconds = 1.0;

        var pids  = new Dictionary<int, (double, double)>();
        var conns = new Dictionary<ConnectionKey, (double, double)>();

        lock (_gate)
        {
            foreach (var kv in _pidAcc)
                pids[kv.Key] = (kv.Value.Up / intervalSeconds, kv.Value.Down / intervalSeconds);
            foreach (var kv in _connAcc)
                conns[kv.Key] = (kv.Value.Up / intervalSeconds, kv.Value.Down / intervalSeconds);
            _pidAcc.Clear();
            _connAcc.Clear();
        }
        return (pids, conns);
    }

    public IReadOnlyDictionary<int, (long Up, long Down)> PidTotals()
    {
        lock (_gate) { return new Dictionary<int, (long, long)>(_pidTotal); }
    }

    public IReadOnlyDictionary<ConnectionKey, (long Up, long Down)> ConnTotals()
    {
        lock (_gate) { return new Dictionary<ConnectionKey, (long, long)>(_connTotal); }
    }

    /// <summary>Drop counter entries for PIDs that no longer exist.</summary>
    public void ForgetPids(IEnumerable<int> pids)
    {
        lock (_gate)
        {
            foreach (var pid in pids)
            {
                _pidAcc.Remove(pid);
                _pidTotal.Remove(pid);
            }
            // Connection totals aren't keyed by pid, so they age out
            // naturally when the connection itself goes away (we'll add a
            // GC pass later if memory becomes a concern on long sessions).
        }
    }

    /// <summary>
    /// Reserved for a future per-connection event view. The Python build
    /// kept the last ~50 packets per connection; nothing in the current
    /// GUI consumes this yet so it remains empty.
    /// </summary>
    public IReadOnlyList<ConnectionEvent> RecentEvents(ConnectionKey conn) =>
        Array.Empty<ConnectionEvent>();
}
