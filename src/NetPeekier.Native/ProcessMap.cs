// Port of netpeekier/procmap.py
//
// Caches process metadata and the endpoint -> PID table. Refreshing the
// connection table is relatively expensive, so the monitor calls Refresh()
// on an interval (1 Hz) rather than per event, and packet attribution hits
// the cached dicts.
//
// STATUS: skeleton; needs IpHelper.SnapshotConnections (Phase 2) before it
// produces data. Process name/exe resolution will use QueryFullProcessImageName
// via CsWin32.

using System.Diagnostics;
using NetPeekier.Core;

namespace NetPeekier.Native;

public sealed class ProcessMap
{
    private readonly object _gate = new();

    // (proto, local_ip, local_port) -> pid     (exact match, preferred)
    private Dictionary<(string Proto, string Ip, int Port), int> _exact = new();
    // (proto, local_port) -> pid               (loose fallback; ip wildcarded)
    private Dictionary<(string Proto, int Port), int> _loose = new();

    private readonly Dictionary<int, string>  _names = new();
    private readonly Dictionary<int, string>  _exes  = new();
    // process start time per cached pid, to detect PID reuse (a recycled pid
    // belonging to a different process must not keep the old name/exe).
    private readonly Dictionary<int, DateTime> _ctime = new();

    private IReadOnlyList<Connection> _rawConns = Array.Empty<Connection>();
    private DateTime _rawConnsTs = DateTime.MinValue;

    public string Name(int? pid)
    {
        if (pid is null) return "System Idle / Unknown";
        var p = pid.Value;
        lock (_gate)
        {
            if (_names.TryGetValue(p, out var cached)) return cached;
            try
            {
                using var proc = Process.GetProcessById(p);
                _names[p] = proc.ProcessName;
                try { _ctime[p] = proc.StartTime; } catch { /* access denied; ignore */ }
                return _names[p];
            }
            catch
            {
                _names[p] = $"PID {p}";
                return _names[p];
            }
        }
    }

    public string Exe(int? pid)
    {
        if (pid is null) return "";
        var p = pid.Value;
        lock (_gate)
        {
            if (_exes.TryGetValue(p, out var cached)) return cached;
            // TODO (Phase 2): use QueryFullProcessImageName via CsWin32 for
            // protected processes. Process.MainModule throws AccessDenied on
            // services and 32-vs-64 mismatches. For now, best-effort:
            try
            {
                using var proc = Process.GetProcessById(p);
                _exes[p] = proc.MainModule?.FileName ?? "";
            }
            catch
            {
                _exes[p] = "";
            }
            return _exes[p];
        }
    }

    public int? PidForEndpoint(string proto, string localIp, int localPort)
    {
        lock (_gate)
        {
            if (_exact.TryGetValue((proto, localIp, localPort), out var pid))
                return pid;
            return _loose.TryGetValue((proto, localPort), out var loose) ? loose : null;
        }
    }

    public int? PidForPort(string proto, int localPort)
    {
        lock (_gate)
        {
            return _loose.TryGetValue((proto, localPort), out var pid) ? pid : null;
        }
    }

    /// <summary>
    /// Re-enumerate. Call from the monitor tick. <paramref name="minInterval"/>
    /// throttles to keep cheap repeats from being expensive.
    /// </summary>
    public void Refresh(bool force = false, double minIntervalSeconds = 0.9)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _rawConnsTs).TotalSeconds < minIntervalSeconds) return;

        var conns = IpHelper.SnapshotConnections();
        var exact = new Dictionary<(string, string, int), int>();
        var loose = new Dictionary<(string, int), int>();
        foreach (var c in conns)
        {
            if (c.LocalPort == 0) continue;
            exact[(c.Protocol, c.LocalIp, c.LocalPort)] = c.Pid;
            loose[(c.Protocol, c.LocalPort)] = c.Pid;
        }

        lock (_gate)
        {
            _exact = exact;
            _loose = loose;
            _rawConns = conns;
            _rawConnsTs = now;
            PruneCaches();
        }
    }

    public IReadOnlyDictionary<int, List<Connection>> SnapshotConnectionsByPid()
    {
        IReadOnlyList<Connection> source;
        lock (_gate) { source = _rawConns; }
        var byPid = new Dictionary<int, List<Connection>>();
        foreach (var c in source)
        {
            if (!byPid.TryGetValue(c.Pid, out var list))
                byPid[c.Pid] = list = new List<Connection>();
            list.Add(c);
        }
        return byPid;
    }

    public IReadOnlyList<int> ListeningPorts(IEnumerable<Connection> conns) =>
        conns.Where(c => c.Status == "LISTEN" && c.LocalPort != 0)
             .Select(c => c.LocalPort)
             .Distinct()
             .OrderBy(p => p)
             .ToList();

    private void PruneCaches()
    {
        var live = new HashSet<int>(_loose.Values);
        foreach (var pid in _exact.Values) live.Add(pid);

        foreach (var pid in _names.Keys.ToList())
        {
            if (!live.Contains(pid))
            {
                _names.Remove(pid);
                _exes.Remove(pid);
                _ctime.Remove(pid);
            }
        }
        // PID-reuse check: if the process at this PID now has a different
        // start time than we cached, drop the stale name/exe entry.
        foreach (var pid in live)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                var ct = proc.StartTime;
                if (_ctime.TryGetValue(pid, out var seen) && seen != ct)
                {
                    _names.Remove(pid);
                    _exes.Remove(pid);
                }
                _ctime[pid] = ct;
            }
            catch { /* process gone; will be pruned on next pass */ }
        }
    }
}
