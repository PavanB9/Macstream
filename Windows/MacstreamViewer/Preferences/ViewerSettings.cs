using System.IO;
using System.Text.Json;
using MacstreamViewer.Diagnostics;
using MacstreamViewer.Tray;

namespace MacstreamViewer.Preferences;

/// <summary>
/// The viewer's remembered settings, in the project at .local\settings\viewer.json. Changed
/// from the Settings window (toolbar or tray). A setting left empty (null) falls back to its
/// environment variable, then to the built-in default, so the old MACSTREAM_* variables
/// still work until the user picks something in the window.
/// </summary>
public sealed class ViewerSettings
{
    public bool ClipboardSync { get; set; }

    /// <summary>Reverse the wheel direction on the Mac (MACSTREAM_SCROLL_INVERT).</summary>
    public bool? ScrollInvert { get; set; }

    /// <summary>Pixels scrolled on the Mac per wheel notch (MACSTREAM_SCROLL_PX, default 50).</summary>
    public double? ScrollPixelsPerNotch { get; set; }

    /// <summary>Mac key for each Windows modifier: cmd, option, control, shift or none
    /// (MACSTREAM_MAC_MODIFIERS). Null = default.</summary>
    public string? CtrlKey { get; set; }
    public string? RightCtrlKey { get; set; }
    public string? AltKey { get; set; }
    public string? WinKey { get; set; }

    /// <summary>What the PC shows: false/null = the Mac's own screen (mirror), true = an extra
    /// Mac display made for this PC.</summary>
    public bool? ExtraDisplay { get; set; }

    /// <summary>Extra display size in pixels; null/0 = this PC monitor's size.</summary>
    public int? ExtraDisplayWidth { get; set; }
    public int? ExtraDisplayHeight { get; set; }

    /// <summary>Extra display text size: 0/null = automatic, 1 = normal, 2 = Retina (larger).</summary>
    public int? ExtraDisplayTextScale { get; set; }

    /// <summary>Highest frame rate for the extra display: 0/null = 60 (default), 90 or 120.</summary>
    public int? ExtraDisplayMaxFps { get; set; }

    /// <summary>Older form of <see cref="ExtraDisplayMaxFps"/> (true = 120); still read once.</summary>
    public bool? SmootherMotion { get; set; }

    /// <summary>Where the MacBook's own screen is, seen from this PC's monitor: off (null),
    /// left, right, above or below. With the extra display, pushing the mouse off that edge
    /// moves it onto the Mac's own screen.</summary>
    public string? OwnScreenSide { get; set; }

    /// <summary>Which part of that edge: auto (null), whole, first (upper / left half) or
    /// second (lower / right half).</summary>
    public string? OwnScreenEdgePart { get; set; }

    private static string? FilePath()
    {
        var root = MacstreamProcesses.ProjectRoot();
        return root == null ? null : Path.Combine(root, ".local", "settings", "viewer.json");
    }

    public static ViewerSettings Load()
    {
        try
        {
            var path = FilePath();
            if (path != null && File.Exists(path))
                return JsonSerializer.Deserialize<ViewerSettings>(File.ReadAllText(path)) ?? new ViewerSettings();
        }
        catch (Exception ex)
        {
            DiagLog.Write($"Settings: could not read viewer.json: {ex.Message}");
        }
        return new ViewerSettings();
    }

    public void Save()
    {
        try
        {
            var path = FilePath();
            if (path == null)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            DiagLog.Write($"Settings: could not save viewer.json: {ex.Message}");
        }
    }
}
