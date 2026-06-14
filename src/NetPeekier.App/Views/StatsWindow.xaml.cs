using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using NetPeekier.Native;

namespace NetPeekier.App.Views;

/// <summary>
/// Live CPU / GPU / RAM dashboard. Polls the app's SystemMonitor once a
/// second. Any sensor the backend couldn't read shows "--". Temperatures
/// and GPU details require LibreHardwareMonitorLib.dll next to the exe;
/// without it those fields stay "--" and the status bar says so.
/// </summary>
public partial class StatsWindow : Window
{
    private readonly SystemMonitor _sys;
    private readonly DispatcherTimer _timer;

    public StatsWindow()
    {
        InitializeComponent();
        _sys = ((App)System.Windows.Application.Current).SystemMonitor;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();

        WindowGeometryPersistence.Apply(this, "stats");
        Refresh();
    }

    private void Refresh()
    {
        var s = _sys.Snapshot();

        CpuLoad.Text  = Pct(s.CpuLoad);
        CpuClock.Text = Mhz(s.CpuClock);
        CpuTemp.Text  = Temp(s.CpuTemp);

        GpuLoad.Text  = Pct(s.GpuLoad);
        GpuClock.Text = Mhz(s.GpuClock);
        GpuTemp.Text  = Temp(s.GpuTemp);

        RamUsed.Text  = RamText(s.RamUsed, s.RamUsedGb, s.RamTotalGb);
        RamClock.Text = Mhz(s.RamClock);
        RamTemp.Text  = Temp(s.RamTemp);

        SourceLine.Text = _sys.TempSource == "none"
            ? "Temperatures: no sensor library (drop LibreHardwareMonitorLib.dll next to the exe)"
            : $"Temperatures: {_sys.TempSource}";
    }

    private static string Pct(double? v)  => v is null ? "--" : v.Value.ToString("0", CultureInfo.InvariantCulture) + "%";
    private static string Mhz(double? v)  => v is null ? "--" : v.Value.ToString("0", CultureInfo.InvariantCulture) + " MHz";
    private static string Temp(double? v) => v is null ? "--" : v.Value.ToString("0", CultureInfo.InvariantCulture) + " °C";

    /// <summary>
    /// "12.3 / 32.0 GB (38%)" when GB figures are available, else falls back
    /// to a bare percentage, else "--".
    /// </summary>
    private static string RamText(double? pct, double? usedGb, double? totalGb)
    {
        if (usedGb is { } u && totalGb is { } t)
        {
            var pctStr = pct is { } p ? $" ({p:0}%)" : "";
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0} / {1:0.0} GB{2}", u, t, pctStr);
        }
        return Pct(pct);
    }
}
