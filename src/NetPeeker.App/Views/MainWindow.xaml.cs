using System.Windows;
using System.Windows.Threading;
using NetPeeker.Core;
using NetPeeker.Native;

namespace NetPeeker.App.Views;

public partial class MainWindow : Window
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(1000);

    private readonly Monitor _monitor = ((App)System.Windows.Application.Current).Monitor;
    private readonly DispatcherTimer _timer;

    public MainWindow()
    {
        InitializeComponent();

        var modeHint = _monitor.HasPerProcessSpeed
            ? $"backend: {_monitor.BackendName} — full per-process speeds"
            : "backend: connection table only — per-process speeds unavailable";
        StatusText.Text = $"{modeHint}   (running as admin)";

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = RefreshInterval,
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Closed += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        // Phase 4: replace this with proper data-binding to a MainViewModel.
        // For now we just poke the monitor for a snapshot and rewrite the
        // dashboard labels so we can sanity-check end-to-end wiring.
        var (procs, totals) = _monitor.Snapshot();
        var unit = _monitor.Settings.SpeedUnit;

        UpNowText.Text    = $"now: {Formatting.HumanSpeed(totals.UpNow, unit)}";
        UpPeakText.Text   = $"peak: {Formatting.HumanSpeed(totals.UpPeak, unit)}";
        DownNowText.Text  = $"now: {Formatting.HumanSpeed(totals.DownNow, unit)}";
        DownPeakText.Text = $"peak: {Formatting.HumanSpeed(totals.DownPeak, unit)}";

        AppGrid.ItemsSource = procs
            .Select(p => new
            {
                p.Name,
                p.Pid,
                Up   = Formatting.HumanSpeed(p.UpBps,   unit),
                Down = Formatting.HumanSpeed(p.DownBps, unit),
                Total = Formatting.HumanBytes(p.UpTotal + p.DownTotal),
                Listening = Formatting.PortsStr(p.ListeningPorts),
                p.Tag,
                Blocked = p.Blocked ? "yes" : "",
            })
            .ToList();
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnAbout(object sender, RoutedEventArgs e) =>
        MessageBox.Show(
            "Net-Peeker 2.0\n\nPort of the Python build to C# / .NET 8 with WFP + ETW.\nNo third-party drivers.",
            "About Net-Peeker",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
}
