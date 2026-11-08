using System.Diagnostics;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MacstreamViewer.Decoder;
using MacstreamViewer.Diagnostics;
using MacstreamViewer.Display;
using MacstreamViewer.Input;
using MacstreamViewer.Localization;
using MacstreamViewer.Network;
using MacstreamViewer.Preferences;
using MacstreamViewer.Protocol;
using MacstreamViewer.Rendering;
using MacstreamViewer.Security;
using MacstreamViewer.Sharing;
using MacstreamViewer.Tray;
using MacstreamViewer.Video;

namespace MacstreamViewer;

public partial class MainWindow : Window, ITrayHost, ISettingsHost
{
    private readonly record struct QueuedH264Frame(FramePayload Payload, VideoDecoderCodec Codec, bool SuppressOutput);
    private enum Usb4IpStatusKind
    {
        Detecting,
        Found,
        NotFound,
        Error,
    }

    private const int ReceiveBackpressureHighWatermark = 6;
    private const int ReceiveBackpressureLowWatermark = 2;
    private static readonly int MaxReceiveBackpressureSleepMs = 0;
    private const int EmergencyQueuedH264Frames = 12;
    private const int MaxQueueLatencyMs = 120;
    private const int ResumeQueueLatencyMs = 70;
    private const int MaxQueuedFramesForOutput = 1;
    private const long MacAbsoluteEpochOffsetMs = 978_307_200_000;
    private static readonly TimeSpan LocalCursorIdleTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly FrameReceiver _receiver;
    private readonly FrameRenderer _renderer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IntPtr _windowHandle;
    private readonly DispatcherTimer _localCursorIdleTimer;
    private readonly MacMouseController _macMouse;
    private readonly MacKeyboardController _macKeyboard;
    private TrayIcon? _tray;
    private readonly ClipboardSync _clipboard;
    private readonly ViewerSettings _settings = ViewerSettings.Load();
    private SettingsWindow? _settingsWindow;
    private bool _quitting;
    private H264Decoder? _h264Decoder;
    private readonly ConcurrentQueue<QueuedH264Frame> _h264Queue = new();
    private readonly SemaphoreSlim _h264Signal = new(0);
    private CancellationTokenSource? _decodeCts;
    private Task? _decodeTask;
    private int _queuedH264Frames;
    private volatile bool _dropUntilKeyFrame;
    private volatile bool _resetDecoderOnNextKeyFrame;
    private volatile bool _suppressOutputUntilLowLatency;
    private bool _suppressCurrentDecodeOutput;
    private DateTime _lastCatchUpTime = DateTime.MinValue;

    private int _frameCount;
    private long _totalBytes;
    private DateTime _lastFpsUpdate = DateTime.UtcNow;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private int _displayWidth;
    private int _displayHeight;
    private bool _decoderFailed;
    private bool _hasDecodedFrame;
    private DateTime _lastReceiveStatusUpdate = DateTime.MinValue;
    private string? _lastReceiveError;
    private long _minObservedMacClockOffsetMs = long.MaxValue;
    private DateTime _lastLatencyLogTime = DateTime.MinValue;
    private DateTime _lastBackpressureLogTime = DateTime.MinValue;
    private int _initialH264FrameLogs;
    private int _initialRawFrameLogs;
    private int _renderedFrameCount;
    private bool _isFullscreen;
    private WindowState _previousWindowState = WindowState.Normal;
    private WindowStyle _previousWindowStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _previousResizeMode = ResizeMode.CanResize;
    private VideoDecoderCodec _decoderCodec = VideoDecoderCodec.H264;
    /// <summary>Frame size the current decoder was made for (0 = unknown).</summary>
    private int _decoderInitWidth;
    private int _decoderInitHeight;
    private bool _manualDisconnectRequested;
    private string? _lastHost;
    private int _lastPort;
    private ClientDisplayInfo? _lastDisplayInfo;
    private CancellationTokenSource? _autoReconnectCts;
    private int _autoReconnectAttempt;
    private int _lastLayoutPhysicalWidth;
    private int _lastLayoutPhysicalHeight;
    private double _lastLayoutScale;
    private bool _lastLayoutPixelPerfect;
    private DateTime _lastLayoutLogTime = DateTime.MinValue;
    private CancellationTokenSource? _usb4IpDetectionCts;
    private bool _isLocalCursorOverDisplay;
    private bool _isLocalCursorHidden;
    private Point? _lastLocalCursorPosition;
    private Usb4IpStatusKind _usb4IpStatus = Usb4IpStatusKind.Detecting;
    private string? _usb4IpStatusHost;

    public MainWindow()
    {
        InitializeComponent();
        _windowHandle = new WindowInteropHelper(this).EnsureHandle();
        DiagLog.Write($"app started: {AppContext.BaseDirectory}");

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        _logger = _loggerFactory.CreateLogger<MainWindow>();
        _receiver = new FrameReceiver();
        _renderer = new FrameRenderer(_loggerFactory.CreateLogger<FrameRenderer>(), _windowHandle);

        _receiver.OnH264Frame += (_, frame) => OnCompressedFrame(frame, VideoDecoderCodec.H264);
        _receiver.OnHevcFrame += (_, frame) => OnCompressedFrame(frame, VideoDecoderCodec.Hevc);
        _receiver.OnRawFrame += OnRawFrame;
        _receiver.OnConnectionChanged += OnConnectionChanged;
        _receiver.OnReceiveError += OnReceiveError;
        _receiver.ConfirmPairing = code => Dispatcher.InvokeAsync(() =>
        {
            StatusText.Text = $"Pairing with the Mac. Code: {code}";
            StatusText.Visibility = Visibility.Visible;
            StatusText.Foreground = System.Windows.Media.Brushes.White;
            var answer = MessageBox.Show(this,
                $"Pairing this PC with the Mac.\n\nCode: {code}\n\n"
                + "The Mac should now show the same code. If it does, click Yes here and Allow on the Mac.\n"
                + "If the Mac shows no request or a different code, click No.",
                "Macstream pairing", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            return answer == MessageBoxResult.Yes;
        }).Task;
        _receiver.OnServerStats += (_, stats) => _lastServerStats = stats;
        _receiver.OnPingRoundTrip += (_, ms) => _lastRttMs = ms;
        _statsTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) => OnStatsTick();

        _renderer.OnFrameRendered += OnFrameRendered;
        _renderer.OnImageSourceChanged += OnImageSourceChanged;
        DisplayHost.SizeChanged += (_, _) => UpdateDisplayImageLayout();

        // Native swap-chain video window (low-latency path). Hidden until video is presented,
        // because WPF content (status text, cursor overlay) can't draw on top of it.
        _swapChainHost = new SwapChainVideoHost { Visibility = Visibility.Collapsed };
        DisplayHost.Children.Insert(0, _swapChainHost);
        _renderer.AttachSwapChainHost(_swapChainHost);
        _renderer.SwapChainHostNeeded += (_, _) =>
            Dispatcher.BeginInvoke(() => _swapChainHost.Visibility = Visibility.Visible);
        _renderer.SwapChainPresentingChanged += (_, presenting) =>
            Dispatcher.BeginInvoke(() => SetSwapChainPresentation(presenting));
        Loaded += (_, _) => StartUsb4IpAutoDetection();

        _localCursorIdleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = LocalCursorIdleTimeout
        };
        _localCursorIdleTimer.Tick += (_, _) => HideLocalCursorIfIdle();
        DisplayHost.MouseEnter += OnDisplayHostMouseEnter;
        DisplayHost.MouseMove += OnDisplayHostMouseMove;
        DisplayHost.MouseLeave += OnDisplayHostMouseLeave;

