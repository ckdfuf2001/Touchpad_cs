using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using TouchPadCloneV2.Core;

namespace TouchPadCloneV2;

/// <summary>
/// In-process touch repro: drives the REAL PadWindow handlers with a fake
/// TouchDevice (real timers, real SendInput, real state machine) while a
/// low-level mouse hook records every synthetic click with position.
/// Run: TOUCHPAD_SELFTEST=1 dist\TouchPadCloneV2.exe  (console output, exit code)
/// </summary>
public static class SelfTest
{
    // ---- fake touch device ----
    private sealed class FakeTouch : TouchDevice
    {
        public Point Pos;
        public FakeTouch(int id) : base(id) { }
        public override TouchPoint GetTouchPoint(IInputElement relativeTo) =>
            new(this, Pos, new Rect(Pos, new Size(1, 1)), TouchAction.Move);
        public override TouchPointCollection GetIntermediateTouchPoints(
            IInputElement relativeTo)
        {
            var c = new TouchPointCollection();
            c.Add(GetTouchPoint(relativeTo));
            return c;
        }
    }

    // ---- mouse LL hook recorder ----
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MOUSEWHEEL = 0x020A;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt; public uint mouseData; public uint flags;
        public uint time; public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn,
        IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode,
        IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public sealed record Click(long Ms, string Btn, bool Down, int X, int Y);

    private static readonly List<Click> _clicks = new();
    private static HookProc? _hookProc;
    private static IntPtr _hook;
    private static readonly long T0 =
        Environment.TickCount64;

