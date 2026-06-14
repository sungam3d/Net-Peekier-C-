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
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
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

    // ---- Win32 process-image resolution ---------------------------------
    // Process.ProcessName / MainModule fail on a LOT of processes (services,
    // elevated, 32-vs-64-bit mismatch), which is why an earlier version saw
    // every row fall back to "PID nnnn". QueryFullProcessImageName via a
    // LIMITED-information OpenProcess handle works for almost everything when
    // we're elevated, and degrades gracefully otherwise.

    [Flags]
    private enum ProcessAccess : uint
    {
        QueryInformation        = 0x0400,
        QueryLimitedInformation = 0x1000,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(ProcessAccess access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess, int flags, StringBuilder buffer, ref int size);

    /// <summary>
    /// Full image path for a pid via QueryFullProcessImageName, or "" if it
    /// can't be resolved (process gone, or insufficient rights even with a
    /// limited handle). Never throws.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string ImagePath(int pid)
    {
        if (pid <= 0) return "";
        IntPtr h = OpenProcess(ProcessAccess.QueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero)
            h = OpenProcess(ProcessAccess.QueryInformation, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
        }
        finally { CloseHandle(h); }
    }

    public string Name(int? pid)
    {
        if (pid is null) return "System Idle / Unknown";
        var p = pid.Value;
        lock (_gate)
        {
            // Only treat a cached value as authoritative if it's a REAL name
            // (not the "PID n" fallback). This stops a transient failure from
            // poisoning the cache forever, which is what made every row show
            // "PID nnnn".
            if (_names.TryGetValue(p, out var cached) &&
                !cached.StartsWith("PID ", StringComparison.Ordinal))
                return cached;

            // Prefer the Win32 image path (works for services/elevated procs).
            string name = "";
            try
            {
                var path = OperatingSystem.IsWindows() ? ImagePath(p) : "";
                if (!string.IsNullOrEmpty(path))
                {
                    name = Path.GetFileName(path);
                    _exes[p] = path;   // opportunistically fill the exe cache too
                }
            }
            catch { /* fall through */ }

            // Fallback to the managed API for the odd case Win32 misses.
            if (string.IsNullOrEmpty(name))
            {
                try
                {
                    using var proc = Process.GetProcessById(p);
                    name = proc.ProcessName;
                    if (!string.IsNullOrEmpty(name) &&
                        !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        name += ".exe";
                    try { _ctime[p] = proc.StartTime; } catch { /* access denied */ }
                }
                catch { /* process gone or no access */ }
            }

            if (string.IsNullOrEmpty(name)) name = $"PID {p}";
            _names[p] = name;
            return name;
        }
    }

    public string Exe(int? pid)
    {
        if (pid is null) return "";
        var p = pid.Value;
        lock (_gate)
        {
            // Same anti-poison rule: only trust a non-empty cached path.
            if (_exes.TryGetValue(p, out var cached) && !string.IsNullOrEmpty(cached))
                return cached;

            string exe = "";
            try
            {
                exe = OperatingSystem.IsWindows() ? ImagePath(p) : "";
            }
            catch { /* ignore */ }

            if (string.IsNullOrEmpty(exe))
            {
                // Last resort: managed MainModule (often denied; that's fine).
                try
                {
                    using var proc = Process.GetProcessById(p);
                    exe = proc.MainModule?.FileName ?? "";
                }
                catch { /* ignore */ }
            }

            _exes[p] = exe;
            return exe;
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