        // Windows -> Mac mouse control (subscribed after the idle-cursor handlers above).
        _macMouse = new MacMouseController(
            this,
            DisplayHost,
            _windowHandle,
            videoSize: () => (_displayWidth, _displayHeight),
            canSend: () => _receiver.IsConnected && _hasDecodedFrame,
            send: _receiver.SendInput);
        _macMouse.ExtraDisplayActive = () => _lastServerStats is { ExtraDisplay: true };
        _receiver.OnInputFeedback += (_, payload) => Dispatcher.BeginInvoke(() => _macMouse.HandleFeedback(payload));
        // Windows -> Mac keyboard control: same on/off switch, only while this window has focus.
        _macKeyboard = new MacKeyboardController(
            this,
            _windowHandle,
            forwarding: () => _macMouse.Enabled && _receiver.IsConnected && _hasDecodedFrame,
            toggleControl: () => _macMouse.Enabled = !_macMouse.Enabled,
            toggleFullscreen: ToggleFullscreen,
            send: _receiver.SendInput);
        _macMouse.EnabledChanged += (_, on) =>
        {
            if (!on)
                _macKeyboard.ReleaseAll("control turned off");
            UpdateMacControlButtonText();
            _tray?.Refresh();
        };
        UpdateMacControlButtonText();
        ApplySavedInputSettings();
        _receiver.Screen = SavedScreenRequest();
        DiagLog.Write($"Mac screen setting: {_receiver.Screen}");
        _clipboard = new ClipboardSync(this, _windowHandle,
            enabled: _settings.ClipboardSync,
            connected: () => _receiver.IsConnected,
            send: _receiver.SendClipboard);
        _receiver.OnClipboard += (_, payload) => _clipboard.HandleRemote(payload);
        _clipboard.EnabledChanged += (_, on) =>
        {
            _settings.ClipboardSync = on;
            _settings.Save();
            UpdateClipboardButtonText();
            _tray?.Refresh();
        };
        UpdateClipboardButtonText();
        ApplyUiText();
        _tray = new TrayIcon(this);
        Icon = TrayIcon.CreateWindowIcon();
    }

    // ---- Settings window (Preferences/SettingsWindow.cs), saved in viewer.json ----

    /// <summary>Settings saved in viewer.json win over the MACSTREAM_* variables.</summary>
    private void ApplySavedInputSettings()
    {
        if (_settings.ScrollInvert is { } invert)
            _macMouse.ScrollInverted = invert;
        if (_settings.ScrollPixelsPerNotch is { } px)
            _macMouse.ScrollPixelsPerNotch = px;
        _macMouse.OwnScreen = SavedOwnScreen();
        foreach (var (key, target) in new[]
                 {
                     ("ctrl", _settings.CtrlKey), ("rightctrl", _settings.RightCtrlKey),
                     ("alt", _settings.AltKey), ("win", _settings.WinKey),
                 })
        {
            if (target != null)
                _macKeyboard.SetModifierTarget(key, target);
        }
        DiagLog.Write($"Input settings: scroll {(_macMouse.ScrollInverted ? "reversed" : "normal")}, "
            + $"{_macMouse.ScrollPixelsPerNotch:0} px per notch; keys {_macKeyboard.ModifierDescription}; "
            + $"MacBook screen {_macMouse.OwnScreen}");
    }

    private OwnScreenOptions SavedOwnScreen()
    {
        var side = _settings.OwnScreenSide switch
        {
            "left" => MacSide.Left,
            "right" => MacSide.Right,
            "above" => MacSide.Above,
            "below" => MacSide.Below,
            _ => MacSide.Off,
        };
        var part = _settings.OwnScreenEdgePart switch
        {
            "whole" => EdgePart.Whole,
            "first" => EdgePart.FirstHalf,
            "second" => EdgePart.SecondHalf,
            _ => EdgePart.Automatic,
        };
        return new OwnScreenOptions(side, part);
    }

    OwnScreenOptions ISettingsHost.OwnScreen
    {
        get => _macMouse.OwnScreen;
        set
        {
            _macMouse.OwnScreen = value;
            _settings.OwnScreenSide = value.Side switch
            {
                MacSide.Left => "left",
                MacSide.Right => "right",
                MacSide.Above => "above",
                MacSide.Below => "below",
                _ => null,
            };
            _settings.OwnScreenEdgePart = value.Part switch
            {
                EdgePart.Whole => "whole",
                EdgePart.FirstHalf => "first",
                EdgePart.SecondHalf => "second",
                _ => null,
            };
            _settings.Save();
            DiagLog.Write($"MacBook screen setting changed: {value}");
        }
    }

    private void ShowSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        if (IsVisible && WindowState != WindowState.Minimized)
            _settingsWindow.Owner = this;
        else
            _settingsWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettings();

    void ITrayHost.ShowSettings() => ShowSettings();

    bool ISettingsHost.ClipboardSyncEnabled
    {
        get => _clipboard.Enabled;
        set => _clipboard.Enabled = value;   // saved by EnabledChanged
    }

    bool ISettingsHost.ScrollInverted
    {
        get => _macMouse.ScrollInverted;
        set
        {
            _macMouse.ScrollInverted = value;
            _settings.ScrollInvert = value;
            _settings.Save();
        }
    }

    double ISettingsHost.ScrollPixelsPerNotch
    {
        get => _macMouse.ScrollPixelsPerNotch;
        set
        {
            _macMouse.ScrollPixelsPerNotch = value;
            _settings.ScrollPixelsPerNotch = _macMouse.ScrollPixelsPerNotch;
            _settings.Save();
        }
    }

    private MacScreenRequest SavedScreenRequest() => new(
        _settings.ExtraDisplay == true,
        _settings.ExtraDisplayWidth ?? 0,
        _settings.ExtraDisplayHeight ?? 0,
        _settings.ExtraDisplayTextScale ?? 0);

    MacScreenRequest ISettingsHost.MacScreen
    {
        get => _receiver.Screen;
        set
        {
            if (value == _receiver.Screen)
                return;
            _receiver.Screen = value;
            _settings.ExtraDisplay = value.ExtraDisplay;
            _settings.ExtraDisplayWidth = value.Width;
            _settings.ExtraDisplayHeight = value.Height;
            _settings.ExtraDisplayTextScale = value.TextScale;
            _settings.Save();
            DiagLog.Write($"Mac screen setting changed: {value}");
            // The Mac reads this when the viewer connects: reconnect now (auto reconnect
            // takes over after a short pause, as after any dropped connection).
            if (_receiver.IsConnected)
            {
                _macKeyboard.ReleaseAll("Mac screen changed");
                _receiver.SendInput(MacInputPayload.ReleaseAll());
                _receiver.Disconnect();
            }
        }
    }

    (int Width, int Height) ISettingsHost.MonitorSize
    {
        get
        {
            var info = WindowsDisplayInfo.FromWindow(this);
            return (info.Width, info.Height);
        }
    }

    string ISettingsHost.ModifierTarget(string windowsKey) => _macKeyboard.ModifierTarget(windowsKey);

    void ISettingsHost.SetModifierTarget(string windowsKey, string macKey)
    {
        _macKeyboard.SetModifierTarget(windowsKey, macKey);
        switch (windowsKey)
        {
            case "ctrl": _settings.CtrlKey = macKey; break;
            case "rightctrl": _settings.RightCtrlKey = macKey; break;
            case "alt": _settings.AltKey = macKey; break;
            case "win": _settings.WinKey = macKey; break;
        }
        _settings.Save();
    }

    string ISettingsHost.PairingDescription
    {
        get
        {
            var store = PairingStore.Shared;
            if (store.LoadError != null)
                return "The pairing file can't be read (damaged, or made by another Windows user), so Macstream "
                    + "won't connect. Click Forget this Mac to start over.";
            return store.MacFingerprint is { } fingerprint
                ? $"Paired with your Mac. Its key: {fingerprint} (the Mac's menu bar shows the same)."
                : "Not paired yet. The first connection shows a 6-digit code on both computers to confirm.";
        }
    }

    bool ISettingsHost.CanForgetPairing => PairingStore.Shared.LoadError != null || PairingStore.Shared.MacPublic != null;

    void ISettingsHost.ForgetPairing()
    {
        _manualDisconnectRequested = true;
        _autoReconnectCts?.Cancel();
        if (_receiver.IsConnected)
        {
            _macKeyboard.ReleaseAll("forget pairing");
            _receiver.SendInput(MacInputPayload.ReleaseAll());
            _receiver.Disconnect();
        }
        PairingStore.Shared.Forget();
    }

    // ---- Tray icon (Tray/TrayIcon.cs) ----

    bool ITrayHost.IsConnected => _receiver.IsConnected;
    bool ITrayHost.CanConnect => ConnectButton.IsEnabled;
    string? ITrayHost.ConnectedHost => _receiver.IsConnected ? _lastHost : null;
    bool ITrayHost.MacControlEnabled => _macMouse.Enabled;
    bool ITrayHost.IsFullscreen => _isFullscreen;
    bool ITrayHost.ClipboardSyncEnabled => _clipboard.Enabled;
    void ITrayHost.ToggleClipboardSync() => _clipboard.Enabled = !_clipboard.Enabled;

    private void ClipboardButton_Click(object sender, RoutedEventArgs e)
    {
        _clipboard.Enabled = !_clipboard.Enabled;
    }

    private void UpdateClipboardButtonText()
    {
        ClipboardButton.Content = _clipboard.Enabled ? "Clipboard: On" : "Clipboard: Off";
        ClipboardButton.Background = _clipboard.Enabled
            ? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32))
            : new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
    }

    void ITrayHost.ShowViewer()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            SystemCommands.RestoreWindow(this);
        Activate();
    }

    void ITrayHost.Connect()
    {
        if (!_receiver.IsConnected && ConnectButton.IsEnabled)
            ConnectButton_Click(this, new RoutedEventArgs());
    }

    void ITrayHost.Disconnect()
    {
        if (_receiver.IsConnected)
            DisconnectButton_Click(this, new RoutedEventArgs());
    }

    void ITrayHost.ToggleMacControl() => _macMouse.Enabled = !_macMouse.Enabled;

    void ITrayHost.ToggleFullscreen()
    {
        ((ITrayHost)this).ShowViewer();
        ToggleFullscreen();
    }

    /// <summary>
    /// Releases anything held on the Mac, stops the other Macstream processes on this PC (a
    /// second viewer, NetworkTest, build helpers) and closes the viewer.
    /// </summary>
    void ITrayHost.QuitEverything()
    {
        // Run after the tray menu has finished handling the click (closing disposes the menu).
        Dispatcher.BeginInvoke(() =>
        {
            if (_quitting)
                return;
            _quitting = true;
            _manualDisconnectRequested = true;
            _autoReconnectCts?.Cancel();
            DiagLog.Write("Quit everything (tray)");
            _macKeyboard.ReleaseAll("quit everything");
            if (_receiver.IsConnected)
                _receiver.SendInput(MacInputPayload.ReleaseAll());
            MacstreamProcesses.StopOthers();
            Close();
            Application.Current.Shutdown();
        });
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var host = HostTextBox.Text.Trim();
        var port = ParsePortOrDefault();

        Console.WriteLine($"[UI] Connect clicked: {host}:{port}");
        DiagLog.Write($"UI connect request: {host}:{port}");
        _manualDisconnectRequested = false;
        _autoReconnectCts?.Cancel();
        ConnectButton.IsEnabled = false;
        StatusText.Text = AppText.Connecting(host, port);

        try
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                StatusText.Text = AppText.DetectingMacUsb4Ip;
                var detected = await Usb4IpDetector.DetectAsync(port, fullSweep: true);
                if (detected == null)
                    throw new InvalidOperationException(AppText.MacUsb4IpNotDetected);

                ApplyDetectedUsb4Ip(detected);
                host = detected.Host;
            }

            var displayInfo = WindowsDisplayInfo.FromWindow(this);
            DiagLog.Write($"connecting with display info: {displayInfo.Width}x{displayInfo.Height}@{displayInfo.RefreshRate}, dpi={displayInfo.Dpi}");
            await ConnectToServerAsync(host, port, displayInfo, isAutoReconnect: false);
            Console.WriteLine("[UI] ConnectAsync returned successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UI] connect error: {ex}");
            DiagLog.Write(ex, "connect error");
            MessageBox.Show(AppText.ConnectionFailed(ex.Message), AppText.ErrorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            ConnectButton.IsEnabled = true;
            StatusText.Text = AppText.ConnectionFailedShort;
        }
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        _manualDisconnectRequested = true;
        _autoReconnectCts?.Cancel();
        _receiver.Disconnect();
    }

    private async Task ConnectToServerAsync(string host, int port, ClientDisplayInfo displayInfo, bool isAutoReconnect)
    {
        _lastHost = host;
        _lastPort = port;
        _lastDisplayInfo = displayInfo;
        if (isAutoReconnect)
            DiagLog.Write($"auto reconnect started: {host}:{port}, {displayInfo.Width}x{displayInfo.Height}@{displayInfo.RefreshRate}, dpi={displayInfo.Dpi}");

        try
        {
            await _receiver.ConnectAsync(host, port, displayInfo);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MacstreamAuthException && IsLinkLocalAddress(host))
        {
            // Self-assigned 169.254.x.x addresses change when either machine restarts. Instead of
            // failing, look for the Mac on the direct link and connect to wherever it is now.
            DiagLog.Write($"Connect to {host} failed ({ex.Message}); searching the direct link for the Mac");
            await Dispatcher.InvokeAsync(() => StatusText.Text = AppText.DetectingMacUsb4Ip);
            var detected = await Usb4IpDetector.DetectAsync(port, preferredHost: null, fullSweep: true);
            if (detected == null || string.Equals(detected.Host, host, StringComparison.Ordinal))
                throw;

            await Dispatcher.InvokeAsync(() => ApplyDetectedUsb4Ip(detected));
            _lastHost = detected.Host;
            await _receiver.ConnectAsync(detected.Host, port, displayInfo);
        }
    }

    private readonly SwapChainVideoHost _swapChainHost;

    private void SetSwapChainPresentation(bool presenting)
    {
        _swapChainHost.Visibility = presenting ? Visibility.Visible : Visibility.Collapsed;
        DisplayImage.Visibility = presenting ? Visibility.Collapsed : Visibility.Visible;
        if (presenting)
            StatusText.Visibility = Visibility.Collapsed;
    }

    private static bool IsLinkLocalAddress(string host) =>
        System.Net.IPAddress.TryParse(host, out var address) &&
        address.GetAddressBytes() is [169, 254, _, _];

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _isFullscreen)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            TopToolbar.Visibility = Visibility.Visible;
            BottomStatusBar.Visibility = Visibility.Visible;
            WindowStyle = _previousWindowStyle;
            ResizeMode = _previousResizeMode;
            WindowState = _previousWindowState;
            _isFullscreen = false;
        }
        else
        {
            _previousWindowState = WindowState;
            _previousWindowStyle = WindowStyle;
            _previousResizeMode = ResizeMode;
            TopToolbar.Visibility = Visibility.Collapsed;
            BottomStatusBar.Visibility = Visibility.Collapsed;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _isFullscreen = true;
            ShowFullscreenHint();
        }

        UpdateFullscreenButtonText();
    }

    private void ApplyUiText()
    {
        Title = AppText.WindowTitle;
        MacAddressLabel.Text = AppText.MacAddressLabel;
        PortLabel.Text = AppText.PortLabel;
        ConnectButton.Content = AppText.ConnectButton;
        DisconnectButton.Content = AppText.DisconnectButton;
        UpdateFullscreenButtonText();
        ConnectionStatus.Text = _receiver.IsConnected ? AppText.Connected : AppText.NotConnected;
        UpdateUsb4IpStatusText();

        if (!_receiver.IsConnected)
        {
            StatusText.Text = _lastReceiveError == null
                ? AppText.NotConnectedPrompt
                : AppText.ReceiveInterrupted(_lastReceiveError);
        }
        else if (!_hasDecodedFrame && StatusText.Visibility == Visibility.Visible)
        {
            StatusText.Text = AppText.WaitingVideoData;
        }
    }

    /// <summary>
    /// While the Mac gets the keyboard, Esc goes to the Mac, so say how to leave full screen.
    /// Shown for a few seconds over the picture, only when it would not hide a status message.
    /// </summary>
    private void ShowFullscreenHint()
    {
        if (!_hasDecodedFrame || !_macMouse.Enabled || StatusText.Visibility == Visibility.Visible)
            return;
        const string hint = "Full screen: press Ctrl+Alt+F11 to leave (Esc goes to the Mac)";
        StatusText.Text = hint;
        StatusText.Foreground = System.Windows.Media.Brushes.White;
        StatusText.Visibility = Visibility.Visible;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (StatusText.Text == hint)
                StatusText.Visibility = Visibility.Collapsed;
        };
        timer.Start();
    }

    private void UpdateFullscreenButtonText()
    {
        FullscreenButton.Content = _isFullscreen ? AppText.ExitFullscreenButton : AppText.FullscreenButton;
    }

    private void MacControlButton_Click(object sender, RoutedEventArgs e)
    {
        _macMouse.Enabled = !_macMouse.Enabled;
    }

    private void UpdateMacControlButtonText()
    {
        MacControlButton.Content = _macMouse.Enabled ? "Mac control: On" : "Mac control: Off";
        MacControlButton.ToolTip = (_macMouse.Enabled
                ? "Mouse and keyboard go to the Mac while this window is focused."
                : "Mouse and keyboard stay on Windows.")
            + $" Ctrl+Alt+F12 switches this on/off. Keys: {_macKeyboard.ModifierDescription}."
            + " Alt+Tab switches Windows apps; Win+Tab switches Mac apps. Ctrl+Alt+F11: full screen on/off.";
        MacControlButton.Background = _macMouse.Enabled
            ? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32))
            : new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
    }

    private void OnDisplayHostMouseEnter(object sender, MouseEventArgs e)
    {
        _isLocalCursorOverDisplay = true;
        ShowLocalCursor();
        RestartLocalCursorIdleTimer(e.GetPosition(DisplayHost));
    }

    private void OnDisplayHostMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(DisplayHost);
        if (!_isLocalCursorHidden && _lastLocalCursorPosition is { } last &&
            Math.Abs(position.X - last.X) < 0.5 &&
            Math.Abs(position.Y - last.Y) < 0.5)
        {
            return;
        }

        _isLocalCursorOverDisplay = true;
        ShowLocalCursor();
        RestartLocalCursorIdleTimer(position);
    }

    private void OnDisplayHostMouseLeave(object sender, MouseEventArgs e)
    {
        _isLocalCursorOverDisplay = false;
        _lastLocalCursorPosition = null;
        _localCursorIdleTimer.Stop();
        ShowLocalCursor();
    }

    private void RestartLocalCursorIdleTimer(Point position)
    {
        _lastLocalCursorPosition = position;
        _localCursorIdleTimer.Stop();
        _localCursorIdleTimer.Start();
    }

    private void HideLocalCursorIfIdle()
    {
        _localCursorIdleTimer.Stop();
        if (!_isLocalCursorOverDisplay)
            return;

        DisplayHost.Cursor = Cursors.None;
        _isLocalCursorHidden = true;
    }

    private void ShowLocalCursor()
    {
        if (!_isLocalCursorHidden)
            return;

        DisplayHost.Cursor = null;
        _isLocalCursorHidden = false;
    }

    private void OnConnectionChanged(object? sender, bool connected)
    {
        Console.WriteLine($"[UI] OnConnectionChanged: {connected}");
        DiagLog.Write($"connection state: {connected}");
        Dispatcher.Invoke(() =>
        {
            if (connected)
            {
                _autoReconnectCts?.Cancel();
                _autoReconnectAttempt = 0;
                _manualDisconnectRequested = false;
                StatusDot.Fill = System.Windows.Media.Brushes.LimeGreen;
                ConnectionStatus.Text = AppText.Connected;
                StatusText.Text = AppText.WaitingVideoData;
                StatusText.Foreground = System.Windows.Media.Brushes.White;
                ConnectButton.IsEnabled = false;
                DisconnectButton.IsEnabled = true;
                _frameCount = 0;
                _totalBytes = 0;
                Interlocked.Exchange(ref _intervalBytes, 0);
                Interlocked.Exchange(ref _renderedSinceTick, 0);
                _lastStatsTick = Stopwatch.GetTimestamp();
                _lastServerStats = null;
                _lastRttMs = null;
                _statsTimer.Start();
                _macMouse.OnConnected();
                _clipboard.OnConnected();
                _displayWidth = 0;
                _displayHeight = 0;
                _decoderFailed = false;
                _hasDecodedFrame = false;
                _renderedFrameCount = 0;
                ClearH264Queue();
                _dropUntilKeyFrame = false;
                _resetDecoderOnNextKeyFrame = false;
                _suppressOutputUntilLowLatency = false;
                _suppressCurrentDecodeOutput = false;
                _lastCatchUpTime = DateTime.MinValue;
                _minObservedMacClockOffsetMs = long.MaxValue;
                _lastLatencyLogTime = DateTime.MinValue;
                _lastBackpressureLogTime = DateTime.MinValue;
                _initialH264FrameLogs = 0;
                _initialRawFrameLogs = 0;
                _decodeCts?.Cancel();
                _decodeCts = new CancellationTokenSource();
                _decodeTask = Task.Run(() => DecodeLoop(_decodeCts.Token));
                _decoderCodec = VideoDecoderCodec.H264;
                _lastReceiveStatusUpdate = DateTime.MinValue;
                _lastReceiveError = null;
                StatusText.Visibility = Visibility.Visible;
            }
            else
            {
                StatusDot.Fill = System.Windows.Media.Brushes.Red;
                ConnectionStatus.Text = AppText.NotConnected;
                StatusText.Text = _lastReceiveError == null
                    ? AppText.NotConnectedPrompt
                    : AppText.ReceiveInterrupted(_lastReceiveError);
                SetSwapChainPresentation(false);
                StatusText.Visibility = Visibility.Visible;
                DisplayImage.Source = null;
                ConnectButton.IsEnabled = true;
                DisconnectButton.IsEnabled = false;
                _statsTimer.Stop();
                _macMouse.OnDisconnected();
                _macKeyboard.OnDisconnected();
                FpsText.Text = "";
                FrameSizeText.Text = "";
                _decodeCts?.Cancel();
                _h264Signal.Release();
                ClearH264Queue();
                _renderer.ResetSwapChain();   // the swap chain uses the decoder's D3D11 device
                _h264Decoder?.Dispose();
                _h264Decoder = null;
            }
            _tray?.Refresh();
        });

        if (!connected && !_manualDisconnectRequested)
            ScheduleAutoReconnect();
    }

    private void ScheduleAutoReconnect()
    {
        if (_manualDisconnectRequested || string.IsNullOrWhiteSpace(_lastHost) || _lastPort <= 0)
            return;

        _autoReconnectCts?.Cancel();
        var reconnectCts = new CancellationTokenSource();
        _autoReconnectCts = reconnectCts;
        var token = reconnectCts.Token;
        var attempt = Math.Min(Interlocked.Increment(ref _autoReconnectAttempt), 10);
        var delayMs = Math.Min(5000, 500 * attempt);
        var host = _lastHost;
        var port = _lastPort;

        DiagLog.Write($"auto reconnect scheduled: attempt #{attempt} in {delayMs}ms, {host}:{port}");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, token);
                if (token.IsCancellationRequested || _manualDisconnectRequested)
                    return;

                await Dispatcher.InvokeAsync(() =>
                {
                    StatusText.Text = AppText.AutoReconnecting(attempt);
                    StatusText.Visibility = Visibility.Visible;
                    StatusText.Foreground = System.Windows.Media.Brushes.White;
                    ConnectButton.IsEnabled = false;
                    DisconnectButton.IsEnabled = false;
                });

                var reconnectTarget = await Dispatcher.InvokeAsync(() =>
                {
                    var uiHost = HostTextBox.Text.Trim();
                    var uiPort = ParsePortOrDefault();
                    return (
                        Host: string.IsNullOrWhiteSpace(uiHost) ? host : uiHost,
                        Port: uiPort > 0 ? uiPort : port,
                        DisplayInfo: WindowsDisplayInfo.FromWindow(this));
                });
                await ConnectToServerAsync(
                    reconnectTarget.Host,
                    reconnectTarget.Port,
                    reconnectTarget.DisplayInfo,
                    isAutoReconnect: true);
                DiagLog.Write($"auto reconnect succeeded: attempt={attempt}");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                DiagLog.Write(ex, $"auto reconnect failed: attempt={attempt}");
                // Pairing refused, or not the paired Mac: retrying on its own would only nag
                // the Mac with more requests, so stop and let the user click Connect.
                var stopRetrying = ex is MacstreamAuthException { Retryable: false };
                await Dispatcher.InvokeAsync(() =>
                {
                    StatusText.Text = stopRetrying ? ex.Message : AppText.AutoReconnectFailed(ex.Message);
                    StatusText.Visibility = Visibility.Visible;
                    StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                    ConnectButton.IsEnabled = true;
                    DisconnectButton.IsEnabled = false;
                });

                if (!stopRetrying && !token.IsCancellationRequested && !_manualDisconnectRequested)
                    ScheduleAutoReconnect();
            }
        }, token);
    }

    private void StartUsb4IpAutoDetection()
    {
        _usb4IpDetectionCts?.Cancel();
        var cts = new CancellationTokenSource();
        _usb4IpDetectionCts = cts;
        _ = Task.Run(() => Usb4IpDetectionLoop(cts.Token), cts.Token);
    }

    private async Task Usb4IpDetectionLoop(CancellationToken ct)
    {
        try
        {
            await Task.Delay(500, ct);

            while (!ct.IsCancellationRequested)
            {
                if (_receiver.IsConnected)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        SetUsb4IpStatus(Usb4IpStatusKind.Found, HostTextBox.Text.Trim());
                    });
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    continue;
                }

                var target = await Dispatcher.InvokeAsync(() => (
                    CurrentHost: HostTextBox.Text.Trim(),
                    Port: ParsePortOrDefault()));

                await Dispatcher.InvokeAsync(() =>
                {
                    SetUsb4IpStatus(Usb4IpStatusKind.Detecting);
                });

                // Background loop: cheap probes only (typed host + ARP neighbours on the direct link).
                // The full 169.254/16 sweep runs only when the user clicks Connect with an empty address.
                var result = await Usb4IpDetector.DetectAsync(target.Port, target.CurrentHost, ct, fullSweep: false);
                if (result != null)
                {
                    await Dispatcher.InvokeAsync(() => ApplyDetectedUsb4Ip(result));
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                }
                else
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        SetUsb4IpStatus(Usb4IpStatusKind.NotFound);
                    });
                    await Task.Delay(TimeSpan.FromSeconds(8), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            DiagLog.Write(ex, "USB4IP auto-detection error");
            await Dispatcher.InvokeAsync(() =>
            {
                SetUsb4IpStatus(Usb4IpStatusKind.Error);
            });
        }
    }

    private void SetUsb4IpStatus(Usb4IpStatusKind status, string? host = null)
    {
        _usb4IpStatus = status;
        _usb4IpStatusHost = host;
        UpdateUsb4IpStatusText();
    }

    private void UpdateUsb4IpStatusText()
    {
        Usb4IpStatusText.Text = _usb4IpStatus switch
        {
            Usb4IpStatusKind.Found => AppText.Usb4Host(_usb4IpStatusHost ?? HostTextBox.Text.Trim()),
            Usb4IpStatusKind.NotFound => AppText.Usb4NotFound,
            Usb4IpStatusKind.Error => AppText.Usb4Error,
            _ => AppText.Usb4Detecting,
        };

        Usb4IpStatusText.Foreground = _usb4IpStatus switch
        {
            Usb4IpStatusKind.Found => Brushes.LimeGreen,
            Usb4IpStatusKind.NotFound or Usb4IpStatusKind.Error => Brushes.Orange,
            _ => Brushes.LightGray,
        };
    }

    private void ApplyDetectedUsb4Ip(Usb4IpDetectionResult result)
    {
        _lastHost = result.Host;
        _lastPort = result.Port;

        SetUsb4IpStatus(Usb4IpStatusKind.Found, result.Host);

        if (!_receiver.IsConnected &&
            !HostTextBox.IsKeyboardFocusWithin &&
            !string.Equals(HostTextBox.Text.Trim(), result.Host, StringComparison.OrdinalIgnoreCase))
        {
            HostTextBox.Text = result.Host;
            DiagLog.Write(
                $"USB4IP updated the address field: {result.Host}, local={result.LocalAddress}, iface={result.InterfaceName}");
        }

        if (!_receiver.IsConnected && StatusText.Visibility == Visibility.Visible)
        {
            StatusText.Text = AppText.DetectedMacUsb4Ip(result.Host);
            StatusText.Foreground = Brushes.White;
        }
    }

    private int ParsePortOrDefault()
    {
        return int.TryParse(PortTextBox.Text.Trim(), out var port) && port > 0 && port <= 65535
            ? port
            : ProtocolConstants.VideoPort;
    }

    private void OnCompressedFrame(FramePayload frame, VideoDecoderCodec codec)
    {
        _frameCount++;
        _totalBytes += frame.Data.Length;
        Interlocked.Add(ref _intervalBytes, frame.Data.Length);
        var codecName = codec == VideoDecoderCodec.Hevc ? "HEVC" : "H264";
        if (_initialH264FrameLogs < 3 || frame.IsKeyFrame)
        {
            if (!frame.IsKeyFrame)
                _initialH264FrameLogs++;
            DiagLog.Write($"{codecName} frame: idx={frame.FrameIndex}, key={frame.IsKeyFrame}, size={frame.Width}x{frame.Height}, bytes={frame.Data.Length}");
        }

        var now = DateTime.UtcNow;
        if (!_hasDecodedFrame && (now - _lastReceiveStatusUpdate).TotalMilliseconds >= 500)
        {
            _lastReceiveStatusUpdate = now;
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = frame.IsKeyFrame
                    ? AppText.ReceivedKeyFrame(codecName, frame.FrameIndex)
                    : AppText.ReceivedFrameWaitingDecoder(codecName, frame.FrameIndex);
                StatusText.Foreground = System.Windows.Media.Brushes.White;
            });
        }

        // If the decoder failed to initialize, only count frames; don't crash
        if (_decoderFailed)
        {
            if (_frameCount % 100 == 0)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = AppText.ReceivingNoDecoder(_frameCount, _totalBytes / 1024);
                });
            }
            return;
        }

        var queuedFrames = Volatile.Read(ref _queuedH264Frames);
        var queueLatencyMs = EstimateQueueLatencyMs(frame);
        if ((now - _lastLatencyLogTime).TotalSeconds >= 2.0)
        {
            _lastLatencyLogTime = now;
            DiagLog.Write($"H264 low-latency state: queue={queuedFrames}, lag={queueLatencyMs}ms, key={frame.IsKeyFrame}, idx={frame.FrameIndex}");
        }

        var suppressOutput = false;

        if (_dropUntilKeyFrame)
        {
            if (!frame.IsKeyFrame)
            {
                return;
            }

            ClearH264Queue();
            _dropUntilKeyFrame = false;
            _resetDecoderOnNextKeyFrame = true;
            _suppressOutputUntilLowLatency = queueLatencyMs > ResumeQueueLatencyMs;
            suppressOutput = _suppressOutputUntilLowLatency;

            if (suppressOutput)
                DiagLog.Write($"catch-up keyframe received, warming up the decoder first lag={queueLatencyMs}ms, idx={frame.FrameIndex}");
            else
                DiagLog.Write($"low-latency keyframe received, resuming decode lag={queueLatencyMs}ms, idx={frame.FrameIndex}");
        }
        else if (_suppressOutputUntilLowLatency)
        {
            if (queueLatencyMs <= ResumeQueueLatencyMs || queuedFrames <= MaxQueuedFramesForOutput)
            {
                _suppressOutputUntilLowLatency = false;
                DiagLog.Write($"catch-up warm-up done, resuming output queued={queuedFrames}, lag={queueLatencyMs}ms, key={frame.IsKeyFrame}, idx={frame.FrameIndex}");
            }
            else
            {
                suppressOutput = true;
                if (queuedFrames >= EmergencyQueuedH264Frames)
                {
                    DiagLog.Write($"queue still behind during catch-up, flushing to the next keyframe queued={queuedFrames}, lag={queueLatencyMs}ms, currentKey={frame.IsKeyFrame}, idx={frame.FrameIndex}");
                    ClearH264Queue();
                    _resetDecoderOnNextKeyFrame = true;

                    if (!frame.IsKeyFrame)
                    {
                        BeginWaitForKeyFrame();
                        return;
                    }
                }
            }
        }
        else if (queuedFrames >= EmergencyQueuedH264Frames && queueLatencyMs > ResumeQueueLatencyMs)
        {
            DiagLog.Write($"H264 decode is behind, flushing to the next keyframe queued={queuedFrames}, lag={queueLatencyMs}ms, currentKey={frame.IsKeyFrame}, idx={frame.FrameIndex}");
            _lastCatchUpTime = now;
            ClearH264Queue();
            _resetDecoderOnNextKeyFrame = true;

            if (!frame.IsKeyFrame)
            {
                BeginWaitForKeyFrame();
                _suppressOutputUntilLowLatency = true;
                return;
            }

            _suppressOutputUntilLowLatency = queueLatencyMs > ResumeQueueLatencyMs;
            suppressOutput = _suppressOutputUntilLowLatency;
            if (suppressOutput)
                DiagLog.Write($"warming up the decoder from a stale keyframe lag={queueLatencyMs}ms, idx={frame.FrameIndex}");
        }
        else if (queuedFrames > MaxQueuedFramesForOutput && queueLatencyMs > MaxQueueLatencyMs)
        {
            DiagLog.Write($"H264 latency too high, soft catch-up skipping stale output queued={queuedFrames}, lag={queueLatencyMs}ms, currentKey={frame.IsKeyFrame}, idx={frame.FrameIndex}");
            _lastCatchUpTime = now;
            _suppressOutputUntilLowLatency = true;
            suppressOutput = true;
        }

        EnqueueH264Frame(frame, codec, suppressOutput);
        ApplyReceiveBackpressureIfNeeded();
    }

    private void EnqueueH264Frame(FramePayload frame, VideoDecoderCodec codec, bool suppressOutput)
    {
        _h264Queue.Enqueue(new QueuedH264Frame(frame, codec, suppressOutput));
        Interlocked.Increment(ref _queuedH264Frames);
        _h264Signal.Release();
    }

    private void ApplyReceiveBackpressureIfNeeded()
    {
        if (MaxReceiveBackpressureSleepMs <= 0)
            return;

        if (Volatile.Read(ref _queuedH264Frames) < ReceiveBackpressureHighWatermark)
            return;

        var sleepMs = 0;
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _queuedH264Frames) > ReceiveBackpressureLowWatermark &&
               sw.ElapsedMilliseconds < MaxReceiveBackpressureSleepMs)
        {
            Thread.Sleep(1);
            sleepMs++;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastBackpressureLogTime).TotalSeconds >= 2.0)
        {
            _lastBackpressureLogTime = now;
            DiagLog.Write($"receive backpressure: slept={sleepMs}ms, queue={Volatile.Read(ref _queuedH264Frames)}");
        }
    }

    private long EstimateQueueLatencyMs(FramePayload frame)
    {
        if (frame.Timestamp == 0)
            return 0;

        var localMacEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - MacAbsoluteEpochOffsetMs;
        var observedOffset = localMacEpochMs - unchecked((long)frame.Timestamp);
        if (observedOffset < _minObservedMacClockOffsetMs)
            _minObservedMacClockOffsetMs = observedOffset;

        return Math.Max(0, observedOffset - _minObservedMacClockOffsetMs);
    }

    private async Task DecodeLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _h264Signal.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (_h264Queue.TryDequeue(out var queuedFrame))
            {
                if (Interlocked.Decrement(ref _queuedH264Frames) < 0)
                    Interlocked.Exchange(ref _queuedH264Frames, 0);

                DecodeH264Frame(queuedFrame);
            }
        }
    }

    private void DecodeH264Frame(QueuedH264Frame queuedFrame)
    {
        var frame = queuedFrame.Payload;
        var codec = queuedFrame.Codec;
        if (_resetDecoderOnNextKeyFrame)
        {
            if (!frame.IsKeyFrame)
            {
                return;
            }

            _renderer.ResetSwapChain();   // the swap chain uses the decoder's D3D11 device
            _h264Decoder?.Dispose();
            _h264Decoder = null;
            _decoderFailed = false;
            _resetDecoderOnNextKeyFrame = false;
            DiagLog.Write($"resetting the decoder and resuming from a keyframe idx={frame.FrameIndex}");
        }

        // A keyframe of another size: the Mac switched displays (Settings > Mac screen). Start
        // a fresh decoder for it rather than relying on the old one to resize.
        if (_h264Decoder != null && frame.IsKeyFrame && frame.Width > 0 && frame.Height > 0 &&
            _decoderInitWidth > 0 && (frame.Width != _decoderInitWidth || frame.Height != _decoderInitHeight))
        {
            _renderer.ResetSwapChain();   // the swap chain uses the decoder's D3D11 device
            _h264Decoder.Dispose();
            _h264Decoder = null;
            _decoderFailed = false;
            DiagLog.Write($"video size changed to {frame.Width}x{frame.Height}; new decoder, idx={frame.FrameIndex}");
        }

        if (_h264Decoder != null && _decoderCodec != codec)
        {
            if (!frame.IsKeyFrame)
                return;

            _h264Decoder.Dispose();
            _h264Decoder = null;
            _decoderFailed = false;
            DiagLog.Write($"switching video decoder: {_decoderCodec} -> {codec}, idx={frame.FrameIndex}");
        }

        if (_h264Decoder == null)
        {
            _decoderCodec = codec;
            _h264Decoder = new H264Decoder(_loggerFactory.CreateLogger<H264Decoder>(), codec);
            var decoderWidth = frame.Width > 0 ? frame.Width : 1920;
            var decoderHeight = frame.Height > 0 ? frame.Height : 1080;
            _decoderInitWidth = frame.Width;
            _decoderInitHeight = frame.Height;

            try
            {
                if (_h264Decoder.Initialize(decoderWidth, decoderHeight))
                {
                    _h264Decoder.ShouldOutputFrame = ShouldOutputDecodedFrame;
                    _h264Decoder.ShouldUseD3D11Output = () => _renderer.CanUseD3D11Direct;
                    _h264Decoder.OnFrameDecoded += OnDecodedFrame;
                    _h264Decoder.OnD3D11FrameDecoded += OnD3D11FrameDecoded;
                    Dispatcher.Invoke(() =>
                    {
                        _displayWidth = decoderWidth;
                        _displayHeight = decoderHeight;
                        _renderer.Initialize(_displayWidth, _displayHeight);
                        DisplayImage.Source = _renderer.Bitmap;
                        UpdateDisplayImageLayout();
                        StatusText.Visibility = Visibility.Collapsed;
                    });
                }
                else
                {
                    _h264Decoder.Dispose();
                    _h264Decoder = null;
                    _decoderFailed = true;
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = AppText.HardwareDecoderInitFailed(codec.ToString());
                        StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Decoder] init error: {ex.Message}");
                DiagLog.Write(ex, "decoder init error");
                _renderer.ResetSwapChain();   // the swap chain uses the decoder's D3D11 device
                _h264Decoder?.Dispose();
                _h264Decoder = null;
                _decoderFailed = true;
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = AppText.DecoderUnavailable(ex.Message);
                    StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                });
            }
        }

        if (_h264Decoder != null)
        {
            try
            {
                _suppressCurrentDecodeOutput = queuedFrame.SuppressOutput;
                _h264Decoder.Decode(frame.Data, frame.IsKeyFrame);
            }
            catch (Exception ex)
            {
                DiagLog.Write(ex, "H.264 decode failed, waiting for the next keyframe");
                _decoderFailed = false;
                _resetDecoderOnNextKeyFrame = true;
                BeginWaitForKeyFrame();
                _suppressOutputUntilLowLatency = true;
            }
            finally
            {
                _suppressCurrentDecodeOutput = false;
            }
        }
    }

    private bool ShouldOutputDecodedFrame()
    {
        if (_suppressCurrentDecodeOutput)
            return false;

        if (Volatile.Read(ref _queuedH264Frames) > MaxQueuedFramesForOutput)
            return false;

        return !_renderer.HasPendingFrame;
    }

    private long _lastKeyFrameRequestTicks;

    /// <summary>
    /// Stop decoding predicted frames until the next keyframe, and ask the Mac for one now
    /// instead of waiting for the encoder's periodic keyframe (which can be a minute away
    /// when the Mac screen is mostly static).
    /// </summary>
    private void BeginWaitForKeyFrame()
    {
        _dropUntilKeyFrame = true;

        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastKeyFrameRequestTicks) < 250)
            return;

        Interlocked.Exchange(ref _lastKeyFrameRequestTicks, now);
        _receiver.RequestKeyFrame();
    }

    private void ClearH264Queue()
    {
        while (_h264Queue.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref _queuedH264Frames, 0);
    }

    private void OnDecodedFrame(object? sender, DecodedFrame decodedFrame)
    {
        _hasDecodedFrame = true;

        // Re-initialize if the resolution changed
        if (decodedFrame.Width != _displayWidth || decodedFrame.Height != _displayHeight)
        {
            _displayWidth = decodedFrame.Width;
            _displayHeight = decodedFrame.Height;

            Dispatcher.Invoke(() =>
            {
                _renderer.Initialize(_displayWidth, _displayHeight);
                DisplayImage.Source = _renderer.Bitmap;
                UpdateDisplayImageLayout();
                StatusText.Text = $"{_decoderCodec} {_displayWidth}x{_displayHeight}";
                StatusText.Visibility = Visibility.Collapsed;
            });
        }

        _renderer.RenderDecodedFrame(new DecodedFrameData
        {
            Width = decodedFrame.Width,
            Height = decodedFrame.Height,
            PixelData = decodedFrame.PixelData,
            PixelDataLength = decodedFrame.PixelDataLength,
            ReturnBuffer = decodedFrame.ReturnBuffer,
        });
    }

    private void OnD3D11FrameDecoded(object? sender, D3D11DecodedFrame decodedFrame)
    {
        _hasDecodedFrame = true;

        if (decodedFrame.Width != _displayWidth || decodedFrame.Height != _displayHeight)
        {
            _displayWidth = decodedFrame.Width;
            _displayHeight = decodedFrame.Height;

            Dispatcher.Invoke(() =>
            {
                _renderer.Initialize(_displayWidth, _displayHeight);
                DisplayImage.Source = _renderer.Bitmap;
                UpdateDisplayImageLayout();
                StatusText.Text = $"{_decoderCodec} D3D11 {_displayWidth}x{_displayHeight}";
                StatusText.Visibility = Visibility.Collapsed;
            });
        }

        if (!_renderer.RenderD3D11Frame(decodedFrame))
            decodedFrame.Release();
    }

    private void OnRawFrame(object? sender, RawFramePayload frame)
    {
        _frameCount++;
        _totalBytes += frame.PixelData.Length;
        Interlocked.Add(ref _intervalBytes, frame.PixelData.Length);
        if (_initialRawFrameLogs < 3)
        {
            _initialRawFrameLogs++;
            DiagLog.Write($"RAW frame: idx={frame.FrameIndex}, bytes={frame.PixelData.Length}");
        }

        var pixelData = frame.PixelData.Span;
        var pixelCount = pixelData.Length / 4;

        if (_displayWidth == 0)
        {
            if (frame.Width > 0 && frame.Height > 0)
            {
                _displayWidth = frame.Width;
                _displayHeight = frame.Height;
            }

            var resolutions = new (int w, int h)[]
            {
                (1920, 1080), (2560, 1440), (3840, 2160),
                (1440, 900), (1680, 1050), (1280, 800),
                (2560, 1600), (1728, 1117), (1470, 956),
                (1512, 982), (1800, 1169)
            };

            foreach (var (w, h) in resolutions)
            {
                if (_displayWidth == 0 && pixelData.Length >= w * h * 4)
                {
                    _displayWidth = w;
                    _displayHeight = h;
                    break;
                }
            }

            if (_displayWidth == 0)
            {
                _displayWidth = (int)Math.Sqrt(pixelCount * 16 / 9.0);
                _displayWidth = (_displayWidth / 2) * 2;
                _displayHeight = pixelCount / _displayWidth;
            }

            Dispatcher.Invoke(() =>
            {
                _renderer.Initialize(_displayWidth, _displayHeight);
                DisplayImage.Source = _renderer.Bitmap;
                UpdateDisplayImageLayout();
                StatusText.Text = $"RAW {_displayWidth}x{_displayHeight}";
                StatusText.Visibility = Visibility.Collapsed;
            });

            _logger.LogInformation("RAW frame resolution: {Width}x{Height}", _displayWidth, _displayHeight);
            DiagLog.Write($"RAW frame resolution: {_displayWidth}x{_displayHeight}");
        }

        _renderer.RenderRawFrame(frame);
    }

    private void OnReceiveError(object? sender, string message)
    {
        _lastReceiveError = message;
        DiagLog.Write($"receive error: {message}");
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = AppText.ReceiveInterrupted(message);
            StatusText.Visibility = Visibility.Visible;
            StatusText.Foreground = System.Windows.Media.Brushes.Orange;
        });
    }

    private void OnFrameRendered(object? sender, EventArgs e)
    {
        _renderedFrameCount++;
        if (_renderedFrameCount <= 3)
        {
            DiagLog.Write($"rendered frame #{_renderedFrameCount}");
        }

        Interlocked.Increment(ref _renderedSinceTick);
    }

    // --- Live stats (status bar + diag.log), refreshed once per second while connected --------

    private readonly DispatcherTimer _statsTimer;
    private long _intervalBytes;
    private int _renderedSinceTick;
    private long _lastStatsTick;
    private ServerStatsPayload? _lastServerStats;
    private double? _lastRttMs;

    private void OnStatsTick()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Math.Max((now - _lastStatsTick) / (double)Stopwatch.Frequency, 0.001);
        _lastStatsTick = now;
        var renderedFps = Interlocked.Exchange(ref _renderedSinceTick, 0) / elapsed;
        var receivedMbps = Interlocked.Exchange(ref _intervalBytes, 0) * 8.0 / elapsed / 1_000_000.0;

        _receiver.SendPing();

        var rtt = _lastRttMs is { } ms ? $"  ·  RTT {ms:F1} ms" : "";
        FpsText.Text = $"FPS {renderedFps:F0}  ·  {receivedMbps:F1} Mbps{rtt}";

        if (_lastServerStats is { } s)
        {
            var codec = s.IsHevc ? "HEVC" : "H.264";
            var encoderDetail = s.HasV2Fields
                ? $", encoder {s.EncoderMsAvg:F1}{(s.LowLatency ? ", low-latency" : "")}{(s.YuvCapture ? ", YUV" : "")}"
                : "";
            FrameSizeText.Text =
                $"{s.Width}×{s.Height} {codec}{(s.ExtraDisplay ? " extra display" : "")}  ·  Mac capture {s.CaptureFps:F0} / encode {s.EncodeFps:F0} fps" +
                $"  ·  encode {s.EncodeMsAvg:F1} ms (max {s.EncodeMsMax:F0}{encoderDetail})  ·  deferred {s.SkippedForBackpressure}  ·  dropped {s.DroppedFrames}";
            DiagLog.Write(
                $"STATS fps={renderedFps:F1} rx={receivedMbps:F1}Mbps rtt={_lastRttMs:F2}ms | mac {s.Width}x{s.Height} {codec} " +
                $"cap={s.CaptureFps:F1} enc={s.EncodeFps:F1} encMs={s.EncodeMsAvg:F1}/{s.EncodeMsMax:F1} " +
                $"tx={s.BitrateKbps / 1000.0:F1}Mbps target={s.TargetBitrateKbps / 1000}Mbps maxQP={s.MaxQp} " +
                $"deferred={s.SkippedForBackpressure} dropped={s.DroppedFrames} " +
                $"vtMs={s.EncoderMsAvg:F1} lowLatency={s.LowLatency} yuv={s.YuvCapture} extraDisplay={s.ExtraDisplay}");
        }
        else
        {
            FrameSizeText.Text = "";
        }
    }

    private void OnImageSourceChanged(object? sender, EventArgs e)
    {
        DisplayImage.Source = _renderer.Bitmap;
        UpdateDisplayImageLayout();
    }

    private void UpdateDisplayImageLayout()
    {
        if (_displayWidth <= 0 || _displayHeight <= 0)
            return;

        if (!DisplayImage.Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(UpdateDisplayImageLayout);
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var hostWidth = DisplayHost.ActualWidth;
        var hostHeight = DisplayHost.ActualHeight;
        if (hostWidth <= 0 || hostHeight <= 0)
            return;

        var hostPhysicalWidth = hostWidth * dpi.DpiScaleX;
        var hostPhysicalHeight = hostHeight * dpi.DpiScaleY;
        var scale = Math.Min(hostPhysicalWidth / _displayWidth, hostPhysicalHeight / _displayHeight);
        if (scale <= 0)
            return;

        if (scale > 0.995)
            scale = 1.0;

        var physicalWidth = Math.Max(1, Math.Round(_displayWidth * scale));
        var physicalHeight = Math.Max(1, Math.Round(_displayHeight * scale));
        var pixelPerfect = (int)physicalWidth == _displayWidth && (int)physicalHeight == _displayHeight;
        RenderOptions.SetBitmapScalingMode(
            DisplayImage,
            pixelPerfect ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        DisplayImage.Width = physicalWidth / dpi.DpiScaleX;
        DisplayImage.Height = physicalHeight / dpi.DpiScaleY;
        LogDisplayLayoutIfNeeded(
            hostWidth,
            hostHeight,
            dpi.DpiScaleX,
            dpi.DpiScaleY,
            hostPhysicalWidth,
            hostPhysicalHeight,
            physicalWidth,
            physicalHeight,
            scale,
            pixelPerfect);
    }

    private void LogDisplayLayoutIfNeeded(
        double hostWidth,
        double hostHeight,
        double dpiScaleX,
        double dpiScaleY,
        double hostPhysicalWidth,
        double hostPhysicalHeight,
        double physicalWidth,
        double physicalHeight,
        double scale,
        bool pixelPerfect)
    {
        var roundedPhysicalWidth = (int)physicalWidth;
        var roundedPhysicalHeight = (int)physicalHeight;
        var now = DateTime.UtcNow;
        var layoutChanged =
            _lastLayoutPhysicalWidth != roundedPhysicalWidth ||
            _lastLayoutPhysicalHeight != roundedPhysicalHeight ||
            _lastLayoutPixelPerfect != pixelPerfect ||
            Math.Abs(_lastLayoutScale - scale) >= 0.005;

        if (!layoutChanged && (now - _lastLayoutLogTime).TotalSeconds < 5)
            return;

        _lastLayoutPhysicalWidth = roundedPhysicalWidth;
        _lastLayoutPhysicalHeight = roundedPhysicalHeight;
        _lastLayoutScale = scale;
        _lastLayoutPixelPerfect = pixelPerfect;
        _lastLayoutLogTime = now;

        DiagLog.Write(
            $"display layout: video={_displayWidth}x{_displayHeight}, host={hostWidth:F1}x{hostHeight:F1}dip/{hostPhysicalWidth:F0}x{hostPhysicalHeight:F0}px, dpiScale={dpiScaleX:F2}x{dpiScaleY:F2}, output={roundedPhysicalWidth}x{roundedPhysicalHeight}px, scale={scale * 100:F1}%, pixelPerfect={pixelPerfect}");
    }

    protected override void OnClosed(EventArgs e)
    {
        _tray?.Dispose();
        _tray = null;
        _clipboard.Dispose();
        _localCursorIdleTimer.Stop();
        ShowLocalCursor();
        _usb4IpDetectionCts?.Cancel();
        _decodeCts?.Cancel();
        _h264Signal.Release();
        _receiver.Disconnect();
        _renderer.ResetSwapChain();   // the swap chain uses the decoder's D3D11 device
        _h264Decoder?.Dispose();
        _renderer.Dispose();
        _loggerFactory.Dispose();
        base.OnClosed(e);
    }
}
