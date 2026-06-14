// Port of netpeekier/sysstats.py
//
// CPU / GPU / RAM stats for the dashboard's third column. Best-effort and
// degrades gracefully: a sensor we can't read shows "--", never throws into
// the monitor loop.
//
// Sources, in order of preference:
//   * PerformanceCounter (PDH) -> CPU load + clock, RAM used%
//   * LibreHardwareMonitorLib.dll (referenced as a binary) -> CPU/GPU/RAM
//     temperatures + GPU clock/load. Optional: if the dll isn't next to the
//     exe we just skip the temps. Needs admin (same as the Python build).
//   * NVML for an NVIDIA fallback if Libre isn't there.
//
// STATUS: Phase 5 polish.

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

public sealed class SystemMonitor : IDisposable
{
    public SystemMonitor(double intervalSeconds = 2.0) { }
    public void Start() { /* TODO Phase 5 */ }
    public void Stop()  { /* TODO Phase 5 */ }
    public SystemStatsSnapshot Snapshot() => new();
    public void Dispose() => Stop();
}
