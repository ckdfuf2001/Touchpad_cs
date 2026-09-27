using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
            Console.WriteLine("  injected tail:");
            int from = Math.Max(0, Injected.Count - 12);
            for (int i = from; i < Injected.Count; i++)
            {
                var e = Injected[i];
                Console.WriteLine($"    {e.Kind} {e.Btn} ({e.X},{e.Y})");
            }
            _fails++;
        }
    }

    /// <summary>
    /// Our own injections since marker <paramref name="n"/>. The low-level
    /// hook is kept only for the "button not stuck" check - it also records
    /// the physical mouse and whatever a context menu does, which made the
    /// counts flap between runs.
    /// </summary>
    private static List<(long Ms, string Kind, string Btn, int X, int Y)> Since(int n)
    {
        lock (Injected)
            return Injected.GetRange(n, Injected.Count - n);
    }

    private static int Mark() { lock (Injected) return Injected.Count; }

    private static readonly List<(long Ms, string Kind, string Btn, int X, int Y)>
        Injected = TouchPadCloneV2.Core.InputSim.Injected;

    /// <summary>Press count for a button (a click records as down+up).</summary>
    private static int Dns(
        List<(long Ms, string Kind, string Btn, int X, int Y)> c, string b)
    {
        int n = 0;
        foreach (var e in c) if (e.Kind != "up" && e.Btn == b) n++;
        return n;
    }

    /// <summary>
    /// Hook delivery can lag the UI thread; wait for quiescence before
    /// counting, or events land in the next case's window.
    /// </summary>
    private static async Task Drain()
    {
        for (int i = 0; i < 12; i++)
        {
            int n = _clicks.Count;
            await Task.Delay(120);
            if (_clicks.Count == n) return;
        }
    }

    public static async Task<int> Run()
    {
        Console.WriteLine("== TouchPadCloneV2 selftest ==");
        _hookProc = Hook;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc,
            GetModuleHandle(null), 0);
        Console.WriteLine($"hook={_hook != IntPtr.Zero}");

        // Sandbox: our clicks must land on a window that does nothing with
        // them. notepad is a packaged app on Win11 and often reports no
        // MainWindowHandle, so fall back to a screen point (top-left strip)
        // and say so - the assertions no longer depend on the target window,
        // only on where we aimed.
        Process? np = null;
        try
        {
            np = Process.Start(new ProcessStartInfo("notepad.exe")
                { UseShellExecute = true });
            for (int i = 0; i < 25 && (np == null || np.MainWindowHandle == IntPtr.Zero); i++)
            {
                await Task.Delay(200);
                try { np?.Refresh(); } catch { }
            }
        }
        catch (Exception ex) { Console.WriteLine("notepad: " + ex.Message); }

        // Record what WE inject (the hook below only guards stuck buttons).
        InputSim.RecordInjections = true;

        // Deterministic settings: the USER's settings.json must not decide
        // what the assertions are (a remapped DoubleTap changes the click
        // counts and the run becomes unreproducible).
        var settings = new AppSettings
        {
            TapToClick = true,
            FakeCursor = true,
            ShowFakeArrow = true,
            LongPressMs = 500,
            MultiTapMs = 900,   // the shipped default - see AppSettings
            HoldCancelDip = 10,
        };
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
        double FakeX() => (double)fx!.GetValue(_pad)!;
        double FakeY() => (double)fy!.GetValue(_pad)!;
        // Cases share one pad, and a case ENDS inside the multi-tap window
        // (a double leaves a triple armed). Without settling, the next case's
        // first press is read as a continuation of the previous one and the
        // counts flap depending on how long the hook took to drain.
        //
        // The virtual cursor is re-centred too: it is shared state as well,
        // and the pointer is clamped at the screen edges, so a case that
        // walked it into a corner silently zeroed the NEXT case's
        // displacement measurement ("moved=0px of 128px").
        async Task Settle()
        {
            CenterFakeCursor();
            // The grab window outlives the 750ms settle (it is 2x MultiTapMs),
            // so a tap near the end of one case would arm a grab in the next
            // and turn its plain press into a drag. Cases share the pad, so
            // clear it here like the cursor.
            try
            {
                typeof(PadWindow).GetField("_grabArmUntil", NF)
                    ?.SetValue(_pad, DateTime.MinValue);
            }
            catch { }
            await Task.Delay(750);
        }

        void CenterFakeCursor()
        {
            try
            {
                fx!.SetValue(_pad, SystemParameters.VirtualScreenLeft
                    + SystemParameters.VirtualScreenWidth / 2.0);
                fy!.SetValue(_pad, SystemParameters.VirtualScreenTop
                    + SystemParameters.VirtualScreenHeight / 2.0);
            }
            catch { }
        }

        // ---- 1. tap ----
        {
            int n = Mark();
            await Settle();
            var d = new FakeTouch(id++);
            Down(d, px, py); await Task.Delay(80); Up(d);
            await Task.Delay(300);
            await Drain();
            var c = Since(n);
            Check(c.Count == 1 && c[0].Kind == "click" && c[0].Btn == "left",
                "tap = one left click", $"ev={c.Count}");
        }
        // ---- 2. one hold, three things it must get right: the right click
        // itself, the label that survives the release, and the physical mouse
        // cursor that must NOT be handed back (a context menu lives at the
        // cursor; moving it away dismisses the menu, which is how a working
        // right-click looked like a no-op).
        {
            var lbl = (System.Windows.Controls.TextBlock)_pad.FindName("StatusLabel");
            int n = Mark();
            await Settle();
            int mx = 90, my = 90;
            InputSim.SetCursor(mx, my);
            await Task.Delay(80);
            var d = new FakeTouch(id++);
            Down(d, px, py); await Task.Delay(800); Up(d);
            await Task.Delay(700);
            await Drain();
            var c = Since(n);
            var (cx, cy) = InputSim.Cursor();
            Check(c.Count == 1 && c[0].Kind == "click" && c[0].Btn == "right",
                "long-press = one right click", $"ev={c.Count}");
            // The label used to be overwritten unconditionally with
            // "up gesture Nms", so lifting after a right-click made it claim
            // nothing had happened.
            Check(lbl.Text.Contains("long-press") && lbl.Text.Contains("right_click"),
                "label shows the long-press after release", $"label=\"{lbl.Text}\"");
            Check(cx != mx || cy != my,
                "cursor NOT handed back after a right click (menu stays open)",
                $"now=({cx},{cy}) phys=({mx},{my})");
        }
        // ---- 3. double + immediate release (double-click) ----
        {
            int n = Mark();
            await Settle();
            var a = new FakeTouch(id++);
            Down(a, px, py); await Task.Delay(60); Up(a);
            await Task.Delay(150);
            var b = new FakeTouch(id++);
            Down(b, px, py); await Task.Delay(80); Up(b);
            await Task.Delay(400);
            await Drain();
            var c = Since(n);
            int downs = Dns(c, "left");
            Check(downs == 3, "double-tap quick = click + double (3 downs)",
                $"downs={downs}");
        }
        // ---- 3b. a grab-armed press must not also race a long-press. Real
        // press after a double-click, pause, then move: the long-press came
        // due first (500ms) and was cancelled 4ms before firing, so the drag
        // only began at 527ms and nearly became a right-click. Post-multi-tap
        // presses are drag-only.
        {
            int n = Mark();
            await Settle();
            var a = new FakeTouch(id++);
            Down(a, px, py); await Task.Delay(60); Up(a);
            await Task.Delay(120);
            var b = new FakeTouch(id++);
            Down(b, px, py); await Task.Delay(60); Up(b);
            await Task.Delay(900);            // past the chain window
            var c = new FakeTouch(id++);
            Down(c, px, py);
            await Task.Delay(700);            // the pause that used to lose it
            for (int i = 1; i <= 4; i++) { Move(c, px + 25 * i, py); await Task.Delay(30); }
            Up(c);
            await Task.Delay(500);
            await Drain();
            var ev = Since(n);
            Check(Dns(ev, "right") == 0,
                "a grab-armed press holds 700ms without firing a long-press",
                $"r={Dns(ev, "right")} l={Dns(ev, "left")}");
        }
        // ---- 3b. the same at HUMAN speed. Measured on the device: a natural
        // double tap goes down 700-900ms after the first one lifts, and the
        // window was 600ms, so almost every double tap came out as two
        // separate single clicks - one session's log had not a single
        // double_click in it. The window is 900ms now and this is the case
        // that says so.
        {
            int n = Mark();
            await Settle();
            var a = new FakeTouch(id++);
            Down(a, px, py); await Task.Delay(70); Up(a);
            await Task.Delay(700);
            var b = new FakeTouch(id++);
            Down(b, px, py); await Task.Delay(70); Up(b);
            await Task.Delay(400);
            await Drain();
            var c = Since(n);
            int downs = Dns(c, "left");
            Check(downs == 3,
                "a double tap 700ms apart (human speed) = click + double",
                $"downs={downs} (want 3)");
        }
        // ---- 4. click, then press + MOVE = drag. The press must land on the
        // AIM POINT (fake cursor), never on the touch point: a bare Down()
        // after a cursor park lost the race with the OS yank and pressed our
        // own pad instead of the target (measured: drag did nothing). The
        // release must also hand the real cursor back to the mouse.
        {
            int n = Mark();
            await Settle();
            int mx = 70, my = 70;
            InputSim.SetCursor(mx, my);
            await Task.Delay(80);
            var a = new FakeTouch(id++);
            Down(a, px, py); await Task.Delay(60); Up(a);
            await Task.Delay(150);
            double fx0 = FakeX(), fy0 = FakeY();
            var b = new FakeTouch(id++);
            Down(b, px, py); await Task.Delay(150);
            for (int i = 1; i <= 5; i++)
            {
                Move(b, px + 20 * i, py); await Task.Delay(30);
            }
            await Task.Delay(100);
            Up(b);
            await Task.Delay(600);
            await Drain();
            var c = Since(n);
            var downsL = c.Where(e => e.Kind != "up" && e.Btn == "left").ToList();
            // tap click + the held press. Like the original's Drag action, the
            // grab sends no extra push click.
            Check(downsL.Count == 2, "click+move = click then drag (2 L downs)",
                $"downs={downsL.Count}");
            if (downsL.Count == 2)
            {
                var d2 = downsL[^1];   // the held press, after the click
                double ddx = d2.X - fx0, ddy = d2.Y - fy0;
                Check(Math.Abs(ddx) <= 2 && Math.Abs(ddy) <= 2,
                    "drag press lands ON the aim point",
                    $"down@({d2.X},{d2.Y}) aim=({fx0:0},{fy0:0})");
            }
            // What "correct" means here depends on the mode, so assert the one
            // that is actually configured rather than pinning yesterday's.
            var (cx, cy) = InputSim.Cursor();
            double ax = FakeX(), ay = FakeY();
            bool oneCursor = (bool?)typeof(PadWindow)
                .GetField("RealCursorOnly", BindingFlags.NonPublic
                    | BindingFlags.Static)?.GetValue(null) ?? false;
            if (oneCursor)
            {
                // One cursor: the cursor must be where the drag ENDED.
                Check(Math.Abs(cx - ax) <= 3 && Math.Abs(cy - ay) <= 3,
                    "after the drag the cursor stays at the drop point",
                    $"now=({cx},{cy}) drop=({ax:0},{ay:0}) phys=({mx},{my})");
            }
            else
            {
                // Dual cursor: the drag is carried by the real cursor, which
                // must be handed back to the physical mouse afterwards.
                Check(Math.Abs(cx - mx) <= 3 && Math.Abs(cy - my) <= 3,
                    "after the drag the cursor is handed back to the mouse",
                    $"now=({cx},{cy}) want=({mx},{my}) drop=({ax:0},{ay:0})");
            }
        }
        // ---- 4c. while a button is held, the OS cursor must be ON the
        // virtual one. A drag is carried entirely by the cursor, and a touch
        // drag used to leave it parked at the press point - the drag was then
        // driven by whatever else wrote the cursor (the OS touch-to-mouse
        // promotion, unscaled, different baseline), which is what made a
        // touch drag come out as a shaky circle. Checked BEFORE the release,
        // because releasing hands the cursor back to the physical mouse.
        {
            int n = Mark();
            await Settle();
            var a = new FakeTouch(id++);
            Down(a, px, py); await Task.Delay(60); Up(a);
            await Task.Delay(150);
            var b = new FakeTouch(id++);
            Down(b, px, py); await Task.Delay(150);
            for (int i = 1; i <= 5; i++)
            {
                Move(b, px + 20 * i, py); await Task.Delay(30);
            }
            await Task.Delay(100);
            var (cx, cy) = InputSim.Cursor();
            double ax = FakeX(), ay = FakeY();
            Up(b);
            await Task.Delay(400);
            await Drain();
            var c = Since(n);
            Check(Math.Abs(cx - ax) <= 2 && Math.Abs(cy - ay) <= 2,
                "mid-drag the OS cursor rides the virtual cursor",
                $"os=({cx},{cy}) fake=({ax:0},{ay:0}) moves={c.Count(e => e.Kind == "move")}");
        }
        // ---- 4d. THE flow: tap, press, move. The press is the chained second
        // one, it can grab, and it must NOT also race a long-press - that
        // race sat 4ms from the edge on real hardware (press, pause, move:
        // "long canceled (peak 12)" against a threshold of 10, so the drag
        // only started because the cancel won by 4ms). A pause of any length
        // has to stay a pause.
        {
            int n = Mark();
            await Settle();
            var a = new FakeTouch(id++);
            Down(a, px, py); await Task.Delay(60); Up(a);      // the click
            await Task.Delay(150);
            var b = new FakeTouch(id++);
            Down(b, px, py);
            await Task.Delay(700);                             // the pause
            for (int i = 1; i <= 4; i++) { Move(b, px + 25 * i, py); await Task.Delay(30); }
            Up(b);
            await Task.Delay(500);
            await Drain();
            var ev = Since(n);
            // tap click, then the held press (no extra push click).
            Check(Dns(ev, "right") == 0 && Dns(ev, "left") == 2,
                "tap, press, pause 700ms, move = drag and never a right-click",
                $"l={Dns(ev, "left")} (want 2) r={Dns(ev, "right")}");
        }
        // ---- 4e. THE reported flow, at human speed: tap, then a SECOND later
        // press and move. The tap-chain window has closed by then, so the
        // press used to be a plain one with no grab armed - it roamed the
        // cursor and produced NO button at all (logged: TOUCHDOWN, long
        // canceled, cursor moves, TOUCHUP gesture, and no BTN down / DRAG end
        // anywhere). A single tap now arms the grab as well.
        {
            int n = Mark();
            await Settle();
            var a = new FakeTouch(id++);
            Down(a, px, py); await Task.Delay(60); Up(a);      // the click
            await Task.Delay(1000);                            // human gap
            var b = new FakeTouch(id++);
            Down(b, px, py); await Task.Delay(120);
            for (int i = 1; i <= 4; i++) { Move(b, px + 25 * i, py); await Task.Delay(30); }
            await Task.Delay(100);
            Up(b);
            await Task.Delay(500);
            await Drain();
            var ev = Since(n);
            int downs = ev.Count(e => e.Kind == "down" && e.Btn == "left");
            int ups = ev.Count(e => e.Kind == "up" && e.Btn == "left");
            Check(downs == 1 && ups == 1,
                "tap, wait 1s, press+move = a real drag press and release",
                $"down={downs} up={ups} ev={ev.Count}");
        }
        // ---- 5. the same after a DOUBLE tap: the third press must still be
        // able to grab. Reported as "no drag after a double-click". Both
        // timings matter: inside the tap-chain window the press is a
        // chained one, and a human needs longer than that between the double
        // and the next press - where the press used to be a PLAIN one, which
        // never arms a grab at all (it only moves the pointer and the
        // release emits nothing).
        foreach (var (label, gap) in new[]
        {
            ("inside the chain window", 120),
            ("after the chain window", 1400),
        })
        {
            int n = Mark();
            await Settle();
            var a = new FakeTouch(id++);
            Down(a, px, py); await Task.Delay(60); Up(a);
            await Task.Delay(120);
            var b = new FakeTouch(id++);
            Down(b, px, py); await Task.Delay(60); Up(b);
            await Task.Delay(gap);
            var c = new FakeTouch(id++);
            Down(c, px, py); await Task.Delay(150);
            for (int i = 1; i <= 5; i++)
            {
                Move(c, px + 20 * i, py); await Task.Delay(30);
            }
            await Task.Delay(100);
            Up(c);
            await Task.Delay(500);
            await Drain();
            var ev = Since(n);
            int ldowns = Dns(ev, "left"), rdowns = Dns(ev, "right");
            // 3 from the double (1 + 2) + the held press (no extra push click).
            Check(ldowns == 4 && rdowns == 0,
                $"double-tap then press+move ({label}) = drag still grabs",
                $"l={ldowns} (want 4) r={rdowns} ev={ev.Count}");
        }
        // ---- 5b. and the grab must NOT leak into ordinary use: a press with
        // no multi-tap before it is a plain pointer move, and its release
        // emits nothing. If this ever grabs, every cursor move on the pad
        // would turn into a drag.
        {
            int n = Mark();
            await Settle();
            var d = new FakeTouch(id++);
            Down(d, px, py); await Task.Delay(150);
            for (int i = 1; i <= 5; i++) { Move(d, px + 20 * i, py); await Task.Delay(30); }
            await Task.Delay(100);
            Up(d);
            await Task.Delay(400);
            await Drain();
            var ev = Since(n);
            // Only ACTIONS count. In one-cursor mode the cursor genuinely
            // moves during this press, so moves are expected and correct;
            // what must not happen is a click/grab.
            int acts = ev.Count(e => e.Kind != "move");
            Check(acts == 0,
                "a plain press+move is a pointer move, NOT a grab",
                $"acts={acts} moves={ev.Count - acts}");
        }
        // ---- 6. click, then press and HOLD STILL. The hold of a press that
        // follows a tap belongs to SecondHold (drag by default), NOT to
        // the long-press. Reported: "after a double-click, holding just logs a
        // right-click; it should be click-and-drag". The button must go down
        // and STAY down - that is what a drag needs - so this asserts a left
        // down that is not immediately undone, and no right click at all.
        {
            int n = Mark();
            await Settle();
            var a = new FakeTouch(id++);
            Down(a, px, py); await Task.Delay(60); Up(a);
            await Task.Delay(150);
            var b = new FakeTouch(id++);
            Down(b, px, py); await Task.Delay(1100);
            int held = Dns(Since(n), "left");
            int upWhileHeld = Since(n).Count(e => e.Kind == "up" && e.Btn == "left");
            Up(b);
            await Task.Delay(400);
            await Drain();
            var c = Since(n);
            int ldowns = Dns(c, "left"), rdowns = Dns(c, "right");
            int clicks = c.Count(e => e.Kind == "click" && e.Btn == "left");
            Check(rdowns == 0, "hold after a tap does NOT fire a right click",
                $"r={rdowns} l={ldowns}");
            // Exactly one complete click (the tap's). Like the original's Drag
            // action, the grab adds no push click, and its release must not add
            // one either: a click at the drop point is not part of a drag.
            Check(clicks == 1,
                "drag does not add a push click (only the tap's)",
                $"clicks={clicks} (want 1: tap only, none on grab/release)");
            Check(ldowns >= 2 && upWhileHeld == 0,
                "hold after a tap = the button is HELD down (click-and-drag)",
                $"l={ldowns} upsWhileHeld={upWhileHeld} heldAt1100={held}");
        }
        // ---- 7. REGRESSION: a drag that then parks must stay silent. Once
        // the press is a drag, NOTHING may fire until release - the old
        // edge-triggered 120px test let the timer live and a right-click
        // landed in the middle of the move. Two rates, because the real
        // complaint was about the SLOW one: at the old threshold of 100
        // DIP/150ms a measured 0.13 DIP/ms positioning move (19) read as
        // "still" and every attempt ended in a right-click.
        // Task.Delay cannot honour 12ms (the Windows timer granularity is
        // 15.6ms), so these rates sit far from the boundary on purpose -
        // otherwise the cases measure the scheduler instead of the rule.
        foreach (var (label, step, gap) in new[]
        {
            ("slow 0.13 DIP/ms = 19", 4, 30),
            ("fast 1.6 DIP/ms = 240", 40, 25),
        })
        {
            int n = Mark();
            await Settle();
            var d = new FakeTouch(id++);
            Down(d, px, py);
            for (int i = 1; i <= 4; i++) { Move(d, px + step * i, py); await Task.Delay(gap); }
            await Task.Delay(700); // park: the rate check passes here
            Up(d);
            await Task.Delay(400);
            await Drain();
            var c = Since(n);
            // A travelled press owns itself until release: no click, no wheel,
            // no key. Cursor MOVES are expected and correct here - a drag has
            // to carry the cursor - so they are not counted as actions.
            int acts = c.Count(e => e.Kind != "move");
            Check(acts == 0, $"drag then park ({label}) = no actions at all",
                $"acts={acts} moves={c.Count - acts}");
        }

        // ---- 8. REGRESSION: a HARD one-finger scrape must move the cursor
        // and fire NOTHING. Every gesture action is two-finger by policy:
        // scratching fast across the pad used to fire SwipeUp (a scroll),
        // which is the one thing a single finger must never do. (The
        // downward flick and the diagonal drag were separate cases for the
        // same rule; the one-finger swipe code is gone, so one case covers it.)
        {
            int n = Mark();
            await Settle();
            double aimX = FakeX(), aimY = FakeY();
            var d = new FakeTouch(id++);
            Down(d, px, py);
            // A real digitizer samples a moving finger every few ms, so a
            // fast scrape is delivered as a dense burst.
            for (int i = 1; i <= 6; i++) Move(d, px, py - 25 * i);
            await Task.Delay(40);
            Up(d);
            await Task.Delay(400);
            await Drain();
            var c = Since(n);
            int scrapeActs = c.Count(e => e.Kind != "move");
            Check(scrapeActs == 0, "hard 1-finger scrape fires NO gesture",
                $"acts={scrapeActs} moves={c.Count - scrapeActs}");
            Check(FakeX() < aimX - 20 || FakeY() < aimY - 20,
                "1-finger scrape moved the cursor",
                $"({aimX:0},{aimY:0}) -> ({FakeX():0},{FakeY():0})");
        }
        // ---- 9. two fingers: vertical = live scroll (the gesture path
        // that IS allowed to fire with motion).
        {
            int n = Mark();
            await Settle();
            var a = new FakeTouch(id++);
            var b = new FakeTouch(id++);
            Down(a, px - 40, py);
            await Task.Delay(40);
            Down(b, px + 40, py);
            await Task.Delay(40);
            for (int i = 1; i <= 4; i++)
            {
                Move(a, px - 40, py - 30 * i);
                Move(b, px + 40, py - 30 * i);
                await Task.Delay(30);
            }
            Up(a); Up(b);
            await Task.Delay(400);
            await Drain();
            var c = Since(n);
            Check(c.Any(e => e.Kind == "wheel" && e.Btn.StartsWith("v")),
                "two-finger vertical = scroll", $"ev={c.Count}");
        }
        // ---- 9b. after a two-finger scroll, the finger that stays on the pad
        // has travelled the whole scroll. That travel was never applied to the
        // cursor (the scroll branch returns before the cursor update), so it
        // sat in p - f.Start and the first move after the lift handed the
        // ENTIRE scroll distance to the cursor at once, times the gain - the
        // cursor jumped the moment the scroll ended. Re-anchored on release,
        // a small move must stay small.
        {
            int n = Mark();
            await Settle();
            double aimX = FakeX(), aimY = FakeY();
            var a = new FakeTouch(id++);
            var b = new FakeTouch(id++);
            Down(a, px - 40, py);
            await Task.Delay(40);
            Down(b, px + 40, py);
            await Task.Delay(40);
            for (int i = 1; i <= 4; i++)   // a vertical scroll, 120 DIP total
            {
                Move(a, px - 40, py - 30 * i);
                Move(b, px + 40, py - 30 * i);
                await Task.Delay(30);
            }
            double scrollX = FakeX(), scrollY = FakeY();
            Up(a);                          // one finger stays down
            await Task.Delay(60);
            Move(b, px + 40, py - 130);     // a 10 DIP move from where b sits
            await Task.Delay(120);
            double moveX = FakeX(), moveY = FakeY();
            Up(b);
            await Task.Delay(400);
            await Drain();
            var c = Since(n);
            double during = Math.Abs(scrollX - aimX) + Math.Abs(scrollY - aimY);
            double after = Math.Abs(moveX - aimX) + Math.Abs(moveY - aimY);
            Check(c.Any(e => e.Kind == "wheel"), "scroll during it still scrolls",
                $"ev={c.Count}");
            Check(during <= 2, "the cursor does NOT move while scrolling",
                $"moved={during:0}px");
            // 10 DIP x gain. The old bug applied the 120 DIP of scroll travel
            // instead, i.e. 12x this.
            Check(after < 60, "a small move after the scroll stays small",
                $"after={after:0}px (bug would be ~{120 * 1.6:0})");
        }
        // ---- 10. a MOVING press owns the session until it lifts: a second
        // contact arriving mid-drag must be refused outright. Otherwise one
        // drag also produced a two-finger scroll, or a button/key tile fired
        // on top of it - the "other actions run while I am dragging" report.
        {
            int n = Mark();
            await Settle();
            var a = new FakeTouch(id++);
            var b = new FakeTouch(id++);
            Down(a, px, py);
            // A decisive move: over the creep floor, so the press is a drag.
            for (int i = 1; i <= 3; i++) { Move(a, px, py - 40 * i); await Task.Delay(30); }
            Down(b, px + 60, py);
            await Task.Delay(40);
            for (int i = 1; i <= 3; i++) { Move(b, px + 60, py - 30 * i); await Task.Delay(30); }
            Up(a); Up(b);
            await Task.Delay(400);
            await Drain();
            var c = Since(n);
            int fActs = c.Count(e => e.Kind != "move");
            Check(fActs == 0,
                "2nd finger during a drag fires nothing (no scroll, no click)",
                $"acts={fActs} moves={c.Count - fActs}");
        }
        // ---- 11. a slow creep must NOT scroll, must NOT carry the cursor
        // away, and must be visible EARLY: the pin that held the pointer
        // still until the hold resolved made the pad read as dead.
        {
            int n = Mark();
            await Settle();
            double aimX = FakeX(), aimY = FakeY();
            var d = new FakeTouch(id++);
            Down(d, px, py);
            await Task.Delay(60);
            for (int i = 1; i <= 3; i++) { Move(d, px + 15 * i, py); await Task.Delay(16); }
            double early = Math.Abs(FakeX() - aimX) + Math.Abs(FakeY() - aimY);
            double x = px;
            for (int i = 0; i < 12; i++)   // 1 DIP / 40ms = 0.025 DIP/ms drift
            {
                x += 1;
                Move(d, x, py);
                await Task.Delay(40);
            }
            double creepX = FakeX(), creepY = FakeY();
            Up(d);
            await Task.Delay(400);
            await Drain();
            var c = Since(n);
            Check(!c.Any(e => e.Kind == "wheel"), "creep = no scroll", $"ev={c.Count}");
            // The pointer is NOT pinned while a hold is pending: normal
            // movement on this panel is the same speed as the creep
            // (0.33-0.81 DIP/ms measured), so any pin threshold either never
            // releases - the pad reads as dead for 500ms - or releases on the
            // creep anyway. Immediate movement wins; HoldDamp (default 1.0)
            // is the optional softening.
            double total = Math.Abs(creepX - aimX) + Math.Abs(creepY - aimY);
            Check(early > 5 && total > 2,
                "creep moves the cursor at once (no dead pad)",
                $"early={early:0}px total={total:0}px");
        }

        // ---- 12. REGRESSION (real panel): a "still" finger on this digitizer
        // drifts ~0.4 DIP per SECOND - log-measured at 1-12 DIP of NET travel
        // over half a second to three seconds, i.e. ~0.06 DIP per 150ms
        // window. An earlier note claimed 0.4 DIP/ms (65 per 150ms), which
        // forced the threshold up to 100 and made ordinary slow movement read
        // as "still". A drifting finger must still count as a HOLD. This is
        // the other half of case 7: drift holds, real movement does not.
        {
            int n = Mark();
            await Settle();
            var d = new FakeTouch(id++);
            Down(d, px, py);
            double x = px;
            for (int i = 0; i < 20; i++)   // ~800ms of drift at 0.025 DIP/ms
            {
                x += 1;
                Move(d, x, py);
                await Task.Delay(40);
            }
            Up(d);
            await Task.Delay(400);
            await Drain();
            var c = Since(n);
            Check(Dns(c, "right") == 1,
                "drifting finger still counts as a HOLD (right click)",
                $"r={Dns(c, "right")} ev={c.Count}");
        }
        // ---- 13. no button left pressed. The real invariant is OUR stream
        // being balanced (a drag that never releases is a stuck button).
        // GetAsyncKeyState cannot be used per test: it reports the physical
        // mouse, so on a live desktop it flaps for reasons unrelated to us.
        {
            var all = Since(0);
            var bad = new List<string>();
            foreach (var b in new[] { "left", "right" })
            {
                int dn = 0, up = 0;
                foreach (var e in all)
                {
                    if (e.Btn != b) continue;
                    if (e.Kind == "up") up++;
                    else if (e.Kind == "down") dn++;   // a click is dn+up in one
                }
                if (dn != up) bad.Add($"{b} {dn}/{up}");
            }
            Check(bad.Count == 0, "no stuck buttons (stream balanced)",
                bad.Count == 0 ? $"{all.Count} events" : string.Join(" ", bad));
            Console.WriteLine($"  (os async key state: L={(GetAsyncKeyState(1) & 0x8000) != 0}"
                + $" R={(GetAsyncKeyState(2) & 0x8000) != 0} - physical mouse, informational only)");
        }

        UnhookWindowsHookEx(_hook);
        // The pad hid the system cursor for the fake one; without this the
        // process exits with the cursor still invisible for the whole desktop
        // (ShowCursor is a global counter).
        InputSim.RestoreCursor();
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
