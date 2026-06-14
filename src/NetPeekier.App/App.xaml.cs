using System.Windows;
using System.Windows.Threading;
using NetPeekier.App.Views;
using NetPeekier.Native;

namespace NetPeekier.App;

public partial class App : Application
{
    public NetworkMonitor NetworkMonitor { get; } = new(TimeSpan.FromSeconds(1));

    // Per-exe prompt suppression: once we've shown the dialog for an exe,
    // don't show it again until the monitor reports the same exe again
    // through LockdownPrompt (NetworkMonitor itself dedupes via _lockdownPending).
    private readonly HashSet<string> _promptedExes = new(StringComparer.OrdinalIgnoreCase);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Wire the lockdown callback BEFORE Start so the first sweep can
        // surface prompts.
        NetworkMonitor.LockdownPrompt = ShowLockdownPrompt;

        try { NetworkMonitor.Start(); }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to start network monitor:\n\n{ex.Message}\n\nThe app will continue with limited data.",
                "Net-Peekier",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { NetworkMonitor.Stop(); } catch { /* ignore */ }
        try { NetworkMonitor.Dispose(); } catch { /* ignore */ }
        base.OnExit(e);
    }

    /// <summary>
    /// Invoked from the monitor's background thread when lockdown blocks an
    /// unknown exe. We marshal to the UI thread and pop a non-modal dialog
    /// so the user can decide. Per-exe dedupe avoids piling up dialogs if
    /// the same process makes lots of attempts in quick succession.
    /// </summary>
    private void ShowLockdownPrompt(string exe, string processName)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            lock (_promptedExes)
            {
                if (!_promptedExes.Add(exe)) return;
            }
            try
            {
                var dlg = new LockdownDialog(exe, processName);
                if (MainWindow is { } main && main.IsLoaded)
                    dlg.Owner = main;
                dlg.Closed += (_, _) =>
                {
                    lock (_promptedExes) _promptedExes.Remove(exe);
                };
                dlg.Show();
            }
            catch (Exception ex)
            {
                // The prompt is best-effort UI; failures here should never
                // crash the monitor.
                Console.Error.WriteLine($"[netpeekier] lockdown prompt failed: {ex}");
            }
        }));
    }
}
