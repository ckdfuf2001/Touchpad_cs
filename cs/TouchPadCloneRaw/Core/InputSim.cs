using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TouchPadCloneV2.Core;

/// <summary>
/// Win32 output primitives. All cursor/keyboard output goes through here.
/// No driver, no hooks - exactly like the original TouchMousePointer:
/// mouse_event + keybd_event + Set/GetPhysicalCursorPos (physical pixels,
/// no DPI virtualisation). No SendInput anywhere.
/// </summary>
public static class InputSim
{
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
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    /// <summary>
    /// Click injection, the same primitive the reference uses - NOT SendInput.
    /// Measured in a minimal harness: a drag built from SendInput absolute
    /// moves selected nothing, while the same drag built from the reference's
    /// calls did.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern void mouse_event(uint dwFlags, int dx, int dy,
        uint dwData, IntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan,
        uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalCursorPos(out POINT lpPoint);

    /// <summary>
    /// Cursor move in PHYSICAL pixels - the same call the working reference on
    /// this machine uses (TouchMousePointer). SetCursorPos is the logical
    /// variant and differs under DPI virtualisation; physical is unambiguous.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern bool SetPhysicalCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT2 { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT2 lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);


    /// <summary>
    /// OBSERVATION MODE (false): all mouse/keyboard/cursor output is
    /// neutered to log-only ("WOULD ..."). Recognition, logging and the HUD
    /// keep running, so touch behaviour can be measured without our output
    /// fighting it. One-line revert restores full output. Everything is in
    /// git, so nothing is lost.
    /// </summary>
    public static bool OutputEnabled = false;

    private static void Mouse(uint flags, int dx = 0, int dy = 0, int data = 0)
    {
        if (!OutputEnabled) return;   // silent: moves would flood the log
        mouse_event(flags, dx, dy, unchecked((uint)data), InjectTag);
    }

    private static void Key(ushort vk, bool up)
    {
        if (!OutputEnabled)
        {
            DebugLog.Write($"WOULD key 0x{vk:X} {(up ? "up" : "down")}");
            return;
        }
        keybd_event((byte)vk, 0, up ? KEYEVENTF_KEYUP : 0, UIntPtr.Zero);
    }

    public static (int X, int Y) Cursor()
    {
        GetPhysicalCursorPos(out var p);
        return (p.X, p.Y);
    }

    /// <summary>
    /// Test-only injection recorder. A low-level mouse hook sees the whole
    /// desktop - the physical mouse and context menus pollute it, so the
    /// selftest asserts on what WE sent instead. Off (and empty) in normal
    /// runs.
    /// </summary>
    public static bool RecordInjections;
    public static readonly List<(long Ms, string Kind, string Btn, int X, int Y)>
        Injected = new();

    private static string _lastOut = "-";

    private static void Note(string kind, string btn, int x, int y)
    {
        _lastOut = $"{kind} {btn} @({x},{y})";
        if (!RecordInjections) return;
        lock (Injected)
            Injected.Add((Environment.TickCount64, kind, btn, x, y));
    }

    /// <summary>
    /// ACTUAL system mouse state for the on-screen HUD (bottom-right):
    /// real button states + cursor position (physical + logical) +
    /// suppression. Raw input only - our own last event is NOT shown
    /// (observation: logic removed).
    /// </summary>
    public static string ActualState()
    {
        var (x, y) = Cursor();
        string log = "?";
        try { if (GetCursorPos(out var q)) log = $"{q.X},{q.Y}"; } catch { }
        bool l = (GetAsyncKeyState(0x01) & 0x8000) != 0;
        bool r = (GetAsyncKeyState(0x02) & 0x8000) != 0;
        string sup = CursorSuppressed() ? " sup" : "";
        return $"L:{(l ? "DN" : "up")} R:{(r ? "DN" : "up")} P({x},{y}) G({log}){sup}";
    }

    public static void SetCursor(int x, int y)
    {
        if (!OutputEnabled) return;   // read-modify-write would yank-jump
        var (cx, cy) = Cursor();
        Move((double)(x - cx), (double)(y - cy));
    }

    private static uint DownFlag(string button) => button switch
    {
        "right" => MOUSEEVENTF_RIGHTDOWN,
        "middle" => MOUSEEVENTF_MIDDLEDOWN,
        _ => MOUSEEVENTF_LEFTDOWN,
    };

    private static uint UpFlag(string button) => button switch
    {
        "right" => MOUSEEVENTF_RIGHTUP,
        "middle" => MOUSEEVENTF_MIDDLEUP,
        _ => MOUSEEVENTF_LEFTUP,
    };

