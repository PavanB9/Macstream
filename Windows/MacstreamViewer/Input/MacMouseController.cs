using System.Buffers.Binary;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using MacstreamViewer.Diagnostics;
using MacstreamViewer.Protocol;

namespace MacstreamViewer.Input;

/// <summary>
/// Windows -> Mac mouse control. While the pointer is over the video picture, moves, clicks,
/// drags and wheel scrolling are sent to the Mac as INPUT_EVENT packets.
///
/// Coordinates are sent as fractions of the picture (the same letterbox math the renderers
/// use), so they are independent of window size and DPI. Nothing is sent from the black bars,
/// except while a button is held (a drag may leave the picture; it is clamped to the edge).
///
/// Stuck-button safety: every button pressed here is released on the Mac when the button comes
/// up, when this window loses focus or mouse capture, or when control is turned off. A 250 ms
/// heartbeat lets the Mac release buttons by itself if the PC stops talking (cable pulled).
///
/// The Mac's own screen: while the picture is an extra Mac display and OwnScreen names the
/// side the MacBook is on, pushing the pointer against that edge of the viewer's video area
/// (full screen, maximized or a window) moves it onto the MacBook's own screen. The
/// Windows pointer is then held inside the viewer and its movement is sent as relative moves
/// until the Mac says it came back over that edge. Focus loss and control off end it too.
/// </summary>
public sealed class MacMouseController
{
    private const int WmMouseHWheel = 0x020E;
    private const int WheelDelta = 120;

    private readonly Window _window;
    private readonly FrameworkElement _surface;
    private readonly IntPtr _windowHandle;
    private readonly Func<(int Width, int Height)> _videoSize;
    private readonly Func<bool> _canSend;
    private readonly Action<byte[]> _send;
    private readonly DispatcherTimer _heartbeat;
    private readonly bool[] _held = new bool[3];
    private readonly byte[] _clickCounts = new byte[3];
    private double _scrollPixelsPerNotch;
    private int _scrollSign;
    private readonly bool _hideLocalCursor;
    private double _scrollRemainderX;
    private double _scrollRemainderY;
    private Point? _lastSentPosition;
    private bool _enabled = true;
    private bool _cursorHidden;

    // The Mac's own screen (see the class comment).
    private bool _onOwnScreen;
    private DateTime? _enterRequestedAt;
    private bool _edgeArmed = true;
    private EdgeZone? _zone;
    private ScreenRect? _barrier;
    private (int X, int Y) _center;
    private bool _loggedOwnScreenUnavailable;

    // Diagnostics: what was queued for the Mac, logged every 2 s while it is non-zero, so
    // diag.log shows whether input leaves this PC at all.
    private int _queuedMoves;
    private int _queuedButtons;
    private int _queuedScrolls;
    private int _heartbeatTicks;
    private DateTime _lastNotSentLog = DateTime.MinValue;

    public event EventHandler<bool>? EnabledChanged;

    /// <param name="window">Owning window (focus loss releases buttons; horizontal wheel hook).</param>
    /// <param name="surface">Element covering the video area; receives the mouse events.</param>
    /// <param name="videoSize">Current video size in pixels, (0, 0) when there is no picture yet.</param>
    /// <param name="canSend">True while connected and showing video.</param>
    /// <param name="send">Queues one INPUT_EVENT payload.</param>
    public MacMouseController(Window window, FrameworkElement surface, IntPtr windowHandle,
        Func<(int Width, int Height)> videoSize, Func<bool> canSend, Action<byte[]> send)
    {
        _window = window;
        _surface = surface;
        _windowHandle = windowHandle;
        _videoSize = videoSize;
        _canSend = canSend;
        _send = send;
        _scrollPixelsPerNotch = ReadDouble("MACSTREAM_SCROLL_PX", DefaultScrollPixelsPerNotch, 1, 1000);
        _scrollSign = EnvVars.Get("MACSTREAM_SCROLL_INVERT") == "1" ? -1 : 1;
        _hideLocalCursor = EnvVars.Get("MACSTREAM_SHOW_LOCAL_CURSOR") != "1";

        surface.MouseMove += OnMouseMove;
        surface.MouseDown += OnMouseDown;
        surface.MouseUp += OnMouseUp;
        surface.MouseWheel += OnMouseWheel;
        surface.MouseLeave += (_, _) =>
        {
            if (!AnyHeld && !_onOwnScreen)
                ShowCursor();
            ClearBarrier();
        };
        surface.LostMouseCapture += (_, _) => ReleaseAll("mouse capture lost");
        window.Deactivated += (_, _) =>
        {
            ReleaseAll("window lost focus");
            LeaveOwnScreen("window lost focus", place: null);
            ClearBarrier();
        };
        HwndSource.FromHwnd(windowHandle)?.AddHook(WndProc);

        _heartbeat = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
        _heartbeat.Tick += (_, _) =>
        {
            if (Active)
                _send(MacInputPayload.Heartbeat());
            if (++_heartbeatTicks % 8 == 0)
                LogQueuedInput();
        };
    }

