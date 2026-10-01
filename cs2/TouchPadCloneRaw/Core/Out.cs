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
}