    private static IntPtr Hook(int nCode, IntPtr wp, IntPtr lp)
    {
        if (nCode >= 0)
        {
            int msg = wp.ToInt32();
            string? btn = msg switch
            {
                WM_LBUTTONDOWN or WM_LBUTTONUP => "L",
                WM_RBUTTONDOWN or WM_RBUTTONUP => "R",
                _ => null,
            };
            if (btn != null)
            {
                var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lp);
                lock (_clicks)
                    _clicks.Add(new Click(Environment.TickCount64 - T0, btn,
                        msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN,
                        s.pt.x, s.pt.y));
            }
        }
        return CallNextHookEx(_hook, nCode, wp, lp);
    }

    // ---- driver ----
    private static PadWindow? _pad;
    private static FrameworkElement? _surface;
    private static MethodInfo? _mDown, _mMove, _mUp;
    private static int _fails;

    private static TouchEventArgs Ev(FakeTouch dev)
    {
        var args = new TouchEventArgs(dev, Environment.TickCount);
        args.RoutedEvent = UIElement.TouchDownEvent;
        return args;
    }

    private static void Down(FakeTouch d, double x, double y)
    {
        d.Pos = new Point(x, y);
        _mDown!.Invoke(_pad, new object[] { _surface!, Ev(d) });
    }

    private static void Move(FakeTouch d, double x, double y)
    {
        d.Pos = new Point(x, y);
        _mMove!.Invoke(_pad, new object[] { _surface!, Ev(d) });
    }

    private static void Up(FakeTouch d)
    {
        _mUp!.Invoke(_pad, new object[] { _surface!, Ev(d) });
    }

    private static void Check(bool cond, string what, string info = "")
    {
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {what} {info}");
        if (!cond)
        {
            Console.WriteLine("  raw hook tail:");
            int from = Math.Max(0, _clicks.Count - 12);
            for (int i = from; i < _clicks.Count; i++)
            {
                var e = _clicks[i];
                Console.WriteLine($"    {e.Ms} {e.Btn} {(e.Down ? "dn" : "up")} ({e.X},{e.Y})");
            }
            _fails++;
        }
    }

    private static List<Click> Since(int n) =>
        _clicks.GetRange(n, _clicks.Count - n);

    private static bool Stuck()
    {
        for (int i = 0; i < 5; i++)
        {
            if ((GetAsyncKeyState(1) & 0x8000) != 0 ||
                (GetAsyncKeyState(2) & 0x8000) != 0) return true;
            Task.Delay(20).Wait();
        }
        return false;
    }

    public static async Task<int> Run()
    {
        Console.WriteLine("== TouchPadCloneV2 selftest ==");
        _hookProc = Hook;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc,
            GetModuleHandle(null), 0);
        Console.WriteLine($"hook={_hook != IntPtr.Zero}");

        // sandbox: notepad eats stray clicks harmlessly
        Process? np = null;
        try
        {
            np = Process.Start(new ProcessStartInfo("notepad.exe")
                { UseShellExecute = true });
            for (int i = 0; i < 50 && (np == null || np.MainWindowHandle == IntPtr.Zero); i++)
            {
                await Task.Delay(200);
                try { np?.Refresh(); } catch { }
            }
        }
        catch (Exception ex) { Console.WriteLine("notepad: " + ex.Message); }

        var settings = AppSettings.Load();
        _pad = new PadWindow(settings);
        // real startup path also does this - layout must be set or HitTest
        // finds no tile and every gesture silently no-ops:
        try
        {
            var presets = PresetParser.ParseFile(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "presets", "Default.ini"));
            _pad.SetLayout(presets["floatpad"]);
        }
        catch (Exception ex) { Console.WriteLine("preset: " + ex.Message); }
        _surface = (FrameworkElement)_pad.FindName("Surface");
        var t = typeof(PadWindow);
        const BindingFlags NF = BindingFlags.NonPublic | BindingFlags.Instance;
        Type[] sig = [typeof(object), typeof(TouchEventArgs)];
        _mDown = t.GetMethod("OnTouchDown", NF, null, sig, null);
        _mMove = t.GetMethod("OnTouchMove", NF, null, sig, null);
        _mUp = t.GetMethod("OnTouchUp", NF, null, sig, null);
        _pad.Show();
        await Task.Delay(500);

        // anchor fake into notepad via reflection (safe click zone)
        var fx = t.GetField("_fakeX", NF);
        var fy = t.GetField("_fakeY", NF);
        var fi = t.GetField("_fakeInit", NF);
        try
        {
            if (np != null)
            {
                await Task.Delay(300);
                var r = new RECT();
                // foreground notepad approx: assume centered; use its rect
                GetWindowRect(np.MainWindowHandle, ref r);
                double cx = (r.L + r.R) / 2.0, cy = (r.T + r.B) / 2.0 + 40;
                fx!.SetValue(_pad, cx); fy!.SetValue(_pad, cy);
                fi!.SetValue(_pad, true);
                Console.WriteLine($"sandbox notepad rect=({r.L},{r.T},{r.R},{r.B}) fake=({cx},{cy})");
            }
        }
        catch (Exception ex) { Console.WriteLine("sandbox: " + ex.Message); }

        double px = 170, py = 150; // pad-tile point (DIPs, floatpad center)
        int id = 100;

        // ---- 1. tap ----
        {
            int n = _clicks.Count;
            var d = new FakeTouch(id++);
            Down(d, px, py); await Task.Delay(80); Up(d);
            await Task.Delay(300);
            var c = Since(n);
            Check(c.Count == 2 && c[0].Down && !c[1].Down && c[0].Btn == "L",
                "tap = one left click", $"ev={c.Count}");
            Check(!Stuck(), "tap: no stuck button");
        }
        // ---- 2. long hold 700ms ----
        {
            int n = _clicks.Count;
            var d = new FakeTouch(id++);
            Down(d, px, py); await Task.Delay(700); Up(d);
            await Task.Delay(300);
            var c = Since(n);
            Check(c.Count == 2 && c[0].Btn == "R" && c[0].Down && !c[1].Down,
                "long-press = one right click", $"ev={c.Count}");
            Check(!Stuck(), "hold: no stuck button");
        }
        // ---- 3. double + immediate release (double-click) ----
        {
            int n = _clicks.Count;
            var a = new FakeTouch(id++);
            Down(a, px, py); await Task.Delay(60); Up(a);
            await Task.Delay(150);
            var b = new FakeTouch(id++);
            Down(b, px, py); await Task.Delay(80); Up(b);
            await Task.Delay(400);
            var c = Since(n);
            int downs = 0;
            foreach (var e in c) if (e.Down && e.Btn == "L") downs++;
            Check(downs == 3, "double-tap quick = click + double (3 downs)",
                $"downs={downs}");
            Check(!Stuck(), "dbltap: no stuck button");
        }
        // ---- 4. double + HOLD + drag + release ----
        {
            int n = _clicks.Count;
            var a = new FakeTouch(id++);
            Down(a, px, py); await Task.Delay(60); Up(a);
            await Task.Delay(150);
            var b = new FakeTouch(id++);
            Down(b, px, py); await Task.Delay(400); // hold, no move
            for (int i = 1; i <= 5; i++)
            {
                Move(b, px + 12 * i, py); await Task.Delay(30);
            }
            await Task.Delay(100);
            Up(b);
            await Task.Delay(400);
            var c = Since(n);
            int downs = 0;
            foreach (var e in c) if (e.Down && e.Btn == "L") downs++;
            Check(downs == 2, "double-hold-drag = click + hold (2 downs, no extra)",
                $"downs={downs}");
            Check(!Stuck(), "dblhold: no stuck button");
        }
        // ---- 5. triple tap quick: click + natural double + triple event ----
        {
            int n = _clicks.Count;
            for (int k = 0; k < 3; k++)
            {
                var d = new FakeTouch(id++);
                Down(d, px, py); await Task.Delay(60); Up(d);
                await Task.Delay(150);
            }
            await Task.Delay(400);
            var c = Since(n);
            int downs = 0;
            foreach (var e in c) if (e.Down && e.Btn == "L") downs++;
            // 1 + (down + extra) + (down + triple×3) = 7
            Check(downs == 7, "triple tap = 7 downs incl. triple event",
                $"downs={downs}");
            Check(!Stuck(), "triple: no stuck button");
        }

        UnhookWindowsHookEx(_hook);
        try { np?.Kill(); } catch { }
        Console.WriteLine(_fails == 0 ? "ALL SELFTESTS PASSED"
            : $"{_fails} FAILURES");
        return _fails == 0 ? 0 : 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int L, T, R, B;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr h, ref RECT r);
}
