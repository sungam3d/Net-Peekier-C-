// Startup / crash logger.
//
// Designed to be useful in the exact situation we're in: an app that won't
// launch and gives no clue why. Every interesting code path calls
// Diag.Log("step n"); any unhandled exception calls Diag.LogException.
// The log file ends up next to settings.txt under log/startup.log so the
// user can just send it along with a bug report.
//
// Rules:
//   - NEVER throws. A logger that crashes a crash investigation is worse
//     than no logger at all. Every IO call is wrapped.
//   - Falls back if the primary path fails: log/startup.log →
//     %LOCALAPPDATA%/NetPeekier/startup.log → %TEMP%/NetPeekier-startup.log
//     → null sink. The user-visible message points at whichever one stuck.
//   - Thread-safe.

using System.Globalization;

namespace NetPeekier.Core;

public static class Diag
{
    private static readonly object _gate = new();
    private static string? _path;
    private static bool _initialized;

    /// <summary>The path Diag is actually writing to, or null if nothing worked.</summary>
    public static string? LogPath => _path;

    /// <summary>Initialize the logger. Safe to call multiple times.</summary>
    public static void Init()
    {
        lock (_gate)
        {
            if (_initialized) return;
            _initialized = true;
            _path = TryOpen();
            WriteHeader();
        }
    }

    public static void Log(string message)
    {
        try
        {
            if (!_initialized) Init();
            if (_path is null) return;
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.UtcNow:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            lock (_gate)
            {
                try { File.AppendAllText(_path, line); } catch { /* swallow */ }
            }
        }
        catch { /* swallow */ }
    }

    public static void LogException(string where, Exception ex)
    {
        try
        {
            Log($"EXCEPTION in {where}: {ex.GetType().Name}: {ex.Message}");
            Log("Stack:");
            foreach (var line in (ex.StackTrace ?? "").Split('\n'))
                Log("  " + line.TrimEnd('\r'));
            if (ex.InnerException is { } inner)
                LogException(where + " (inner)", inner);
        }
        catch { /* swallow */ }
    }

    private static string? TryOpen()
    {
        // 1. log/startup.log next to the exe — preferred, matches the rest of
        //    the app's file conventions.
        try
        {
            var dir = Paths.EnsureLogDir();
            var p = Path.Combine(dir, "startup.log");
            File.AppendAllText(p, "");        // create / touch
            return p;
        }
        catch { /* fall through */ }

        // 2. %LOCALAPPDATA%/NetPeekier/startup.log — works even when the exe
        //    is in a read-only location like Program Files.
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(local, "NetPeekier");
            Directory.CreateDirectory(dir);
            var p = Path.Combine(dir, "startup.log");
            File.AppendAllText(p, "");
            return p;
        }
        catch { /* fall through */ }

        // 3. %TEMP%/NetPeekier-startup.log — last resort, always writable.
        try
        {
            var p = Path.Combine(Path.GetTempPath(), "NetPeekier-startup.log");
            File.AppendAllText(p, "");
            return p;
        }
        catch { /* give up */ }

        return null;
    }

    private static void WriteHeader()
    {
        try
        {
            if (_path is null) return;
            var hdr = string.Create(CultureInfo.InvariantCulture, $@"
========================================================================
Net-Peekier startup — {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}
PID:    {Environment.ProcessId}
User:   {Environment.UserDomainName}\{Environment.UserName}
OS:     {Environment.OSVersion}
CLR:    {Environment.Version}
CWD:    {Environment.CurrentDirectory}
Exe:    {Environment.ProcessPath}
========================================================================
");
            File.AppendAllText(_path, hdr);
        }
        catch { /* swallow */ }
    }
}
