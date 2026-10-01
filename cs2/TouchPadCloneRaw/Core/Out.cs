using System;
using System.Runtime.InteropServices;

namespace TouchPadRaw.Core;

/// <summary>
/// System-state reads (cursor, buttons, suppression) for the HUD.
/// NO output methods yet - gestures arrive per dictation, and every one
/// goes through here so injections stay tagged and countable.
/// </summary>
public static class Out
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT2 { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT2 lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(out CURSORINFO pci);

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public uint cbSize; public uint flags;
        public IntPtr hCursor; public POINT ptScreenPos;
    }

    private const uint CURSOR_SUPPRESSED = 0x00000002;

    /// <summary>Last-known-good cursor (physical px). Never (0,0).</summary>
    public static (int X, int Y) Cursor()
    {
        try
        {
            if (GetPhysicalCursorPos(out var p) && (p.X != 0 || p.Y != 0))
                return (p.X, p.Y);
        }
        catch { }
        return (0, 0);
    }

    public static bool Suppressed()
    {
        try
        {
            var ci = new CURSORINFO { cbSize = (uint)Marshal.SizeOf<CURSORINFO>() };
            return GetCursorInfo(out ci) && (ci.flags & CURSOR_SUPPRESSED) != 0;
        }
        catch { return false; }
    }

    /// <summary>Raw system mouse state for the HUD. Reads only.</summary>
    public static string ActualState()
    {
        var (x, y) = Cursor();
        string log = "?";
        try { if (GetCursorPos(out var q)) log = $"{q.X},{q.Y}"; } catch { }
        bool l = false, r = false;
        try
        {
            l = (GetAsyncKeyState(0x01) & 0x8000) != 0;
            r = (GetAsyncKeyState(0x02) & 0x8000) != 0;
        }
        catch { }
        string sup = Suppressed() ? " sup" : "";
        return $"L:{(l ? "DN" : "up")} R:{(r ? "DN" : "up")} P({x},{y}) G({log}){sup}";
    }

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy,
        uint dwData, IntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool SetPhysicalCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint MOUSEEVENTF_XDOWN = 0x0080;
    private const uint MOUSEEVENTF_XUP = 0x0100;
    private const uint INPUT_MOUSE = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs,
        [MarshalAs(UnmanagedType.LPArray), In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern UIntPtr GetMessageExtraInfo();

    /// <summary>True when the message being processed carries our tag
    /// (our own re-emitted input looping back).</summary>
    public static bool IsOurs()
    {
        try
        {
            return (GetMessageExtraInfo().ToUInt64() & 0xFFFFFF00UL)
                == 0xFF515700UL;
        }
        catch { return false; }
    }

    /// <summary>
    /// Re-emit as REAL input at the same screen point (full fidelity:
    /// focus/capture/drag work, unlike posted messages). Tagged.
    /// Coordinates are physical screen pixels.
    /// </summary>
    public static void Forward(uint flags, int x, int y, uint data)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77);
        int vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        if (vw <= 1 || vh <= 1) return;
        var inp = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT
            {
                dx = (int)((x - vx) * 65535.0 / (vw - 1)),
                dy = (int)((y - vy) * 65535.0 / (vh - 1)),
                mouseData = data,
                dwFlags = (uint)(flags | MOUSEEVENTF_ABSOLUTE),
                time = 0,
                dwExtraInfo = InjectTag,
            },
        };
        inp.mi.dx = Math.Clamp(inp.mi.dx, 0, 65535);
        inp.mi.dy = Math.Clamp(inp.mi.dy, 0, 65535);
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    private static readonly IntPtr InjectTag = new(
        unchecked((int)(0xFF515700u | (uint)(Environment.CurrentManagedThreadId & 0xFF))));

    /// <summary>Logical cursor position (what apps see). Practically never fails.</summary>
    public static (int X, int Y) Logical()
    {
        try
        {
            if (GetCursorPos(out var q)) return (q.X, q.Y);
        }
        catch { }
        return (0, 0);
    }

    private static int _anchorX = int.MinValue, _anchorY = int.MinValue;

    /// <summary>Fix #1: previous-G anchor. Every mouse event below fires
    /// here instead of its requested position while set.</summary>
    public static void SetAnchor(int x, int y) { _anchorX = x; _anchorY = y; }

    public static (int X, int Y) Anchor() => (_anchorX, _anchorY);

    /// <summary>Anchor lives one touch session (set on first DOWN).</summary>
    public static void ClearAnchor() { _anchorX = int.MinValue; _anchorY = int.MinValue; }

    /// <summary>Absolute queue placement without click.</summary>
    public static void PlaceAt(int x, int y)
    {
        Log.Write($"BTN place @({x},{y})");
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77);
        int vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        if (vw > 1 && vh > 1)
        {
            int nx = (int)((x - vx) * 65535.0 / (vw - 1));
            int ny = (int)((y - vy) * 65535.0 / (vh - 1));
            mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                Math.Clamp(nx, 0, 65535), Math.Clamp(ny, 0, 65535),
                0, InjectTag);
        }
        else
        {
            try { SetPhysicalCursorPos(x, y); } catch { }
        }
    }

    /// <summary>Press (at given coords). Tagged.</summary>
    public static void DownAt(int x, int y, string button = "left")
    {
        PlaceAt(x, y);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, InjectTag);
        Log.Write($"BTN down {button} @({x},{y})");
    }

    /// <summary>Release (at given coords). Tagged.</summary>
    public static void UpAt(int x, int y, string button = "left")
    {
        PlaceAt(x, y);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, InjectTag);
        Log.Write($"BTN up {button} @({x},{y})");
    }

    /// <summary>Positionless release. Tagged.</summary>
    public static void Up(string button = "left")
    {
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, InjectTag);
        Log.Write($"BTN up {button} (safety)");
    }
}
