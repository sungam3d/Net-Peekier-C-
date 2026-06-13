// ETW per-process byte counters via Microsoft-Windows-Kernel-Network.
//
// This is the C# replacement for capture.WinDivertBackend's sniff side. We
// open a kernel session, enable the network keyword, and accumulate bytes
// per PID and per connection. No driver, no signing, no BSOD risk: it's the
// same channel Resource Monitor and PerfView use.
//
// STATUS: Phase 2 TODO. The shape below mirrors capture.CaptureBackend's
// public interface so Monitor.cs can drive it identically.
//
// IMPLEMENTATION NOTES (for when we fill this in):
//
//   1. Use TraceEventSession from Microsoft.Diagnostics.Tracing.TraceEvent.
//      The kernel session name should be `KernelLogger` (NT Kernel Logger)
//      or its modern equivalent. ETW kernel sessions can only have one
//      consumer per provider per machine, so we use Stop() defensively
//      first to clear any orphaned session from a prior crashed run.
//
//   2. Enable: KernelTraceEventParser.Keywords.NetworkTCPIP. Hook handlers
//      for TcpIpRecv / TcpIpSend / UdpIpRecv / UdpIpSend.
//
//   3. On each event: extract PID, size, local/remote 5-tuple. Add to two
//      dictionaries (per-PID accumulator, per-Connection accumulator),
//      plus their cumulative twins for the "total" columns.
//
//   4. DrainRates(interval) returns the accumulators as (up, down) rates
//      and clears them, mirroring capture.drain_rates.

using NetPeeker.Core;

namespace NetPeeker.Native;

public sealed class EtwMonitor : IDisposable
{
    public bool Available { get; private set; }

    public EtwMonitor(ProcessMap procmap) { /* TODO Phase 2 */ }

    public void Start() => throw new NotImplementedException("Phase 2 TODO: open kernel ETW session");
    public void Stop()  { /* TODO: close session */ }
    public void Dispose() => Stop();

    /// <summary>
    /// Drain the accumulators into rates (bytes/sec) and reset.
    /// Returns (pidRates, connRates).
    /// </summary>
    public (IReadOnlyDictionary<int, (double Up, double Down)> PidRates,
            IReadOnlyDictionary<ConnectionKey, (double Up, double Down)> ConnRates)
        DrainRates(double intervalSeconds) =>
        (new Dictionary<int, (double, double)>(),
         new Dictionary<ConnectionKey, (double, double)>());

    /// <summary>Cumulative bytes_up/bytes_down per PID since start.</summary>
    public IReadOnlyDictionary<int, (long Up, long Down)> PidTotals() =>
        new Dictionary<int, (long, long)>();

    /// <summary>Cumulative bytes_up/bytes_down per connection since start.</summary>
    public IReadOnlyDictionary<ConnectionKey, (long Up, long Down)> ConnTotals() =>
        new Dictionary<ConnectionKey, (long, long)>();

    public void ForgetPids(IEnumerable<int> pids) { /* TODO */ }

    public IReadOnlyList<ConnectionEvent> RecentEvents(ConnectionKey conn) =>
        Array.Empty<ConnectionEvent>();
}
