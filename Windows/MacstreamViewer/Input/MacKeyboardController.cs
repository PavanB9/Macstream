using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MacstreamViewer.Diagnostics;
using MacstreamViewer.Protocol;

namespace MacstreamViewer.Input;

/// <summary>
/// Windows -> Mac keyboard control. While this window is the focused window and Mac control
/// is on, keys are sent to the Mac instead of Windows, including Windows shortcuts such as the
/// Windows key or Alt+F4 (like Remote Desktop does), so they reach the Mac. Two exceptions:
/// - Alt+Tab stays Windows' own app switcher, so you can always get back to Windows apps.
/// - Windows key + Tab opens the Mac's app switcher (Command+Tab) and works like Alt+Tab on
///   Windows: keep the Windows key down and press Tab to move on, Shift+Tab to go back, let go
///   to switch.
/// MACSTREAM_ALT_TAB=mac sends Alt+Tab to the Mac instead (and leaves Win+Tab alone).
///
/// How it stays contained:
/// - The low-level keyboard hook exists only while this window is focused. It is removed as
///   soon as another window gets focus, and every key it checks is ignored unless this window
///   is the foreground window, so other programs' typing is never seen or sent.
/// - Keys are only sent while Mac control is on. Ctrl+Alt+F12 switches Mac control on/off;
///   when it is off every key goes to Windows as usual (Ctrl+Alt+Del always does).
///   Ctrl+Alt+F11 switches the viewer's full screen on/off (Esc goes to the Mac).
/// - Keys that were pressed are released on the Mac when control is switched off, the window
///   loses focus or the connection ends; the Mac also releases them itself if the PC goes quiet.
/// - The hook runs on its own thread, so a busy UI can never stall the PC's keyboard.
/// </summary>
public sealed class MacKeyboardController : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const uint WmQuit = 0x0012;
    private const uint LlkhfExtended = 0x01;
    private const uint LlkhfUp = 0x80;
    private const uint VkF12 = 0x7B;
    private const uint VkF11 = 0x7A;
    private const uint VkTab = 0x09;
    private const uint VkLeftShift = 0xA0, VkRightShift = 0xA1;
    private const uint VkLeftWin = 0x5B, VkRightWin = 0x5C;
    private const ushort MacTab = 0x30, MacCommand = 0x37;
    // Marks the keys this class itself types into Windows, so the hook lets them through.
    private static readonly UIntPtr OwnInputMarker = (UIntPtr)0x4D414353;   // "MACS"
    private const uint VkLeftCtrl = 0xA2, VkRightCtrl = 0xA3, VkLeftAlt = 0xA4, VkRightAlt = 0xA5;

    private readonly Window _window;
    private readonly IntPtr _windowHandle;
    private readonly Func<bool> _forwarding;
    private readonly Action _toggleControl;
    private readonly Action _toggleFullscreen;
    private readonly Action<byte[]> _send;
    private readonly MacKeyMap _keyMap = new();
    private readonly LowLevelKeyboardProc _hookProc;   // kept alive while the hook exists
    private readonly object _lock = new();

    // Guarded by _lock.
    private readonly HashSet<ushort> _heldMacKeys = new();     // keys this side pressed on the Mac
    private readonly HashSet<uint> _physicalDown = new();      // Windows keys currently down
    private readonly HashSet<uint> _swallowUps = new();        // key-ups that must not reach Windows
    private bool _loggedFirstKey;
    private bool _macSwitcherActive;      // Win+Tab: Command is held on the Mac for its app switcher
    private bool _switcherAddedCommand;   // ...and we pressed that Command ourselves
    // Types Alt+Tab into Windows; a field so the tests can check it without typing anything.
    private readonly Action<bool, bool> _typeAltTab = TypeAltTabIntoWindows;
    private readonly bool _altTabToWindows =
        !string.Equals(EnvVars.Get("MACSTREAM_ALT_TAB"), "mac", StringComparison.OrdinalIgnoreCase);

    private Thread? _hookThread;
    private uint _hookThreadId;
    private bool _disposed;

    /// <param name="forwarding">True while keys should go to the Mac (control on, connected).</param>
    /// <param name="toggleControl">Switches Mac control on/off (the Ctrl+Alt+F12 hotkey).</param>
    /// <param name="send">Queues one INPUT_EVENT payload (thread-safe).</param>
    /// <param name="toggleFullscreen">Switches full screen (the Ctrl+Alt+F11 hotkey).</param>
    public MacKeyboardController(Window window, IntPtr windowHandle, Func<bool> forwarding,
        Action toggleControl, Action toggleFullscreen, Action<byte[]> send)
    {
        _toggleFullscreen = toggleFullscreen;
        _window = window;
        _windowHandle = windowHandle;
        _forwarding = forwarding;
        _toggleControl = toggleControl;
        _send = send;
        _hookProc = HookProc;

        window.Activated += (_, _) => StartHook();
        window.Deactivated += (_, _) =>
        {
            ReleaseAll("window lost focus");
            StopHook();
        };
        window.Closed += (_, _) => Dispose();
        DiagLog.Write($"Mac keyboard map: {_keyMap.Describe()}; Ctrl+Alt+F12 switches Mac control on/off, Ctrl+Alt+F11 full screen");
    }

    public string ModifierDescription => _keyMap.Describe();

    /// <summary>Current Mac key for one of <see cref="MacKeyMap.SettableKeys"/>.</summary>
    public string ModifierTarget(string windowsKey)
    {
        lock (_lock)
            return _keyMap.TargetOf(windowsKey);
    }

    /// <summary>Remaps a Windows modifier (Settings window). Releases held keys first.</summary>
    public void SetModifierTarget(string windowsKey, string macKey)
    {
        ReleaseAll("key mapping changed");
        bool changed;
        lock (_lock)
            changed = _keyMap.Set(windowsKey, macKey);
        if (changed)
            DiagLog.Write($"Mac keyboard map: {ModifierDescription}");
    }

    /// <summary>Releases every key this side is holding down on the Mac.</summary>
    public void ReleaseAll(string reason)
    {
        ushort[] keys;
        lock (_lock)
        {
            if (_heldMacKeys.Count == 0)
                return;
            // Ordinary keys first, then modifiers, like letting go of a shortcut.
            keys = _heldMacKeys.OrderBy(k => IsMacModifier(k) ? 1 : 0).ToArray();
            _heldMacKeys.Clear();
            _macSwitcherActive = false;
            _switcherAddedCommand = false;
            // The Windows keys are still physically down: keep their key-ups away from Windows,
            // which never saw them go down.
            _swallowUps.UnionWith(_physicalDown);
        }
        foreach (var key in keys)
            _send(MacInputPayload.Key(down: false, key));
        DiagLog.Write($"Released {keys.Length} Mac key(s) ({reason})");
    }

    /// <summary>Call when the connection ends (the Mac releases everything on disconnect).</summary>
    public void OnDisconnected()
    {
        lock (_lock)
        {
            _heldMacKeys.Clear();
            _loggedFirstKey = false;
            _macSwitcherActive = false;
            _switcherAddedCommand = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseAll("closing");
        StopHook();
    }

    private static bool IsMacModifier(ushort key) => key is >= 0x36 and <= 0x3F;

    // --- Hook thread ----------------------------------------------------------------------

    private void StartHook()
    {
        if (_disposed || _hookThread != null)
            return;

        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() => HookThreadMain(ready))
        {
            IsBackground = true,
            Name = "Mac keyboard hook",
        };
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(2)))
            DiagLog.Write("Mac keyboard hook did not start in time");
        _hookThread = thread;
    }

    private void StopHook()
    {
        var thread = _hookThread;
        if (thread == null)
            return;
        _hookThread = null;
        if (_hookThreadId != 0)
            PostThreadMessage(_hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        if (!thread.Join(TimeSpan.FromSeconds(1)))
            DiagLog.Write("Mac keyboard hook thread did not stop in time");
        lock (_lock)
        {
            _physicalDown.Clear();
            _swallowUps.Clear();
        }
    }

    private void HookThreadMain(ManualResetEventSlim ready)
    {
        _hookThreadId = GetCurrentThreadId();
        var hook = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            DiagLog.Write($"Mac keyboard hook could not be installed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            _hookThreadId = 0;
            ready.Set();
            return;
        }

        ready.Set();
        try
        {
            // The system calls HookProc from inside GetMessage on this thread.
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
            }
        }
        finally
        {
            UnhookWindowsHookEx(hook);
            _hookThreadId = 0;
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                if (HandleKey(info))
                    return (IntPtr)1;   // handled: Windows does not see this key
            }
            catch (Exception ex)
            {
                DiagLog.Write($"Mac keyboard hook error: {ex.Message}");
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>Returns true when the key was taken (sent to the Mac or used as the hotkey).</summary>
    private bool HandleKey(KbdLlHookStruct info)
    {
        // Keys this class typed into Windows itself (Alt+Tab hand-over) pass straight through.
        if (info.dwExtraInfo == OwnInputMarker)
            return false;

        var up = (info.flags & LlkhfUp) != 0;
        var vk = info.vkCode;
        bool firstKey = false;
        bool handOverAltTab = false, withShift = false, rightAlt = false;
        var payloads = new List<byte[]>(3);

        lock (_lock)
        {
            var wasDown = !up && !_physicalDown.Add(vk);
            if (up)
                _physicalDown.Remove(vk);

            // Only ever act on keys meant for this window.
            if (GetForegroundWindow() != _windowHandle)
                return false;

            if (up && _swallowUps.Remove(vk))
                return true;

            // Ctrl+Alt+F12: switch Mac control on/off; Ctrl+Alt+F11: full screen on/off.
            // Both work whether or not keys are going to the Mac, and never reach the Mac.
            if (!up && (vk == VkF12 || vk == VkF11) && IsDown(VkLeftCtrl, VkRightCtrl) && IsDown(VkLeftAlt, VkRightAlt))
            {
                _swallowUps.Add(vk);
                if (!wasDown)
                    _window.Dispatcher.BeginInvoke(DispatcherPriority.Send, vk == VkF12 ? _toggleControl : _toggleFullscreen);
                return true;
            }

            if (!_forwarding())
                return false;

            // AltGr on European layouts adds a fake Left Ctrl (scan code 0x21D); the Mac only
            // needs the Right Alt (Option) part.
            if ((info.scanCode & 0x200) != 0)
                return true;

            // Alt+Tab: Windows' own app switcher. Windows never saw the Alt (it went to the Mac
            // as Option), so let go of everything on the Mac and type Alt+Tab into Windows.
            if (_altTabToWindows && !up && vk == VkTab && IsDown(VkLeftAlt, VkRightAlt))
            {
                foreach (var key in _heldMacKeys.OrderBy(k => IsMacModifier(k) ? 1 : 0))
                    payloads.Add(MacInputPayload.Key(down: false, key));
                _heldMacKeys.Clear();
                _macSwitcherActive = _switcherAddedCommand = false;
                handOverAltTab = true;
                withShift = IsDown(VkLeftShift, VkRightShift);
                rightAlt = _physicalDown.Contains(VkRightAlt) && !_physicalDown.Contains(VkLeftAlt);
            }
            // Windows key + Tab: the Mac's app switcher (Command+Tab), held open by the Windows key.
            else if (_altTabToWindows && !up && vk == VkTab && IsDown(VkLeftWin, VkRightWin))
            {
                if (!_macSwitcherActive)
                {
                    _macSwitcherActive = true;
                    // If the Windows key is mapped to something other than Command (e.g.
                    // Control), let go of that; the switcher needs Command.
                    if (_keyMap.WindowsKeyTarget is { } winTarget && winTarget != MacCommand && _heldMacKeys.Remove(winTarget))
                        payloads.Add(MacInputPayload.Key(down: false, winTarget));
                    _switcherAddedCommand = _heldMacKeys.Add(MacCommand);
                    if (_switcherAddedCommand)
                        payloads.Add(MacInputPayload.Key(down: true, MacCommand));
                }
                _heldMacKeys.Add(MacTab);
                payloads.Add(MacInputPayload.Key(down: true, MacTab));
            }
            // Letting go of the Windows key closes the Mac switcher on the chosen app.
            else if (_macSwitcherActive && up && (vk == VkLeftWin || vk == VkRightWin))
            {
                _macSwitcherActive = false;
                // Let go of Command: the one added for the switcher, or the Windows key's own
                // Command (the default mapping). Either way the switch happens now.
                var held = _switcherAddedCommand ? MacCommand : _keyMap.Map(info.scanCode, (info.flags & LlkhfExtended) != 0, vk);
                if (held is { } heldKey && _heldMacKeys.Remove(heldKey))
                    payloads.Add(MacInputPayload.Key(down: false, heldKey));
                _switcherAddedCommand = false;
            }

            else
            {
                var mac = _keyMap.Map(info.scanCode, (info.flags & LlkhfExtended) != 0, vk);
                if (mac is not { } key)
                    return false;   // not forwarded: stays on Windows

                if (up)
                {
                    if (!_heldMacKeys.Remove(key))
                        return false;   // pressed before control started (or handed to Windows): let Windows have its up
                    payloads.Add(MacInputPayload.Key(down: false, key));
                }
                else
                {
                    _heldMacKeys.Add(key);   // a repeat (key held) is sent again; the Mac auto-repeats it
                    payloads.Add(MacInputPayload.Key(down: true, key));
                    if (!_loggedFirstKey)
                    {
                        _loggedFirstKey = true;
                        firstKey = true;
                    }
                }
            }
        }

        foreach (var payload in payloads)
            _send(payload);
        if (firstKey)
            DiagLog.Write("Mac input: sending keyboard to the Mac");
        if (handOverAltTab)
            _typeAltTab(withShift, rightAlt);
        return true;
    }

    /// <summary>
    /// Types Alt(+Shift)+Tab into Windows. The physical Alt is still held, so Windows' switcher
    /// stays open until it is let go, exactly like a normal Alt+Tab; those later key-ups are
    /// not taken by this class (nothing is held on the Mac any more), so Windows gets them.
    /// </summary>
    private static void TypeAltTabIntoWindows(bool withShift, bool rightAlt)
    {
        var inputs = new List<Input>(3)
        {
            KeyInput(rightAlt ? (ushort)0xA5 : (ushort)0xA4, 0x38, rightAlt),
        };
        if (withShift)
            inputs.Add(KeyInput(0xA0, 0x2A, false));
        inputs.Add(KeyInput(0x09, 0x0F, false));
        var arr = inputs.ToArray();
        if (SendInput((uint)arr.Length, arr, Marshal.SizeOf<Input>()) != arr.Length)
            DiagLog.Write($"Alt+Tab hand-over to Windows failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    }

    private static Input KeyInput(ushort vk, ushort scan, bool extended) => new()
    {
        type = 1,   // INPUT_KEYBOARD
        ki = new KeybdInput
        {
            wVk = vk,
            wScan = scan,
            dwFlags = extended ? 0x0001u : 0,   // KEYEVENTF_EXTENDEDKEY
            dwExtraInfo = OwnInputMarker,
        },
    };

    private bool IsDown(uint left, uint right) => _physicalDown.Contains(left) || _physicalDown.Contains(right);

    // --- Win32 --------------------------------------------------------------------------------

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    // INPUT is 40 bytes on x64 (the union is sized by MOUSEINPUT).
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KeybdInput ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
