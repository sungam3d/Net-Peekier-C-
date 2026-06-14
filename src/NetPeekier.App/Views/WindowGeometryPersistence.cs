// Per-window position + size persistence.
//
// Settings already has a `WindowGeometries` dictionary keyed by a string
// the caller chooses ("main", "connections", "firewall", etc.). On window
// load we read the geometry string into Left/Top/Width/Height; on close we
// serialize and persist.
//
// Geometry format is "left,top,width,height" — minimal and forgiving;
// parse failures fall back to the window's design-time defaults.

using System.Globalization;
using System.Windows;
using NetPeekier.Core;
using NetPeekier.Native;

namespace NetPeekier.App.Views;

internal static class WindowGeometryPersistence
{
    /// <summary>Wire a window so its geometry restores on open and saves on close.</summary>
    public static void Apply(Window window, string key)
    {
        var monitor = ((App)System.Windows.Application.Current).NetworkMonitor;
        var s = monitor.Settings;

        if (s.WindowGeometryFor(key) is { } geo) ApplyString(window, geo);

        window.Closed += (_, _) =>
        {
            // Only persist if the window has a sensible size (a closed-while-
            // minimized window would otherwise overwrite real values with
            // garbage from the restore bounds).
            if (window.WindowState == WindowState.Minimized) return;

            var stored = string.Create(CultureInfo.InvariantCulture,
                $"{(int)window.Left},{(int)window.Top},{(int)window.Width},{(int)window.Height}");
            s.SetWindowGeometry(key, stored);
            try { s.Save(); } catch { /* best-effort */ }
        };
    }

    private static void ApplyString(Window w, string geo)
    {
        var parts = geo.Split(',');
        if (parts.Length != 4) return;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var l)) return;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) return;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var width))  return;
        if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height)) return;

        // Sanity: refuse off-screen / tiny restores so a broken settings.txt
        // can't make the window invisible.
        if (width < 200 || height < 150) return;
        if (l < -32000 || t < -32000) return;

        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left   = l;
        w.Top    = t;
        w.Width  = width;
        w.Height = height;
    }
}