    public const double DefaultScrollPixelsPerNotch = 50;

    /// <summary>Where the MacBook's own screen is (off by default).</summary>
    public OwnScreenOptions OwnScreen
    {
        get => _ownScreen;
        set
        {
            _ownScreen = value;
            ClearBarrier();
            if (value.Side == MacSide.Off)
                LeaveOwnScreen("turned off", place: null);
        }
    }
    private OwnScreenOptions _ownScreen = OwnScreenOptions.Off;

    /// <summary>True while the picture is an extra Mac display (from the Mac's stats).</summary>
    public Func<bool> ExtraDisplayActive { get; set; } = () => false;

    /// <summary>True while the pointer is on the Mac's own screen.</summary>
    public bool OnOwnScreen => _onOwnScreen;

    /// <summary>Wheel direction on the Mac: true = reversed. Default from MACSTREAM_SCROLL_INVERT.</summary>
    public bool ScrollInverted
    {
        get => _scrollSign < 0;
        set => _scrollSign = value ? -1 : 1;
    }

    /// <summary>Pixels scrolled on the Mac per wheel notch (1-1000). Default from MACSTREAM_SCROLL_PX.</summary>
    public double ScrollPixelsPerNotch
    {
        get => _scrollPixelsPerNotch;
        set
        {
            _scrollPixelsPerNotch = Math.Clamp(value, 1, 1000);
            _scrollRemainderX = 0;
            _scrollRemainderY = 0;
        }
    }

