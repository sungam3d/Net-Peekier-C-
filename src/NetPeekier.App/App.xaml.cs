using System.Windows;
using System.Windows.Threading;
using NetPeekier.App.Views;
using NetPeekier.Core;
using NetPeekier.Native;

namespace NetPeekier.App;

public partial class App : Application
{
    // Static ctor runs before any instance ctor and any field initializer,
    // making this the earliest place to install crash handlers. If even the
    // property initializer below throws (which is one of the ways the app
    // can "not launch"), this catches it.
    static App()
    {
        try
        {
            Diag.Init();
            Diag.Log("App static ctor: installing AppDomain unhandled handler");
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    Diag.LogException("AppDomain.UnhandledException", ex);
                else
                    Diag.Log($"AppDomain.UnhandledException: non-Exception object {e.ExceptionObject}");
            };
        }
        catch { /* never let logging crash startup */ }
    }

    public NetworkMonitor NetworkMonitor { get; }

    // Per-exe prompt suppression: once we've shown the dialog for an exe,
    // don't show it again until the monitor reports the same exe again
    // through LockdownPrompt (NetworkMonitor itself dedupes via _lockdownPending).
    private readonly HashSet<string> _promptedExes = new(StringComparer.OrdinalIgnoreCase);

    public App()
    {
        try
        {
            Diag.Log("App instance ctor: about to construct NetworkMonitor");
            NetworkMonitor = new NetworkMonitor(TimeSpan.FromSeconds(1));
            Diag.Log("App instance ctor: NetworkMonitor constructed");
        }
        catch (Exception ex)
        {
            Diag.LogException("App ctor / NetworkMonitor", ex);
            throw;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Diag.Log("App.OnStartup entered");
        try
        {
            base.OnStartup(e);

            // Dispatcher handler catches anything that escapes the UI thread —
            // includes failures inside InitializeComponent and bindings.
            // We show ONE message box for the first crash; subsequent
            // exceptions just go to the log so the user isn't drowned in
            // dialogs while we're already shutting down.
            int shown = 0;
            DispatcherUnhandledException += (s, args) =>
            {
                Diag.LogException("Dispatcher.UnhandledException", args.Exception);
                if (System.Threading.Interlocked.CompareExchange(ref shown, 1, 0) == 0)
                {
                    try
                    {
                        MessageBox.Show(
                            $"Net-Peekier crashed:\n\n{args.Exception.GetType().Name}: {args.Exception.Message}\n\n" +
                            $"Full details: {Diag.LogPath}",
                            "Net-Peekier — fatal error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                    catch { /* if even the message box fails, give up gracefully */ }
                    args.Handled = true;
                    Shutdown(1);
                }
                else
                {
                    // Already shutting down. Swallow so we don't show
                    // duplicate dialogs while the dispatcher drains.
                    args.Handled = true;
                }
            };

            Diag.Log("Wiring LockdownPrompt");
            NetworkMonitor.LockdownPrompt = ShowLockdownPrompt;

            Diag.Log("Calling NetworkMonitor.Start()");
            NetworkMonitor.Start();
            Diag.Log("NetworkMonitor.Start() returned");
        }
        catch (Exception ex)
        {
            Diag.LogException("OnStartup body", ex);
            // Surface to user — but only AFTER logging, so the log captures
            // the cause even if the MessageBox itself misbehaves.
            try
            {
                MessageBox.Show(
                    $"Failed to start network monitor:\n\n{ex.Message}\n\nSee {Diag.LogPath} for details.",
                    "Net-Peekier",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch { /* ignore */ }
        }
        Diag.Log("OnStartup completed; WPF will now load StartupUri (MainWindow)");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Diag.Log("OnExit entered");
        try { NetworkMonitor?.Stop();    } catch (Exception ex) { Diag.LogException("OnExit / Stop", ex); }
        try { NetworkMonitor?.Dispose(); } catch (Exception ex) { Diag.LogException("OnExit / Dispose", ex); }
        base.OnExit(e);
        Diag.Log("OnExit completed");
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
                Diag.LogException("LockdownPrompt", ex);
            }
        }));
    }
}
