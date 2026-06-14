// Port of netpeekier/sysstats.py
//
// CPU / GPU / RAM stats for the dashboard's third column. Best-effort and
// degrades gracefully, exactly like the Python build: a sensor we can't read
// shows "--" and never throws into the monitor loop.
//
// Sources, in order of preference:
//   * PerformanceCounter (PDH)  -> CPU load, RAM used%   (always present)
//   * GlobalMemoryStatusEx       -> RAM used/total GB     (always present)
//   * registry                   -> CPU base clock MHz    (always present)
//   * LibreHardwareMonitorLib    -> CPU/GPU/RAM temps + GPU clock/load
//     (referenced via NuGet, MPL-2.0; ships automatically. Needs admin to
//      read the sensors — same as the Python build. If sensor init fails,
//      temps just show "--".)
//
// LibreHardwareMonitor is now a real compile-time reference rather than a
// reflection-loaded drop-in dll: the package and its native helper are
// copied next to the exe by the build, so there's nothing for the user to
// place by hand.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LibreHardwareMonitor.Hardware;
using NetPeekier.Core;

namespace NetPeekier.Native;

public sealed class SystemStatsSnapshot
{
    public double? CpuLoad   { get; init; }   // %
    public double? CpuClock  { get; init; }   // MHz
    public double? CpuTemp   { get; init; }   // °C
    public double? GpuLoad   { get; init; }   // %
    public double? GpuClock  { get; init; }   // MHz
    public double? GpuTemp   { get; init; }   // °C
    public double? RamUsed   { get; init; }   // %
    public double? RamUsedGb { get; init; }   // GiB in use
    public double? RamTotalGb{ get; init; }   // GiB installed
    public double? RamClock  { get; init; }   // MHz
    public double? RamTemp   { get; init; }   // °C
}

[SupportedOSPlatform("windows")]
public sealed class SystemMonitor : IDisposable
{
    private readonly double _interval;
    private readonly object _gate = new();
    private SystemStatsSnapshot _stats = new();

    private CancellationTokenSource? _cts;
    private Task? _worker;

    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _ramCounter;       // % committed bytes in use

    private LibreHwmBridge? _hwm;

    /// <summary>For the status bar / about info: where temps come from.</summary>
    public string TempSource { get; private set; } = "none";

    public SystemMonitor(double intervalSeconds = 2.0) { _interval = intervalSeconds; }

    public void Start()
    {
        if (_worker is not null) return;
        if (!OperatingSystem.IsWindows()) return;

        InitCounters();
        InitOptionalSensors();

        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => Loop(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); _worker?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _worker = null;
        try { _cpuCounter?.Dispose(); } catch { /* ignore */ }
        try { _ramCounter?.Dispose(); } catch { /* ignore */ }
        try { _hwm?.Close(); } catch { /* ignore */ }
        _cpuCounter = null;
        _ramCounter = null;
    }

    public void Dispose() => Stop();

    public SystemStatsSnapshot Snapshot()
    {
        lock (_gate) { return _stats; }
    }

    // =====================================================================
    // Setup.
    // =====================================================================

