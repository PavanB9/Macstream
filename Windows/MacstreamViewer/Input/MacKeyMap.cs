using MacstreamViewer.Diagnostics;

namespace MacstreamViewer.Input;

/// <summary>
/// Maps Windows keys to Mac virtual key codes (HIToolbox kVK_*), by physical position: the
/// Windows scan code decides the key, and the Mac's own keyboard layout decides the character.
///
/// Modifiers are remapped so Windows habits work on the Mac. Default: Ctrl -> Command (so
/// Ctrl+C/V copy and paste), Windows key -> Command too (Win+Space, Win+Q... act like the Mac's
/// Command key), Alt -> Option, and Right Ctrl -> Control (for the Mac's own Control
/// shortcuts, e.g. Control+C in Terminal). Change it with, for example,
///   MACSTREAM_MAC_MODIFIERS=ctrl=control,alt=option,win=cmd
/// (targets: cmd, option, control, shift, none; "ctrl" sets both Ctrl keys).
/// </summary>
public sealed class MacKeyMap
{
    // Scan codes as reported by the low-level keyboard hook; 0xE000 marks the extended keys.
    private const int Extended = 0xE000;
    private const int LeftCtrl = 0x1D, RightCtrl = Extended | 0x1D;
    private const int LeftAlt = 0x38, RightAlt = Extended | 0x38;
    private const int LeftWin = Extended | 0x5B, RightWin = Extended | 0x5C;

    private const uint VkPause = 0x13;
    private const uint VkNumLock = 0x90;
    private const ushort MacKeypadClear = 0x47;

    private static readonly Dictionary<int, ushort> Keys = new()
    {
        [0x01] = 0x35,                                                    // Esc
        [0x02] = 0x12, [0x03] = 0x13, [0x04] = 0x14, [0x05] = 0x15,       // 1 2 3 4
        [0x06] = 0x17, [0x07] = 0x16, [0x08] = 0x1A, [0x09] = 0x1C,       // 5 6 7 8
        [0x0A] = 0x19, [0x0B] = 0x1D, [0x0C] = 0x1B, [0x0D] = 0x18,       // 9 0 - =
        [0x0E] = 0x33, [0x0F] = 0x30,                                     // Backspace, Tab
        [0x10] = 0x0C, [0x11] = 0x0D, [0x12] = 0x0E, [0x13] = 0x0F,       // Q W E R
        [0x14] = 0x11, [0x15] = 0x10, [0x16] = 0x20, [0x17] = 0x22,       // T Y U I
        [0x18] = 0x1F, [0x19] = 0x23, [0x1A] = 0x21, [0x1B] = 0x1E,       // O P [ ]
        [0x1C] = 0x24,                                                    // Enter
        [0x1E] = 0x00, [0x1F] = 0x01, [0x20] = 0x02, [0x21] = 0x03,       // A S D F
        [0x22] = 0x05, [0x23] = 0x04, [0x24] = 0x26, [0x25] = 0x28,       // G H J K
        [0x26] = 0x25, [0x27] = 0x29, [0x28] = 0x27, [0x29] = 0x32,       // L ; ' `
        [0x2A] = 0x38, [0x2B] = 0x2A,                                     // Left Shift, backslash
        [0x2C] = 0x06, [0x2D] = 0x07, [0x2E] = 0x08, [0x2F] = 0x09,       // Z X C V
        [0x30] = 0x0B, [0x31] = 0x2D, [0x32] = 0x2E, [0x33] = 0x2B,       // B N M ,
        [0x34] = 0x2F, [0x35] = 0x2C, [0x36] = 0x3C,                      // . / Right Shift
        [0x37] = 0x43, [0x39] = 0x31, [0x3A] = 0x39,                      // Keypad *, Space, Caps Lock
        [0x3B] = 0x7A, [0x3C] = 0x78, [0x3D] = 0x63, [0x3E] = 0x76,       // F1-F4
        [0x3F] = 0x60, [0x40] = 0x61, [0x41] = 0x62, [0x42] = 0x64,       // F5-F8
        [0x43] = 0x65, [0x44] = 0x6D, [0x57] = 0x67, [0x58] = 0x6F,       // F9 F10 F11 F12
        [0x64] = 0x69, [0x65] = 0x6B, [0x66] = 0x71, [0x67] = 0x6A,       // F13-F16
        [0x68] = 0x40, [0x69] = 0x4F, [0x6A] = 0x50, [0x6B] = 0x5A,       // F17-F20
        [0x47] = 0x59, [0x48] = 0x5B, [0x49] = 0x5C, [0x4A] = 0x4E,       // Keypad 7 8 9 -
        [0x4B] = 0x56, [0x4C] = 0x57, [0x4D] = 0x58, [0x4E] = 0x45,       // Keypad 4 5 6 +
        [0x4F] = 0x53, [0x50] = 0x54, [0x51] = 0x55,                      // Keypad 1 2 3
        [0x52] = 0x52, [0x53] = 0x41,                                     // Keypad 0 .
        [0x56] = 0x0A,                                                    // ISO key next to Left Shift
        [0x70] = 0x68, [0x73] = 0x5E, [0x7D] = 0x5D,                      // JIS Kana, Ro, Yen
        [Extended | 0x1C] = 0x4C,                                         // Keypad Enter
        [Extended | 0x35] = 0x4B,                                         // Keypad /
        [Extended | 0x47] = 0x73, [Extended | 0x4F] = 0x77,               // Home, End
        [Extended | 0x49] = 0x74, [Extended | 0x51] = 0x79,               // Page Up, Page Down
        [Extended | 0x52] = 0x72, [Extended | 0x53] = 0x75,               // Insert (Mac Help), Delete (forward)
        [Extended | 0x48] = 0x7E, [Extended | 0x50] = 0x7D,               // Up, Down
        [Extended | 0x4B] = 0x7B, [Extended | 0x4D] = 0x7C,               // Left, Right
    };

