// Tests for executable-path validation. This logic lives in
// NetPeekier.Core (ExeValidation) so it can be tested without dragging the
// native, NuGet-bound NetPeekier.Native assemblies into the test project.
// WfpFirewall.ValidExe simply delegates here.

using NetPeekier.Core;
using static NetPeekier.Core.Tests.TestRunner;

namespace NetPeekier.Core.Tests;

public static class WfpFirewallTests
{
    public static void RunAll() => ValidExe();

    private static void ValidExe()
    {
        Section("ExeValidation.ValidExe");

        Test("absolute exe path",
            () => True(ExeValidation.ValidExe(@"C:\Windows\System32\notepad.exe"), "normal exe"));
        Test("nested path",
            () => True(ExeValidation.ValidExe(@"C:\Program Files\App\app.exe"), "nested"));
        Test("uppercase EXE",
            () => True(ExeValidation.ValidExe(@"C:\X.EXE"), "uppercase"));

        Test("null",                () => False(ExeValidation.ValidExe(null),            "null"));
        Test("empty",               () => False(ExeValidation.ValidExe(""),              "empty"));
        Test("whitespace only",     () => False(ExeValidation.ValidExe("   "),           "ws"));
        Test("relative",            () => False(ExeValidation.ValidExe("notepad.exe"),   "relative"));
        Test("no drive letter",     () => False(ExeValidation.ValidExe(@"\Windows\notepad.exe"), "no drive"));
        Test("not .exe",            () => False(ExeValidation.ValidExe(@"C:\app.dll"),   "dll"));
        Test("wildcard rejected",   () => False(ExeValidation.ValidExe(@"C:\*.exe"),     "wildcard"));
        Test("pipe rejected",       () => False(ExeValidation.ValidExe(@"C:\a|b.exe"),   "pipe"));
        Test("newline rejected",    () => False(ExeValidation.ValidExe("C:\\a\nb.exe"),  "newline"));
    }
}
