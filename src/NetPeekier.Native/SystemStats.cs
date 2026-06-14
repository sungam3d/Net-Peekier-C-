// Port of netpeekier/sysstats.py
//
// CPU / GPU / RAM stats for the dashboard's third column. Best-effort and
// degrades gracefully, exactly like the Python build: a sensor we can't read
// shows "--" and never throws into the monitor loop.
//
// Sources, in order of preference:
//   * PerformanceCounter (PDH)  -> CPU load, RAM used%   (always present)
//   * WMI / registry            -> CPU clock MHz          (always present)
//   * LibreHardwareMonitorLib   -> CPU/GPU/RAM temps + GPU clock/load
//     (OPTIONAL: loaded by reflection from a dll dropped next to the exe so
//      we don't take a hard NuGet dependency. Needs admin, same as Python.)
//
// The LibreHardwareMonitor path is wired through reflection deliberately:
// it lets the app ship without the dll and light up temps only if the user
// drops LibreHardwareMonitorLib.dll beside NetPeekier.exe. No compile-time
// dependency, no crash if it's missing.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
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
                Diag.Log("SystemMonitor: no temperature library found (temps will show --)");
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

        // Optional hardware sensors (temps, GPU details).
        LibreHwmReading hwm = default;
        if (_hwm is not null)
        {
            try { hwm = _hwm.Read(); } catch (Exception ex) { Diag.LogException("SystemMonitor.hwm.Read", ex); }
        }

        return new SystemStatsSnapshot
        {
            CpuLoad  = cpuLoad,
            CpuClock = cpuClock,
            RamUsed  = ramUsed,
            CpuTemp  = hwm.CpuTemp,
            GpuTemp  = hwm.GpuTemp,
            GpuLoad  = hwm.GpuLoad,
            GpuClock = hwm.GpuClock,
            RamTemp  = hwm.RamTemp,
            RamClock = hwm.RamClock,
        };
    }

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
// LibreHardwareMonitor bridge (reflection, optional dependency).
//
// We never reference LibreHardwareMonitorLib at compile time. Instead, if
// the dll is sitting next to our exe at runtime, we load it and drive its
// Computer/IHardware/ISensor API entirely through reflection. This keeps
// the dll a pure drop-in: present = temps light up, absent = temps show
// "--", no other difference.
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

[SupportedOSPlatform("windows")]
internal sealed class LibreHwmBridge
{
    private readonly object _computer;
    private readonly Type _computerType;
    private readonly Array _hardwareArray;       // refreshed each read via property

    // Enum values resolved once.
    private readonly object _sensorTemperature;
    private readonly object _sensorLoad;
    private readonly object _sensorClock;
    private readonly object _hwTypeCpu;
    private readonly object _hwTypeMemory;

    private LibreHwmBridge(
        object computer, Type computerType,
        object sensorTemperature, object sensorLoad, object sensorClock,
        object hwTypeCpu, object hwTypeMemory)
    {
        _computer = computer;
        _computerType = computerType;
        _sensorTemperature = sensorTemperature;
        _sensorLoad = sensorLoad;
        _sensorClock = sensorClock;
        _hwTypeCpu = hwTypeCpu;
        _hwTypeMemory = hwTypeMemory;
        _hardwareArray = Array.Empty<object>();
    }

    public static LibreHwmBridge? TryCreate()
    {
        // Look for the dll next to the executing assembly.
        var baseDir = AppContext.BaseDirectory;
        var dll = Path.Combine(baseDir, "LibreHardwareMonitorLib.dll");
        if (!File.Exists(dll)) return null;

        Assembly asm;
        try { asm = Assembly.LoadFrom(dll); }
        catch (Exception ex) { Diag.LogException("LibreHwmBridge.LoadFrom", ex); return null; }

        try
        {
            var computerType = asm.GetType("LibreHardwareMonitor.Hardware.Computer")
                ?? throw new InvalidOperationException("Computer type not found");
            var hardwareTypeEnum = asm.GetType("LibreHardwareMonitor.Hardware.HardwareType")!;
            var sensorTypeEnum   = asm.GetType("LibreHardwareMonitor.Hardware.SensorType")!;

            var computer = Activator.CreateInstance(computerType)!;

            // computer.IsCpuEnabled = true; etc.
            SetProp(computer, "IsCpuEnabled", true);
            SetProp(computer, "IsGpuEnabled", true);
            SetProp(computer, "IsMemoryEnabled", true);

            // computer.Open();
            computerType.GetMethod("Open", Type.EmptyTypes)!.Invoke(computer, null);

            return new LibreHwmBridge(
                computer, computerType,
                Enum.Parse(sensorTypeEnum, "Temperature"),
                Enum.Parse(sensorTypeEnum, "Load"),
                Enum.Parse(sensorTypeEnum, "Clock"),
                Enum.Parse(hardwareTypeEnum, "Cpu"),
                Enum.Parse(hardwareTypeEnum, "Memory"));
        }
        catch (Exception ex)
        {
            Diag.LogException("LibreHwmBridge.TryCreate", ex);
            return null;
        }
    }

    public void Close()
    {
        try { _computerType.GetMethod("Close", Type.EmptyTypes)?.Invoke(_computer, null); }
        catch { /* ignore */ }
    }

    public LibreHwmReading Read()
    {
        // Mirrors sysstats._read_hardwaremonitor: gather candidate sensors,
        // then pick the headline value per metric.
        var cpuTemps = new Dictionary<string, double>();
        var gpuTemps = new Dictionary<string, double>();
        var memTemps = new Dictionary<string, double>();
        var gpuClocks = new Dictionary<string, double>();
        var gpuLoads = new Dictionary<string, double>();
        var memClocks = new Dictionary<string, double>();

        var hardware = (System.Collections.IEnumerable)_computerType
            .GetProperty("Hardware")!.GetValue(_computer)!;

        foreach (var hw in hardware)
        {
            try { hw.GetType().GetMethod("Update", Type.EmptyTypes)!.Invoke(hw, null); } catch { /* ignore */ }

            var htype = hw.GetType().GetProperty("HardwareType")!.GetValue(hw)!;
            var htypeName = htype.ToString() ?? "";
            bool isCpu = htype.Equals(_hwTypeCpu);
            bool isGpu = htypeName.StartsWith("Gpu", StringComparison.Ordinal);
            bool isMem = htype.Equals(_hwTypeMemory);

            var sensors = (System.Collections.IEnumerable)hw.GetType()
                .GetProperty("Sensors")!.GetValue(hw)!;

            foreach (var sensor in sensors)
            {
                var valObj = sensor.GetType().GetProperty("Value")!.GetValue(sensor);
                if (valObj is null) continue;
                double val = Convert.ToDouble(valObj);

                var st   = sensor.GetType().GetProperty("SensorType")!.GetValue(sensor)!;
                var name = (sensor.GetType().GetProperty("Name")!.GetValue(sensor) as string ?? "").ToLowerInvariant();

                if (st.Equals(_sensorTemperature))
                {
                    if (isCpu) cpuTemps[name] = val;
                    else if (isGpu) gpuTemps[name] = val;
                    else if (isMem) memTemps[name] = val;
                }
                else if (st.Equals(_sensorLoad))
                {
                    if (isGpu) gpuLoads[name] = val;
                }
                else if (st.Equals(_sensorClock))
                {
                    if (isGpu) gpuClocks[name] = val;
                    else if (isMem) memClocks[name] = val;
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

    private static void SetProp(object target, string name, object value)
    {
        var p = target.GetType().GetProperty(name);
        p?.SetValue(target, value);
    }
}
