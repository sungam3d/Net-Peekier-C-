// Port of netpeekier/util.py — small formatting helpers shared by the GUI.

using System.Globalization;

namespace NetPeekier.Core;

public static class Formatting
{
    /// <summary>
    /// Bytes/sec → display string. <paramref name="unit"/> is one of
    /// auto / B/s / KB/s / MB/s. "auto" scales each value individually; a
    /// fixed unit always renders in that unit so a whole column reads
    /// consistently.
    /// </summary>
    public static string HumanSpeed(double bps, string unit = "auto")
    {
        var ic = CultureInfo.InvariantCulture;
        return unit switch
        {
            "B/s"  => string.Format(ic, "{0:F0} B/s",  bps),
            "KB/s" => string.Format(ic, "{0:F2} KB/s", bps / 1024.0),
            "MB/s" => string.Format(ic, "{0:F2} MB/s", bps / (1024.0 * 1024.0)),
            _      => bps < 1
                        ? "0 B/s"
                        : bps < 1024
                            ? string.Format(ic, "{0:F0} B/s", bps)
                            : bps < 1024.0 * 1024.0
                                ? string.Format(ic, "{0:F2} KB/s", bps / 1024.0)
                                : string.Format(ic, "{0:F2} MB/s", bps / (1024.0 * 1024.0)),
        };
    }

    /// <summary>Header suffix for a fixed unit, e.g. " (KB/s)". Empty for auto.</summary>
    public static string UnitSuffix(string unit) =>
        unit == "auto" ? "" : $" ({unit})";

    public static string HumanBytes(double n)
    {
        var ic = CultureInfo.InvariantCulture;
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        foreach (var u in units)
        {
            if (n < 1024)
                return u == "B"
                    ? string.Format(ic, "{0:F0} {1}", n, u)
                    : string.Format(ic, "{0:F2} {1}", n, u);
            n /= 1024;
        }
        return string.Format(ic, "{0:F2} EB", n);
    }

    public static string PortsStr(IReadOnlyList<int> ports, int limit = 6)
    {
        if (ports.Count == 0) return "";
        var shown = string.Join(", ", ports.Take(limit));
        if (ports.Count > limit) shown += ", ...";
        return shown;
    }
}
