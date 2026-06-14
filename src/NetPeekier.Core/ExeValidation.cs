// Executable-path validation, shared by the firewall (which won't add a
// filter for a bad path) and the GUI (which validates before submitting).
//
// Lives in Core, not Native, because it's pure string logic with no Win32
// dependency — which also lets it be unit-tested without dragging the
// native, NuGet-bound assemblies into the test project.

namespace NetPeekier.Core;

public static class ExeValidation
{
    /// <summary>
    /// True if <paramref name="exePath"/> looks like a safe, absolute Windows
    /// path to an .exe. Rejects empty/relative paths, wildcards, shell
    /// metacharacters, and anything not ending in .exe. Mirrors the Python
    /// build's firewall._valid_exe.
    /// </summary>
    public static bool ValidExe(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        var p = exePath.Trim().Trim('"').Trim();
        if (p.Length < 4) return false;
        if (p.IndexOfAny(new[] { '*', '?', '"', '\n', '\r', '\t', '|', '&', ';' }) >= 0)
            return false;
        // Require a drive-letter absolute path: "X:\..."
        if (p.Length <= 3 || p[1] != ':' || p[2] != '\\') return false;
        if (!p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