    private void InitCounters()
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue();   // prime; first read is always 0
        }
        catch (Exception ex) { Diag.LogException("SystemMonitor.InitCounters/cpu", ex); _cpuCounter = null; }

        try
        {
            _ramCounter = new PerformanceCounter("Memory", "% Committed Bytes In Use");
            _ramCounter.NextValue();
        }
        catch (Exception ex) { Diag.LogException("SystemMonitor.InitCounters/ram", ex); _ramCounter = null; }
    }

    private void InitOptionalSensors()
    {
        try
        {
            _hwm = LibreHwmBridge.TryCreate();
            if (_hwm is not null)
            {
                TempSource = "LibreHardwareMonitor";
                Diag.Log("SystemMonitor: LibreHardwareMonitor sensors active");
            }
            else
            {
                Diag.Log("SystemMonitor: sensor init failed (temps will show --; admin required)");
            }
        }
        catch (Exception ex)
        {
            Diag.LogException("SystemMonitor.InitOptionalSensors", ex);
            _hwm = null;
        }
    }

    // =====================================================================
    // Polling loop.
    // =====================================================================

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            SystemStatsSnapshot snap;
            try { snap = Read(); }
            catch (Exception ex) { Diag.LogException("SystemMonitor.Read", ex); snap = new(); }

            lock (_gate) { _stats = snap; }

            try { await Task.Delay(TimeSpan.FromSeconds(_interval), ct); }
            catch (TaskCanceledException) { break; }
        }
    }

    private SystemStatsSnapshot Read()
    {
        double? cpuLoad = null, cpuClock = null, ramUsed = null;

        try { if (_cpuCounter is not null) cpuLoad = _cpuCounter.NextValue(); } catch { /* ignore */ }
        try { if (_ramCounter is not null) ramUsed = _ramCounter.NextValue(); } catch { /* ignore */ }
        try { cpuClock = CpuClockMhz(); } catch { /* ignore */ }

        // Physical RAM used/total in GiB via GlobalMemoryStatusEx. This is a
        // more intuitive figure than the "% committed bytes" counter (which
        // includes the page file), and gives us the dwMemoryLoad percent too.
        double? ramUsedGb = null, ramTotalGb = null, ramLoadPct = null;
        try
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem))
            {
                const double GiB = 1024.0 * 1024 * 1024;
                ramTotalGb = mem.ullTotalPhys / GiB;
                ramUsedGb  = (mem.ullTotalPhys - mem.ullAvailPhys) / GiB;
                ramLoadPct = mem.dwMemoryLoad;
            }
        }
        catch { /* ignore */ }

        // Optional hardware sensors (temps, GPU details).
        LibreHwmReading hwm = default;
        if (_hwm is not null)
        {
            try { hwm = _hwm.Read(); } catch (Exception ex) { Diag.LogException("SystemMonitor.hwm.Read", ex); }
        }

        return new SystemStatsSnapshot
        {
            CpuLoad   = cpuLoad,
            CpuClock  = cpuClock,
            // Prefer the physical-memory load% from GlobalMemoryStatusEx;
            // fall back to the committed-bytes counter if that failed.
            RamUsed   = ramLoadPct ?? ramUsed,
            RamUsedGb = ramUsedGb,
            RamTotalGb= ramTotalGb,
            CpuTemp   = hwm.CpuTemp,
            GpuTemp   = hwm.GpuTemp,
            GpuLoad   = hwm.GpuLoad,
            GpuClock  = hwm.GpuClock,
            RamTemp   = hwm.RamTemp,
            RamClock  = hwm.RamClock,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint   dwLength;
        public uint   dwMemoryLoad;
        public ulong  ullTotalPhys;
        public ulong  ullAvailPhys;
        public ulong  ullTotalPageFile;
        public ulong  ullAvailPageFile;
        public ulong  ullTotalVirtual;
        public ulong  ullAvailVirtual;
        public ulong  ullAvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>
    /// Current CPU max clock in MHz, read from the registry (the same value
    /// Task Manager shows as the "base speed"; live per-core frequency needs
    /// a counter that isn't reliably present, so we report the rated clock).
    /// </summary>
    private static double? CpuClockMhz()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var mhz = key?.GetValue("~MHz");
            if (mhz is int i) return i;
        }
        catch { /* ignore */ }
        return null;
    }
}

// =====================================================================
// LibreHardwareMonitor bridge (typed, via the NuGet reference).
//
// Drives the Computer / IHardware / ISensor API directly. The reading
// logic mirrors sysstats._read_hardwaremonitor: gather candidate sensors
// per metric, then pick the headline value with the same preference rules.
// =====================================================================

internal readonly struct LibreHwmReading
{
    public double? CpuTemp  { get; init; }
    public double? GpuTemp  { get; init; }
    public double? GpuLoad  { get; init; }
    public double? GpuClock { get; init; }
    public double? RamTemp  { get; init; }
    public double? RamClock { get; init; }
}

/// <summary>
/// An IVisitor that updates a piece of hardware and its sub-hardware when
/// the library traverses the tree. LibreHardwareMonitor requires an explicit
/// Update() pass each time you want fresh values; the visitor is the
/// documented way to do that.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) => computer.Traverse(this);
    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware) sub.Accept(this);
    }
    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}

