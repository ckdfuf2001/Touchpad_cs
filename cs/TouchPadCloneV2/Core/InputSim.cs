using System;
using System.Runtime.InteropServices;

namespace TouchPadCloneV2.Core;

/// <summary>
/// Win32 SendInput wrapper. All cursor/keyboard output goes through here.
/// No driver, no hooks - exactly like the original TouchMousePointer.
/// </summary>
public static class InputSim
{
    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
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
    private struct MOUSEINPUT
    {
        public int dx; public int dy; public uint mouseData;
        public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan; public uint dwFlags;
        public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type; public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    private static void Mouse(uint flags, int dx = 0, int dy = 0, int data = 0)
    {
        var inp = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION { mi = new MOUSEINPUT
            {
                dx = dx, dy = dy, mouseData = (uint)data,
                dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero
            } }
        };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    private static void Key(ushort vk, bool up)
    {
        var inp = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION { ki = new KEYBDINPUT
            {
                wVk = vk, wScan = 0,
                dwFlags = up ? KEYEVENTF_KEYUP : 0,
                time = 0, dwExtraInfo = IntPtr.Zero
            } }
        };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    public static (int X, int Y) Cursor()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    public static void SetCursor(int x, int y) =>
        SetCursorPos(x, y);

    /// <summary>
    /// Atomic click at an EXACT position: absolute-move + down + absolute-move
    /// + up in ONE SendInput batch. Measured root cause it fixes: between a
    /// parked Down and its Up (~13ms apart with logging), Windows yanks the
    /// system cursor back onto the live touch contact - observed 900px jumps
    /// (down@fake, up@finger). Scattered pairs never satisfy the OS 4x4px
    /// double-click box (singles pile up -> Explorer rename; doubles fire
    /// twice). Inside one batch nothing interleaves; the pair always lands
    /// on the same pixel.
    /// </summary>
    public static void ClickAt(int x, int y, string button)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        int nx = (int)((x - vx) * 65535.0 / Math.Max(1, vw - 1));
        int ny = (int)((y - vy) * 65535.0 / Math.Max(1, vh - 1));
        uint downFlags = button switch
        {
            "right" => MOUSEEVENTF_RIGHTDOWN,
            "middle" => MOUSEEVENTF_MIDDLEDOWN,
            _ => MOUSEEVENTF_LEFTDOWN,
        };
        uint upFlags = button switch
        {
            "right" => MOUSEEVENTF_RIGHTUP,
            "middle" => MOUSEEVENTF_MIDDLEUP,
            _ => MOUSEEVENTF_LEFTUP,
        };
        INPUT move = new()
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION { mi = new MOUSEINPUT
            {
                dx = nx, dy = ny, mouseData = 0,
                dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                time = 0, dwExtraInfo = IntPtr.Zero
            } }
        };
        INPUT dn = move;
        dn.u.mi.dwFlags = downFlags;
        dn.u.mi.dx = dn.u.mi.dy = 0;
        INPUT upEv = move;
        upEv.u.mi.dwFlags = upFlags;
        upEv.u.mi.dx = upEv.u.mi.dy = 0;
        var batch = new[] { move, dn, move, upEv };
        SendInput((uint)batch.Length, batch, Marshal.SizeOf<INPUT>());
        DebugLog.Write($"BTN click {button} @({x},{y})");
    }

    public static void Click(string button)
    {
        var (x, y) = Cursor();
        ClickAt(x, y, button);
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

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

    public static void HideCursor() => ShowCursor(false);

    /// <summary>Emergency restore: force the cursor visible again.</summary>
    public static void RestoreCursor()
    {
        for (int i = 0; i < 500; i++)
        {
            var ci = new CURSORINFO { cbSize = (uint)Marshal.SizeOf<CURSORINFO>() };
            if (GetCursorInfo(out ci) && (ci.flags & CURSOR_SHOWING) != 0)
                return;
            ShowCursor(true);
        }
    }

    public static void Move(int dx, int dy)
    {
        if (dx != 0 || dy != 0) Mouse(MOUSEEVENTF_MOVE, dx, dy);
    }

    public static void Down(string button)
    {
        Mouse(button switch
        {
            "right" => MOUSEEVENTF_RIGHTDOWN,
            "middle" => MOUSEEVENTF_MIDDLEDOWN,
            _ => MOUSEEVENTF_LEFTDOWN,
        });
    }

    public static void Up(string button)
    {
        Mouse(button switch
        {
            "right" => MOUSEEVENTF_RIGHTUP,
            "middle" => MOUSEEVENTF_MIDDLEUP,
            _ => MOUSEEVENTF_LEFTUP,
        });
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
