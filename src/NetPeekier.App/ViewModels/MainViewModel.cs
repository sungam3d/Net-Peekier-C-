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

    public MainViewModel(NetworkMonitor monitor, SystemMonitor? sysmon = null)
    {
        Diag.Log("MainViewModel.ctor: begin");
        _monitor = monitor;
        _sysmon = sysmon;

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

    // The app owns a SystemMonitor for the dashboard's SYSTEM column.
    private readonly SystemMonitor? _sysmon;

    // Dashboard: upload / download (now / peak / total). These are the raw
    // formatted values (no "now:" prefix) so they sit under the NOW/PEAK/
    // TOTAL labels exactly like the Python build.
    private string _upNow = "0/s";
    public string UpNow { get => _upNow; private set => SetField(ref _upNow, value); }
    private string _upPeak = "0/s";
    public string UpPeak { get => _upPeak; private set => SetField(ref _upPeak, value); }
    private string _upTotal = "0 B";
    public string UpTotal { get => _upTotal; private set => SetField(ref _upTotal, value); }

    private string _downNow = "0/s";
    public string DownNow { get => _downNow; private set => SetField(ref _downNow, value); }
    private string _downPeak = "0/s";
    public string DownPeak { get => _downPeak; private set => SetField(ref _downPeak, value); }
    private string _downTotal = "0 B";
    public string DownTotal { get => _downTotal; private set => SetField(ref _downTotal, value); }

    // Dashboard: SYSTEM column. RAM clock+temp intentionally omitted.
    private string _cpuLoad = "--";
    public string CpuLoad { get => _cpuLoad; private set => SetField(ref _cpuLoad, value); }
    private string _cpuClock = "--";
    public string CpuClock { get => _cpuClock; private set => SetField(ref _cpuClock, value); }
    private string _cpuTemp = "--";
    public string CpuTemp { get => _cpuTemp; private set => SetField(ref _cpuTemp, value); }
    private string _gpuLoad = "--";
    public string GpuLoad { get => _gpuLoad; private set => SetField(ref _gpuLoad, value); }
    private string _gpuClock = "--";
    public string GpuClock { get => _gpuClock; private set => SetField(ref _gpuClock, value); }
    private string _gpuTemp = "--";
    public string GpuTemp { get => _gpuTemp; private set => SetField(ref _gpuTemp, value); }
    private string _ramLoad = "--";
    public string RamLoad { get => _ramLoad; private set => SetField(ref _ramLoad, value); }

    private string _status = "starting up…";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    // ---- LAN/WAN view filters (persist to settings) ---------------------
    public bool ShowLan
    {
        get => _monitor.Settings.ShowLan;
        set
        {
            if (_monitor.Settings.ShowLan == value) return;
            _monitor.Settings.ShowLan = value;
            _monitor.Settings.Save();
            OnPropertyChanged();
            SafeRefresh();
        }
    }

    public bool ShowWan
    {
        get => _monitor.Settings.ShowWan;
        set
        {
            if (_monitor.Settings.ShowWan == value) return;
            _monitor.Settings.ShowWan = value;
            _monitor.Settings.Save();
            OnPropertyChanged();
            SafeRefresh();
        }
    }

    // ---- Lockdown mode --------------------------------------------------
    public bool LockdownMode
    {
        get => _monitor.Settings.LockdownMode;
        set
        {
            if (_monitor.Settings.LockdownMode == value) return;
            // Lockdown needs the firewall on to enforce anything.
            if (value && !_monitor.Settings.FirewallEnabled)
            {
                _monitor.SetFirewallEnabled(true);
                OnPropertyChanged(nameof(FirewallEnabled));
                OnPropertyChanged(nameof(FirewallLightColor));
            }
            _monitor.SetLockdown(value);
            OnPropertyChanged();
            SafeRefresh();
        }
    }

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
            OnPropertyChanged(nameof(FirewallLightColor));
            SafeRefresh();
        }
    }

    /// <summary>Green ● when the firewall is on, red when off (Python parity).</summary>
    public string FirewallLightColor => _monitor.Settings.FirewallEnabled ? "#1e9e3e" : "#cc2b2b";

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

    // ---- context-menu actions ------------------------------------------

    /// <summary>End a process by PID (terminate, then kill if it lingers).</summary>
    public void EndProcess(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            p.Kill();   // .NET's Kill maps to TerminateProcess
        }
        catch (ArgumentException) { /* already gone */ }
        catch (Exception ex)
        {
            Diag.LogException("MainViewModel.EndProcess", ex);
            MessageBox.Show(
                "Could not end the process.\nTry running Net-Peekier as Administrator.",
                "End Process", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        SafeRefresh();
    }

    /// <summary>Open Explorer with the selected process's exe highlighted.</summary>
    public void OpenSelectedPath()
    {
        var exe = SelectedExe;
        if (string.IsNullOrEmpty(exe) || !System.IO.File.Exists(exe))
        {
            MessageBox.Show(
                "No executable path is available for this process.\nTry running as Administrator.",
                "Open Program Path", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{exe}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Diag.LogException("MainViewModel.OpenSelectedPath", ex); }
    }

    /// <summary>Prompt for a tag and apply it to the selected exe.</summary>
    public void SetTagOnSelected(Window owner)
    {
        var exe = SelectedExe;
        if (string.IsNullOrEmpty(exe))
        {
            MessageBox.Show(
                "No executable path available for this process.\nRun as Administrator to resolve it.",
                "Set tag", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var current = _monitor.Settings.ExeTags.TryGetValue(exe, out var t) ? t : "";
        var ans = Views.TagPromptDialog.Ask(owner, current);
        if (ans is null) return;   // cancelled

        ans = ans.Trim();
        if (ans.Length == 0) _monitor.Settings.ExeTags.Remove(exe);
        else                 _monitor.Settings.ExeTags[exe] = ans;
        _monitor.Settings.Save();
        SafeRefresh();
    }

    public void RemoveTagFromSelected()
    {
        var exe = SelectedExe;
        if (string.IsNullOrEmpty(exe)) return;
        if (_monitor.Settings.ExeTags.Remove(exe))
        {
            _monitor.Settings.Save();
            SafeRefresh();
        }
    }

    /// <summary>Block / unblock the selected exe via the firewall.</summary>
    public void BlockSelectedExe(bool block)
    {
        var exe = SelectedExe;
        if (string.IsNullOrEmpty(exe))
        {
            MessageBox.Show(
                "No executable path available for this process.\nRun as Administrator to resolve it.",
                "Net-Peekier", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _monitor.SetBlocked(exe, block);
        SafeRefresh();
    }

    /// <summary>Add / remove the selected exe from the Lockdown allow-list.</summary>
    public void AllowSelected(bool allow)
    {
        var exe = SelectedExe;
        if (string.IsNullOrEmpty(exe)) return;
        if (allow) _monitor.SetBlocked(exe, false);   // allow & block are exclusive
        _monitor.SetAllowed(exe, allow);
        SafeRefresh();
    }

    // ---- refresh loop ---------------------------------------------------
    private void Refresh()
    {
        var (procs, totals) = _monitor.Snapshot();
        var unit = _monitor.Settings.SpeedUnit;

        // Dashboard upload / download (bare values; NOW/PEAK/TOTAL labels are
        // in the XAML, matching the Python build).
        UpNow     = Formatting.HumanSpeed(totals.UpNow, unit);
        UpPeak    = Formatting.HumanSpeed(totals.UpPeak, unit);
        UpTotal   = Formatting.HumanBytes(totals.UpTotal);
        DownNow   = Formatting.HumanSpeed(totals.DownNow, unit);
        DownPeak  = Formatting.HumanSpeed(totals.DownPeak, unit);
        DownTotal = Formatting.HumanBytes(totals.DownTotal);

        UpdateSystemStats();

        var admin = _monitor.Firewall is not null;
        var backend = _monitor.HasPerProcessSpeed
            ? $"{_monitor.BackendName}: live per-process speeds"
            : "connection table only — per-process speeds need elevation";
        var extra = admin ? "" : "   |   NOT elevated: run as Administrator for full visibility";
        Status = $"{backend}{extra}";

        ReconcileTree(procs, unit);
    }

    /// <summary>
    /// Pull the latest CPU/GPU/RAM figures from the SystemMonitor. RAM shows
    /// load only (clock + temp deliberately omitted per the desired layout).
    /// Formatting mirrors the Python build: percent/°C suffixes, MHz→GHz.
    /// </summary>
    private void UpdateSystemStats()
    {
        if (_sysmon is null) return;
        SystemStatsSnapshot s;
        try { s = _sysmon.Snapshot(); }
        catch { return; }

        CpuLoad  = Pct(s.CpuLoad);
        CpuClock = Clock(s.CpuClock);
        CpuTemp  = Temp(s.CpuTemp);
        GpuLoad  = Pct(s.GpuLoad);
        GpuClock = Clock(s.GpuClock);
        GpuTemp  = Temp(s.GpuTemp);
        RamLoad  = Pct(s.RamUsed);
    }

    private static string Pct(double? v)  => v is null ? "--" : v.Value.ToString("0") + "%";
    private static string Temp(double? v) => v is null ? "--" : v.Value.ToString("0") + "\u00b0";
    private static string Clock(double? mhz)
    {
        if (mhz is null) return "--";
        return mhz.Value >= 1000
            ? (mhz.Value / 1000.0).ToString("0.0") + "GHz"
            : mhz.Value.ToString("0") + "MHz";
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
        // LAN/WAN view filter, matching the Python build: a WAN process has an
        // internet remote; everything else (local-only or no remote) is LAN.
        bool showLan = _monitor.Settings.ShowLan;
        bool showWan = _monitor.Settings.ShowWan;
        var filtered = procs.Where(p =>
            (p.UsesWan && showWan) || (!p.UsesWan && showLan)).ToList();

        // Bucket the snapshot by process name.
        var byName = new Dictionary<string, List<ProcStat>>();
        foreach (var p in filtered)
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
