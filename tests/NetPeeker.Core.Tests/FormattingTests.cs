using static NetPeeker.Core.Tests.TestRunner;

namespace NetPeeker.Core.Tests;

public static class FormattingTests
{
    public static void RunAll()
    {
        HumanSpeed();
        HumanBytes();
        PortsStr();
    }

    private static void HumanSpeed()
    {
        Section("Formatting.HumanSpeed");

        Test("auto: 0",       () => Eq("0 B/s",        Formatting.HumanSpeed(0)));
        Test("auto: 500",     () => Eq("500 B/s",      Formatting.HumanSpeed(500)));
        Test("auto: 1500",    () => Eq("1.46 KB/s",    Formatting.HumanSpeed(1500)));
        Test("auto: 2 MB/s",  () => Eq("2.00 MB/s",    Formatting.HumanSpeed(2 * 1024 * 1024)));

        Test("fixed B/s",     () => Eq("1500 B/s",     Formatting.HumanSpeed(1500, "B/s")));
        Test("fixed KB/s",    () => Eq("1.46 KB/s",    Formatting.HumanSpeed(1500, "KB/s")));
        Test("fixed MB/s",    () => Eq("0.00 MB/s",    Formatting.HumanSpeed(1500, "MB/s")));
    }

    private static void HumanBytes()
    {
        Section("Formatting.HumanBytes");

        Test("0",        () => Eq("0 B",      Formatting.HumanBytes(0)));
        Test("999",      () => Eq("999 B",    Formatting.HumanBytes(999)));
        Test("1 KB",     () => Eq("1.00 KB",  Formatting.HumanBytes(1024)));
        Test("1 MB",     () => Eq("1.00 MB",  Formatting.HumanBytes(1024 * 1024)));
        Test("1.5 GB",   () => Eq("1.50 GB",  Formatting.HumanBytes(1.5 * 1024 * 1024 * 1024)));
    }

    private static void PortsStr()
    {
        Section("Formatting.PortsStr");

        Test("empty",       () => Eq("", Formatting.PortsStr(Array.Empty<int>())));
        Test("few",         () => Eq("80, 443", Formatting.PortsStr(new[] { 80, 443 })));
        Test("truncated",   () => Eq("1, 2, 3, 4, 5, 6, ...", Formatting.PortsStr(new[] { 1, 2, 3, 4, 5, 6, 7, 8 })));
    }
}
