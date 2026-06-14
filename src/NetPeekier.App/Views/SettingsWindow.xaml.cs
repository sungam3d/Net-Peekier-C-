using System.Windows;
using NetPeekier.Core;
using NetPeekier.Native;

namespace NetPeekier.App.Views;

/// <summary>
/// Edit the user-tunable settings. On OK, every field is validated; one
/// bad value blocks the save and surfaces a message so the user can fix it.
/// Settings.Save is called once at the end, then ApplySettings(syncFirewall:
/// true) so anything that changed (lockdown, LAN ranges) takes effect now.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly NetworkMonitor _monitor;

    public SettingsWindow()
    {
        InitializeComponent();
        _monitor = ((App)System.Windows.Application.Current).NetworkMonitor;
        Load();
    }

    private void Load()
    {
        var s = _monitor.Settings;

        UnitBox.ItemsSource   = SpeedUnits.All;
        UnitBox.SelectedItem  = SpeedUnits.IsValid(s.SpeedUnit) ? s.SpeedUnit : "auto";

        IdleBox.Text          = s.IdleHideMinutes?.ToString() ?? "";
        LockdownCheck.IsChecked = s.LockdownMode;
        AllowMinBox.Text      = s.AllowMinutes.ToString();
        ShowLanCheck.IsChecked = s.ShowLan;
        ShowWanCheck.IsChecked = s.ShowWan;
        PurgeBox.Text         = s.PacketPurgeMinutes?.ToString() ?? "";
        LanBox.Text           = string.Join(Environment.NewLine, s.LanRanges);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        // Validate first; only mutate Settings when every field is good.
        int? idle = null;
        if (!string.IsNullOrWhiteSpace(IdleBox.Text))
        {
            if (!int.TryParse(IdleBox.Text.Trim(), out var n) || n < 0) { Bad("Idle minutes: not a non-negative integer."); return; }
            idle = n;
        }

        if (!int.TryParse(AllowMinBox.Text.Trim(), out var allowMin) || allowMin < 1)
        { Bad("Temp-allow duration: must be 1 or more minutes."); return; }

        int? purge = null;
        if (!string.IsNullOrWhiteSpace(PurgeBox.Text))
        {
            if (!int.TryParse(PurgeBox.Text.Trim(), out var n) || n < 0) { Bad("Forget-pids minutes: not a non-negative integer."); return; }
            purge = n;
        }

        var lanLines = LanBox.Text
            .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        foreach (var line in lanLines)
        {
            if (!IpCalc.ValidIpSpec(line) || !line.Contains('/'))
            {
                Bad($"LAN ranges: '{line}' is not a CIDR. Examples: 10.0.0.0/8, fe80::/10");
                return;
            }
        }
        if (lanLines.Count == 0) lanLines = new List<string>(DefaultLanRanges.All);

        var s = _monitor.Settings;
        s.SpeedUnit       = UnitBox.SelectedItem as string ?? "auto";
        s.IdleHideMinutes = idle;
        s.LockdownMode    = LockdownCheck.IsChecked == true;
        s.AllowMinutes    = allowMin;
        s.ShowLan         = ShowLanCheck.IsChecked == true;
        s.ShowWan         = ShowWanCheck.IsChecked == true;
        s.PacketPurgeMinutes = purge;
        s.LanRanges       = lanLines;

        // Push to the monitor: ApplySettings re-parses LAN ranges and
        // re-syncs the firewall (lockdown changes need this).
        _monitor.ApplySettings(syncFirewall: true);

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Bad(string msg) =>
        MessageBox.Show(this, msg, "Invalid setting", MessageBoxButton.OK, MessageBoxImage.Warning);
}
