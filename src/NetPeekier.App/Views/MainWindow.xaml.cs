using System.Windows;
using System.Windows.Controls;
using NetPeekier.App.ViewModels;

namespace NetPeekier.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        NetPeekier.Core.Diag.Log("MainWindow.ctor: begin");
        try
        {
            InitializeComponent();
            NetPeekier.Core.Diag.Log("MainWindow.ctor: InitializeComponent ok");
            var app = (App)System.Windows.Application.Current;
            _vm = new MainViewModel(app.NetworkMonitor);
            NetPeekier.Core.Diag.Log("MainWindow.ctor: MainViewModel constructed");
            DataContext = _vm;
            Closed += (_, _) => _vm.Stop();
            WindowGeometryPersistence.Apply(this, "main");
            NetPeekier.Core.Diag.Log("MainWindow.ctor: done");
        }
        catch (Exception ex)
        {
            NetPeekier.Core.Diag.LogException("MainWindow.ctor", ex);
            throw;
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnOpenFirewall(object sender, RoutedEventArgs e)
    {
        var win = new FirewallWindow { Owner = this };
        win.Show();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow { Owner = this };
        win.ShowDialog();
    }

    private void OnOpenStats(object sender, RoutedEventArgs e)
    {
        var win = new StatsWindow { Owner = this };
        win.Show();
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var win = new AboutWindow { Owner = this };
        win.ShowDialog();
    }

    private void OnOpenConnections(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is null)
        {
            MessageBox.Show("Select a process first.", "Net-Peekier",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var win = new ConnectionsWindow(_vm.Selected.Pid, _vm.Selected.Name) { Owner = this };
        win.Show();
    }

    private void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Open the connections detail window for the double-clicked row,
        // ignoring header / scroll-bar clicks (where there's no row under
        // the cursor).
        if (sender is DataGrid grid
            && grid.SelectedItem is ProcessRow row)
        {
            var win = new ConnectionsWindow(row.Pid, row.Name) { Owner = this };
            win.Show();
        }
    }
}
