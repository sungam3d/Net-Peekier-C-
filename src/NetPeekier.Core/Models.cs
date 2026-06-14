// Port of netpeekier/models.py
//
// Plain data containers shared across the app. No behaviour beyond a couple
// of computed properties; the monitor, capture backend and GUI all speak the
// same language through these.

namespace NetPeekier.Core;

/// <summary>
/// A single captured connection event (one row in the Connection Events
/// window — formerly the Captured Packets / hex view).
///
/// The Python build, which had WinDivert in the data path, also carried the
/// raw bytes for a hex dump. The C# build is driverless, so this is summary
/// metadata only — payload is intentionally absent.
/// </summary>
public sealed record ConnectionEvent
{
    public required double Timestamp { get; init; }        // epoch seconds
    public required bool   Outbound  { get; init; }        // true = local→remote
    public required string Protocol  { get; init; }        // TCP / UDP / ICMP / ...
    public required string LocalIp   { get; init; }
    public required int    LocalPort { get; init; }
    public required string RemoteIp  { get; init; }
    public required int    RemotePort{ get; init; }
    public required int    Length    { get; init; }        // bytes on the wire
    public int?            Pid       { get; init; }

    public string DirectionArrow => Outbound ? "-->" : "<--";
}

/// <summary>A live socket belonging to a process (Detail Information rows).</summary>
public sealed class Connection
{
    public required int    Pid        { get; init; }
    public required string Protocol   { get; init; }      // TCP / UDP
    public required string LocalIp    { get; init; }
    public required int    LocalPort  { get; init; }
    public required string RemoteIp   { get; init; }
    public required int    RemotePort { get; init; }
    public required string Status     { get; init; }      // ESTABLISHED / LISTEN / ...
    public double StartTime { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    // Live per-connection rates (bytes/sec), filled in by the monitor.
    public double UpBps   { get; set; }
    public double DownBps { get; set; }
    // Cumulative bytes on this connection since app start.
    public double UpTotal   { get; set; }
    public double DownTotal { get; set; }

    /// <summary>Stable identity used to correlate events and carry rates over.</summary>
    public ConnectionKey Key => new(Protocol, LocalIp, LocalPort, RemoteIp, RemotePort);

    /// <summary>
    /// Best-effort traffic direction for the row. A listening socket has no
    /// single direction. When bytes are measured, the dominant side wins;
    /// otherwise: a socket with a remote endpoint is outbound from our view.
    /// </summary>
    public string DirectionArrow
    {
        get
        {
            if (Status == "LISTEN" || RemotePort == 0) return "";
            if (UpTotal > 0 || DownTotal > 0)
                return UpTotal >= DownTotal ? "-->" : "<--";
            if (UpBps > 0 || DownBps > 0)
                return UpBps >= DownBps ? "-->" : "<--";
            return "-->";
        }
    }
}

/// <summary>
/// Stable composite key for a connection. Used as a dictionary key inside
/// the monitor — IEquatable comes free from the record.
/// </summary>
public readonly record struct ConnectionKey(
    string Protocol, string LocalIp, int LocalPort, string RemoteIp, int RemotePort);

/// <summary>One row in the main application list.</summary>
public sealed class ProcStat
{
    public required int    Pid  { get; init; }
    public required string Name { get; init; }
    public string Exe { get; init; } = "";

    public double UpBps     { get; set; }
    public double DownBps   { get; set; }
    public double UpTotal   { get; set; }       // cumulative bytes sent since app start
    public double DownTotal { get; set; }       // cumulative bytes received since app start

    public List<int>        ListeningPorts { get; init; } = new();
    public List<Connection> Connections    { get; init; } = new();

    public bool   Blocked   { get; set; }
    public int    UpLimit   { get; set; }       // bytes/sec, 0 = unlimited (display-only in C# build)
    public int    DownLimit { get; set; }
    public string Tag       { get; set; } = ""; // user-assigned group tag
    public bool   UsesWan   { get; set; }       // any connection has a WAN (internet) remote
}

/// <summary>Dashboard figures.</summary>
public sealed class Totals
{
    public double UpNow    { get; set; }
    public double DownNow  { get; set; }
    public double UpPeak   { get; set; }
    public double DownPeak { get; set; }
    public double UpTotal  { get; set; }        // cumulative bytes sent since app start
    public double DownTotal{ get; set; }        // cumulative bytes received since app start

    public Totals Clone() => new()
    {
        UpNow     = UpNow,
        DownNow   = DownNow,
        UpPeak    = UpPeak,
        DownPeak  = DownPeak,
        UpTotal   = UpTotal,
        DownTotal = DownTotal,
    };
}
