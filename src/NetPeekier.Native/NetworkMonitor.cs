// Port of netpeekier/monitor.py
//
// Background worker that produces a fresh snapshot ~1/sec. It owns the
// ProcessMap and the ETW backend, and on each tick:
//   1. refreshes the connection/PID tables,
//   2. drains per-PID and per-connection byte counters into rates,
//   3. assembles a list<ProcStat> (one row per process with network activity)
//      plus dashboard Totals,
//   4. hands a thread-safe snapshot to whoever asks (the GUI polls it).
//
// This is intentionally near-identical in shape to monitor.py so behavioural
// parity ports line-by-line.

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using NetPeekier.Core;

namespace NetPeekier.Native;

public sealed class NetworkMonitor : IDisposable
{
    public Settings Settings { get; }
    public ProcessMap ProcessMap { get; }
    public EtwMonitor Backend { get; }
    public HistoryLogger History { get; }

    /// <summary>
    /// The WFP engine handle, lazily-opened on first access. We do it lazily
    /// so non-Windows test scenarios and unit tests of NetworkMonitor's pure-data
    /// surface can construct a NetworkMonitor without touching fwpuclnt.dll.
    /// Returns null if WFP can't be reached (engine open failed, not on
    /// Windows, etc.) — every call site handles null gracefully.
    /// </summary>
    public WfpFirewall? Firewall => _firewall ??= TryOpenFirewall();
    private WfpFirewall? _firewall;

    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
    private static bool IsWindows() => OperatingSystem.IsWindows();

    private static WfpFirewall? TryOpenFirewall()
    {
        if (!IsWindows()) return null;
        try { return new WfpFirewall(); }
        catch
        {
            // Not elevated, fwpuclnt.dll missing, or engine open denied -
            // the app keeps running in monitor-only mode.
            return null;
        }
    }

    public string BackendName => Backend.Available ? "ETW Kernel-Network" : "connection table (no per-process bytes)";
    public bool HasPerProcessSpeed => Backend.Available;

    private readonly object _gate = new();
    private List<ProcStat> _procs = new();
    private readonly Totals _totals = new();

    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;
    private Task? _worker;

    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private TimeSpan _lastTick;

    // Activity tracking for idle hiding and dead-PID cleanup.
    private readonly Dictionary<int, double>   _lastActive  = new();   // pid -> elapsed seconds
    private readonly Dictionary<int, HashSet<ConnectionKey>> _prevConns = new();
    private readonly Dictionary<int, (long Up, long Down)>   _prevPidTotals = new();

    // "Forget pids after N minutes" linger cache. When a process stops having
    // live connections / traffic we keep showing it (with its last-known
    // stats) until this window expires, as long as it's still running. Keyed
    // by pid -> last wall-clock time (epoch seconds) it was seen active.
    private readonly Dictionary<int, double>     _lastSeen = new();
    private readonly Dictionary<int, ProcStat>   _lastStat = new();

    // LAN networks parsed from settings, cached.
    private List<IPNetworkRange> _lanNets;

    // System-wide IO baseline, used both for fallback rates and the cumulative
    // "session totals" the dashboard shows even in psutil-only mode.
    private long _baselineUp;
    private long _baselineDown;
    private long _lastIoUp;
    private long _lastIoDown;
    private TimeSpan _lastIoTime;

    // Lockdown state, mirroring monitor.py.
    private readonly Dictionary<string, double> _tempAllow = new();   // exe -> expiry epoch
    private readonly HashSet<string> _lockdownBlocked = new();
    private readonly HashSet<string> _lockdownPending = new();
    private readonly HashSet<string> _lockdownDecided = new();

    public Action<string, string>? LockdownPrompt { get; set; }

