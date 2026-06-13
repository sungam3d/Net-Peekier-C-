using static NetPeeker.Core.Tests.TestRunner;

namespace NetPeeker.Core.Tests;

public static class IpCalcTests
{
    public static void RunAll()
    {
        ValidIpSpec();
        ValidPorts();
        ComplementPorts();
        BlockRangesExcept();
    }

    private static void ValidIpSpec()
    {
        Section("IpCalc.ValidIpSpec");

        Test("plain v4",         () => True(IpCalc.ValidIpSpec("192.168.1.1"),   "192.168.1.1"));
        Test("plain v6",         () => True(IpCalc.ValidIpSpec("::1"),           "::1"));
        Test("CIDR v4",          () => True(IpCalc.ValidIpSpec("10.0.0.0/8"),    "10.0.0.0/8"));
        Test("CIDR v6",          () => True(IpCalc.ValidIpSpec("fc00::/7"),      "fc00::/7"));
        Test("range v4",         () => True(IpCalc.ValidIpSpec("1.2.3.4-1.2.3.10"), "range"));
        Test("any",              () => True(IpCalc.ValidIpSpec("any"),           "any"));
        Test("wildcard",         () => True(IpCalc.ValidIpSpec("*"),             "*"));
        Test("comma list",       () => True(IpCalc.ValidIpSpec("1.1.1.1,2.2.2.2,10.0.0.0/24"), "list"));

        Test("empty rejected",   () => False(IpCalc.ValidIpSpec(""),      "empty"));
        Test("null rejected",    () => False(IpCalc.ValidIpSpec(null),    "null"));
        Test("junk rejected",    () => False(IpCalc.ValidIpSpec("not-an-ip"), "junk"));
        Test("reversed range",   () => False(IpCalc.ValidIpSpec("1.2.3.10-1.2.3.4"), "reversed"));
        Test("mixed family",     () => False(IpCalc.ValidIpSpec("1.2.3.4-::1"),     "mixed"));
        Test("bad prefix",       () => False(IpCalc.ValidIpSpec("10.0.0.0/64"),     "bad v4 prefix"));
        Test("trailing comma",   () => False(IpCalc.ValidIpSpec("1.2.3.4,"),        "trailing comma"));
    }

    private static void ValidPorts()
    {
        Section("IpCalc.ValidPorts");

        Test("empty allowed",    () => True(IpCalc.ValidPorts(""),        "empty = all"));
        Test("null allowed",     () => True(IpCalc.ValidPorts(null),      "null = all"));
        Test("single",           () => True(IpCalc.ValidPorts("80"),      "80"));
        Test("range",            () => True(IpCalc.ValidPorts("1000-2000"), "range"));
        Test("list",             () => True(IpCalc.ValidPorts("80,443,8080"), "list"));
        Test("mixed",            () => True(IpCalc.ValidPorts("22,80-90,443"), "mixed"));

        Test("zero rejected",    () => False(IpCalc.ValidPorts("0"),       "zero"));
        Test("too big rejected", () => False(IpCalc.ValidPorts("65536"),   "65536"));
        Test("negative rejected",() => False(IpCalc.ValidPorts("-1"),      "negative"));
        Test("reversed rejected",() => False(IpCalc.ValidPorts("100-50"),  "reversed"));
        Test("junk rejected",    () => False(IpCalc.ValidPorts("abc"),     "junk"));
    }