    /// <summary>User switch for controlling the Mac (on by default).</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            if (!value)
            {
                ReleaseAll("control turned off");
                LeaveOwnScreen("control turned off", place: null);
                ClearBarrier();
            }
            _enabled = value;
            if (!value)
                ShowCursor();
            _lastSentPosition = null;
            DiagLog.Write($"Mac control {(value ? "on" : "off")}");
            EnabledChanged?.Invoke(this, value);
        }
    }

    private bool Active => _enabled && _canSend();

    private bool AnyHeld => _held[0] || _held[1] || _held[2];

    /// <summary>Call when a connection is established.</summary>
    public void OnConnected()
    {
        ResetLocalState();
        _heartbeat.Start();
    }

    /// <summary>Call when the connection ends. The Mac releases held buttons on disconnect.</summary>
    public void OnDisconnected()
    {
        _heartbeat.Stop();
        LeaveOwnScreen("disconnected", place: null);
        ClearBarrier();
        ResetLocalState();
        if (_surface.IsMouseCaptured)
            _surface.ReleaseMouseCapture();
        ShowCursor();
    }

    private void ResetLocalState()
    {
        Array.Clear(_held);
        Array.Clear(_clickCounts);
        _scrollRemainderX = 0;
        _scrollRemainderY = 0;
        _lastSentPosition = null;
        _enterRequestedAt = null;
        _edgeArmed = true;
    }

    // --- Coordinate mapping -----------------------------------------------------------------

    /// <summary>
    /// Maps a point on the surface to fractions of the video picture (clamped to 0..1).
    /// <paramref name="inside"/> says whether the point is on the picture itself.
    /// </summary>
    private bool TryMap(Point p, out float x, out float y, out bool inside)
    {
        x = y = 0;
        inside = false;
        var (videoWidth, videoHeight) = _videoSize();
        var hostWidth = _surface.ActualWidth;
        var hostHeight = _surface.ActualHeight;
        if (videoWidth <= 0 || videoHeight <= 0 || hostWidth <= 0 || hostHeight <= 0)
            return false;

        var scale = Math.Min(hostWidth / videoWidth, hostHeight / videoHeight);
        var pictureWidth = videoWidth * scale;
        var pictureHeight = videoHeight * scale;
        var fx = (p.X - (hostWidth - pictureWidth) / 2) / pictureWidth;
        var fy = (p.Y - (hostHeight - pictureHeight) / 2) / pictureHeight;
        if (!double.IsFinite(fx) || !double.IsFinite(fy))
            return false;

        inside = fx >= 0 && fx < 1 && fy >= 0 && fy < 1;
        x = (float)Math.Clamp(fx, 0, 1);
        y = (float)Math.Clamp(fy, 0, 1);
        return true;
    }

    private static int ButtonIndex(MouseButton button) => button switch
    {
        MouseButton.Left => 0,
        MouseButton.Right => 1,
        MouseButton.Middle => 2,
        _ => -1,   // side buttons are not forwarded
    };

    // --- Event handlers ---------------------------------------------------------------------

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!Active)
        {
            LeaveOwnScreen("not connected", place: null);
            ClearBarrier();
            return;
        }
        if (_onOwnScreen)
        {
            MoveOnOwnScreen();
            return;
        }
        if (CheckOwnScreenEdge())
            return;

        var position = e.GetPosition(_surface);
        if (!TryMap(position, out var x, out var y, out var inside) || (!inside && !AnyHeld))
        {
            ShowCursor();
            return;
        }

        if (inside)
            HideCursor();
        else
            ShowCursor();

        if (_lastSentPosition is { } last &&
            Math.Abs(position.X - last.X) < 0.25 && Math.Abs(position.Y - last.Y) < 0.25)
            return;

        _lastSentPosition = position;
        _send(MacInputPayload.MouseMove(x, y));
        _queuedMoves++;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Active)
        {
            LogNotSent(e.ChangedButton);
            return;
        }

        var index = ButtonIndex(e.ChangedButton);
        if (index < 0 || _held[index])
            return;

        if (!TryMap(e.GetPosition(_surface), out var x, out var y, out var inside) || !inside)
            return;

        _held[index] = true;
        _clickCounts[index] = (byte)Math.Clamp(e.ClickCount, 1, 3);
        _send(MacInputPayload.MouseButton(down: true, (byte)index, _clickCounts[index], x, y));
        _queuedButtons++;
        HideCursor();

        // Keep receiving the drag even if the pointer leaves the window.
        if (!_surface.IsMouseCaptured)
            _surface.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        var index = ButtonIndex(e.ChangedButton);
        if (index < 0 || !_held[index])
            return;

        _held[index] = false;
        if (_canSend() && TryMap(e.GetPosition(_surface), out var x, out var y, out _))
            _send(MacInputPayload.MouseButton(down: false, (byte)index, _clickCounts[index], x, y));
        else if (_canSend())
            _send(MacInputPayload.ReleaseAll());

        if (!AnyHeld && _surface.IsMouseCaptured)
            _surface.ReleaseMouseCapture();
        e.Handled = true;
    }

    // --- The Mac's own screen ------------------------------------------------------------------

    private ScreenRect SurfaceScreenRect()
    {
        var topLeft = _surface.PointToScreen(new Point(0, 0));   // physical pixels
        var bottomRight = _surface.PointToScreen(new Point(_surface.ActualWidth, _surface.ActualHeight));
        return new ScreenRect((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y),
            (int)Math.Round(bottomRight.X), (int)Math.Round(bottomRight.Y));
    }

    private bool TryCurrentZone(out EdgeZone zone)
    {
        zone = default;
        return _ownScreen.Side != MacSide.Off && ExtraDisplayActive() && _window.IsActive
            && PresentationSource.FromVisual(_surface) != null
            && MacOwnScreenEdge.TryGetZone(_windowHandle, SurfaceScreenRect(), _ownScreen, out zone);
    }

    /// <summary>On a normal move: holds the pointer at the Mac's part of the edge (so it can't
    /// slip past the viewer there) and asks the Mac to take it once it reaches the edge.
    /// Returns true while that request is waiting for an answer: no position on the picture
    /// may be sent then, because the Mac takes any such position as "the mouse is back on the
    /// streamed display" and would undo the move straight away (in full screen the edge pixel
    /// is on the picture, so this used to happen every time).</summary>
    private bool CheckOwnScreenEdge()
    {
        if (_enterRequestedAt is { } asked && DateTime.UtcNow - asked > TimeSpan.FromSeconds(1))
            _enterRequestedAt = null;   // no answer: an older Mac app, or it's busy
        if (!TryCurrentZone(out var zone) || !MacOwnScreenEdge.TryGetCursor(out var x, out var y))
        {
            ClearBarrier();
            return _enterRequestedAt != null;
        }

        var distance = zone.DistanceFromEdge(x, y);
        // Only from the inside (during a drag the pointer can be outside the viewer, and
        // clipping would pull it back).
        var inZone = distance >= 0 && zone.Contains(x, y);
        if (inZone)
            SetBarrier(zone.Barrier(MacOwnScreenEdge.Desktop()));
        else
            ClearBarrier();

        if (distance >= 4)
            _edgeArmed = true;
        if (_enterRequestedAt != null)
            return true;
        if (distance > 0 || !inZone || !_edgeArmed)
            return false;

        _edgeArmed = false;
        _zone = zone;
        _enterRequestedAt = DateTime.UtcNow;
        _send(MacInputPayload.OwnScreenEnter(_ownScreen.WireSide, zone.Along(x, y)));
        return true;
    }

    private void SetBarrier(ScreenRect rect)
    {
        if (_barrier is { } current && current.Equals(rect))
            return;
        MacOwnScreenEdge.Clip(rect);
        _barrier = rect;
    }

    private void ClearBarrier()
    {
        if (_barrier == null || _onOwnScreen)
            return;
        MacOwnScreenEdge.Unclip();
        _barrier = null;
    }

    /// <summary>Hold the Windows pointer in the middle of the viewer; each move away from
    /// there is sent to the Mac (in picture pixels) and the pointer is put back.</summary>
    private void BeginOwnScreen()
    {
        _onOwnScreen = true;
        _enterRequestedAt = null;
        var surface = SurfaceScreenRect();
        _center = ((surface.Left + surface.Right) / 2, (surface.Top + surface.Bottom) / 2);
        MacOwnScreenEdge.Clip(surface);
        _barrier = surface;
        MacOwnScreenEdge.SetCursor(_center.X, _center.Y);
        HideCursor();
        DiagLog.Write($"Mouse moved onto the Mac's own screen ({_ownScreen}, zone {_zone})");
    }

    private void MoveOnOwnScreen()
    {
        if (!MacOwnScreenEdge.TryGetCursor(out var x, out var y))
            return;
        int dx = x - _center.X, dy = y - _center.Y;
        if (dx == 0 && dy == 0)
            return;
        MacOwnScreenEdge.SetCursor(_center.X, _center.Y);
        var perPixel = PhysicalPixelsPerVideoPixel();
        _send(MacInputPayload.RelativeMove((float)(dx / perPixel), (float)(dy / perPixel)));
        _queuedMoves++;
    }

    private double PhysicalPixelsPerVideoPixel()
    {
        var (videoWidth, videoHeight) = _videoSize();
        if (videoWidth <= 0 || videoHeight <= 0 || _surface.ActualWidth <= 0 || _surface.ActualHeight <= 0)
            return 1;
        var scale = Math.Min(_surface.ActualWidth / videoWidth, _surface.ActualHeight / videoHeight);
        var dpi = VisualTreeHelper.GetDpi(_surface).DpiScaleX;
        var value = scale * dpi;
        return double.IsFinite(value) && value > 0.05 ? value : 1;
    }

    /// <summary>Gives the pointer back to Windows, optionally at a screen point.</summary>
    private void LeaveOwnScreen(string reason, (int X, int Y)? place)
    {
        _enterRequestedAt = null;
        if (!_onOwnScreen)
            return;
        _onOwnScreen = false;
        MacOwnScreenEdge.Unclip();
        _barrier = null;
        if (place is { } p)
            MacOwnScreenEdge.SetCursor(p.X, p.Y);
        _edgeArmed = false;
        _lastSentPosition = null;
        DiagLog.Write($"Mouse back from the Mac's own screen ({reason})");
    }

    /// <summary>INPUT_FEEDBACK from the Mac. Call on the UI thread.</summary>
    public void HandleFeedback(byte[] payload)
    {
        if (payload.Length == 0)
            return;
        switch (payload[0])
        {
            case 0x01:   // entered the Mac's own screen
                if (_enterRequestedAt != null && Active)
                    BeginOwnScreen();
                break;
            case 0x02:   // no own screen (lid closed)
                _enterRequestedAt = null;
                if (!_loggedOwnScreenUnavailable)
                {
                    _loggedOwnScreenUnavailable = true;
                    DiagLog.Write("The Mac has no own screen to move the mouse to (lid closed?)");
                }
                break;
            case 0x03 when payload.Length == 5:   // back over the edge, f32 along
            {
                var along = BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(1));
                if (!float.IsFinite(along))
                    along = 0.5f;
                var zone = TryCurrentZone(out var now) ? now : _zone;
                LeaveOwnScreen("came back over the edge", zone?.PointAt(along, inset: 4));
                break;
            }
            case 0x04 when payload.Length == 9:   // the pointer is on the extra display at x, y
            {
                var fx = BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(1));
                var fy = BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(5));
                LeaveOwnScreen("moved onto the extra display on the Mac", PicturePointOnScreen(fx, fy));
                break;
            }
        }
    }

    /// <summary>Screen point (physical pixels) of a 0..1 position on the picture.</summary>
    private (int X, int Y)? PicturePointOnScreen(float fx, float fy)
    {
        var (videoWidth, videoHeight) = _videoSize();
        if (!float.IsFinite(fx) || !float.IsFinite(fy) || videoWidth <= 0 || videoHeight <= 0
            || PresentationSource.FromVisual(_surface) == null)
            return null;
        var scale = Math.Min(_surface.ActualWidth / videoWidth, _surface.ActualHeight / videoHeight);
        var pictureWidth = videoWidth * scale;
        var pictureHeight = videoHeight * scale;
        var local = new Point(
            (_surface.ActualWidth - pictureWidth) / 2 + Math.Clamp(fx, 0, 1) * pictureWidth,
            (_surface.ActualHeight - pictureHeight) / 2 + Math.Clamp(fy, 0, 1) * pictureHeight);
        var screen = _surface.PointToScreen(local);
        return ((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Active || !TryMap(e.GetPosition(_surface), out _, out _, out var inside) || !inside)
            return;

        _scrollRemainderY += e.Delta * _scrollPixelsPerNotch / WheelDelta * _scrollSign;
        var dy = (int)Math.Truncate(_scrollRemainderY);
        _scrollRemainderY -= dy;
        if (dy != 0)
        {
            _send(MacInputPayload.Scroll(0, dy));
            _queuedScrolls++;
        }
        e.Handled = true;
    }

    /// <summary>WPF has no horizontal-wheel event; catch WM_MOUSEHWHEEL (tilt wheel, touchpads).</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmMouseHWheel || !Active || !_surface.IsMouseOver)
            return IntPtr.Zero;

        if (!TryMap(Mouse.GetPosition(_surface), out _, out _, out var inside) || !inside)
            return IntPtr.Zero;

        var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);   // > 0 = tilt right
        _scrollRemainderX += delta * _scrollPixelsPerNotch / WheelDelta * _scrollSign;
        var dx = (int)Math.Truncate(_scrollRemainderX);
        _scrollRemainderX -= dx;
        if (dx != 0)
        {
            _send(MacInputPayload.Scroll(dx, 0));
            _queuedScrolls++;
        }
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>Releases every button this side pressed on the Mac.</summary>
    private void ReleaseAll(string reason)
    {
        if (!AnyHeld)
            return;

        Array.Clear(_held);
        if (_canSend())
            _send(MacInputPayload.ReleaseAll());
        if (_surface.IsMouseCaptured)
            _surface.ReleaseMouseCapture();
        DiagLog.Write($"Released Mac mouse buttons ({reason})");
    }

    // --- Diagnostics ----------------------------------------------------------------------

    private void LogQueuedInput()
    {
        if (_queuedMoves == 0 && _queuedButtons == 0 && _queuedScrolls == 0)
            return;
        DiagLog.Write($"Mac input queued (last 2 s): moves={_queuedMoves}, button downs={_queuedButtons}, scrolls={_queuedScrolls}");
        _queuedMoves = _queuedButtons = _queuedScrolls = 0;
    }

    /// <summary>Says once every 2 s why a click on the picture was not sent.</summary>
    private void LogNotSent(MouseButton button)
    {
        var now = DateTime.UtcNow;
        if (now - _lastNotSentLog < TimeSpan.FromSeconds(2))
            return;
        _lastNotSentLog = now;
        DiagLog.Write($"Mac input: {button} click not sent (control switch {(_enabled ? "on" : "off")}, connected and showing video: {_canSend()})");
    }

    // --- Local cursor -------------------------------------------------------------------------
    // The Mac's own pointer is drawn in the video. Hiding the Windows pointer over the picture
    // avoids seeing two (MACSTREAM_SHOW_LOCAL_CURSOR=1 keeps it).

    private void HideCursor()
    {
        if (!_hideLocalCursor)
            return;
        // Set every time: MainWindow's idle-cursor logic may have reset it meanwhile.
        if (_surface.Cursor != Cursors.None)
            _surface.Cursor = Cursors.None;
        _cursorHidden = true;
    }

    private void ShowCursor()
    {
        if (!_cursorHidden)
            return;
        _surface.Cursor = null;
        _cursorHidden = false;
    }

    private static double ReadDouble(string name, double fallback, double min, double max)
    {
        var text = EnvVars.Get(name);
        return double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : fallback;
    }
}