    private static readonly Dictionary<string, (ushort Left, ushort Right)?> MacModifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cmd"] = (0x37, 0x36), ["command"] = (0x37, 0x36),
        ["option"] = (0x3A, 0x3D), ["opt"] = (0x3A, 0x3D), ["alt"] = (0x3A, 0x3D),
        ["control"] = (0x3B, 0x3E), ["ctrl"] = (0x3B, 0x3E),
        ["shift"] = (0x38, 0x3C),
        ["none"] = null,
    };

    private readonly Dictionary<int, ushort?> _modifiers = new();

    public MacKeyMap()
    {
        SetModifier("ctrl", "cmd");
        _modifiers[RightCtrl] = 0x3E;   // Right Ctrl -> Mac Right Control
        SetModifier("alt", "option");
        SetModifier("win", "cmd");

        var text = EnvVars.Get("MACSTREAM_MAC_MODIFIERS");
        if (string.IsNullOrWhiteSpace(text))
            return;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !SetModifier(pair[0], pair[1]))
                DiagLog.Write($"MACSTREAM_MAC_MODIFIERS: ignored '{part}' (use ctrl|alt|win = cmd|option|control|shift|none)");
        }
    }

    /// <summary>Ctrl -> Command etc., for the log and the tooltip.</summary>
    public string Describe() =>
        $"Ctrl={Name(_modifiers[LeftCtrl])}, Right Ctrl={Name(_modifiers[RightCtrl])}, "
        + $"Alt={Name(_modifiers[LeftAlt])}, Win={Name(_modifiers[LeftWin])}";

    /// <summary>Windows keys the Settings window can change, and their defaults.</summary>
    public static readonly (string Key, string Label, string Default)[] SettableKeys =
    {
        ("ctrl", "Ctrl (left)", "cmd"),
        ("rightctrl", "Right Ctrl", "control"),
        ("alt", "Alt", "option"),
        ("win", "Windows key", "cmd"),
    };

    /// <summary>Mac keys a Windows modifier can become, with their display names.</summary>
    public static readonly (string Target, string Label)[] SettableTargets =
    {
        ("cmd", "Command"), ("option", "Option"), ("control", "Control"), ("shift", "Shift"), ("none", "Nothing"),
    };

    /// <summary>The current target ("cmd", "option", ...) of one of <see cref="SettableKeys"/>.</summary>
    public string TargetOf(string windowsKey) =>
        _modifiers[windowsKey switch
        {
            "rightctrl" => RightCtrl,
            "alt" => LeftAlt,
            "win" => LeftWin,
            _ => LeftCtrl,
        }] switch
        {
            0x37 or 0x36 => "cmd",
            0x3A or 0x3D => "option",
            0x3B or 0x3E => "control",
            0x38 or 0x3C => "shift",
            _ => "none",
        };

    /// <summary>
    /// Maps one of <see cref="SettableKeys"/> to a Mac key (cmd, option, control, shift, none).
    /// Release held keys first: a key pressed under the old mapping must be released under it.
    /// </summary>
    public bool Set(string windowsKey, string macKey)
    {
        if (!MacModifiers.TryGetValue(macKey, out var target))
            return false;
        switch (windowsKey)
        {
            case "ctrl": _modifiers[LeftCtrl] = target?.Left; return true;
            case "rightctrl": _modifiers[RightCtrl] = target?.Right; return true;
            case "alt" or "win": return SetModifier(windowsKey, macKey);
            default: return false;
        }
    }

    /// <summary>The Mac key the (left) Windows key is mapped to, if any.</summary>
    public ushort? WindowsKeyTarget => _modifiers[LeftWin];

    private static string Name(ushort? mac) => mac switch
    {
        0x37 or 0x36 => "Command",
        0x3A or 0x3D => "Option",
        0x3B or 0x3E => "Control",
        0x38 or 0x3C => "Shift",
        _ => "nothing",
    };

    private bool SetModifier(string windowsKey, string macKey)
    {
        if (!MacModifiers.TryGetValue(macKey, out var target))
            return false;
        (int Left, int Right) keys;
        switch (windowsKey.ToLowerInvariant())
        {
            case "ctrl" or "control": keys = (LeftCtrl, RightCtrl); break;
            case "alt": keys = (LeftAlt, RightAlt); break;
            case "win" or "windows": keys = (LeftWin, RightWin); break;
            default: return false;
        }
        _modifiers[keys.Left] = target?.Left;
        _modifiers[keys.Right] = target?.Right;
        return true;
    }

    /// <summary>
    /// The Mac key code for a Windows key, or null when the key is not forwarded (media keys,
    /// Print Screen, Pause, the menu key...): those keep working on Windows.
    /// </summary>
    public ushort? Map(uint scanCode, bool extended, uint virtualKey)
    {
        if (virtualKey == VkNumLock)
            return MacKeypadClear;     // the Mac keypad has Clear where Num Lock is
        if (virtualKey == VkPause)
            return null;

        var scan = (int)(scanCode & 0xFF);
        if (scan == 0)
            return null;
        // Right Shift is sometimes reported as extended; it is the same key.
        var code = extended && scan != 0x36 ? Extended | scan : scan;

        if (_modifiers.TryGetValue(code, out var modifier))
            return modifier;
        return Keys.TryGetValue(code, out var mac) ? mac : null;
    }
}
