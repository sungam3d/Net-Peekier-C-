using static NetPeekier.Core.Tests.TestRunner;

namespace NetPeekier.Core.Tests;

public static class SettingsTests
{
    public static void RunAll()
    {
        Defaults();
        RoundTrip();
        ConvenienceViews();
    }

    private static void Defaults()
    {
        Section("Settings.Defaults");

        var s = new Settings();
        Test("default speed unit",     () => Eq("auto", s.SpeedUnit));
        Test("default firewall on",    () => True(s.FirewallEnabled,   "FirewallEnabled"));
        Test("default lockdown off",   () => False(s.LockdownMode,     "LockdownMode"));
        Test("default LAN ranges",     () => Eq(DefaultLanRanges.All.Count, s.LanRanges.Count));
        Test("default empty rules",    () => Eq(0, s.IpRules.Count));
        Test("default empty blocks",   () => Eq(0, s.BlockedExes.Count));
    }

    private static void RoundTrip()
    {
        Section("Settings.RoundTrip (JSON)");

        // Build a settings object with most fields populated, serialize via
        // the same JsonSerializerOptions as Save(), then deserialize and
        // confirm everything came back the same.
        var s = new Settings
        {
            SpeedUnit = "KB/s",
            PacketPurgeMinutes = 60,
            BlockedExes = new() { @"C:\Bad\App.exe" },
            ExeLimits = new() { [@"C:\X.exe"] = new[] { 1024, 2048 } },
            ExeTags = new() { [@"C:\X.exe"] = "browsers" },
            TagLimits = new() { ["browsers"] = new[] { 0, 8192 } },
            TagBlocked = new() { "ads" },
            FirewallEnabled = false,
            LockdownMode = true,
            AllowedExes = new() { @"C:\Good.exe" },
            AllowMinutes = 15,
            IpRules = new()
            {
                new IpRule
                {
                    Exe = @"C:\X.exe",
                    Action = "allow",
                    Direction = "out",
                    RemoteIp = "1.2.3.0/24",
                    Ports = "80,443",
                    Protocol = "tcp",
                    Note = "test",
                }
            },
        };

        // Round-trip through JSON using the same options Settings.Save uses.
        var opts = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(s, opts);
        var t = System.Text.Json.JsonSerializer.Deserialize<Settings>(json, opts)!;

        Test("speed unit",         () => Eq("KB/s", t.SpeedUnit));
        Test("packet purge mins",  () => Eq((int?)60, t.PacketPurgeMinutes));
        Test("blocked_exes",       () => EqLists(s.BlockedExes, t.BlockedExes));
        Test("exe_limits values",  () => EqLists(s.ExeLimits[@"C:\X.exe"], t.ExeLimits[@"C:\X.exe"]));
        Test("exe_tags",           () => Eq("browsers", t.ExeTags[@"C:\X.exe"]));
        Test("tag_limits",         () => EqLists(new[] { 0, 8192 }, t.TagLimits["browsers"]));
        Test("tag_blocked",        () => EqLists(new[] { "ads" }, t.TagBlocked));
        Test("firewall_enabled",   () => False(t.FirewallEnabled, "FirewallEnabled"));
        Test("lockdown_mode",      () => True(t.LockdownMode,     "LockdownMode"));
        Test("allowed_exes",       () => EqLists(new[] { @"C:\Good.exe" }, t.AllowedExes));
        Test("allow_minutes",      () => Eq(15, t.AllowMinutes));
        Test("ip_rules count",     () => Eq(1, t.IpRules.Count));
        Test("ip_rules content",
            () =>
            {
                var r = t.IpRules[0];
                Eq("allow", r.Action);
                Eq("1.2.3.0/24", r.RemoteIp);
                Eq("80,443", r.Ports);
                Eq("tcp", r.Protocol);
                Eq("test", r.Note);
            });
    }

    private static void ConvenienceViews()
    {
        Section("Settings.ConvenienceViews");

        var s = new Settings
        {
            ExeLimits = new() { [@"C:\A.exe"] = new[] { 1024, 0 } },
            TagLimits = new() { ["g"] = new[] { 100, 200 } },
            ExeTags = new() { [@"C:\A.exe"] = "g", [@"C:\B.exe"] = "g" },
            TagBlocked = new() { "g" },
            AllowedExes = new() { @"C:\C.exe" },
            TagAllowed = new() { "h" },
        };
        s.ExeTags[@"C:\D.exe"] = "h";

        Test("ExeLimit known",
            () =>
            {
                var (up, down) = s.ExeLimit(@"C:\A.exe");
                Eq(1024, up); Eq(0, down);
            });
        Test("ExeLimit unknown -> (0,0)",
            () =>
            {
                var (up, down) = s.ExeLimit(@"C:\nope.exe");
                Eq(0, up); Eq(0, down);
            });
        Test("ExesWithTag",
            () => EqLists(
                new[] { @"C:\A.exe", @"C:\B.exe" },
                s.ExesWithTag("g").OrderBy(x => x)));
        Test("AllTags",
            () => EqLists(new[] { "g", "h" }, s.AllTags()));
        Test("IsAllowedExe direct",
            () => True(s.IsAllowedExe(@"C:\C.exe"), "direct allow"));
        Test("IsAllowedExe via tag",
            () => True(s.IsAllowedExe(@"C:\D.exe"), "tag allow"));
        Test("IsAllowedExe negative",
            () => False(s.IsAllowedExe(@"C:\A.exe"), "A is blocked-by-tag"));
    }
}
