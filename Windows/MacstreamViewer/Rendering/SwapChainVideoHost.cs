using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MacstreamViewer.Rendering;

/// <summary>
/// A native child window covering the video area. The Mac stream is presented into it with a
/// DXGI flip-model swap chain (see D3D11VideoRenderer swap-chain mode), which bypasses WPF's
/// own composition.
///
/// The window is a plain "static" control without SS_NOTIFY, so it answers WM_NCHITTEST with
/// HTTRANSPARENT: mouse input passes through to the WPF window underneath, so the existing WPF
/// mouse handling (cursor hiding, and later Windows-to-Mac control) keeps working unchanged.
/// Like any HwndHost, WPF content can't be drawn on top of it ("airspace"), so MainWindow only
/// shows it while video is actually being presented.
/// </summary>
public sealed class SwapChainVideoHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const int WmSize = 0x0005;
    private const int WmEraseBkgnd = 0x0014;

    public IntPtr ChildHandle { get; private set; }

    /// <summary>Client size in physical pixels.</summary>
    public int PixelWidth { get; private set; }

    public int PixelHeight { get; private set; }

    /// <summary>Raised on the UI thread when the client size (physical pixels) changes.</summary>
    public event Action<int, int>? PixelSizeChanged;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        ChildHandle = CreateWindowEx(
            0,
            "static",
            string.Empty,
            WsChild | WsVisible | WsClipSiblings | WsClipChildren,
            0,
            0,
            1,
            1,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (ChildHandle == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        return new HandleRef(this, ChildHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
        ChildHandle = IntPtr.Zero;
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmEraseBkgnd:
                // The swap chain paints the whole client area; skip GDI background fills (flicker).
                handled = true;
                return new IntPtr(1);
            case WmSize:
                var packed = lParam.ToInt64();
                var width = (int)(packed & 0xFFFF);
                var height = (int)((packed >> 16) & 0xFFFF);
                if (width != PixelWidth || height != PixelHeight)
                {
                    PixelWidth = width;
                    PixelHeight = height;
                    PixelSizeChanged?.Invoke(width, height);
                }
                break;
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
