using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MacstreamViewer.Diagnostics;
using MacstreamViewer.Protocol;

namespace MacstreamViewer.Sharing;

/// <summary>
/// Clipboard sync with the Mac: text and images (PNG), over the encrypted connection
/// (CLIPBOARD packet, see PROTOCOL.md). Off until the user turns it on (toolbar, tray or
/// Settings); the main window remembers the choice (Preferences/ViewerSettings.cs).
///
/// Privacy rules (the Mac follows the same ones):
/// - This toggle decides for both machines: the Mac only watches its clipboard while this
///   side has said "sync on".
/// - Only copies made while sync is on cross over. What was already on the clipboard is not
///   sent, and there is no history: each copy replaces the last one.
/// - Copies that apps mark as private (password managers) are not sent, and text or images
///   from the Mac are marked so Windows never uploads them to the cloud clipboard.
/// - Contents are never logged, only their size.
/// - When a copy has both text and a picture (e.g. from Word), the text is sent. Copied files
///   are not sent.
/// Use from the UI thread, except <see cref="HandleRemote"/>.
/// </summary>
public sealed class ClipboardSync : IDisposable
{
    public const int MaxTextBytes = 512 * 1024;
    /// <summary>Largest picture sent either way, as PNG (a full Retina screenshot is 3-6 MB).</summary>
    public const int MaxImageBytes = 12 * 1024 * 1024;
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private readonly Dispatcher _dispatcher;
    private readonly IntPtr _hwnd;
    private readonly HwndSource? _source;
    private readonly Func<bool> _connected;
    private readonly Action<byte[]> _send;
    private bool _enabled;
    private bool _listening;
    private string? _lastRemoteText;
    /// <summary>Clipboard sequence number right after we wrote the Mac's copy (not sent back).</summary>
    private uint _ownWriteSequence;
    private bool _disposed;

    public event EventHandler<bool>? EnabledChanged;

