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

    private readonly Monitor _monitor;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<int, ProcessRow> _rowByPid = new();

    public MainViewModel(Monitor monitor)
    {
        _monitor = monitor;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        BlockSelectedCommand   = new RelayCommand(BlockSelected,   () => CanModifySelected);
        UnblockSelectedCommand = new RelayCommand(UnblockSelected, () => CanModifySelected);
        RemoveAllRulesCommand  = new RelayCommand(RemoveAllRules);
        ToggleFirewallCommand  = new RelayCommand(ToggleFirewall);

        // First refresh runs immediately so the first paint isn't blank.
        Refresh();
    }

    // ---- bindable state -------------------------------------------------
    public ObservableCollection<ProcessRow> Processes { get; } = new();

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

    private ProcessRow? _selected;
    public ProcessRow? Selected
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

    private bool CanModifySelected => Selected is { } row && !string.IsNullOrEmpty(row.Exe);

    // ---- commands -------------------------------------------------------
    public RelayCommand BlockSelectedCommand { get; }
    public RelayCommand UnblockSelectedCommand { get; }
    public RelayCommand RemoveAllRulesCommand { get; }
    public RelayCommand ToggleFirewallCommand { get; }

    private void BlockSelected()
    {
        if (Selected is { Exe: { Length: > 0 } exe })
            _monitor.SetBlocked(exe, true);
    }

    private void UnblockSelected()
    {
        if (Selected is { Exe: { Length: > 0 } exe })
            _monitor.SetBlocked(exe, false);
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

        // Reconcile by PID. Rows for PIDs no longer present get removed;
        // new PIDs become new rows; existing rows update in place.
        var seenPids = new HashSet<int>();
        foreach (var p in procs)
        {
            seenPids.Add(p.Pid);
            if (!_rowByPid.TryGetValue(p.Pid, out var row))
            {
                row = new ProcessRow(p.Pid);
                _rowByPid[p.Pid] = row;
                Processes.Add(row);
            }
            row.Refresh(p, unit);
        }
        for (int i = Processes.Count - 1; i >= 0; i--)
        {
            if (!seenPids.Contains(Processes[i].Pid))
            {
                _rowByPid.Remove(Processes[i].Pid);
                Processes.RemoveAt(i);
            }
        }
    }

    public void Stop() => _timer.Stop();
}