    public NetworkMonitor(TimeSpan? interval = null)
    {
        Diag.Log("NetworkMonitor.ctor: begin");
        _interval  = interval ?? TimeSpan.FromSeconds(1);
        Settings   = Settings.Load();
        Diag.Log("NetworkMonitor.ctor: Settings loaded");
        ProcessMap = new ProcessMap();
        Backend    = new EtwMonitor(ProcessMap);
        History    = new HistoryLogger(Path.Combine(Paths.EnsureLogDir(), "history.jsonl"));
        Diag.Log("NetworkMonitor.ctor: ProcessMap+EtwMonitor+History constructed");

        _lanNets = ParseLanNets(Settings.LanRanges);

        var io = ReadSystemIo();
        _baselineUp   = io.BytesSent;
        _baselineDown = io.BytesReceived;
        _lastIoUp     = io.BytesSent;
        _lastIoDown   = io.BytesReceived;
        _lastIoTime   = _wall.Elapsed;
        _lastTick     = _wall.Elapsed;
        Diag.Log("NetworkMonitor.ctor: system IO baseline taken");

        // Reconcile: anything currently in our WFP sublayer that we created
        // should also be in settings (and vice-versa on apply).
        try
        {
            Diag.Log("NetworkMonitor.ctor: about to access Firewall (may open WFP engine)");
            if (Firewall is { } fw)
            {
                Diag.Log("NetworkMonitor.ctor: WFP engine open; calling ListBlocked()");
                foreach (var exe in fw.ListBlocked())
                    if (!string.IsNullOrEmpty(exe) && !Settings.BlockedExes.Contains(exe))
                        Settings.BlockedExes.Add(exe);
                Diag.Log("NetworkMonitor.ctor: WFP reconcile done");
            }
            else
            {
                Diag.Log("NetworkMonitor.ctor: Firewall is null (not on Windows / not elevated / WFP open failed)");
            }
        }
        catch (Exception ex)
        {
            Diag.LogException("NetworkMonitor.ctor / WFP reconcile", ex);
            // Best-effort: missing filters reconcile next time the user
            // applies a change.
        }
        Diag.Log("NetworkMonitor.ctor: done");
    }

