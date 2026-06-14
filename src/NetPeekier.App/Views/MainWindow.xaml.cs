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
            _vm = new MainViewModel(app.NetworkMonitor, app.SystemMonitor);
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

    private void OnOpenPackets(object sender, RoutedEventArgs e)
    {
        var win = new PacketsWindow { Owner = this };
        win.Show();
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var win = new AboutWindow { Owner = this };
        win.ShowDialog();
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // The TreeView's SelectedItem isn't two-way bindable, so we push the
        // selection into the VM here. It accepts either node kind.
        _vm.Selected = e.NewValue as IProcessNode;
    }

    private void OnOpenConnections(object sender, RoutedEventArgs e)
    {
        var (pid, name) = ResolveSelectedPid();
        if (pid is null)
        {
            MessageBox.Show("Select a process first.", "Net-Peekier",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var win = new ConnectionsWindow(pid.Value, name) { Owner = this };
        win.Show();
    }

    private void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Double-click on a leaf opens its connections. Double-click on a
        // group with one member opens that member; on a multi-member group
        // we let the default expand/collapse happen instead.
        var (pid, name) = ResolveSelectedPid();
        if (pid is not null)
        {
            // Don't hijack the expander toggle on multi-child groups.
            if (_vm.Selected is ProcessGroup g && g.Children.Count > 1) return;
            var win = new ConnectionsWindow(pid.Value, name) { Owner = this };
            win.Show();
        }
    }

    /// <summary>
    /// Resolve the (pid, displayName) the detail window should open for the
    /// current selection. A leaf resolves to itself; a group resolves to its
    /// first child PID.
    /// </summary>
    private (int? Pid, string Name) ResolveSelectedPid()
    {
        switch (_vm.Selected)
        {
            case ProcessRow row:
                return (row.Pid, row.Name);
            case ProcessGroup grp when grp.Children.Count > 0:
                var first = grp.Children[0];
                return (first.Pid, first.Name);
            default:
                return (null, "");
        }
    }

    // ---- context menu handlers -----------------------------------------
    // Each delegates to the VM, which owns the monitor + settings. The VM
    // surfaces any user-facing messages (no exe path, access denied, etc.).

    private void OnCtxConnections(object sender, RoutedEventArgs e) => OnOpenConnections(sender, e);

    private void OnCtxEndProcess(object sender, RoutedEventArgs e)
    {
        var (pid, name) = ResolveSelectedPid();
        if (pid is null) return;
        if (MessageBox.Show(
                $"End {name} (PID {pid})?\n\nUnsaved work in that program will be lost.",
                "End Process", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;
        _vm.EndProcess(pid.Value);
    }

    private void OnCtxOpenPath(object sender, RoutedEventArgs e)   => _vm.OpenSelectedPath();
    private void OnCtxSetTag(object sender, RoutedEventArgs e)     => _vm.SetTagOnSelected(this);
    private void OnCtxRemoveTag(object sender, RoutedEventArgs e)  => _vm.RemoveTagFromSelected();
    private void OnCtxBlock(object sender, RoutedEventArgs e)      => _vm.BlockSelectedExe(true);
    private void OnCtxUnblock(object sender, RoutedEventArgs e)    => _vm.BlockSelectedExe(false);
    private void OnCtxAllow(object sender, RoutedEventArgs e)      => _vm.AllowSelected(true);
    private void OnCtxRemoveAllow(object sender, RoutedEventArgs e)=> _vm.AllowSelected(false);
}
