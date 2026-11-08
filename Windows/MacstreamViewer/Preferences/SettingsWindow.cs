using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MacstreamViewer.Display;
using MacstreamViewer.Input;

namespace MacstreamViewer.Preferences;

/// <summary>What the Settings window reads and changes; implemented by MainWindow, which
/// applies each change at once and saves it in viewer.json.</summary>
public interface ISettingsHost
{
    bool ClipboardSyncEnabled { get; set; }
    /// <summary>Mirror or extra display; setting it saves and reconnects.</summary>
    MacScreenRequest MacScreen { get; set; }
    /// <summary>This PC monitor's size in pixels.</summary>
    (int Width, int Height) MonitorSize { get; }
    /// <summary>Where the MacBook's own screen is, for moving the mouse onto it.</summary>
    OwnScreenOptions OwnScreen { get; set; }
    bool ScrollInverted { get; set; }
    double ScrollPixelsPerNotch { get; set; }
    string ModifierTarget(string windowsKey);
    void SetModifierTarget(string windowsKey, string macKey);

    /// <summary>"Paired with your Mac (key AB12 CD34)", "Not paired yet..." or the pairing-file error.</summary>
    string PairingDescription { get; }
    bool CanForgetPairing { get; }
    /// <summary>Disconnects and forgets the Mac (after the window has asked the user).</summary>
    void ForgetPairing();
}

