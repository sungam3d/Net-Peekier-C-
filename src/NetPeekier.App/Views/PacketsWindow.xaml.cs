using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NetPeekier.Core;
using NetPeekier.Native;

namespace NetPeekier.App.Views;

/// <summary>
/// Live packet capture with a hex/ASCII payload view. Starts capture lazily
/// when opened (capture has overhead, so we don't run it app-wide). If Npcap
/// isn't installed, shows an install prompt instead of the capture UI.
///
/// The grid refreshes from the capture ring buffer at ~2 Hz; selecting a row
/// renders that packet's payload as a classic offset / hex / ASCII dump.
/// </summary>
public partial class PacketsWindow : Window
{
    private readonly PacketCapture? _cap;
    private readonly NetworkMonitor _monitor;
    private readonly DispatcherTimer _timer;

    // Row VM for the grid.
    public sealed class PacketRow
    {
        public required string Time { get; init; }
        public required string ProcessName { get; init; }
        public required string PidText { get; init; }
        public required string Protocol { get; init; }
        public required string DirectionArrow { get; init; }
        public required string Local { get; init; }
        public required string Remote { get; init; }
        public required int Length { get; init; }
        public required CapturedPacket Source { get; init; }   // for the hex view
    }

    public PacketsWindow()
    {
        InitializeComponent();
        var app = (App)System.Windows.Application.Current;
        _cap = app.PacketCapture;
        _monitor = app.NetworkMonitor;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _timer.Tick += (_, _) => RefreshGrid();

        WindowGeometryPersistence.Apply(this, "packets");
        Loaded += (_, _) => StartCaptureOrPrompt();
        Closed += (_, _) => _timer.Stop();   // leave capture running? No — stop it.
    }

    private void StartCaptureOrPrompt()
    {
        if (_cap is null || !PacketCapture.NpcapInstalled)
        {
            ShowNoNpcap(true);
            return;
        }

        ShowNoNpcap(false);
        if (!_cap.Available)
        {
            _cap.Start();
        }

        if (!_cap.Available)
        {
            // Start failed despite wpcap.dll being present — surface it.
            ShowNoNpcap(true);
            StatusLine.Text = "Npcap present but capture could not start — see log/startup.log.";
            return;
        }

        PopulateFilter();
        _timer.Start();
        RefreshGrid();
    }

    private void ShowNoNpcap(bool show)
    {
        NoNpcapPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CaptureArea.Visibility  = show ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- toolbar handlers ----------------------------------------------

    private void OnTogglePause(object sender, RoutedEventArgs e)
    {
        if (_cap is null) return;
        _cap.Paused = !_cap.Paused;
        PauseButton.Content = _cap.Paused ? "Resume" : "Pause";
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _cap?.Clear();
        RefreshGrid();
        HexView.Text = "";
    }

    private void OnDownloadNpcap(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://npcap.com/#download",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Diag.LogException("PacketsWindow.OnDownloadNpcap", ex); }
    }

    private void OnRecheck(object sender, RoutedEventArgs e) => StartCaptureOrPrompt();

    private void PopulateFilter()
    {
        // Build a PID filter list from current processes with network activity.
        var (procs, _) = _monitor.Snapshot();
        var items = new List<FilterItem> { new("All processes", null) };
        foreach (var p in procs.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            items.Add(new FilterItem($"{p.Name} (PID {p.Pid})", p.Pid));

        var prev = (FilterCombo.SelectedItem as FilterItem)?.Pid;
        FilterCombo.ItemsSource = items;
        FilterCombo.DisplayMemberPath = nameof(FilterItem.Label);
        // Restore previous selection if still present, else "All".
        FilterCombo.SelectedItem = items.FirstOrDefault(i => i.Pid == prev) ?? items[0];
    }

    private sealed record FilterItem(string Label, int? Pid);

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_cap is null) return;
        _cap.FilterPid = (FilterCombo.SelectedItem as FilterItem)?.Pid;
        RefreshGrid();
    }

    // ---- grid + hex ----------------------------------------------------

    private void RefreshGrid()
    {
        if (_cap is null || !_cap.Available) return;

        // Preserve selection across refresh by remembering the selected
        // packet's identity (timestamp+ports is good enough for display).
        var selected = (PacketGrid.SelectedItem as PacketRow)?.Source;

        var snap = _cap.Snapshot(max: 2000);
        var rows = new List<PacketRow>(snap.Count);
        foreach (var p in snap)
        {
            rows.Add(new PacketRow
            {
                Time           = DateTimeOffset.FromUnixTimeMilliseconds((long)(p.Timestamp * 1000))
                                    .LocalDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                ProcessName    = p.ProcessName,
                PidText        = p.Pid?.ToString() ?? "—",
                Protocol       = p.Protocol,
                DirectionArrow = p.DirectionArrow,
                Local          = $"{p.LocalIp}:{p.LocalPort}",
                Remote         = $"{p.RemoteIp}:{p.RemotePort}",
                Length         = p.Length,
                Source         = p,
            });
        }

        PacketGrid.ItemsSource = rows;
        CountLabel.Text = $"{_cap.Count} packets buffered";
        StatusLine.Text = _cap.Paused ? "Paused" : "Capturing…";

        // Re-select the same underlying packet if it's still in view.
        if (selected is not null)
        {
            var match = rows.FirstOrDefault(r => ReferenceEquals(r.Source, selected));
            if (match is not null) PacketGrid.SelectedItem = match;
        }
    }

    private void OnPacketSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PacketGrid.SelectedItem is not PacketRow row)
        {
            HexView.Text = "";
            return;
        }
        HexView.Text = FormatHexDump(row.Source.Payload);
    }

    /// <summary>
    /// Classic hex dump: 16 bytes per line, "OFFSET   HH HH .. HH  |ascii|".
    /// Non-printable bytes render as '.'. Matches the Python tool's view.
    /// </summary>
    private static string FormatHexDump(byte[] data)
    {
        if (data.Length == 0) return "(no payload)";

        var sb = new StringBuilder(data.Length * 4);
        for (int offset = 0; offset < data.Length; offset += 16)
        {
            sb.Append(offset.ToString("X8", CultureInfo.InvariantCulture));
            sb.Append("   ");

            int lineLen = Math.Min(16, data.Length - offset);
            for (int i = 0; i < 16; i++)
            {
                if (i < lineLen)
                    sb.Append(data[offset + i].ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
                else
                    sb.Append("   ");
                if (i == 7) sb.Append(' ');     // gap after 8 bytes
            }

            sb.Append(" |");
            for (int i = 0; i < lineLen; i++)
            {
                byte b = data[offset + i];
                sb.Append(b >= 0x20 && b < 0x7f ? (char)b : '.');
            }
            sb.Append('|');
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
