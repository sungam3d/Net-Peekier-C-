using System.Windows;
using NetPeekier.Native;

namespace NetPeekier.App.Views;

/// <summary>
/// Shown when lockdown mode catches an unfamiliar exe. Buttons map to the
/// three NetworkMonitor methods that resolve the decision:
///   - Block permanently → SetBlocked(exe, true)
///   - Temp allow        → AllowTemporarily(exe, Settings.AllowMinutes)
///   - Allow permanently → SetAllowed(exe, true)
/// </summary>
public partial class LockdownDialog : Window
{
    private readonly string _exe;
    private readonly NetworkMonitor _monitor;

    public LockdownDialog(string exe, string processName)
    {
        InitializeComponent();
        _exe = exe;
        _monitor = ((App)System.Windows.Application.Current).NetworkMonitor;
        NameLabel.Text = processName;
        ExeLabel.Text  = exe;
    }

    private void OnBlock(object sender, RoutedEventArgs e)
    {
        _monitor.LockdownBlock(_exe, permanent: true);
        Close();
    }

    private void OnDisallow(object sender, RoutedEventArgs e)
    {
        // Keep it blocked for this session (the lockdown sweep already
        // blocked it); don't make it permanent. It'll be re-prompted if the
        // process restarts.
        _monitor.LockdownBlock(_exe, permanent: false);
        Close();
    }

    private void OnTemp(object sender, RoutedEventArgs e)
    {
        _monitor.AllowTemporarily(_exe, _monitor.Settings.AllowMinutes);
        Close();
    }

    private void OnAllow(object sender, RoutedEventArgs e)
    {
        _monitor.SetAllowed(_exe, allowed: true);
        Close();
    }
}