    /// <summary>
    /// Held-button audit: counts presses without matching releases (Down/
    /// DownAt +1, Up/UpAt -1; atomic ClickAt balanced, skipped). Measured
    /// +63 left-downs over ups (buttons held forever, every move a drag).
    /// EndSession drains (while>0 Up) so leaks cannot accumulate across
    /// sessions no matter which release path missed. No-op when balanced.
    /// </summary>
    private static readonly Dictionary<string, int> _held = new();
    private static void HeldDown(string button)
    {
        _held[button] = _held.TryGetValue(button, out int n) ? n + 1 : 1;
    }
    private static void HeldUp(string button)
    {
        if (_held.TryGetValue(button, out int n) && n > 0)
            _held[button] = n - 1;
    }
    public static int DrainHeld()
    {
        int total = 0;
        foreach (var b in new[] { "left", "right", "middle" })
        {
            while (_held.TryGetValue(b, out int n) && n > 0)
            {
                _held[b] = n - 1;
                if (OutputEnabled)
                    mouse_event(UpFlag(b), 0, 0, 0, InjectTag);
                total++;
            }
        }
        if (total > 0)
            DebugLog.Write($"BTN {(OutputEnabled ? "drain" : "WOULD drain")} {total} stuck press(es)");
        return total;
    }

    /// <summary>
    /// Click at an EXACT position, the way the working reference does it: put
    /// the cursor there physically, then inject down and up with mouse_event.
    /// The pair still lands on the same pixel because the cursor is parked
    /// first and nothing else runs between the two calls.
    /// </summary>
    public static void ClickAt(int x, int y, string button)
    {
        Note("click", button, x, y);
        if (!OutputEnabled)
        {
            DebugLog.Write($"WOULD click {button} @({x},{y})");
            return;
        }
        SetPhysicalCursorPos(x, y);
        mouse_event(DownFlag(button), 0, 0, 0, InjectTag);
        mouse_event(UpFlag(button), 0, 0, 0, InjectTag);
        DebugLog.Write($"BTN click {button} @({x},{y})");
    }

    /// <summary>
    /// Park the cursor at the point, then press - the reference's way.
    /// </summary>
    public static void DownAt(int x, int y, string button)
    {
        Note("down", button, x, y);
        if (!OutputEnabled)
        {
            DebugLog.Write($"WOULD down {button} @({x},{y})");
            return;
        }
        SetPhysicalCursorPos(x, y);
        mouse_event(DownFlag(button), 0, 0, 0, InjectTag);
        HeldDown(button);
        DebugLog.Write($"BTN down {button} @({x},{y})");
    }

    /// <summary>Mirror of DownAt for the release.</summary>
    public static void UpAt(int x, int y, string button)
    {
        Note("up", button, x, y);
        if (!OutputEnabled)
        {
            DebugLog.Write($"WOULD up {button} @({x},{y})");
            return;
        }
        SetPhysicalCursorPos(x, y);
        mouse_event(UpFlag(button), 0, 0, 0, InjectTag);
        HeldUp(button);
        DebugLog.Write($"BTN up {button} @({x},{y})");
    }

    /// <summary>
    /// Park the cursor, then wheel. Wheel messages go to the window under the
    /// cursor, which is why the position is set first.
    /// </summary>
    public static void WheelAt(int x, int y, int delta, bool horizontal)
    {
        Note("wheel", (horizontal ? "h" : "v") + delta, x, y);
        if (!OutputEnabled)
        {
            DebugLog.Write($"WOULD wheel {(horizontal ? "h" : "v")}{delta} @({x},{y})");
            return;
        }
        SetPhysicalCursorPos(x, y);
        mouse_event(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL,
            0, 0, unchecked((uint)delta), InjectTag);
    }

    public static void Click(string button)
    {
        var (x, y) = Cursor();
        ClickAt(x, y, button);
    }

