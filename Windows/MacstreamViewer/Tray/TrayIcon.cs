using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using MacstreamViewer.Diagnostics;
using Forms = System.Windows.Forms;

namespace MacstreamViewer.Tray;

/// <summary>What the tray menu shows and does; implemented by MainWindow.</summary>
public interface ITrayHost
{
    bool IsConnected { get; }
    bool CanConnect { get; }
    string? ConnectedHost { get; }
    bool MacControlEnabled { get; }
    bool IsFullscreen { get; }
    bool ClipboardSyncEnabled { get; }
    void ShowViewer();
    void Connect();
    void Disconnect();
    void ToggleMacControl();
    void ToggleFullscreen();
    void ToggleClipboardSync();
    void ShowSettings();
    void QuitEverything();
}

/// <summary>
/// Notification-area (tray) icon for the viewer: status, connect/disconnect, Mac control,
/// full screen, Settings, the log, and "Quit everything". Double-click opens the viewer.
/// All calls must come from the UI thread.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly ITrayHost _host;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _status;
    private readonly Forms.ToolStripMenuItem _connect;
    private readonly Forms.ToolStripMenuItem _macControl;
    private readonly Forms.ToolStripMenuItem _fullscreen;
    private readonly Forms.ToolStripMenuItem _clipboard;
    private readonly Icon _connectedIcon;
    private readonly Icon _idleIcon;
    private bool _disposed;

    public TrayIcon(ITrayHost host)
    {
        _host = host;
        Forms.Application.EnableVisualStyles();   // modern look for the tray menu
        var size = Forms.SystemInformation.SmallIconSize.Width;
        _connectedIcon = MakeIcon(size, Color.FromArgb(0x4C, 0xD9, 0x64));
        _idleIcon = MakeIcon(size, Color.FromArgb(0x9A, 0x9A, 0x9A));

        _status = new Forms.ToolStripMenuItem { Enabled = false };
        var show = new Forms.ToolStripMenuItem("Show Macstream", null, (_, _) => _host.ShowViewer())
        {
            Font = new Font(Forms.SystemInformation.MenuFont, FontStyle.Bold),
        };
        _connect = new Forms.ToolStripMenuItem("Connect", null, (_, _) =>
        {
            if (_host.IsConnected)
                _host.Disconnect();
            else
                _host.Connect();
        });
        _macControl = new Forms.ToolStripMenuItem("Mac control (Ctrl+Alt+F12)", null, (_, _) => _host.ToggleMacControl());
        _fullscreen = new Forms.ToolStripMenuItem("Full screen (Ctrl+Alt+F11)", null, (_, _) => _host.ToggleFullscreen());
        _clipboard = new Forms.ToolStripMenuItem("Clipboard sync", null, (_, _) => _host.ToggleClipboardSync())
        {
            ToolTipText = "Copy on one computer, paste on the other (text and pictures)",
        };
        var settings = new Forms.ToolStripMenuItem("Settings...", null, (_, _) => _host.ShowSettings())
        {
            ToolTipText = "Clipboard, scrolling, key mapping, pairing",
        };
        var openLog = new Forms.ToolStripMenuItem("Open log folder", null, (_, _) => OpenLogFolder());
        var quit = new Forms.ToolStripMenuItem("Quit everything", null, (_, _) => _host.QuitEverything())
        {
            ToolTipText = "Releases any keys held on the Mac, disconnects and closes every Macstream process on this PC",
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange(new Forms.ToolStripItem[]
        {
            _status, new Forms.ToolStripSeparator(),
            show, _connect, _macControl, _clipboard, _fullscreen, new Forms.ToolStripSeparator(),
            settings, openLog, new Forms.ToolStripSeparator(),
            quit,
        });
        menu.Opening += (_, _) => Refresh();

        _icon = new Forms.NotifyIcon
        {
            Icon = _idleIcon,
            Text = "Macstream",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                _host.ShowViewer();
        };
        Refresh();
    }

    /// <summary>Updates the icon, tooltip and menu from the viewer's state.</summary>
    public void Refresh()
    {
        if (_disposed)
            return;

        var connected = _host.IsConnected;
        var statusText = connected
            ? $"Connected to the Mac{(_host.ConnectedHost is { } h ? $" ({h})" : "")}"
            : "Not connected";
        _status.Text = statusText;
        _icon.Icon = connected ? _connectedIcon : _idleIcon;
        var tip = $"Macstream - {statusText}";
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;   // NotifyIcon limit

        _connect.Text = connected ? "Disconnect" : "Connect";
        _connect.Enabled = connected || _host.CanConnect;
        _macControl.Checked = _host.MacControlEnabled;
        _fullscreen.Checked = _host.IsFullscreen;
        _clipboard.Checked = _host.ClipboardSyncEnabled;
    }

    private static void OpenLogFolder()
    {
        try
        {
            var folder = Path.GetDirectoryName(DiagLog.Path)!;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagLog.Write(ex, "could not open the log folder");
        }
    }

    /// <summary>The window/taskbar icon: the same screen glyph, without the status dot.</summary>
    public static System.Windows.Media.ImageSource CreateWindowIcon()
    {
        using var icon = MakeIcon(32, null);
        var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle, System.Windows.Int32Rect.Empty,
            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());   // copies the pixels
        source.Freeze();
        return source;
    }

    /// <summary>A small screen glyph with a status dot, drawn at runtime (no icon file needed).</summary>
    private static Icon MakeIcon(int size, Color? dot)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size / 16f;
            using var screen = new SolidBrush(Color.FromArgb(0x1E, 0x88, 0xE5));
            using var outline = new Pen(Color.White, Math.Max(1f, s));
            var body = new RectangleF(1 * s, 2 * s, 14 * s, 9.5f * s);
            g.FillRectangle(screen, body);
            g.DrawRectangle(outline, body.X, body.Y, body.Width, body.Height);
            g.FillRectangle(Brushes.White, 6 * s, 11.5f * s, 4 * s, 1.5f * s);   // stand
            g.FillRectangle(Brushes.White, 4 * s, 13 * s, 8 * s, 1.5f * s);
            if (dot is { } dotColor)
            {
                using var dotBrush = new SolidBrush(dotColor);
                using var dotEdge = new Pen(Color.FromArgb(0x20, 0x20, 0x20), Math.Max(1f, s * 0.8f));
                var d = new RectangleF(9.5f * s, 6.5f * s, 6 * s, 6 * s);
                g.FillEllipse(dotBrush, d);
                g.DrawEllipse(dotEdge, d);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();   // own copy, so the handle can be freed
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _icon.Visible = false;   // removes it from the tray right away, no ghost icon
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _connectedIcon.Dispose();
        _idleIcon.Dispose();
    }
}
