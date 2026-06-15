using System.Windows;
using NetPeekier.Core;
using NetPeekier.Native;

namespace NetPeekier.App.Views;

/// <summary>
/// One-stop view of the firewall state: which exes are blocked, and what
/// per-IP rules are configured. Edits go through the NetworkMonitor, which keeps
/// settings and the live WFP state in sync.
/// </summary>
public partial class FirewallWindow : Window
{
    private readonly NetworkMonitor _monitor;

    public FirewallWindow()
    {
        InitializeComponent();
        _monitor = ((App)System.Windows.Application.Current).NetworkMonitor;
        WindowGeometryPersistence.Apply(this, "firewall");
        Refresh();
    }

    private void Refresh()
    {
        EnabledCheck.IsChecked = _monitor.Settings.FirewallEnabled;
        EngineStatus.Text = _monitor.Firewall is null
            ? "WFP engine: not available (run as administrator)"
            : "WFP engine: ready";

        BlockedList.ItemsSource = _monitor.Settings.BlockedExes
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AllowedList.ItemsSource = _monitor.Settings.AllowedExes
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        RulesGrid.ItemsSource = _monitor.Settings.IpRules.ToList();

        RefreshTags();
    }

    private void RefreshTags()
    {
        var s = _monitor.Settings;
        var rows = s.AllTags()
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(tag =>
            {
                var members = s.ExesWithTag(tag).ToList();
                return new TagRow
                {
                    Tag     = tag,
                    Count   = members.Count,
                    Blocked = s.TagBlocked.Contains(tag) ? "yes" : "",
                    Allowed = s.TagAllowed.Contains(tag) ? "yes" : "",
                    Members = string.Join(", ", members.Select(System.IO.Path.GetFileName)),
                };
            })
            .ToList();
        TagGrid.ItemsSource = rows;
    }

    private sealed class TagRow
    {
        public string Tag { get; init; } = "";
        public int    Count { get; init; }
        public string Blocked { get; init; } = "";
        public string Allowed { get; init; } = "";
        public string Members { get; init; } = "";
    }

    private string? SelectedTag => (TagGrid.SelectedItem as TagRow)?.Tag;

    private void OnBlockTag(object sender, RoutedEventArgs e)
    {
        if (SelectedTag is not { } tag) return;
        if (!_monitor.Settings.TagBlocked.Contains(tag)) _monitor.Settings.TagBlocked.Add(tag);
        _monitor.Settings.Save();
        _monitor.ApplySettings(syncFirewall: true);
        Refresh();
    }

    private void OnUnblockTag(object sender, RoutedEventArgs e)
    {
        if (SelectedTag is not { } tag) return;
        _monitor.Settings.TagBlocked.Remove(tag);
        _monitor.Settings.Save();
        _monitor.ApplySettings(syncFirewall: true);
        Refresh();
    }

    private void OnAllowTag(object sender, RoutedEventArgs e)
    {
        if (SelectedTag is not { } tag) return;
        if (!_monitor.Settings.TagAllowed.Contains(tag)) _monitor.Settings.TagAllowed.Add(tag);
        _monitor.Settings.TagBlocked.Remove(tag);   // allow & block are exclusive
        _monitor.Settings.Save();
        _monitor.ApplySettings(syncFirewall: true);
        Refresh();
    }

    private void OnUnallowTag(object sender, RoutedEventArgs e)
    {
        if (SelectedTag is not { } tag) return;
        _monitor.Settings.TagAllowed.Remove(tag);
        _monitor.Settings.Save();
        _monitor.ApplySettings(syncFirewall: true);
        Refresh();
    }

    private void OnRemoveAllowed(object sender, RoutedEventArgs e)
    {
        if (AllowedList.SelectedItem is not string exe) return;
        _monitor.SetAllowed(exe, false);
        Refresh();
    }

    private void OnToggleEnabled(object sender, RoutedEventArgs e)
    {
        _monitor.SetFirewallEnabled(EnabledCheck.IsChecked == true);
        Refresh();
    }

    private void OnUnblockSelected(object sender, RoutedEventArgs e)
    {
        if (BlockedList.SelectedItem is not string exe) return;
        _monitor.SetBlocked(exe, false);
        Refresh();
    }

    private void OnAddIpRule(object sender, RoutedEventArgs e)
    {
        var dlg = new IpRuleDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var (ok, msg) = _monitor.AddIpRule(dlg.Result);
        if (!ok) MessageBox.Show(msg, "Add rule failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        Refresh();
    }

    private void OnRemoveSelectedRule(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not IpRule rule) return;
        var (ok, msg) = _monitor.RemoveIpRule(rule);
        if (!ok) MessageBox.Show(msg, "Remove rule failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        Refresh();
    }

    private void OnRemoveAll(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Remove every firewall rule Net-Peekier has installed?\n\nThis clears blocked apps and per-IP rules in one go. Settings are wiped too.",
            "Remove all rules",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var (count, msg) = _monitor.RemoveAllFirewallRules();
        MessageBox.Show(msg, "Net-Peekier", MessageBoxButton.OK, MessageBoxImage.Information);
        Refresh();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
