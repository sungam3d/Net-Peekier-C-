// MainViewModel: the bind-target of MainWindow.xaml.
//
// Polls the monitor once a second, then reconciles its snapshot with the
// observable Processes collection — by PID — so the DataGrid never sees a
// wholesale rebuild (which would lose selection, scroll, focus). Rows for
// PIDs that disappeared get removed; rows for new PIDs get added; existing
// rows are updated in place.

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using NetPeekier.Core;
using NetPeekier.Native;

namespace NetPeekier.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(1000);

    private readonly NetworkMonitor _monitor;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<int, ProcessRow> _rowByPid = new();
    private readonly Dictionary<string, ProcessGroup> _groupByName = new();

    public MainViewModel(NetworkMonitor monitor)
    {
        Diag.Log("MainViewModel.ctor: begin");
        _monitor = monitor;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
        _timer.Tick += (_, _) => SafeRefresh();
        _timer.Start();

        BlockSelectedCommand   = new RelayCommand(BlockSelected,   () => CanModifySelected);
        UnblockSelectedCommand = new RelayCommand(UnblockSelected, () => CanModifySelected);
        RemoveAllRulesCommand  = new RelayCommand(RemoveAllRules);
        ToggleFirewallCommand  = new RelayCommand(ToggleFirewall);
        Diag.Log("MainViewModel.ctor: timer + commands wired");

        // First refresh runs immediately so the first paint isn't blank.
        // Use SafeRefresh so a bad Settings.SpeedUnit or empty snapshot
        // can't crash the whole window construction.
        SafeRefresh();
        Diag.Log("MainViewModel.ctor: first refresh done");
    }

    private void SafeRefresh()
    {
        try { Refresh(); }
        catch (Exception ex)
        {
            Diag.LogException("MainViewModel.Refresh", ex);
        }
    }

    // ---- bindable state -------------------------------------------------
    // Top level is groups (by process name); each group holds its PID rows.
    public ObservableCollection<ProcessGroup> Groups { get; } = new();

    private string _upNow = "now: --";
    public string UpNow { get => _upNow; private set => SetField(ref _upNow, value); }

    private string _upPeak = "peak: --";
    public string UpPeak { get => _upPeak; private set => SetField(ref _upPeak, value); }

    private string _downNow = "now: --";
    public string DownNow { get => _downNow; private set => SetField(ref _downNow, value); }

    private string _downPeak = "peak: --";
    public string DownPeak { get => _downPeak; private set => SetField(ref _downPeak, value); }

    private string _sessionTotal = "session: --";
    public string SessionTotal { get => _sessionTotal; private set => SetField(ref _sessionTotal, value); }

    private string _status = "starting up…";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    private IProcessNode? _selected;
    public IProcessNode? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                BlockSelectedCommand.RaiseCanExecuteChanged();
                UnblockSelectedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// The exe path the firewall commands should act on, resolved from the
    /// current selection. A group resolves to the exe of its first member;
    /// a leaf row resolves to its own exe.
    /// </summary>
    private string? SelectedExe => _selected switch
    {
        ProcessRow r              => string.IsNullOrEmpty(r.Exe) ? null : r.Exe,
        ProcessGroup g            => g.Children.Select(c => c.Exe)
                                                .FirstOrDefault(e => !string.IsNullOrEmpty(e)),
        _                         => null,
    };

    public bool FirewallEnabled
    {
        get => _monitor.Settings.FirewallEnabled;
        set
        {
            if (_monitor.Settings.FirewallEnabled == value) return;
            _monitor.SetFirewallEnabled(value);
            OnPropertyChanged();
        }
    }

    private bool CanModifySelected => !string.IsNullOrEmpty(SelectedExe);

    // ---- commands -------------------------------------------------------
    public RelayCommand BlockSelectedCommand { get; }
    public RelayCommand UnblockSelectedCommand { get; }
    public RelayCommand RemoveAllRulesCommand { get; }
    public RelayCommand ToggleFirewallCommand { get; }

    private void BlockSelected()
    {
        if (SelectedExe is { } exe) _monitor.SetBlocked(exe, true);
    }

    private void UnblockSelected()
    {
        if (SelectedExe is { } exe) _monitor.SetBlocked(exe, false);
    }

    private void RemoveAllRules()
    {
        var result = MessageBox.Show(
            "Remove every firewall rule Net-Peekier has installed?\n\nThis affects only Net-Peekier filters (in our private WFP sublayer).",
            "Remove all rules",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var (count, msg) = _monitor.RemoveAllFirewallRules();
        MessageBox.Show($"{msg}", "Net-Peekier", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ToggleFirewall() => FirewallEnabled = !FirewallEnabled;

    // ---- refresh loop ---------------------------------------------------
    private void Refresh()
    {
        var (procs, totals) = _monitor.Snapshot();
        var unit = _monitor.Settings.SpeedUnit;

        UpNow   = $"now: {Formatting.HumanSpeed(totals.UpNow, unit)}";
        UpPeak  = $"peak: {Formatting.HumanSpeed(totals.UpPeak, unit)}";
        DownNow = $"now: {Formatting.HumanSpeed(totals.DownNow, unit)}";
        DownPeak= $"peak: {Formatting.HumanSpeed(totals.DownPeak, unit)}";
        SessionTotal = $"session: ↑ {Formatting.HumanBytes(totals.UpTotal)}    ↓ {Formatting.HumanBytes(totals.DownTotal)}";

        var mode = _monitor.HasPerProcessSpeed
            ? $"backend: {_monitor.BackendName}"
            : "backend: connection table only — per-process speeds unavailable";
        var fw = _monitor.Firewall is null ? "WFP: not available" : "WFP: ready";
        Status = $"{mode}    |    {fw}";

        ReconcileTree(procs, unit);
    }

    /// <summary>
    /// Build / update the two-level tree: process-name groups at the top,
    /// individual PIDs underneath. Everything is reconciled in place (groups
    /// by name, children by PID) so expand/collapse state and selection
    /// survive each tick — the same stability guarantee the old flat list
    /// had, extended to the hierarchy.
    /// </summary>
    private void ReconcileTree(IReadOnlyList<ProcStat> procs, string unit)
    {
        // Bucket the snapshot by process name.
        var byName = new Dictionary<string, List<ProcStat>>();
        foreach (var p in procs)
        {
            var key = string.IsNullOrEmpty(p.Name) ? "(unknown)" : p.Name;
            if (!byName.TryGetValue(key, out var list))
                byName[key] = list = new List<ProcStat>();
            list.Add(p);
        }

        // Update / add groups.
        foreach (var (name, members) in byName)
        {
            if (!_groupByName.TryGetValue(name, out var group))
            {
                group = new ProcessGroup(name);
                _groupByName[name] = group;
                Groups.Add(group);
            }

            // Reconcile this group's children by PID.
            var seenPids = new HashSet<int>();
            foreach (var m in members)
            {
                seenPids.Add(m.Pid);
                if (!_rowByPid.TryGetValue(m.Pid, out var row))
                {
                    row = new ProcessRow(m.Pid);
                    _rowByPid[m.Pid] = row;
                    group.Children.Add(row);
                }
                else if (!ReferenceEquals(FindParent(row), group))
                {
                    // PID's process name changed (rare, e.g. exec) — move it.
                    RemoveRowFromItsGroup(row);
                    group.Children.Add(row);
                }
                row.Refresh(m, unit);
                // Leaf label: bare "PID n" inside a multi-member group, full
                // "name (PID n)" when the group has a single member.
                row.Display = members.Count == 1 ? $"{name}  (PID {m.Pid})" : $"PID {m.Pid}";
            }

            // Drop children that vanished.
            for (int i = group.Children.Count - 1; i >= 0; i--)
            {
                var child = group.Children[i];
                if (!seenPids.Contains(child.Pid))
                {
                    _rowByPid.Remove(child.Pid);
                    group.Children.RemoveAt(i);
                }
            }

            group.RefreshAggregate(members, unit);
        }

        // Drop groups that vanished entirely.
        for (int i = Groups.Count - 1; i >= 0; i--)
        {
            var g = Groups[i];
            if (!byName.ContainsKey(g.Name))
            {
                foreach (var child in g.Children) _rowByPid.Remove(child.Pid);
                _groupByName.Remove(g.Name);
                Groups.RemoveAt(i);
            }
        }
    }

    private ProcessGroup? FindParent(ProcessRow row)
    {
        foreach (var g in Groups)
            if (g.Children.Contains(row)) return g;
        return null;
    }

    private void RemoveRowFromItsGroup(ProcessRow row)
    {
        var parent = FindParent(row);
        parent?.Children.Remove(row);
    }

    public void Stop() => _timer.Stop();
}
