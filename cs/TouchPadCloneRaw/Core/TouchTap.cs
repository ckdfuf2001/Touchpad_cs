using System;
using System.Runtime.InteropServices;

namespace TouchPadCloneV2.Core;

/// <summary>
/// System-wide low-level mouse tap (WH_MOUSE_LL): logs every mouse event
/// on the desktop - ours (tagged) and external (physical mouse, other
/// apps, OS) alike. Buttons/wheel always; moves throttled to a heartbeat.
/// Read-only: never swallows or modifies.
/// </summary>
public static class MouseTap
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
    private const int WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;
    private const int WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook,
        HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode,
        IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private static HookProc? _proc;
    private static IntPtr _hook;
    private static long _lastMoveLog;
    private static long _lastSwallowLog;

    /// <summary>Our pad rect, physical px (set by MainWindow tick).
    /// Used to keep pad coords out of the next-touch anchor.</summary>
    public static int PadL, PadT, PadR, PadB;

    public static void SetPadRect(int l, int t, int r, int b)
    { PadL = l; PadT = t; PadR = r; PadB = b; }

    public static bool InPad(int x, int y) =>
        x >= PadL && x < PadR && y >= PadT && y < PadB;

    public static void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = Proc;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc,
            GetModuleHandle(null), 0);
        Log.Write($"MOUSE tap hook={(_hook != IntPtr.Zero ? "on" : "FAILED")}");
    }

    public static void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private static IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int what = wParam.ToInt32();
                ulong extra = ms.dwExtraInfo.ToUInt64();
                bool tagged = (extra & 0xFFFFFF00UL) == 0xFF515700UL;
                bool promoted = tagged && (extra & 0x80UL) != 0;
                string tag = !tagged ? "ext" : (promoted ? "prom" : "ours");
                long now = Environment.TickCount64;
                // Swallow touch-promoted mouse everywhere: the panel maps
                // 1:1 to the screen, so touches outside our window drive
                // the real desktop (phantom clicks, cursor yank, double-
                // click tracking reset). Our own tag never sets 0x80, the
                // physical mouse never carries this tag, so real input
                // always passes through. Touch messages themselves are
                // unaffected (this hook only sees mouse).
                if (promoted)
                {
                    if (what == WM_MOUSEMOVE)
                    {
                        if (now - _lastSwallowLog > 1000)
                        {
                            _lastSwallowLog = now;
                            Log.Write($"SWALLOW prom move @({ms.pt.X},{ms.pt.Y})");
                        }
                    }
                    else Log.Write($"SWALLOW prom {what} @({ms.pt.X},{ms.pt.Y})");
                    return (IntPtr)1;
                }
                switch (what)
                {
                    case WM_MOUSEMOVE:
                        if (now - _lastMoveLog > 500)
                        {
                            _lastMoveLog = now;
                            Log.Write($"EXT move @({ms.pt.X},{ms.pt.Y}) [{tag}]");
                        }
                        break;
                    case WM_LBUTTONDOWN:
                        Log.Write($"EXT L-down @({ms.pt.X},{ms.pt.Y}) [{tag}]");
                        break;
                    case WM_LBUTTONUP:
                        Log.Write($"EXT L-up @({ms.pt.X},{ms.pt.Y}) [{tag}]");
                        break;
                    case WM_RBUTTONDOWN:
                        Log.Write($"EXT R-down @({ms.pt.X},{ms.pt.Y}) [{tag}]");
                        break;
                    case WM_RBUTTONUP:
                        Log.Write($"EXT R-up @({ms.pt.X},{ms.pt.Y}) [{tag}]");
                        break;
                    case WM_MBUTTONDOWN:
                        Log.Write($"EXT M-down @({ms.pt.X},{ms.pt.Y}) [{tag}]");
                        break;
                    case WM_MBUTTONUP:
                        Log.Write($"EXT M-up @({ms.pt.X},{ms.pt.Y}) [{tag}]");
                        break;
                    case WM_MOUSEWHEEL:
                        Log.Write($"EXT wheel {(short)(ms.mouseData >> 16)} @({ms.pt.X},{ms.pt.Y}) [{tag}]");
                        break;
                    case WM_MOUSEHWHEEL:
                        Log.Write($"EXT hwheel {(short)(ms.mouseData >> 16)} @({ms.pt.X},{ms.pt.Y}) [{tag}]");
                        break;
                    case WM_XBUTTONDOWN:
                    case WM_XBUTTONUP:
                        Log.Write($"EXT X{(what == WM_XBUTTONDOWN ? "down" : "up")} x{(ms.mouseData >> 16)} @({ms.pt.X},{ms.pt.Y}) [{tag}]");
                        break;
                }
            }
        }
        catch { }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