    public void Start()
    {
        Diag.Log("NetworkMonitor.Start: begin");
        if (_worker is not null) { Diag.Log("NetworkMonitor.Start: already running, no-op"); return; }
        try
        {
            ApplySettings(syncFirewall: true);
            Diag.Log("NetworkMonitor.Start: ApplySettings done");
        }
        catch (Exception ex)
        {
            Diag.LogException("NetworkMonitor.Start / ApplySettings", ex);
            // Don't rethrow — without the firewall sync we still want the
            // monitor loop to run.
        }
        try { Backend.Start(); } catch (Exception ex) { Diag.LogException("NetworkMonitor.Start / Backend.Start", ex); }
        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => Loop(_cts.Token));
        Diag.Log("NetworkMonitor.Start: worker task started");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); _worker?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        try { Backend.Stop(); } catch { /* ignore */ }
        try { ClearLockdownBlocks(); } catch { /* ignore */ }
        try { History.Flush(); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        Stop();
        Backend.Dispose();
        try { _firewall?.Dispose(); } catch { /* ignore */ }
        _firewall = null;
    }

    public (IReadOnlyList<ProcStat> Procs, Totals Totals) Snapshot()
    {
        lock (_gate) { return (_procs.ToList(), _totals.Clone()); }
    }

    /// <summary>
    /// Push every rule from settings into the backend in one atomic resync,
    /// clearing anything stale. Idempotent and safe to call after any edit.
    ///
    /// WFP edits (<paramref name="syncFirewall"/>) only happen at startup
    /// or after a master-switch flip; per-edit block/unblock is done
    /// directly by the caller so editing a limit or tag never spawns engine
    /// work from the GUI thread.
    /// </summary>
    public void ApplySettings(bool syncFirewall = false)
    {
        _lanNets = ParseLanNets(Settings.LanRanges);

        if (syncFirewall && Firewall is { } fw)
        {
            try { SyncFirewall(fw); }
            catch
            {
                // Best-effort. Surface to the GUI via a future status flag;
                // crashing the monitor on a single bad filter is worse.
            }
        }

        Settings.Save();
    }

    /// <summary>
    /// Authoritative one-shot resync: clear all our filters, then re-apply
    /// every block / per-IP rule / whitelist from settings. The "clear then
    /// re-apply" approach matches monitor.apply_settings in the Python build
    /// and means a settings file is always the source of truth.
    /// </summary>
    private void SyncFirewall(WfpFirewall fw)
    {
        var s = Settings;

        if (!s.FirewallEnabled)
        {
            // Master switch off: drop every filter we own, keep the config
            // (settings still remember what to block when toggled back on).
            fw.RemoveAllRules();
            return;
        }

        // Step 1: per-app blocks. Direct list + tag-blocked members.
        var toBlock = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in s.BlockedExes)
            if (!string.IsNullOrEmpty(e)) toBlock.Add(e);
        foreach (var tag in s.TagBlocked)
            foreach (var e in s.ExesWithTag(tag))
                if (!string.IsNullOrEmpty(e)) toBlock.Add(e);

        // Remove only the per-app blocks not present in the desired set,
        // then add the ones missing. Cheaper than wholesale clear when the
        // user just edited one rule.
        var currentlyBlocked = new HashSet<string>(fw.ListBlocked(), StringComparer.OrdinalIgnoreCase);
        foreach (var exe in currentlyBlocked.Except(toBlock))
            fw.UnblockApp(exe);
        foreach (var exe in toBlock.Except(currentlyBlocked))
            fw.BlockApp(exe);

        // Step 2: per-IP block rules and whitelists. These are rebuilt
        // wholesale because the per-rule identity doesn't survive a settings
        // round-trip; clearing+re-emitting is correct and cheap.
        fw.RemoveAllIpRules();
        foreach (var r in s.IpRules.Where(r => r.Action == "block"))
            fw.AddIpRule(r);
        foreach (var exe in s.IpRules
                              .Where(r => r.Action == "allow")
                              .Select(r => r.Exe)
                              .Distinct())
        {
            var allows = s.IpRules.Where(r => r.Exe == exe && r.Action == "allow").ToList();
            fw.SetWhitelist(exe, allows);
        }
    }

    // =====================================================================
    // Convenience surface used by the GUI. Each method is the C# port of the
    // matching method on Python's Monitor class; they keep the firewall state in
    // sync with settings and trigger an immediate WFP edit so the user sees
    // the effect right away (rather than waiting for the next tick).
    // =====================================================================

    /// <summary>Block or unblock one exe and persist.</summary>
    public void SetBlocked(string exe, bool blocked)
    {
        if (string.IsNullOrEmpty(exe)) return;
        if (blocked)
        {
            if (!Settings.BlockedExes.Contains(exe)) Settings.BlockedExes.Add(exe);
            if (Settings.FirewallEnabled) Firewall?.BlockApp(exe);
        }
        else
        {
            Settings.BlockedExes.Remove(exe);
            Firewall?.UnblockApp(exe);
        }
        _lockdownDecided.Remove(exe);
        Settings.Save();
    }

    /// <summary>Add or update a per-IP rule. Saves settings and pushes to WFP.</summary>
    public (bool Ok, string Message) AddIpRule(IpRule rule)
    {
        // Replace any existing equivalent rule.
        Settings.IpRules.RemoveAll(r => r.Equivalent(rule));
        Settings.IpRules.Add(rule);

        var result = (true, "");
        if (Settings.FirewallEnabled && Firewall is { } fw)
        {
            result = rule.Action == "allow"
                ? ApplyWhitelistFor(rule.Exe, fw)
                : fw.AddIpRule(rule);
        }
        Settings.Save();
        return result;
    }

    public (bool Ok, string Message) RemoveIpRule(IpRule rule)
    {
        Settings.IpRules.RemoveAll(r => r.Equivalent(rule));
        var result = (true, "");
        if (Firewall is { } fw)
        {
            result = rule.Action == "allow"
                ? ApplyWhitelistFor(rule.Exe, fw)
                : fw.RemoveIpRule(rule);
        }
        Settings.Save();
        return result;
    }

    private (bool, string) ApplyWhitelistFor(string exe, WfpFirewall fw)
    {
        var allows = Settings.IpRules
            .Where(r => r.Exe == exe && r.Action == "allow")
            .ToList();
        return fw.SetWhitelist(exe, allows);
    }

    /// <summary>Master switch. Off lifts every filter we own; On re-applies.</summary>
    public void SetFirewallEnabled(bool enabled)
    {
        Settings.FirewallEnabled = enabled;
        Settings.Save();
        if (Firewall is { } fw)
        {
            if (enabled) SyncFirewall(fw);
            else { fw.RemoveAllRules(); }
        }
    }

    /// <summary>
    /// Toggle Lockdown (default-deny) mode. When turned off, any blocks the
    /// lockdown sweep installed are cleared on the next tick (LockdownSweep
    /// sees the flag off and calls ClearLockdownBlocks). When turned on, the
    /// caller is expected to have ensured the firewall is enabled, since
    /// lockdown can only enforce through it.
    /// </summary>
    public void SetLockdown(bool on)
    {
        Settings.LockdownMode = on;
        Settings.Save();
        if (!on)
        {
            try { ClearLockdownBlocks(); } catch { /* ignore */ }
        }
    }

    /// <summary>Emergency cleanup: drop every filter and clear block state.</summary>
    public (int Count, string Message) RemoveAllFirewallRules()
    {
        if (Firewall is not { } fw) return (0, "Firewall engine unavailable.");
        var (count, msg) = fw.RemoveAllRules();
        Settings.BlockedExes.Clear();
        Settings.TagBlocked.Clear();
        Settings.Save();
        return (count, msg);
    }

    // =====================================================================
    // Per-tick loop.
    // =====================================================================

    private async Task Loop(CancellationToken ct)
    {
        Diag.Log("NetworkMonitor.Loop: tick worker entered");
        int tickCount = 0;
        while (!ct.IsCancellationRequested)
        {
            var t0 = _wall.Elapsed;
            try { Tick(); }
            catch (Exception exc)
            {
                Diag.LogException("NetworkMonitor.Tick", exc);
            }
            if (++tickCount == 1) Diag.Log("NetworkMonitor.Loop: first tick completed");

            var dt = _wall.Elapsed - t0;
            var sleep = _interval - dt;
            if (sleep < TimeSpan.FromMilliseconds(50))
                sleep = TimeSpan.FromMilliseconds(50);
            try { await Task.Delay(sleep, ct); }
            catch (TaskCanceledException) { break; }
        }
        Diag.Log("NetworkMonitor.Loop: tick worker exiting");
    }

    private void Tick()
    {
        var now = _wall.Elapsed;
        var interval = (now - _lastTick).TotalSeconds;
        if (interval <= 0) interval = 1.0;
        _lastTick = now;

        ProcessMap.Refresh(force: true);
        var connsByPid = ProcessMap.SnapshotConnectionsByPid();

        var (pidRates, connRates) = Backend.DrainRates(interval);
        var pidTotals = Backend.PidTotals();
        var connTotals = Backend.ConnTotals();

        var s = Settings;

        // Effective block set: explicit exes plus members of blocked tags.
        // When the master switch is off, nothing is enforced and the live
        // list shows no blocks (the config persists in settings and is
        // shown in the firewall manager).
        var blockedExes = new HashSet<string>();
        if (s.FirewallEnabled)
        {
            foreach (var e in s.BlockedExes) if (!string.IsNullOrEmpty(e)) blockedExes.Add(e);
            foreach (var tag in s.TagBlocked)
                foreach (var e in s.ExesWithTag(tag))
                    if (!string.IsNullOrEmpty(e)) blockedExes.Add(e);
        }

        var procs = new List<ProcStat>();
        double sumUp = 0, sumDown = 0;

        var nowWall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var idleSecs = s.IdleHideMinutes.HasValue ? s.IdleHideMinutes.Value * 60.0 : (double?)null;
        // "Forget pids after N minutes": keep a quiet-but-alive process on
        // screen for this long after its last activity. null/0 = forget
        // immediately (old behaviour).
        var lingerSecs = s.PacketPurgeMinutes.HasValue && s.PacketPurgeMinutes.Value > 0
            ? s.PacketPurgeMinutes.Value * 60.0 : 0.0;

        // Union of pids that have connections, current traffic, or any history.
        var livePids = new HashSet<int>(connsByPid.Keys);
        foreach (var pid in pidRates.Keys)  livePids.Add(pid);
        var allPids = new HashSet<int>(livePids);
        foreach (var pid in pidTotals.Keys) allPids.Add(pid);

        // Also reconsider pids we saw recently (within the linger window) even
        // if they have no live connection / rate this tick, so a process that
        // briefly goes quiet doesn't blink out of the list.
        if (lingerSecs > 0)
        {
            foreach (var kv in _lastSeen)
                if (nowWall - kv.Value <= lingerSecs)
                    allPids.Add(kv.Key);
        }

        var deadPids = new HashSet<int>();
        var seenPids = new HashSet<int>();

        foreach (var pid in allPids)
        {
            bool live = livePids.Contains(pid);
            // Terminated-process cleanup. A pid with no live data is dropped
            // only when it's both past its linger window AND actually gone.
            if (!live)
            {
                bool withinLinger = lingerSecs > 0
                    && _lastSeen.TryGetValue(pid, out var ls)
                    && (nowWall - ls) <= lingerSecs;
                if (!withinLinger && !PidAlive(pid))
                {
                    deadPids.Add(pid);
                    continue;
                }
                if (!withinLinger && !pidTotals.ContainsKey(pid))
                {
                    // Alive but quiet and not within linger and no history to
                    // show — skip it (don't accumulate forever).
                    if (!PidAlive(pid)) { deadPids.Add(pid); }
                    continue;
                }
            }
            seenPids.Add(pid);

            connsByPid.TryGetValue(pid, out var conns);
            conns ??= new List<Connection>();

            pidRates.TryGetValue(pid, out var rate);
            pidTotals.TryGetValue(pid, out var tot);
            sumUp   += rate.Up;
            sumDown += rate.Down;

            // Stamp per-connection rates + totals onto the Connection objects.
            foreach (var c in conns)
            {
                if (connRates.TryGetValue(c.Key, out var cr))
                {
                    c.UpBps   = cr.Up;
                    c.DownBps = cr.Down;
                }
                if (connTotals.TryGetValue(c.Key, out var ct))
                {
                    c.UpTotal   = ct.Up;
                    c.DownTotal = ct.Down;
                }
            }

            // Activity tracking for idle hiding. "Active" = measurable traffic,
            // or the set of connections changed (covers the no-driver case
            // where we can't see bytes).
            var connSet = new HashSet<ConnectionKey>(conns.Select(c => c.Key));
            _prevConns.TryGetValue(pid, out var prevSet);
            var changed = prevSet is null || !prevSet.SetEquals(connSet);
            _prevConns[pid] = connSet;
            if (rate.Up > 0 || rate.Down > 0 || changed)
                _lastActive[pid] = nowWall;
            if (!_lastActive.ContainsKey(pid))
                _lastActive[pid] = nowWall;
            bool idle = idleSecs.HasValue && (nowWall - _lastActive[pid]) > idleSecs.Value;

            // WAN/LAN classification from connections' remote IPs.
            bool usesWan = false;
            foreach (var c in conns)
            {
                if (!string.IsNullOrEmpty(c.RemoteIp) && IsWan(c.RemoteIp, _lanNets))
                {
                    usesWan = true;
                    break;
                }
            }

            var exe = ProcessMap.Exe(pid);
            var (upLim, downLim) = !string.IsNullOrEmpty(exe) ? s.ExeLimit(exe) : (0, 0);
            var listening = ProcessMap.ListeningPorts(conns).ToList();

            var ps = new ProcStat
            {
                Pid       = pid,
                Name      = ProcessMap.Name(pid),
                Exe       = exe,
                UpBps     = rate.Up,
                DownBps   = rate.Down,
                UpTotal   = tot.Up,
                DownTotal = tot.Down,
                ListeningPorts = listening,
                Connections    = conns,
                Blocked        = !string.IsNullOrEmpty(exe) && blockedExes.Contains(exe),
                UpLimit        = upLim,
                DownLimit      = downLim,
                Tag            = !string.IsNullOrEmpty(exe) && s.ExeTags.TryGetValue(exe, out var t) ? t : "",
                UsesWan        = usesWan,
            };

            // Does this pid have anything worth showing right now? Connections,
            // current rate, or cumulative totals all count as "real".
            bool hasData = conns.Count > 0 || rate.Up > 0 || rate.Down > 0
                           || tot.Up > 0 || tot.Down > 0 || listening.Count > 0;

            if (hasData)
            {
                _lastSeen[pid] = nowWall;
                _lastStat[pid] = ps;
                if (!idle) procs.Add(ps);
            }
            else if (lingerSecs > 0
                     && _lastSeen.TryGetValue(pid, out var ls2)
                     && (nowWall - ls2) <= lingerSecs
                     && _lastStat.TryGetValue(pid, out var prevStat))
            {
                // Quiet now, but within the linger window — keep showing the
                // last-known snapshot (rates zeroed) so it doesn't blink out.
                var lingerStat = prevStat with
                {
                    UpBps = 0,
                    DownBps = 0,
                    Connections = new List<Connection>(),
                };
                if (!idle) procs.Add(lingerStat);
            }
            else if (!idle)
            {
                // No data and no linger — show the bare row (name + ports).
                procs.Add(ps);
            }

            // Activity logging: per-exe byte delta since last tick.
            if (!string.IsNullOrEmpty(exe) && (tot.Up > 0 || tot.Down > 0))
            {
                _prevPidTotals.TryGetValue(pid, out var prev);
                var dUp   = tot.Up   >= prev.Up   ? tot.Up   - prev.Up   : tot.Up;
                var dDown = tot.Down >= prev.Down ? tot.Down - prev.Down : tot.Down;
                if (dUp > 0 || dDown > 0)
                    History.Record(exe, ps.Name, dUp, dDown);
            }
            _prevPidTotals[pid] = tot;
        }

        // Forget dead pids everywhere.
        if (deadPids.Count > 0)
        {
            foreach (var pid in deadPids)
            {
                _lastActive.Remove(pid);
                _prevConns.Remove(pid);
                _prevPidTotals.Remove(pid);
                _lastSeen.Remove(pid);
                _lastStat.Remove(pid);
            }
            Backend.ForgetPids(deadPids);
        }
        // Expire linger entries whose window has elapsed (so they don't grow
        // unbounded for short-lived processes).
        if (lingerSecs > 0)
        {
            var expired = _lastSeen.Where(kv => (nowWall - kv.Value) > lingerSecs)
                                   .Select(kv => kv.Key).ToList();
            foreach (var pid in expired)
            {
                if (!PidAlive(pid))
                {
                    _lastSeen.Remove(pid);
                    _lastStat.Remove(pid);
                }
            }
        }
        // Prune activity maps for pids we no longer track at all.
        var stale = _lastActive.Keys.Where(p => !seenPids.Contains(p) && !livePids.Contains(p)).ToList();
        foreach (var pid in stale)
        {
            _lastActive.Remove(pid);
            _prevConns.Remove(pid);
            _prevPidTotals.Remove(pid);
        }

        History.MaybeFlush();

        procs.Sort((a, b) =>
        {
            var byBytes = (b.DownBps + b.UpBps).CompareTo(a.DownBps + a.UpBps);
            return byBytes != 0 ? byBytes : b.Connections.Count.CompareTo(a.Connections.Count);
        });

        // Dashboard totals. When the backend gives real per-PID data, sum it.
        // Otherwise fall back to the OS-wide counters so the dashboard still
        // moves.
        double upNow, downNow;
        if (HasPerProcessSpeed && (sumUp > 0 || sumDown > 0))
        {
            upNow = sumUp;
            downNow = sumDown;
        }
        else
        {
            var io = ReadSystemIo();
            var dt = Math.Max(0.001, (_wall.Elapsed - _lastIoTime).TotalSeconds);
            upNow   = Math.Max(0, (io.BytesSent     - _lastIoUp)   / dt);
            downNow = Math.Max(0, (io.BytesReceived - _lastIoDown) / dt);
            _lastIoUp   = io.BytesSent;
            _lastIoDown = io.BytesReceived;
            _lastIoTime = _wall.Elapsed;
        }

        // Cumulative session totals: system-wide bytes since app start.
        // Using OS counters here keeps this accurate even in no-ETW mode.
        var cur = ReadSystemIo();
        var sessUp   = Math.Max(0, cur.BytesSent     - _baselineUp);
        var sessDown = Math.Max(0, cur.BytesReceived - _baselineDown);

        lock (_gate)
        {
            _procs = procs;
            _totals.UpNow    = upNow;
            _totals.DownNow  = downNow;
            _totals.UpPeak   = Math.Max(_totals.UpPeak,   upNow);
            _totals.DownPeak = Math.Max(_totals.DownPeak, downNow);
            _totals.UpTotal   = sessUp;
            _totals.DownTotal = sessDown;
        }

        // Default-deny enforcement (no-op unless lockdown is on).
        try { LockdownSweep(procs); } catch { /* never throw out of tick */ }
    }

    // =====================================================================
    // Lockdown.
    // =====================================================================

    private void LockdownSweep(IEnumerable<ProcStat> procs)
    {
        var s = Settings;
        if (!(s.LockdownMode && s.FirewallEnabled))
        {
            if (_lockdownBlocked.Count > 0) ClearLockdownBlocks();
            return;
        }
        if (Firewall is not { } fw) return;

        // Expire stale temp-allows so previously-allowed exes get
        // re-evaluated by the sweep below.
        var nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        foreach (var exe in _tempAllow.Where(kv => kv.Value <= nowEpoch).Select(kv => kv.Key).ToList())
        {
            _tempAllow.Remove(exe);
            _lockdownDecided.Remove(exe);
        }

        var own = OwnExePath();
        foreach (var p in procs)
        {
            var exe = p.Exe;
            if (string.IsNullOrEmpty(exe) || !p.UsesWan) continue;
            if (_lockdownDecided.Contains(exe)) continue;
            if (string.Equals(exe, own, StringComparison.OrdinalIgnoreCase)
                || !WfpFirewall.ValidExe(exe))
            {
                _lockdownDecided.Add(exe);
                continue;
            }
            if (IsAllowedNow(exe) || s.BlockedExes.Contains(exe))
            {
                // Don't cache temp-allowed exes; they need re-checking when
                // the allowance expires. Permanent states are safe to cache.
                if (!_tempAllow.ContainsKey(exe)) _lockdownDecided.Add(exe);
                continue;
            }
            if (_lockdownBlocked.Contains(exe))
            {
                _lockdownDecided.Add(exe);
                continue;
            }
            // Deny by default: block now, then ask.
            try { fw.BlockApp(exe); } catch { /* best-effort */ }
            _lockdownBlocked.Add(exe);
            _lockdownDecided.Add(exe);
            if (!_lockdownPending.Contains(exe) && LockdownPrompt is { } prompt)
            {
                _lockdownPending.Add(exe);
                try { prompt(exe, p.Name); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>True if exe is on the permanent allow list or has a live temp allow.</summary>
    private bool IsAllowedNow(string exe)
    {
        if (string.IsNullOrEmpty(exe)) return false;
        if (Settings.IsAllowedExe(exe)) return true;
        return _tempAllow.TryGetValue(exe, out var exp)
            && exp > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }

    private static string OwnExePath()
    {
        try
        {
            var p = Environment.ProcessPath ?? "";
            return string.IsNullOrEmpty(p) ? "" : Path.GetFullPath(p);
        }
        catch { return ""; }
    }

    /// <summary>Allow an exe temporarily (lockdown mode).</summary>
    public void AllowTemporarily(string exe, int minutes)
    {
        if (string.IsNullOrEmpty(exe)) return;
        Settings.AllowMinutes = Math.Max(1, minutes);
        _tempAllow[exe] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0
                          + Settings.AllowMinutes * 60;
        // Lift the lockdown block so traffic flows.
        LockdownUnblock(exe);
        _lockdownPending.Remove(exe);
        Settings.Save();
    }

    /// <summary>User chose to block a prompted process.</summary>
    public void LockdownBlock(string exe, bool permanent)
    {
        if (string.IsNullOrEmpty(exe)) return;
        _lockdownPending.Remove(exe);
        if (permanent)
        {
            SetBlocked(exe, true);
        }
        // Otherwise it's already blocked by the lockdown sweep; leave it.
    }

    /// <summary>Mark an exe as permanently allowed (mirrors SetBlocked).</summary>
    public void SetAllowed(string exe, bool allowed)
    {
        if (string.IsNullOrEmpty(exe)) return;
        if (allowed)
        {
            if (!Settings.AllowedExes.Contains(exe)) Settings.AllowedExes.Add(exe);
            LockdownUnblock(exe);
        }
        else
        {
            Settings.AllowedExes.Remove(exe);
        }
        _lockdownPending.Remove(exe);
        _lockdownDecided.Remove(exe);
        Settings.Save();
    }

    private void LockdownUnblock(string exe)
    {
        _lockdownDecided.Remove(exe);
        if (_lockdownBlocked.Contains(exe))
        {
            _lockdownBlocked.Remove(exe);
            if (!Settings.BlockedExes.Contains(exe))
                try { Firewall?.UnblockApp(exe); } catch { /* ignore */ }
        }
    }

    private void ClearLockdownBlocks()
    {
        try
        {
            if (Firewall is { } fw)
                foreach (var exe in _lockdownBlocked.ToList())
                    if (!Settings.BlockedExes.Contains(exe))
                        try { fw.UnblockApp(exe); } catch { /* ignore */ }
        }
        catch { /* ignore */ }
        _lockdownBlocked.Clear();
        _lockdownPending.Clear();
        _tempAllow.Clear();
        _lockdownDecided.Clear();
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static bool PidAlive(int pid)
    {
        try { using var _ = Process.GetProcessById(pid); return true; }
        catch { return false; }
    }

    /// <summary>Parsed CIDR ranges from Settings.LanRanges.</summary>
    private record struct IPNetworkRange(int Family, System.Numerics.BigInteger Lo, System.Numerics.BigInteger Hi);

    private static List<IPNetworkRange> ParseLanNets(IEnumerable<string> cidrs)
    {
        var nets = new List<IPNetworkRange>();
        foreach (var c in cidrs)
        {
            try
            {
                var bits = c.Split('/');
                if (bits.Length != 2) continue;
                if (!IPAddress.TryParse(bits[0], out var ip)) continue;
                if (!int.TryParse(bits[1], out var prefix)) continue;
                var fam = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 4 : 6;
                var maxBits = fam == 4 ? 32 : 128;
                if (prefix < 0 || prefix > maxBits) continue;

                var bytes = ip.GetAddressBytes();
                Array.Reverse(bytes);
                var padded = new byte[bytes.Length + 1];
                Buffer.BlockCopy(bytes, 0, padded, 0, bytes.Length);
                var n = new System.Numerics.BigInteger(padded);

                var hostBits = maxBits - prefix;
                System.Numerics.BigInteger mask;
                if (hostBits == 0) mask = (System.Numerics.BigInteger.One << maxBits) - 1;
                else mask = ((System.Numerics.BigInteger.One << maxBits) - 1)
                          ^ ((System.Numerics.BigInteger.One << hostBits) - 1);
                var lo = n & mask;
                var hi = lo | ((System.Numerics.BigInteger.One << hostBits) - 1);
                nets.Add(new IPNetworkRange(fam, lo, hi));
            }
            catch { /* skip; matches Python's lenient behaviour */ }
        }
        return nets;
    }

    /// <summary>
    /// True if a remote IP is a routable internet (WAN) address: not in any
    /// configured LAN range, and not otherwise private/local.
    /// </summary>
    private static bool IsWan(string ipStr, List<IPNetworkRange> nets)
    {
        if (!IPAddress.TryParse(ipStr, out var ip)) return false;

        var fam = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 4 : 6;
        var bytes = ip.GetAddressBytes();
        Array.Reverse(bytes);
        var padded = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, padded, 0, bytes.Length);
        var n = new System.Numerics.BigInteger(padded);

        foreach (var net in nets)
            if (net.Family == fam && n >= net.Lo && n <= net.Hi) return false;

        // Built-in classification (matches Python's ip.is_private / loopback /
        // link_local / multicast / unspecified / reserved checks).
        if (IsBuiltinNonWan(ip)) return false;
        return true;
    }

    private static bool IsBuiltinNonWan(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            // 0.0.0.0/8 (unspecified / "this network")
            if (b[0] == 0) return true;
            // 10/8, 172.16/12, 192.168/16, 169.254/16 (link-local),
            // 224/4 multicast, 240/4 reserved
            if (b[0] == 10) return true;
            if (b[0] == 172 && (b[1] & 0xf0) == 16) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            if (b[0] >= 224) return true;
        }
        else
        {
            if (ip.IsIPv6Multicast || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            // Unique-local fc00::/7
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xfe) == 0xfc) return true;
            // Unspecified ::
            if (b.All(x => x == 0)) return true;
        }
        return false;
    }

    private readonly record struct IoCounters(long BytesSent, long BytesReceived);

    /// <summary>
    /// System-wide bytes-sent/received summed across all interfaces.
    /// Equivalent to psutil.net_io_counters() in the Python build.
    /// Used as fallback rates and for the cumulative session totals.
    /// </summary>
    private static IoCounters ReadSystemIo()
    {
        long up = 0, down = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var s = nic.GetIPStatistics();
                up   += s.BytesSent;
                down += s.BytesReceived;
            }
        }
        catch { /* best-effort */ }
        return new IoCounters(up, down);
    }
}
