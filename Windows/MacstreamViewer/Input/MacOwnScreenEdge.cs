using System.Runtime.InteropServices;

namespace MacstreamViewer.Input;

/// <summary>Where the MacBook's own screen is, seen from the PC monitor the viewer is on.</summary>
public enum MacSide
{
    Off,
    Left,
    Right,
    Above,
    Below,
}

/// <summary>Which part of that monitor edge leads to the Mac.</summary>
public enum EdgePart
{
    /// <summary>The part with no other PC monitor beyond it (the second half if there is one all along).</summary>
    Automatic,
    Whole,
    /// <summary>Upper half of a left/right edge, left half of a top/bottom edge.</summary>
    FirstHalf,
    /// <summary>Lower half of a left/right edge, right half of a top/bottom edge.</summary>
    SecondHalf,
}

public readonly record struct OwnScreenOptions(MacSide Side, EdgePart Part)
{
    public static readonly OwnScreenOptions Off = new(MacSide.Off, EdgePart.Automatic);

    /// <summary>Wire value for INPUT_EVENT OwnScreenEnter: 0 = left, 1 = right, 2 = above, 3 = below.</summary>
    public byte WireSide => (byte)(Side - MacSide.Left);

    public override string ToString() => Side == MacSide.Off ? "off" : $"{Side}, {Part}";
}

/// <summary>Screen rectangle in physical pixels; Right and Bottom are exclusive.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ScreenRect
{
    public int Left, Top, Right, Bottom;

    public ScreenRect(int left, int top, int right, int bottom)
    {
        Left = left; Top = top; Right = right; Bottom = bottom;
    }

    public override string ToString() => $"({Left},{Top})-({Right},{Bottom})";
}

/// <summary>
/// The stretch of the viewer's edge that leads to the Mac's own screen, in physical screen
/// pixels. Line is the outermost pixel column (left/right) or row (above/below) of the video
/// area on that side; A0..A1 (A1 exclusive) runs along it: y for a left/right edge, x for a
/// top/bottom edge.
/// </summary>
public readonly record struct EdgeZone(MacSide Side, int Line, int A0, int A1)
{
    private bool Vertical => Side is MacSide.Left or MacSide.Right;

    /// <summary>How far the point is inside from that edge (0 = on the edge pixel).</summary>
    public int DistanceFromEdge(int x, int y) => Side switch
    {
        MacSide.Left => x - Line,
        MacSide.Right => Line - x,
        MacSide.Above => y - Line,
        _ => Line - y,
    };

    public bool Contains(int x, int y)
    {
        var c = Vertical ? y : x;
        return c >= A0 && c < A1;
    }

    /// <summary>0..1 along the zone.</summary>
    public float Along(int x, int y)
    {
        var c = Vertical ? y : x;
        return A1 - A0 <= 1 ? 0.5f : (float)Math.Clamp((c - A0) / (double)(A1 - A0 - 1), 0, 1);
    }

    /// <summary>The point <paramref name="inset"/> pixels inside the edge, at 0..1 along the zone.</summary>
    public (int X, int Y) PointAt(float along, int inset)
    {
        var c = A0 + (int)Math.Round(Math.Clamp(along, 0, 1) * Math.Max(0, A1 - A0 - 1));
        return Side switch
        {
            MacSide.Left => (Line + inset, c),
            MacSide.Right => (Line - inset, c),
            MacSide.Above => (c, Line + inset),
            _ => (c, Line - inset),
        };
    }

    /// <summary>A cursor clip that only stops the pointer at this edge (everything on the
    /// other sides stays reachable), so it can't slip past the viewer onto the Windows desktop
    /// or a PC monitor beyond this part of the edge.</summary>
    public ScreenRect Barrier(ScreenRect desktop) => Side switch
    {
        MacSide.Left => desktop with { Left = Line },
        MacSide.Right => desktop with { Right = Line + 1 },
        MacSide.Above => desktop with { Top = Line },
        _ => desktop with { Bottom = Line + 1 },
    };
}

/// <summary>Monitor geometry and the Win32 cursor calls used for moving the mouse onto the
/// Mac's own screen (see MacMouseController).</summary>
public static class MacOwnScreenEdge
{
    /// <summary>
    /// Finds the zone on the viewer's video area (the part of the window below the toolbar and
    /// above the status bar, or the whole screen in full screen). It works however the viewer
    /// is placed: full screen, maximized, snapped or a normal window; the edge is always the
    /// viewer's own edge on that side, kept on the monitor the viewer is on.
    /// The halves are halves of that edge. Automatic: if the edge lies on the monitor's edge,
    /// the part with no other PC monitor beyond it; otherwise (a window, or the taskbar or the
    /// status bar beyond it) the whole edge.
    /// </summary>
    public static bool TryGetZone(IntPtr hwnd, ScreenRect surface, OwnScreenOptions options, out EdgeZone zone)
    {
        zone = default;
        if (options.Side == MacSide.Off)
            return false;
        var monitorHandle = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitorHandle == IntPtr.Zero || !GetMonitorInfo(monitorHandle, ref info))
            return false;
        var m = info.Monitor;