[SupportedOSPlatform("windows")]
internal sealed class LibreHwmBridge
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();

    private LibreHwmBridge(Computer computer) { _computer = computer; }

    public static LibreHwmBridge? TryCreate()
    {
        try
        {
            var computer = new Computer
            {
                IsCpuEnabled    = true,
                IsGpuEnabled    = true,
                IsMemoryEnabled = true,
            };
            computer.Open();
            // A first traversal so the initial Read() has values to report.
            computer.Accept(new UpdateVisitor());
            return new LibreHwmBridge(computer);
        }
        catch (Exception ex)
        {
            // Most common cause: not elevated. The sensors need ring-0 driver
            // access that LibreHardwareMonitor sets up only under admin.
            Diag.LogException("LibreHwmBridge.TryCreate", ex);
            return null;
        }
    }

    public void Close()
    {
        try { _computer.Close(); } catch { /* ignore */ }
    }

    public LibreHwmReading Read()
    {
        // Refresh every sensor value.
        _computer.Accept(_visitor);

        var cpuTemps  = new Dictionary<string, double>();
        var gpuTemps  = new Dictionary<string, double>();
        var memTemps  = new Dictionary<string, double>();
        var gpuClocks = new Dictionary<string, double>();
        var gpuLoads  = new Dictionary<string, double>();
        var memClocks = new Dictionary<string, double>();

        foreach (var hw in _computer.Hardware)
        {
            var type = hw.HardwareType;
            // Match GPU by the enum name prefix ("GpuNvidia" / "GpuAmd" /
            // "GpuIntel", and the older "GpuAti") so we're robust to enum
            // naming differences across library versions.
            var typeName = type.ToString();
            bool isCpu = type == HardwareType.Cpu;
            bool isGpu = typeName.StartsWith("Gpu", StringComparison.Ordinal);
            bool isMem = type == HardwareType.Memory;

            foreach (var sensor in hw.Sensors)
            {
                if (sensor.Value is not float f) continue;
                double val = f;
                var name = (sensor.Name ?? "").ToLowerInvariant();

                switch (sensor.SensorType)
                {
                    case SensorType.Temperature:
                        if (isCpu) cpuTemps[name] = val;
                        else if (isGpu) gpuTemps[name] = val;
                        else if (isMem) memTemps[name] = val;
                        break;
                    case SensorType.Load:
                        if (isGpu) gpuLoads[name] = val;
                        break;
                    case SensorType.Clock:
                        if (isGpu) gpuClocks[name] = val;
                        else if (isMem) memClocks[name] = val;
                        break;
                }
            }
        }

        return new LibreHwmReading
        {
            CpuTemp  = PickTemp(cpuTemps, prefer: new[] { "package", "tctl", "tdie", "ccd", "core max" }),
            GpuTemp  = PickTemp(gpuTemps, prefer: new[] { "core", "gpu", "edge" }, avoid: new[] { "hot", "junction" }),
            RamTemp  = PickTemp(memTemps, prefer: new[] { "memory", "dimm", "module" }),
            GpuClock = Pick(gpuClocks, prefer: new[] { "core", "gpu" }),
            GpuLoad  = Pick(gpuLoads,  prefer: new[] { "core", "gpu" }),
            RamClock = Pick(memClocks, prefer: new[] { "memory", "clock" }),
        };
    }

    // ---- picking helpers (ported from sysstats._pick / _pick_temp) ------

    private static double? Pick(Dictionary<string, double> named, string[] prefer)
    {
        if (named.Count == 0) return null;
        foreach (var key in prefer)
            foreach (var kv in named)
                if (kv.Key.Contains(key, StringComparison.Ordinal))
                    return kv.Value;
        return named.Values.Max();
    }

    private static double? PickTemp(Dictionary<string, double> named, string[] prefer, string[]? avoid = null)
    {
        var good = named.Where(kv => kv.Value > 0 && kv.Value < 150)
                        .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (good.Count == 0) return null;

        if (avoid is not null)
        {
            var filtered = good.Where(kv => !avoid.Any(a => kv.Key.Contains(a, StringComparison.Ordinal)))
                               .ToDictionary(kv => kv.Key, kv => kv.Value);
            if (filtered.Count > 0) good = filtered;
        }

        foreach (var key in prefer)
            foreach (var kv in good)
                if (kv.Key.Contains(key, StringComparison.Ordinal))
                    return kv.Value;
        return good.Values.Max();
    }
}
