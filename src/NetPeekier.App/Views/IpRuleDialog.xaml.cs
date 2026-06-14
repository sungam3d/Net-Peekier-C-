using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NetPeekier.Core;
using NetPeekier.Native;

namespace NetPeekier.App.Views;

/// <summary>
/// Modal "add IP rule" dialog. Validates against IpCalc and
/// WfpFirewall.ValidExe before allowing OK so the user gets feedback before
/// the firewall reports an error.
/// </summary>
public partial class IpRuleDialog : Window
{
    public IpRule Result { get; private set; } = new();

    public IpRuleDialog() => InitializeComponent();

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Pick executable",
            Filter = "Executable (*.exe)|*.exe",
        };
        if (dlg.ShowDialog(this) == true)
            ExeBox.Text = dlg.FileName;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var exe   = ExeBox.Text.Trim();
        var act   = Pick(ActionBox);
        var dir   = Pick(DirBox);
        var ip    = string.IsNullOrWhiteSpace(IpBox.Text) ? "any" : IpBox.Text.Trim();
        var ports = PortsBox.Text.Trim();
        var proto = Pick(ProtoBox);

        if (!WfpFirewall.ValidExe(exe))
        {
            Bad("Executable path looks wrong. It should be an absolute Windows path ending in .exe.");
            return;
        }
        if (!IpCalc.ValidIpSpec(ip))
        {
            Bad("Remote IP / CIDR is invalid. Examples: 1.2.3.4 / 10.0.0.0/8 / 1.2.3.0-1.2.3.255 / any");
            return;
        }
        if (!IpCalc.ValidPorts(ports))
        {
            Bad("Ports field is invalid. Examples: 80 / 80,443 / 1000-2000 / (empty = all)");
            return;
        }

        Result = new IpRule
        {
            Exe       = exe,
            Action    = act,
            Direction = dir,
            RemoteIp  = ip,
            Ports     = ports,
            Protocol  = proto,
            Note      = "",
        };
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string Pick(ComboBox cb) =>
        (cb.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

    private void Bad(string msg) =>
        MessageBox.Show(this, msg, "Invalid rule", MessageBoxButton.OK, MessageBoxImage.Warning);
}