/// <summary>
/// The viewer's Settings window (toolbar "Settings" or tray "Settings..."). Every change takes
/// effect at once and is remembered; there is no OK/Cancel.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly ISettingsHost _host;
    private readonly ComboBox _screenMode;
    private readonly ComboBox _screenSize;
    private readonly ComboBox _screenText;
    private readonly ComboBox _ownSide;
    private readonly ComboBox _ownPart;
    private readonly CheckBox _clipboard;
    private readonly CheckBox _scrollInvert;
    private readonly Slider _scrollSpeed;
    private readonly TextBlock _scrollSpeedText;
    private readonly List<(string Key, ComboBox Box)> _modifierBoxes = new();
    private readonly TextBlock _pairingText;
    private readonly Button _forget;
    private bool _loading;

    public SettingsWindow(ISettingsHost host)
    {
        _host = host;
        Title = "Macstream Settings";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7));
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var root = new StackPanel { Margin = new Thickness(20, 14, 20, 18) };

        // Mac screen
        root.Children.Add(Heading("Mac screen"));
        _screenMode = new ComboBox { Width = 290, HorizontalAlignment = HorizontalAlignment.Left };
        _screenMode.Items.Add(new ComboBoxItem { Content = "The Mac's own screen (mirror)", Tag = false });
        _screenMode.Items.Add(new ComboBoxItem { Content = "An extra Mac display for this PC", Tag = true });
        root.Children.Add(LabeledRow("Show", _screenMode));

        _screenSize = new ComboBox { Width = 290, HorizontalAlignment = HorizontalAlignment.Left };
        root.Children.Add(LabeledRow("Size", _screenSize));

        _screenText = new ComboBox { Width = 290, HorizontalAlignment = HorizontalAlignment.Left };
        _screenText.Items.Add(new ComboBoxItem { Content = "Automatic", Tag = 0 });
        _screenText.Items.Add(new ComboBoxItem { Content = "Normal (sharpest, most space)", Tag = 1 });
        _screenText.Items.Add(new ComboBoxItem { Content = "Larger (Retina, half the space)", Tag = 2 });
        root.Children.Add(LabeledRow("Text size", _screenText));

        root.Children.Add(new TextBlock
        {
            Text = "An extra display is a second Mac desktop made to fit this PC, pixel for pixel: drag "
                + "windows onto it, or close the lid (with Amphetamine) to use it on its own. Changing this "
                + "reconnects. If the Mac can't make one, it shows its own screen instead.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 6, 0, 0),
        });
        _screenMode.SelectionChanged += (_, _) => ScreenChanged();
        _screenSize.SelectionChanged += (_, _) => ScreenChanged();
        _screenText.SelectionChanged += (_, _) => ScreenChanged();

        // The MacBook's own screen beside this monitor (extra display only)
        _ownSide = new ComboBox { Width = 290, HorizontalAlignment = HorizontalAlignment.Left };
        _ownSide.Items.Add(new ComboBoxItem { Content = "Not used", Tag = MacSide.Off });
        _ownSide.Items.Add(new ComboBoxItem { Content = "On the left of this monitor", Tag = MacSide.Left });
        _ownSide.Items.Add(new ComboBoxItem { Content = "On the right of this monitor", Tag = MacSide.Right });
        _ownSide.Items.Add(new ComboBoxItem { Content = "Above this monitor", Tag = MacSide.Above });
        _ownSide.Items.Add(new ComboBoxItem { Content = "Below this monitor", Tag = MacSide.Below });
        root.Children.Add(LabeledRow("MacBook screen", _ownSide));
        _ownPart = new ComboBox { Width = 290, HorizontalAlignment = HorizontalAlignment.Left };
        root.Children.Add(LabeledRow("Which part", _ownPart));
        root.Children.Add(new TextBlock
        {
            Text = "With the extra display, push the mouse against that edge of the picture area (full "
                + "screen, maximized or a window) and it carries on onto the MacBook's own screen; push it "
                + "back to return. Use part of the edge to keep the rest for Windows, e.g. when another PC "
                + "monitor is on the same side. Automatic: at the monitor's edge, the part with no PC "
                + "monitor beyond it (the lower or right half if one covers it all); otherwise the whole edge.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 6, 0, 0),
        });
        _ownSide.SelectionChanged += (_, _) =>
        {
            if (_loading)
                return;
            FillOwnParts(SelectedOwnSide(), _ownPart.SelectedItem is ComboBoxItem { Tag: EdgePart part } ? part : EdgePart.Automatic);
            OwnScreenChanged();
        };
        _ownPart.SelectionChanged += (_, _) => OwnScreenChanged();

        // Clipboard
        root.Children.Add(Heading("Clipboard"));
        _clipboard = new CheckBox
        {
            Content = "Sync copy and paste with the Mac (text and pictures)",
            ToolTip = "Only copies made while this is on cross over; nothing already on the clipboard, no history.",
        };
        _clipboard.Click += (_, _) => _host.ClipboardSyncEnabled = _clipboard.IsChecked == true;
        root.Children.Add(_clipboard);

        // Scrolling
        root.Children.Add(Heading("Scrolling on the Mac"));
        _scrollInvert = new CheckBox { Content = "Reverse scroll direction" };
        _scrollInvert.Click += (_, _) => _host.ScrollInverted = _scrollInvert.IsChecked == true;
        root.Children.Add(_scrollInvert);

        var speedRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        speedRow.Children.Add(new TextBlock { Text = "Speed", Width = 110, VerticalAlignment = VerticalAlignment.Center });
        _scrollSpeedText = new TextBlock { Width = 70, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_scrollSpeedText, Dock.Right);
        speedRow.Children.Add(_scrollSpeedText);
        _scrollSpeed = new Slider
        {
            Minimum = 10, Maximum = 200, TickFrequency = 10, IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "How far the Mac scrolls for each notch of the mouse wheel (default 50)",
        };
        _scrollSpeed.ValueChanged += (_, e) =>
        {
            _scrollSpeedText.Text = $"{e.NewValue:0} px";
            if (!_loading)
                _host.ScrollPixelsPerNotch = e.NewValue;
        };
        speedRow.Children.Add(_scrollSpeed);   // last child fills the space between
        root.Children.Add(speedRow);

        // Keys
        root.Children.Add(Heading("Keys on the Mac"));
        root.Children.Add(new TextBlock
        {
            Text = "Which Mac key each Windows key becomes.",
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 4),
        });
        foreach (var (key, label, _) in MacKeyMap.SettableKeys)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center });
            var box = new ComboBox { Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var (target, targetLabel) in MacKeyMap.SettableTargets)
                box.Items.Add(new ComboBoxItem { Content = targetLabel, Tag = target });
            var windowsKey = key;
            box.SelectionChanged += (_, _) =>
            {
                if (!_loading && box.SelectedItem is ComboBoxItem { Tag: string target })
                    _host.SetModifierTarget(windowsKey, target);
            };
            row.Children.Add(box);
            root.Children.Add(row);
            _modifierBoxes.Add((key, box));
        }

        var resetKeys = new Button
        {
            Content = "Restore default keys and scrolling",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(110, 8, 0, 0),
            ToolTip = "Ctrl and Windows key = Command, Right Ctrl = Control, Alt = Option; normal scrolling",
        };
        resetKeys.Click += (_, _) =>
        {
            foreach (var (key, _, defaultTarget) in MacKeyMap.SettableKeys)
                _host.SetModifierTarget(key, defaultTarget);
            _host.ScrollInverted = false;
            _host.ScrollPixelsPerNotch = MacMouseController.DefaultScrollPixelsPerNotch;
            LoadValues();
        };
        root.Children.Add(resetKeys);

        // Pairing
        root.Children.Add(Heading("Pairing"));
        _pairingText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        root.Children.Add(_pairingText);
        _forget = new Button
        {
            Content = "Forget this Mac...",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 8, 0, 0),
            ToolTip = "Disconnects; the next connection asks for the 6-digit code on both computers again",
        };
        _forget.Click += (_, _) => ForgetPairing();
        root.Children.Add(_forget);

        // Close
        var close = new Button
        {
            Content = "Close",
            IsCancel = true,
            IsDefault = true,
            Width = 90,
            Padding = new Thickness(0, 3, 0, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        close.Click += (_, _) => Close();
        root.Children.Add(close);

        Content = root;
        LoadValues();
        Activated += (_, _) => LoadValues();   // e.g. clipboard toggled in the toolbar meanwhile
    }

    private void LoadValues()
    {
        _loading = true;
        try
        {
            LoadScreen();
            _clipboard.IsChecked = _host.ClipboardSyncEnabled;
            _scrollInvert.IsChecked = _host.ScrollInverted;
            _scrollSpeed.Value = Math.Clamp(_host.ScrollPixelsPerNotch, _scrollSpeed.Minimum, _scrollSpeed.Maximum);
            _scrollSpeedText.Text = $"{_host.ScrollPixelsPerNotch:0} px";
            foreach (var (key, box) in _modifierBoxes)
            {
                var target = _host.ModifierTarget(key);
                box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == target);
            }
            _pairingText.Text = _host.PairingDescription;
            _forget.IsEnabled = _host.CanForgetPairing;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Sizes offered for an extra display, besides "match this monitor".</summary>
    private static readonly (int Width, int Height)[] ExtraDisplaySizes =
    {
        (1920, 1080), (2560, 1440), (2560, 1600), (3440, 1440), (3840, 2160),
    };

    private void LoadScreen()
    {
        var screen = _host.MacScreen;
        var (monitorWidth, monitorHeight) = _host.MonitorSize;

        _screenSize.Items.Clear();
        _screenSize.Items.Add(new ComboBoxItem
        {
            Content = $"Match this monitor ({monitorWidth} × {monitorHeight})",
            Tag = (0, 0),
        });
        foreach (var (w, h) in ExtraDisplaySizes)
            _screenSize.Items.Add(new ComboBoxItem { Content = $"{w} × {h}", Tag = (w, h) });
        if (screen.Width > 0 && !ExtraDisplaySizes.Contains((screen.Width, screen.Height)))
            _screenSize.Items.Add(new ComboBoxItem { Content = $"{screen.Width} × {screen.Height}", Tag = (screen.Width, screen.Height) });

        _screenMode.SelectedIndex = screen.ExtraDisplay ? 1 : 0;
        _screenSize.SelectedItem = _screenSize.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag is (int w, int h) && w == screen.Width && h == screen.Height)
            ?? _screenSize.Items[0];
        _screenText.SelectedIndex = Math.Clamp(screen.TextScale, 0, 2);
        _screenSize.IsEnabled = screen.ExtraDisplay;
        _screenText.IsEnabled = screen.ExtraDisplay;

        var own = _host.OwnScreen;
        _ownSide.SelectedItem = _ownSide.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (MacSide)i.Tag == own.Side)
            ?? _ownSide.Items[0];
        FillOwnParts(own.Side, own.Part);
        UpdateOwnEnabled(screen.ExtraDisplay);
    }

    private MacSide SelectedOwnSide() =>
        _ownSide.SelectedItem is ComboBoxItem { Tag: MacSide side } ? side : MacSide.Off;

    /// <summary>The part names depend on the side: halves of a left/right edge are upper and
    /// lower, of a top/bottom edge left and right.</summary>
    private void FillOwnParts(MacSide side, EdgePart selected)
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            var vertical = side is MacSide.Left or MacSide.Right or MacSide.Off;
            _ownPart.Items.Clear();
            _ownPart.Items.Add(new ComboBoxItem { Content = "Automatic", Tag = EdgePart.Automatic });
            _ownPart.Items.Add(new ComboBoxItem { Content = "The whole edge", Tag = EdgePart.Whole });
            _ownPart.Items.Add(new ComboBoxItem { Content = vertical ? "Upper half" : "Left half", Tag = EdgePart.FirstHalf });
            _ownPart.Items.Add(new ComboBoxItem { Content = vertical ? "Lower half" : "Right half", Tag = EdgePart.SecondHalf });
            _ownPart.SelectedItem = _ownPart.Items.OfType<ComboBoxItem>().First(i => (EdgePart)i.Tag == selected);
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void UpdateOwnEnabled(bool extraDisplay)
    {
        _ownSide.IsEnabled = extraDisplay;
        _ownPart.IsEnabled = extraDisplay && SelectedOwnSide() != MacSide.Off;
    }

    private void OwnScreenChanged()
    {
        if (_loading)
            return;
        var part = _ownPart.SelectedItem is ComboBoxItem { Tag: EdgePart p } ? p : EdgePart.Automatic;
        _host.OwnScreen = new OwnScreenOptions(SelectedOwnSide(), part);
        UpdateOwnEnabled(_screenMode.SelectedItem is ComboBoxItem { Tag: true });
    }

    private void ScreenChanged()
    {
        if (_loading)
            return;
        var extra = _screenMode.SelectedItem is ComboBoxItem { Tag: true };
        var (width, height) = _screenSize.SelectedItem is ComboBoxItem { Tag: (int w, int h) } ? (w, h) : (0, 0);
        var textScale = _screenText.SelectedItem is ComboBoxItem { Tag: int t } ? t : 0;
        _screenSize.IsEnabled = extra;
        _screenText.IsEnabled = extra;
        UpdateOwnEnabled(extra);
        // Size and text only matter for the extra display; the mirror is always the plain request.
        _host.MacScreen = extra ? new MacScreenRequest(true, width, height, textScale) : MacScreenRequest.Mirror;
    }

    private static DockPanel LabeledRow(string label, FrameworkElement control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(control);
        return row;
    }

    private void ForgetPairing()
    {
        var answer = MessageBox.Show(this,
            "Forget the paired Mac?\n\nThis disconnects. The next time you connect, both computers show a "
            + "6-digit code again and you confirm it on each.\n\n(The Mac keeps this PC's old entry until you "
            + "use \"Forget Paired PCs\" in its menu bar; it can't be used without this PC's old key, which is deleted.)",
            "Macstream", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK)
            return;
        _host.ForgetPairing();
        LoadValues();
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 14, 0, 6),
    };
}