    /// <summary>
    /// Tag our own injections so they can be told apart from real input, the
    /// way the reference does with GetMessageExtraInfo. The signature is the
    /// convention: 0xFF515700 | (thread id &amp; 0xFF).
    /// </summary>
    private static readonly IntPtr InjectTag = new(
        unchecked((int)(0xFF515700 | (uint)(Environment.CurrentManagedThreadId & 0xFF))));

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool bShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(out CURSORINFO pci);

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public uint cbSize; public uint flags;
        public IntPtr hCursor; public POINT ptScreenPos;
    }

    private const uint CURSOR_SHOWING = 0x00000001;
    /// <summary>
    /// Windows hiding the pointer for a live touch. ShowCursor cannot clear
    /// this - it is not the ShowCursor counter - and the old restore loop kept
    /// calling ShowCursor(true) 500 times trying, which drained the shared
    /// counter so far that the cursor could never be hidden again. Measured:
    /// a stuck suppression (e.g. a tap that produced no mouse output, or a
    /// process killed mid-touch) does NOT clear by itself - but one injected
    /// mouse move does. So every touch session ends with ClearSuppression.
    /// </summary>
    private const uint CURSOR_SUPPRESSED = 0x00000002;

    public static bool CursorSuppressed()
    {
        var ci = new CURSORINFO { cbSize = (uint)Marshal.SizeOf<CURSORINFO>() };
        return GetCursorInfo(out ci) && (ci.flags & CURSOR_SUPPRESSED) != 0;
    }

    /// <summary>
    /// Self-heal for stuck suppression: a 1px round trip (net zero - the
    /// cursor ends exactly where it was), only when suppressed. A touch that
    /// produces no mouse output (an empty tap, a strip toggle) otherwise
    /// leaves the pointer invisible until something else happens to move it.
    /// Log rate-limited: at report rate this would bury the log.
    /// </summary>
    private static long _lastSuppressLog;
    public static void ClearSuppression()
    {
        if (!CursorSuppressed()) return;
        // Visibility is NOT output: the suppression-clearing wiggle always
        // runs (every touch shows the mouse). Net-zero, no buttons.
        mouse_event(MOUSEEVENTF_MOVE, 1, 0, 0, InjectTag);
        mouse_event(MOUSEEVENTF_MOVE, -1, 0, 0, InjectTag);
        long now = Environment.TickCount64;
        if (now - _lastSuppressLog > 500)
        {
            _lastSuppressLog = now;
            DebugLog.Write("SUPPRESSION stuck after touch, cleared with a nudge"
                + (CursorSuppressed() ? " (STILL suppressed)" : ""));
        }
    }

    public static void HideCursor()
    {
        if (OutputEnabled) ShowCursor(false);
    }

    /// <summary>Emergency restore: force the cursor visible again.</summary>
    public static void RestoreCursor()
    {
        if (!OutputEnabled) return;
        for (int i = 0; i < 8; i++)
        {
            var ci = new CURSORINFO { cbSize = (uint)Marshal.SizeOf<CURSORINFO>() };
            if (GetCursorInfo(out ci) && (ci.flags & CURSOR_SHOWING) != 0)
                return;
            if ((ci.flags & CURSOR_SUPPRESSED) != 0)
                return;   // nothing to do: the system clears it by itself
            ShowCursor(true);
        }
    }

    /// <summary>
    /// Relative drive with subpixel accumulation: fractions kept, integers
    /// emitted. Rounding per event loses slow motion; accumulation preserves
    /// it. Relative adds always apply (no set-vs-set fight with tracking).
    /// </summary>
    private static double _remX, _remY;
    /// <summary>Move (relative) output switch: true while rebuilding
    /// (cursor overwrite via the queue, the reference way). Buttons/keys
    /// stay neutered under OutputEnabled.</summary>
    public static bool MoveOutputEnabled = true;

    public static void Move(double dx, double dy)
    {
        _remX += dx; _remY += dy;
        int ix = (int)_remX, iy = (int)_remY;
        _remX -= ix; _remY -= iy;
        if (!MoveOutputEnabled) return;
        if (ix != 0 || iy != 0) Mouse(MOUSEEVENTF_MOVE, ix, iy);
    }

    public static void Move(int dx, int dy) => Move((double)dx, (double)dy);

    /// <summary>
    /// Absolute placement through the input queue (mouse_event
    /// MOVE|ABSOLUTE), the reference way of landing the cursor: a real
    /// input event, so it transiently clears suppression and the cursor
    /// appears AT the given point. Idempotent (no cursor read). Physical
    /// pixels; the 0-65535 space spans the virtual screen (negative
    /// origins included).
    /// </summary>
    public static void MoveAbsolute(int x, int y)
    {
        Note("move", "", x, y);
        if (!MoveOutputEnabled) return;
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
    }

    /// <summary>
    /// Put the cursor at an absolute point, in physical pixels, the way the
    /// reference does. Idempotent, so it cannot fight another writer and cannot
    /// accumulate drift.
    /// </summary>
    public static void MoveTo(int x, int y)
    {
        if (!OutputEnabled) return;   // silent, like all moves
        SetPhysicalCursorPos(x, y);
        Note("move", "", x, y);
    }

    /// <summary>
    /// Record a cursor position that was set by something other than a
    /// relative move (SetPhysicalCursorPos). Without it the injection log
    /// cannot see the drag at all - "moves=0" through a whole drag.
    /// </summary>
    public static void NotePosition(int x, int y) => Note("move", "", x, y);

    public static void Down(string button)
    {
        if (!OutputEnabled)
        {
            DebugLog.Write($"WOULD down {button}");
            return;
        }
        Mouse(DownFlag(button));
        HeldDown(button);
    }

    public static void Up(string button)
    {
        if (!OutputEnabled)
        {
            DebugLog.Write($"WOULD up {button}");
            return;
        }
        Mouse(UpFlag(button));
        HeldUp(button);
    }

    public static void Wheel(int delta, bool horizontal = false) =>
        Mouse(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, 0, 0, delta);

    public static void TapKey(int vk) { Key((ushort)vk, false); Key((ushort)vk, true); }
    public static void HoldKey(int vk, bool down) => Key((ushort)vk, !down);

    public static void Combo(int vk, bool ctrl, bool shift, bool alt)
    {
        const int VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12;
        if (ctrl) Key(VK_CONTROL, false);
        if (shift) Key(VK_SHIFT, false);
        if (alt) Key(VK_MENU, false);
        Key((ushort)vk, false);
        Key((ushort)vk, true);
        if (alt) Key(VK_MENU, true);
        if (shift) Key(VK_SHIFT, true);
        if (ctrl) Key(VK_CONTROL, true);
    }
}
