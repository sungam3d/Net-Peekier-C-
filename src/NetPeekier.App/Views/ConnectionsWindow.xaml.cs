using System.Windows;
using System.Windows.Threading;
using NetPeekier.Native;

namespace NetPeekier.App.Views;

/// <summary>
/// Live socket list for one process. Refreshes once a second by pulling the
/// monitor's snapshot and filtering to this PID. Closes when the PID goes
/// away (matches the Python build's behaviour).
/// </summary>
public partial class ConnectionsWindow : Window
{
    private readonly int _pid;
    private readonly Monitor _monitor;
    private readonly DispatcherTimer _timer;

    public ConnectionsWindow(int pid, string processName)
    {
        InitializeComponent();
        _pid = pid;
        _monitor = ((App)System.Windows.Application.Current).Monitor;
        Header.Text = $"Connections — {processName} (PID {pid})";
        Title       = $"Connections — {processName} (PID {pid})";

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1000),
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        WindowGeometryPersistence.Apply(this, "connections");

        Refresh();
    }

    private void Refresh()
    {
        var (procs, _) = _monitor.Snapshot();
        var p = procs.FirstOrDefault(x => x.Pid == _pid);
        if (p is null)
        {
            StatusLine.Text = "Process has exited.";
            ConnGrid.ItemsSource = null;
            return;
        }
        ConnGrid.ItemsSource = p.Connections.ToList();
        StatusLine.Text = $"{p.Connections.Count} connection(s)";
    }
}
