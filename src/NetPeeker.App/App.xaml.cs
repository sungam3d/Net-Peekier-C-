using System.Windows;
using NetPeeker.Native;

namespace NetPeeker.App;

public partial class App : Application
{
    public Monitor Monitor { get; } = new(TimeSpan.FromSeconds(1));

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try { Monitor.Start(); }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to start network monitor:\n\n{ex.Message}\n\nThe app will continue with limited data.",
                "Net-Peeker",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Monitor.Stop(); } catch { /* ignore */ }
        base.OnExit(e);
    }
}
