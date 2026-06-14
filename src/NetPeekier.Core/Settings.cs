// Port of netpeekier/settings.py
//
// All persisted program state, kept inside the program folder. JSON-backed
// (we keep the .txt name for parity with the Python build's settings file,
// but the contents are pure JSON).
//
// Atomic write: serialize to a temp file in the same directory, flush, then
// File.Move(overwrite: true) it over settings.txt. A crash mid-write can
// therefore never truncate the real file (which Load would treat as corrupt
// and reset every block/limit/tag/rule to defaults).

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetPeekier.Core;

public static class SpeedUnits
{
    public static readonly IReadOnlyList<string> All = new[] { "auto", "B/s", "KB/s", "MB/s" };
    public static bool IsValid(string s) => All.Contains(s);
}

/// <summary>
/// Default LAN ranges (private, loopback, link-local). A remote that falls
/// outside all of these is treated as WAN (internet) traffic.
/// </summary>
public static class DefaultLanRanges
{
    public static readonly IReadOnlyList<string> All = new[]
    {
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16",
        "127.0.0.0/8", "169.254.0.0/16",
        "::1/128", "fc00::/7", "fe80::/10",
    };
}

/// <summary>One per-IP firewall rule.</summary>
public sealed class IpRule
{
    public string Exe       { get; set; } = "";
    public string Action    { get; set; } = "block";  // "allow" | "block"
    public string Direction { get; set; } = "out";    // "in" | "out" | "both"
    public string RemoteIp  { get; set; } = "";       // single / CIDR / range / comma-list / "any"
    public string Ports     { get; set; } = "";       // "" or comma-list / ranges
    public string Protocol  { get; set; } = "any";    // "tcp" | "udp" | "any"
    public string? Note     { get; set; }

    public bool Equivalent(IpRule other) =>
        Exe == other.Exe && Action == other.Action && Direction == other.Direction
        && RemoteIp == other.RemoteIp && Ports == other.Ports && Protocol == other.Protocol;
}

public sealed class Settings
{
    // ---- display / housekeeping ------------------------------------------
    public string  SpeedUnit          { get; set; } = "auto";
    public int?    PacketPurgeMinutes { get; set; }

    // ---- firewall + limits (keyed by executable path) --------------------
    public List<string>                     BlockedExes { get; set; } = new();
    public Dictionary<string, int[]>        ExeLimits   { get; set; } = new();  // exe -> [up,down] bytes/s

    // ---- tagging ---------------------------------------------------------
    public Dictionary<string, string>       ExeTags     { get; set; } = new();  // exe -> tag
    public Dictionary<string, int[]>        TagLimits   { get; set; } = new();  // tag -> [up,down] bytes/s
    public List<string>                     TagBlocked  { get; set; } = new();

    // ---- UI persistence --------------------------------------------------
    public Dictionary<string, Dictionary<string, int>> ColumnWidths { get; set; } = new();
    public string?                                     WindowGeometry { get; set; }
    public Dictionary<string, string>                  WindowGeometries { get; set; } = new();

    // ---- view filters ----------------------------------------------------
    public int?    IdleHideMinutes { get; set; }
    public List<string> LanRanges  { get; set; } = new(DefaultLanRanges.All);
    public bool ShowLan { get; set; } = true;
    public bool ShowWan { get; set; } = true;

    // ---- master switches -------------------------------------------------
    public bool FirewallEnabled { get; set; } = true;

    // ---- lockdown --------------------------------------------------------
    public bool         LockdownMode  { get; set; }
    public List<string> AllowedExes   { get; set; } = new();
    public List<string> TagAllowed    { get; set; } = new();
    public int          AllowMinutes  { get; set; } = 5;

    // ---- per-IP rules ----------------------------------------------------
    public List<IpRule> IpRules { get; set; } = new();

    // =====================================================================
    // Convenience views (mirror the Python helpers).
    // =====================================================================
    public (int Up, int Down) ExeLimit(string exe)
    {
        if (ExeLimits.TryGetValue(exe, out var v) && v.Length >= 2)
            return (v[0], v[1]);
        return (0, 0);
    }

    public (int Up, int Down) TagLimit(string tag)
    {
        if (TagLimits.TryGetValue(tag, out var v) && v.Length >= 2)
            return (v[0], v[1]);
        return (0, 0);
    }

    public IEnumerable<string> ExesWithTag(string tag) =>
        ExeTags.Where(kv => kv.Value == tag).Select(kv => kv.Key);

    public IReadOnlyList<string> AllTags()
    {
        var s = new SortedSet<string>(ExeTags.Values);
        foreach (var t in TagLimits.Keys) s.Add(t);
        foreach (var t in TagBlocked)     s.Add(t);
        foreach (var t in TagAllowed)     s.Add(t);
        return s.ToList();
    }

    public IEnumerable<IpRule> IpRulesFor(string exe) =>
        IpRules.Where(r => r.Exe == exe);

    public string? WindowGeometryFor(string key) =>
        WindowGeometries.TryGetValue(key, out var g) ? g : null;

    public void SetWindowGeometry(string key, string geo)
    {
        if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(geo))
        {
            WindowGeometries[key] = geo;
            Save();
        }
    }

    /// <summary>
    /// True if this exe is on the permanent allow list, directly or via an
    /// allowed tag. Mirrors how blocking works with tags.
    /// </summary>
    public bool IsAllowedExe(string exe)
    {
        if (string.IsNullOrEmpty(exe)) return false;
        if (AllowedExes.Contains(exe)) return true;
        return ExeTags.TryGetValue(exe, out var tag)
               && !string.IsNullOrEmpty(tag)
               && TagAllowed.Contains(tag);
    }

    // =====================================================================
    // Persistence.
    // =====================================================================
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
    };

    public static Settings Load()
    {
        try
        {
            if (!File.Exists(Paths.SettingsFile)) return new Settings();
            var text = File.ReadAllText(Paths.SettingsFile);
            var s = JsonSerializer.Deserialize<Settings>(text, JsonOpts);
            return s ?? new Settings();
        }
        catch
        {
            // Same fail-soft behaviour as the Python build: a corrupt or
            // unreadable file gives you defaults rather than crashing.
            return new Settings();
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(Paths.SettingsFile) ?? ".";
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, $".settings-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(fs, this, JsonOpts);
                    fs.Flush(flushToDisk: true);
                }
                // File.Move with overwrite=true is atomic on NTFS, matching
                // os.replace() in the Python build.
                File.Move(tmp, Paths.SettingsFile, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
                throw;
            }
        }
        catch
        {
            // Mirror the Python behaviour: failures here are silent. The UI
            // will retry on the next change. A future improvement is to
            // surface a one-time warning toast.
        }
    }
}
