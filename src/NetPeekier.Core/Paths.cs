// Port of netpeekier/paths.py
//
// Filesystem locations, all anchored next to the exe. Everything we write
// lives under the program folder (the directory that contains NetPeekier.exe),
// so nothing is scattered across the user's profile:
//
//   <root>/settings.txt    all app settings & rules (JSON inside a .txt)
//   <root>/log/            history.jsonl and any exported logs
//
// The only things that live outside this folder are OS-level: WFP filters,
// stored by Windows in its own database; we manage them through the firewall
// engine, not as files.

namespace NetPeekier.Core;

public static class Paths
{
    /// <summary>Folder the executable lives in.</summary>
    public static string Root { get; } = ResolveRoot();

    /// <summary>Path of the settings file (JSON, but named .txt for parity).</summary>
    public static string SettingsFile { get; } = Path.Combine(Root, "settings.txt");

    /// <summary>Folder where history/exported logs go.</summary>
    public static string LogDir { get; } = Path.Combine(Root, "log");

    public static string EnsureLogDir()
    {
        try { Directory.CreateDirectory(LogDir); } catch { /* best-effort */ }
        return LogDir;
    }

    private static string ResolveRoot()
    {
        // AppContext.BaseDirectory is the folder containing the running exe
        // for single-file publishes too. Falls back to current dir for tests.
        try
        {
            var b = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(b)) return Path.GetFullPath(b);
        }
        catch { /* fall through */ }
        return Path.GetFullPath(Environment.CurrentDirectory);
    }
}