        // The part of the video area on this monitor.
        var s = new ScreenRect(Math.Max(surface.Left, m.Left), Math.Max(surface.Top, m.Top),
            Math.Min(surface.Right, m.Right), Math.Min(surface.Bottom, m.Bottom));
        if (s.Right - s.Left < 16 || s.Bottom - s.Top < 16)
            return false;

        var (line, onMonitorEdge) = options.Side switch
        {
            MacSide.Left => (s.Left, s.Left <= m.Left + 1),
            MacSide.Right => (s.Right - 1, s.Right >= m.Right - 1),
            MacSide.Above => (s.Top, s.Top <= m.Top + 1),
            _ => (s.Bottom - 1, s.Bottom >= m.Bottom - 1),
        };

        var vertical = options.Side is MacSide.Left or MacSide.Right;
        int start = vertical ? s.Top : s.Left;
        int end = vertical ? s.Bottom : s.Right;
        int mid = start + (end - start) / 2;
        (int a0, int a1) = options.Part switch
        {
            EdgePart.Whole => (start, end),
            EdgePart.FirstHalf => (start, mid),
            EdgePart.SecondHalf => (mid, end),
            _ => onMonitorEdge ? AutomaticPart(m, options.Side, start, end, mid) : (start, end),
        };
        if (a1 - a0 < 8)
            return false;
        zone = new EdgeZone(options.Side, line, a0, a1);
        return true;
    }

    /// <summary>The longest stretch of the edge with no other monitor beyond it, if it is at
    /// least a fifth of the edge; otherwise the second half (lower / right).</summary>
    private static (int, int) AutomaticPart(ScreenRect m, MacSide side, int start, int end, int mid)
    {
        var vertical = side is MacSide.Left or MacSide.Right;
        var covered = new List<(int A, int B)>();
        foreach (var n in Monitors())
        {
            var adjacent = side switch
            {
                MacSide.Left => Math.Abs(n.Right - m.Left) <= 1,
                MacSide.Right => Math.Abs(n.Left - m.Right) <= 1,
                MacSide.Above => Math.Abs(n.Bottom - m.Top) <= 1,
                _ => Math.Abs(n.Top - m.Bottom) <= 1,
            };
            if (!adjacent)
                continue;
            int a = Math.Max(start, vertical ? n.Top : n.Left);
            int b = Math.Min(end, vertical ? n.Bottom : n.Right);
            if (b > a)
                covered.Add((a, b));
        }

        covered.Sort();
        int bestA = start, bestB = start, cursor = start;
        foreach (var (a, b) in covered)
        {
            if (a - cursor > bestB - bestA)
                (bestA, bestB) = (cursor, a);
            cursor = Math.Max(cursor, b);
        }
        if (end - cursor > bestB - bestA)
            (bestA, bestB) = (cursor, end);

        return bestB - bestA >= (end - start) / 5 ? (bestA, bestB) : (mid, end);
    }

    /// <summary>The whole Windows desktop (all monitors).</summary>
    public static ScreenRect Desktop()
    {
        int x = GetSystemMetrics(76), y = GetSystemMetrics(77);   // SM_X/YVIRTUALSCREEN
        return new ScreenRect(x, y, x + GetSystemMetrics(78), y + GetSystemMetrics(79));
    }

    private static List<ScreenRect> Monitors()
    {
        var list = new List<ScreenRect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref ScreenRect rect, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(h, ref info))
                list.Add(info.Monitor);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static bool TryGetCursor(out int x, out int y)
    {
        if (GetCursorPos(out var p))
        {
            x = p.X; y = p.Y;
            return true;
        }
        x = y = 0;
        return false;
    }

    public static void SetCursor(int x, int y) => SetCursorPos(x, y);

    public static void Clip(ScreenRect rect) => ClipCursor(ref rect);

    public static void Unclip() => ClipCursor(IntPtr.Zero);

    // --- Win32 ---

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public ScreenRect Monitor;
        public ScreenRect Work;
        public uint Flags;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref ScreenRect rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(ref ScreenRect rect);

    [DllImport("user32.dll", EntryPoint = "ClipCursor")]
    private static extern bool ClipCursor(IntPtr rect);
}
