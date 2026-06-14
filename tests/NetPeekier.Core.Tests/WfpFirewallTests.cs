// Tests for the platform-independent bits of WfpFirewall. The actual
// FwpmFilterAdd0 round-trip needs a Windows box with admin -- those checks
// live in the migration plan as Phase-3 verification steps.

using NetPeekier.Native;
using static NetPeekier.Core.Tests.TestRunner;

namespace NetPeekier.Core.Tests;

public static class WfpFirewallTests
{
    public static void RunAll() => ValidExe();

    private static void ValidExe()
    {
        Section("WfpFirewall.ValidExe");

        Test("absolute exe path",
            () => True(WfpFirewall.ValidExe(@"C:\Windows\System32\notepad.exe"), "normal exe"));
        Test("nested path",
            () => True(WfpFirewall.ValidExe(@"C:\Program Files\App\app.exe"), "nested"));
        Test("uppercase EXE",
            () => True(WfpFirewall.ValidExe(@"C:\X.EXE"), "uppercase"));

        Test("null",                () => False(WfpFirewall.ValidExe(null),            "null"));
        Test("empty",               () => False(WfpFirewall.ValidExe(""),              "empty"));
        Test("whitespace only",     () => False(WfpFirewall.ValidExe("   "),           "ws"));
        Test("relative",            () => False(WfpFirewall.ValidExe("notepad.exe"),   "relative"));
        Test("no drive letter",     () => False(WfpFirewall.ValidExe(@"\Windows\notepad.exe"), "no drive"));
        Test("not .exe",            () => False(WfpFirewall.ValidExe(@"C:\app.dll"),   "dll"));
        Test("wildcard rejected",   () => False(WfpFirewall.ValidExe(@"C:\*.exe"),     "wildcard"));
        Test("pipe rejected",       () => False(WfpFirewall.ValidExe(@"C:\a|b.exe"),   "pipe"));
        Test("newline rejected",    () => False(WfpFirewall.ValidExe("C:\\a\nb.exe"),  "newline"));
    }
}
