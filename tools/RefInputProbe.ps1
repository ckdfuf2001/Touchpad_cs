# Records every low-level mouse/keyboard event on the desktop, with dwExtraInfo,
# so the reference's behaviour can be MEASURED instead of guessed.
#
#   powershell -ExecutionPolicy Bypass -File tools\RefInputProbe.ps1
#   ... perform gestures on TouchMousePointer ...
#   Ctrl+C in this window to stop.
# Output: %TEMP%\ref_probe.log
#
# dwExtraInfo tells injected input apart from real input: mouse_event and
# SendInput tag their events (0xFF515700 | tid), so "what did the reference emit
# for this gesture" is answered directly instead of inferred.

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class Probe {
    private const int WH_MOUSE_LL = 14;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
    private const int WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101,
                      WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT {
        public POINT pt; public uint mouseData, flags, time; public IntPtr extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT {
        public uint vkCode, scanCode, flags, time; public IntPtr extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG {
        public IntPtr hwnd; public uint message; public IntPtr wParam, lParam;
        public uint time; public POINT pt;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int id, HookProc p, IntPtr h, uint t);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string n);
    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG m);

    private static HookProc _mp, _kp;
    private static IntPtr _mh, _kh;
    private static StreamWriter _w;
    private static readonly object _lock = new object();
    private static long _t0 = Environment.TickCount;
    private static int _moves;

    private static void Log(string s) {
        lock (_lock) { try { _w.WriteLine(s); _w.Flush(); } catch { } }
    }

    private static string Who(long tag) {
        if (tag == 0) return "real";
        if ((tag & 0xFFFFFF00L) == 0xFF515700L) return "injected";
        return "tag:" + tag.ToString("X");
    }

    private static IntPtr MHook(int n, IntPtr w, IntPtr l) {
        if (n >= 0) {
            int msg = w.ToInt32();
            MSLLHOOKSTRUCT s = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(MSLLHOOKSTRUCT));
            string name = null; string extra = "";
            if (msg == WM_MOUSEMOVE) {
                // Every 20th move keeps the log readable; buttons/keys always.
                if ((++_moves % 20) != 0) return CallNextHookEx(_mh, n, w, l);
                name = "move";
            }
            else if (msg == WM_LBUTTONDOWN) name = "L-down";
            else if (msg == WM_LBUTTONUP) name = "L-up";
            else if (msg == WM_RBUTTONDOWN) name = "R-down";
            else if (msg == WM_RBUTTONUP) name = "R-up";
            else if (msg == WM_MBUTTONDOWN) name = "M-down";
            else if (msg == WM_MBUTTONUP) name = "M-up";
            else if (msg == WM_MOUSEWHEEL) { name = "wheel"; extra = " " + (short)(s.mouseData >> 16); }
            else if (msg == WM_MOUSEHWHEEL) { name = "hwheel"; extra = " " + (short)(s.mouseData >> 16); }
            if (name != null)
                Log(string.Format("{0,7} {1,-6} ({2},{3}) {4}{5}",
                    (long)Environment.TickCount - _t0, name, s.pt.x, s.pt.y,
                    Who(s.extra.ToInt64()), extra));
        }
        return CallNextHookEx(_mh, n, w, l);
    }

    private static IntPtr KHook(int n, IntPtr w, IntPtr l) {
        if (n >= 0) {
            int msg = w.ToInt32();
            if (msg == WM_KEYDOWN || msg == WM_KEYUP ||
                msg == WM_SYSKEYDOWN || msg == WM_SYSKEYUP) {
                KBDLLHOOKSTRUCT s = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(KBDLLHOOKSTRUCT));
                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                Log(string.Format("{0,7} key {1,-4} {2} {3}",
                    (long)Environment.TickCount - _t0, s.vkCode,
                    down ? "down" : "up", Who(s.extra.ToInt64())));
            }
        }
        return CallNextHookEx(_kh, n, w, l);
    }

    public static void Start(string path) {
        _w = new StreamWriter(path, false, Encoding.UTF8);
        _w.WriteLine("elapsed event  position source");
        _t0 = Environment.TickCount;
        _mp = MHook; _kp = KHook;
        IntPtr mod = GetModuleHandle(null);
        _mh = SetWindowsHookEx(WH_MOUSE_LL, _mp, mod, 0);
        _kh = SetWindowsHookEx(WH_KEYBOARD_LL, _kp, mod, 0);
        Log("hooks mouse=" + (_mh != IntPtr.Zero) + " kbd=" + (_kh != IntPtr.Zero));
        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        UnhookWindowsHookEx(_mh);
        UnhookWindowsHookEx(_kh);
        lock (_lock) { try { _w.Close(); } catch { } }
    }
}
'@

$log = Join-Path $env:TEMP "ref_probe.log"
Write-Host "Recording to $log - gesture on the reference app, Ctrl+C here to stop."
[Probe]::Start($log)