    public ClipboardSync(Window window, IntPtr hwnd, bool enabled, Func<bool> connected, Action<byte[]> send)
    {
        _dispatcher = window.Dispatcher;
        _hwnd = hwnd;
        _connected = connected;
        _send = send;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        _enabled = enabled;
        if (_enabled)
            StartListening();
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled)
                return;
            _enabled = value;
            if (value)
                StartListening();
            else
                StopListening();
            if (_connected())
                _send(ClipboardPayload.Sync(value));
            DiagLog.Write($"Clipboard sync {(value ? "on" : "off")}");
            EnabledChanged?.Invoke(this, value);
        }
    }

    /// <summary>Call when connected: tells the Mac to start watching if sync is on.</summary>
    public void OnConnected()
    {
        _lastRemoteText = null;
        if (_enabled)
            _send(ClipboardPayload.Sync(true));
    }

    /// <summary>A CLIPBOARD payload from the Mac. Any thread.</summary>
    public void HandleRemote(byte[] payload)
    {
        if (ClipboardPayload.TryReadText(payload, MaxTextBytes, out var text))
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (!_enabled || _disposed)
                    return;
                _lastRemoteText = text;   // the clipboard-changed message for this must not echo back
                var data = new DataObject();
                data.SetText(text, TextDataFormat.UnicodeText);
                if (TrySetClipboard(data))
                    DiagLog.Write($"Clipboard: received {Encoding.UTF8.GetByteCount(text)} bytes of text from the Mac");
            });
        }
        else if (ClipboardPayload.TryReadImage(payload, MaxImageBytes, out var png))
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (!_enabled || _disposed)
                    return;
                BitmapSource image;
                try
                {
                    var decoder = new PngBitmapDecoder(new MemoryStream(png),
                        BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    image = decoder.Frames[0];
                    image.Freeze();
                }
                catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or IOException)
                {
                    DiagLog.Write($"Clipboard: ignored an unreadable picture from the Mac ({ex.GetType().Name})");
                    return;
                }
                var data = new DataObject();
                data.SetImage(image);                               // for most apps
                data.SetData("PNG", new MemoryStream(png), false);  // keeps transparency for apps that read PNG
                if (TrySetClipboard(data))
                    DiagLog.Write($"Clipboard: received a {image.PixelWidth}x{image.PixelHeight} picture ({png.Length} bytes) from the Mac");
            });
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && _enabled && _connected())
        {
            // Read it a moment later: the app that copied may still hold the clipboard open.
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => SendLocal(attempt: 0)));
        }
        return IntPtr.Zero;
    }

    private void SendLocal(int attempt)
    {
        if (!_enabled || !_connected() || _disposed)
            return;
        if (_ownWriteSequence != 0 && GetClipboardSequenceNumber() == _ownWriteSequence)
            return;   // still our own write of the Mac's copy
        try
        {
            // Password managers and similar tools set these to ask clipboard tools to look away.
            if (System.Windows.Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing") ||
                System.Windows.Clipboard.ContainsData("Clipboard Viewer Ignore"))
            {
                DiagLog.Write("Clipboard: skipped a copy marked private (e.g. a password manager)");
                return;
            }
            if (System.Windows.Clipboard.ContainsText())
            {
                SendText();
                return;
            }
            if (System.Windows.Clipboard.ContainsFileDropList())
                return;   // copied files: not synced
            if (System.Windows.Clipboard.ContainsData("PNG") || System.Windows.Clipboard.ContainsImage())
                SendImage();
        }
        catch (COMException) when (attempt < 5)
        {
            // Clipboard busy (another app has it open): try again shortly.
            RetryLater(() => SendLocal(attempt + 1));
        }
        catch (Exception ex)
        {
            DiagLog.Write($"Clipboard: could not read the clipboard: {ex.Message}");
        }
    }

    private void SendText()
    {
        var text = System.Windows.Clipboard.GetText();
        if (string.IsNullOrEmpty(text))
            return;
        if (text == _lastRemoteText)
            return;   // our own write of the Mac's text
        var payload = ClipboardPayload.Text(text);
        if (payload.Length - 1 > MaxTextBytes)
        {
            DiagLog.Write($"Clipboard: skipped {payload.Length - 1} bytes of text (limit {MaxTextBytes})");
            return;
        }
        _send(payload);
        DiagLog.Write($"Clipboard: sent {payload.Length - 1} bytes of text to the Mac");
    }

    private void SendImage()
    {
        // Prefer the PNG that browsers and screenshot tools put on the clipboard (keeps
        // transparency); otherwise encode the bitmap.
        byte[]? png = null;
        if (System.Windows.Clipboard.ContainsData("PNG") &&
            System.Windows.Clipboard.GetData("PNG") is MemoryStream stream)
        {
            var bytes = stream.ToArray();
            if (ClipboardPayload.IsPng(bytes))
                png = bytes;
        }
        if (png == null && System.Windows.Clipboard.GetImage() is { } bitmap)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new MemoryStream();
            encoder.Save(output);
            png = output.ToArray();
        }
        if (png == null)
            return;
        if (png.Length > MaxImageBytes)
        {
            DiagLog.Write($"Clipboard: skipped a {png.Length / 1024 / 1024} MB picture (limit {MaxImageBytes / 1024 / 1024} MB)");
            return;
        }
        _send(ClipboardPayload.Image(png));
        DiagLog.Write($"Clipboard: sent a picture ({png.Length} bytes) to the Mac");
    }

    private bool TrySetClipboard(DataObject data, int attempt = 0)
    {
        try
        {
            // Keep it on this PC: Windows must not upload the Mac's copy to the cloud clipboard.
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
            System.Windows.Clipboard.SetDataObject(data, copy: true);
            _ownWriteSequence = GetClipboardSequenceNumber();
            return true;
        }
        catch (COMException) when (attempt < 5)
        {
            RetryLater(() => TrySetClipboard(data, attempt + 1));
            return false;
        }
        catch (Exception ex)
        {
            DiagLog.Write($"Clipboard: could not set the clipboard: {ex.Message}");
            return false;
        }
    }

    private void RetryLater(Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    private void StartListening()
    {
        if (_listening || _hwnd == IntPtr.Zero)
            return;
        _listening = AddClipboardFormatListener(_hwnd);
        if (!_listening)
            DiagLog.Write($"Clipboard: AddClipboardFormatListener failed ({Marshal.GetLastWin32Error()})");
    }

    private void StopListening()
    {
        if (!_listening)
            return;
        RemoveClipboardFormatListener(_hwnd);
        _listening = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopListening();
        _source?.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