    private static void ComplementPorts()
    {
        Section("IpCalc.ComplementPorts");

        Test("empty -> full range",
            () => Eq("1-65535", IpCalc.ComplementPorts("")));
        Test("single 80 -> two ranges",
            () => Eq("1-79,81-65535", IpCalc.ComplementPorts("80")));
        Test("80 and 443",
            () => Eq("1-79,81-442,444-65535", IpCalc.ComplementPorts("80,443")));
        Test("range 80-90",
            () => Eq("1-79,91-65535", IpCalc.ComplementPorts("80-90")));
        Test("adjacent merge: 80-90,91-100",
            () => Eq("1-79,101-65535", IpCalc.ComplementPorts("80-90,91-100")));
        Test("overlap merge: 80-100,90-110",
            () => Eq("1-79,111-65535", IpCalc.ComplementPorts("80-100,90-110")));
        Test("full coverage -> empty",
            () => Eq("", IpCalc.ComplementPorts("1-65535")));
        Test("single port 1 -> 2-65535",
            () => Eq("2-65535", IpCalc.ComplementPorts("1")));
        Test("single port 65535 -> 1-65534",
            () => Eq("1-65534", IpCalc.ComplementPorts("65535")));
    }

    private static void BlockRangesExcept()
    {
        Section("IpCalc.BlockRangesExcept");

        Test("empty allowed -> block everything",
            () =>
            {
                var (v4, v6) = IpCalc.BlockRangesExcept(Array.Empty<string>());
                EqLists(new[] { "0.0.0.0-255.255.255.255" }, v4, "v4");
                EqLists(new[] { "::-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff" }, v6, "v6");
            });

        Test("single v4 allowed",
            () =>
            {
                var (v4, v6) = IpCalc.BlockRangesExcept(new[] { "1.2.3.4" });
                EqLists(new[] { "0.0.0.0-1.2.3.3", "1.2.3.5-255.255.255.255" }, v4, "v4");
                // No v6 allowed -> all v6 blocked.
                Eq(1, v6.Count, "v6 count");
            });

        Test("CIDR allowed",
            () =>
            {
                var (v4, _) = IpCalc.BlockRangesExcept(new[] { "10.0.0.0/8" });
                EqLists(
                    new[] { "0.0.0.0-9.255.255.255", "11.0.0.0-255.255.255.255" },
                    v4, "v4");
            });

        Test("CIDR loosely written gets masked (10.0.0.5/8 == 10.0.0.0/8)",
            () =>
            {
                var (v4, _) = IpCalc.BlockRangesExcept(new[] { "10.0.0.5/8" });
                EqLists(
                    new[] { "0.0.0.0-9.255.255.255", "11.0.0.0-255.255.255.255" },
                    v4, "v4");
            });

        Test("two contiguous CIDRs merge",
            () =>
            {
                var (v4, _) = IpCalc.BlockRangesExcept(new[] { "10.0.0.0/9", "10.128.0.0/9" });
                EqLists(
                    new[] { "0.0.0.0-9.255.255.255", "11.0.0.0-255.255.255.255" },
                    v4, "merged");
            });

        Test("two overlapping ranges merge",
            () =>
            {
                var (v4, _) = IpCalc.BlockRangesExcept(new[] { "1.0.0.0-1.0.0.100", "1.0.0.50-1.0.0.200" });
                EqLists(
                    new[] { "0.0.0.0-0.255.255.255", "1.0.0.201-255.255.255.255" },
                    v4, "merged");
            });

        Test("v4 only allows -> v6 fully blocked",
            () =>
            {
                var (_, v6) = IpCalc.BlockRangesExcept(new[] { "192.168.1.1" });
                Eq(1, v6.Count, "v6 count");
                True(v6[0].StartsWith("::-"), "v6 covers full range");
            });

        Test("entire v4 allowed -> empty v4 list",
            () =>
            {
                var (v4, _) = IpCalc.BlockRangesExcept(new[] { "0.0.0.0/0" });
                Eq(0, v4.Count, "v4 empty");
            });

        Test("'any' is skipped as allow entry (matches Python behaviour)",
            () =>
            {
                // 'any' means "no restriction", which in whitelist semantics
                // means: nothing to subtract. Compare to empty input.
                var (v4Any, _) = IpCalc.BlockRangesExcept(new[] { "any" });
                var (v4None, _) = IpCalc.BlockRangesExcept(Array.Empty<string>());
                EqLists(v4None, v4Any, "any vs empty");
            });
    }
}
