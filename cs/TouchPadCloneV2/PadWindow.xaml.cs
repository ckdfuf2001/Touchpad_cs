using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TouchPadCloneV2.Core;
using WPoint = System.Windows.Point;
using WColor = System.Windows.Media.Color;
using WMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace TouchPadCloneV2;

/// <summary>
/// Floating virtual trackpad. Input comes from REAL WPF touch events
/// (TouchDown/Move/Up, one stream per TouchDevice.Id) - never from the OS
/// cursor position - so our own cursor output cannot feed back into the
/// input deltas. Mouse handlers are a fallback for non-touch testing and
/// ignore anything promoted from touch/stylus.
/// </summary>
public partial class PadWindow : Window
{
    private const double TapMs = 220;
    private const double TapMoveDip = 12;
    /// <summary>
    /// Deadzone: sub-threshold tremor never moves the fake cursor.
    /// (User request + fixes aim drift that broke long-press menus.)
    ///
    /// 2, not 6: the gate is measured against the PRESS POINT, so at 6 DIP the
    /// cursor sat still for the first 6 DIP of a stroke and then jumped
    /// 6 x gain pixels at once - the stutter at the start of every movement
    /// that makes this feel laggy next to the reference, which simply moves
    /// the cursor on every event. 2 DIP still absorbs the panel's 1px tremor.
    /// </summary>
    private const double DeadDip = 2;
    /// <summary>
    /// Per-event deadzone, used only in one-cursor mode where each event is
    /// applied as a delta rather than measured against the press point. Much
    /// smaller than DeadDip: a single touch event moves a fraction of a
    /// coarse press-travel threshold, and a per-event gate that large would
    /// swallow ordinary slow movement.
    /// </summary>
    private const double DeadDipPerEvent = 0.5;
    private const double SwipeDip = 60;
    private const int MaxStepPx = 256;
    private const int VkTabTip = 0x09;

    private readonly AppSettings _s;
    private Layout? _layout;
    private readonly Dictionary<int, Finger> _fingers = new();
    private bool _mouseDown;
    private WPoint _mouseLast, _mouseStart;
    private DateTime _mouseT0;
    private double _dpi = 1;

    // ---- Fake-cursor touch session ----
    // Windows yanks the system cursor toward the live touch contact
    // (measured: applied moves eaten, direction reversed), so while a
    // finger is down we roam a FAKE cursor and hide the system one -
    // the original's own "Fake arrow cursor" answer.
    // In trackpad mode the fake cursor is shown PERSISTENTLY (not only
    // mid-touch): one cursor identity, no confusion. The system cursor
    // stays parked exactly under the fake one; any real physical-mouse
    // input hands control back to the system cursor immediately.
    private FakeCursorOverlay? _overlay;
    private bool _session;
    private double _fakeX, _fakeY;
    private double _drawnX = -1e9, _drawnY = -1e9; // force first draw
    private double _chaseX = -1e9, _chaseY = -1e9;
    private long _lastChaseTick, _lastDrawTick;
    private int _moveLogN;
    private DateTime _lastStatus = DateTime.MinValue;
    private bool _fakeInit;
    private DateTime _suppressPhysicalUntil = DateTime.MinValue;
    // ShowCursor is a global counter: HideCursor must be called exactly
    // once per hide (a settings-slider Apply loop once drained it by
    // hundreds and the cursor could never come back). Gate it here.
    private bool _cursorHidden;

    /// <summary>
    /// One-cursor mode: the touch drives the REAL system cursor, which is then
    /// the only cursor on screen. Kept switchable because the machinery for it
    /// is in place, but it is OFF: this hardware fights it at the OS level and
    /// the OS wins.
    ///
    /// Measured on the device: while a finger is down the system moves the
    /// cursor onto the touch contact - which is on the pad - and it does so
    /// after our own SetCursorPos, repeatedly (logged: the cursor sitting on
    /// our own pad, then pinned to (0,0) as the keeper and the pull traded
    /// places). Reading the cursor back to drive it feeds that pull straight
    /// into the position, and a model that ignores the read-back can only
    /// fight it on the output side. The system also hides the cursor while a
    /// touch is active, which is fatal when the cursor is the UI.
    ///
    /// This is exactly why the project has a fake cursor at all: the real one
    /// cannot be relied on during a touch. Making one cursor work would mean
    /// handling raw WM_POINTER and suppressing the promotion, which must be
    /// verified on the device - the selftest calls handlers directly and never
    /// goes through the input pipeline, so it cannot see any of this.
    /// </summary>
    /// <summary>
    /// Read touches from the pointer API instead of WPF's touch pipeline.
    ///
    /// A WPF touch is promoted to a stylus and then to a mouse event, and the
    /// system moves the cursor onto the contact as part of that - the source of
    /// the cursor being dragged onto the pad, the phantom mouse events, the
    /// flickering drag and the need for a fake cursor at all. A window that has
    /// not registered for touch receives WM_POINTER instead of WM_TOUCH, so
    /// unregistering and handling the pointer messages gives us the touches
    /// untouched. The working reference on this machine (TouchMousePointer)
    /// sidesteps the same thing with Raw Input. Flip to false to go back to the
    /// WPF touch handlers.
    /// </summary>
    private static readonly bool UseRawPointer = true;

    /// <summary>
    /// Take touches from RAW INPUT as well, the way the working reference does.
    /// It is what makes a click-through window possible at all: raw input is
    /// delivered no matter which window is under the finger, so the pad no
    /// longer has to stay hittable for the touch, and therefore our own
    /// synthetic clicks cannot land on it.
    ///
    /// While this is on the pointer path is left alone - both feed the same
    /// handlers, and the raw one is logged in full (device caps, ranges, first
    /// contacts) so a real run can confirm the coordinate mapping before the
    /// window is made click-through.
    /// </summary>
    private static readonly bool UseRawInput = true;

    private readonly Core.RawTouchInput _rawTouch = new();

    /// <summary>
    /// The selftest drives the touch handlers itself, so it must NOT also
    /// receive the pointer bridge's input: a real touch during a run fed the
    /// same state machine on top of the scripted ones and the counts went wild
    /// (measured: a duplicate-tap case reporting l=7 where 4 was expected, and
    /// a hold case seeing a right click that no scripted press asked for).
    /// </summary>
    private static readonly bool SelftestMode =
        Environment.GetEnvironmentVariable("TOUCHPAD_SELFTEST") == "1";

    private static readonly bool RealCursorOnly = true;

    /// <summary>
    /// The drag grab - OUR gesture. (Briefly switched off to isolate it; that
    /// was a misreading of the request. What was wanted is that a REAL mouse
    /// drag must not operate in the pad's area - see PadWndProc and the mouse
    /// handlers, which swallow what they receive.)
    /// </summary>
    private static readonly bool DragEnabled = true;

    /// <summary>
    /// Until when the pad's touch surface must let mouse input through to the
    /// window beneath. Set for the duration of a synthetic click or drag, plus
    /// a short tail so the tail end of a press/click is not caught on the
    /// wrong side of the flag.
    /// </summary>
    private DateTime _mouseThroughUntil = DateTime.MinValue;
    private const int MouseThroughTailMs = 120;
    /// <summary>
    /// True while the pad's surface must let mouse input through to the window
    /// beneath.
    ///
    /// Keyed on the SESSION, not on a synthetic button being held: any touch
    /// gesture on the pad - a plain move, a move with a button down, a
    /// double-touch drag - is covered by one condition, with no per-move
    /// refresh to get out of step. Keying it on the button made the hit test
    /// alternate between two moves whenever the finger paused long enough for
    /// the tail to lapse, and the drag ended up split between our window and
    /// the application ("it flickers between the two areas and the release
    /// lands in one of them").
    ///
    /// Gating it on the session is also what makes it safe for TOUCH. Doing it
    /// unconditionally killed the pad outright - the system picks the touch
    /// target with the same hit test, so the surface never received the touch
    /// that would have started a session. Once a session is running the touch
    /// is CAPTURED, so hit testing no longer decides where it goes and the
    /// surface can be transparent to the mouse without losing the finger.
    /// </summary>
    private bool MouseThroughActive =>
        _session || DateTime.Now < _mouseThroughUntil;

    private const int WM_NCHITTEST = 0x0084;
    private static readonly IntPtr HTTRANSPARENT = new(-1);

    private IntPtr PadWndProc(IntPtr hwnd, int msg, IntPtr wParam,
        IntPtr lParam, ref bool handled)
    {
        // Raw input first: it is what will let this window be click-through.
        if (UseRawInput && !SelftestMode && msg == 0x00FF)
            _rawTouch.HandleMessage(lParam);
        // Pointer input: a touch arrives here when the window is not registered
        // for touch, and it must be CONSUMED or WPF promotes it.
        if (UseRawPointer && !SelftestMode)
        {
            if (msg == Core.RawPointer.WM_POINTERCAPTURECHANGED)
            {
                uint lost = Core.RawPointer.IdFromWParam(wParam);
                if (_rawDevices.ContainsKey(lost))
                {
                    handled = true;
                    ForwardRawTouch(lost, down: false, up: true,
                        new Point(0, 0));
                }
                return IntPtr.Zero;
            }
            if (msg is Core.RawPointer.WM_POINTERDOWN
                    or Core.RawPointer.WM_POINTERUPDATE
                    or Core.RawPointer.WM_POINTERUP)
            {
                uint pid = Core.RawPointer.IdFromWParam(wParam);
                if (Core.RawPointer.GetPointerType(pid, out int ptype)
                    && ptype == Core.RawPointer.PT_TOUCH
                    && Core.RawPointer.GetPointerInfo(pid, out var pi))
                {
                    var screen = new Point(pi.ptPixelLocation.x,
                                           pi.ptPixelLocation.y);
                    handled = true;
                    ForwardRawTouch(pid,
                        down: msg == Core.RawPointer.WM_POINTERDOWN,
                        up: msg == Core.RawPointer.WM_POINTERUP, screen);
                    return IntPtr.Zero;
                }
            }
        }
        if (msg != WM_NCHITTEST || !MouseThroughActive) return IntPtr.Zero;
        try
        {
            int sx = unchecked((short)(long)lParam);
            int sy = unchecked((short)((long)lParam >> 16));
            // Let our own synthetic click/drag through to the application
            // underneath, so a press or release aimed at the pad is not lost
            // on our window.
            //
            // ONLY while injecting. Doing this unconditionally made the pad
            // transparent to TOUCH as well - the system picks the touch target
            // with the same hit test - and the surface went dead.
            //
            // Cheap on purpose: WM_NCHITTEST arrives many times per second and
            // this runs on the UI thread, so it is a rectangle comparison, not
            // a WPF hit test (that one walked the visual tree per message and
            // the pad felt slow and stuttery).
            //
            // The title bar and the corner resizers are exempt, and they must
            // stay exempt: making the WHOLE window transparent for the duration
            // of a gesture stopped the pointer input that a gesture needs (a
            // drag after a touch stopped working entirely). The input arrives
            // by hit testing, so the window has to keep answering for the parts
            // a gesture runs over.
            var screen = new Point(sx, sy);
            if (IsOverChrome(screen)) return IntPtr.Zero;
            var win = PointFromScreen(screen);
            if (ResizerAt(win.X, win.Y) != null) return IntPtr.Zero;
            handled = true;
            return HTTRANSPARENT;
        }
        catch { }
        return IntPtr.Zero;
    }

    /// <summary>Rectangle test against the title bar - cheap, unlike a hit test.</summary>
    private bool IsOverChrome(Point screen)
    {
        try
        {
            if (TitleBar.ActualWidth <= 0) return false;
            var tl = TitleBar.PointToScreen(new Point(0, 0));
            var br = TitleBar.PointToScreen(new Point(
                TitleBar.ActualWidth, TitleBar.ActualHeight));
            return screen.X >= tl.X && screen.X <= br.X
                && screen.Y >= tl.Y && screen.Y <= br.Y;
        }
        catch { return false; }
    }

    /// <summary>
    /// A WPF touch device we drive ourselves, so the pointer input can go
    /// through the same gesture handlers WPF's touches used to reach. The
    /// selftest has done exactly this all along with its FakeTouch, so the
    /// handlers are already known to work this way.
    /// </summary>
    private sealed class RawTouchDevice : System.Windows.Input.TouchDevice
    {
        public Point Screen;
        public RawTouchDevice(int id) : base(id) { }

        private Point Local(IInputElement relativeTo) =>
            (relativeTo as Visual)?.PointFromScreen(Screen) ?? Screen;

        public override System.Windows.Input.TouchPoint GetTouchPoint(
            IInputElement relativeTo)
        {
            var p = Local(relativeTo);
            return new System.Windows.Input.TouchPoint(
                this, p, new Rect(p, new Size(1, 1)),
                System.Windows.Input.TouchAction.Move);
        }

        public override System.Windows.Input.TouchPointCollection
            GetIntermediateTouchPoints(IInputElement relativeTo)
        {
            var c = new System.Windows.Input.TouchPointCollection();
            c.Add(GetTouchPoint(relativeTo));
            return c;
        }
    }

    private readonly Dictionary<uint, RawTouchDevice> _rawDevices = new();
    private readonly Dictionary<int, bool> _rawTouchDown = new();

    /// <summary>
    /// Turn a pointer message into the touch handler the gesture code expects.
    /// Down and Move are dispatched; Up releases and forgets the contact, so a
    /// pointer id that goes away without an UP (capture changed) cannot leave a
    /// finger stuck down.
    /// </summary>
    private void ForwardRawTouch(uint pointerId, bool down, bool up, Point screen)
    {
        try
        {
            if (down && !_rawDevices.TryGetValue(pointerId, out var dev))
            {
                dev = new RawTouchDevice(unchecked((int)pointerId));
                _rawDevices[pointerId] = dev;
            }
            if (!_rawDevices.TryGetValue(pointerId, out dev))
            {
                if (up) return;   // an Up for a contact we never saw
                return;
            }
            if (!up) dev.Screen = screen;
            var args = new TouchEventArgs(dev, Environment.TickCount);
            if (down)
            {
                args.RoutedEvent = UIElement.TouchDownEvent;
                OnTouchDown(Surface, args);
            }
            else if (up)
            {
                args.RoutedEvent = UIElement.TouchUpEvent;
                OnTouchUp(Surface, args);
                _rawDevices.Remove(pointerId);
            }
            else
            {
                args.RoutedEvent = UIElement.TouchMoveEvent;
                OnTouchMove(Surface, args);
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"RAWTOUCH forward failed: {ex.GetType().Name}");
        }
    }

    /// <summary>Keep the surface mouse-transparent while an injection runs.</summary>
    private void HoldMouseThrough() =>
        _mouseThroughUntil = DateTime.Now.AddMilliseconds(MouseThroughTailMs);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after,
        int x, int y, int cx, int cy, uint flags);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001,
        SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    private System.Windows.Threading.DispatcherTimer? _topTimer;

    /// <summary>
    /// Re-assert "always on top" for the pad AND for the cursor, in that
    /// order.
    ///
    /// Topmost="True" is only the initial state: another application that puts
    /// its own window on top afterwards goes above the pad and covers it, and
    /// nothing brings it back - which is what "the touch and the mouse should
    /// be on top, but they get covered" is. Re-asserting on a timer is the
    /// usual answer; it is what the cursor overlay already does implicitly by
    /// re-placing itself on every move.
    ///
    /// Order matters: putting the pad on top moves it above the cursor, so the
    /// cursor has to be brought forward afterwards or it disappears the moment
    /// it passes over the pad.
    /// </summary>
    private void EnsureTopmost()
    {
        try
        {
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;
            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            // Cursor above the pad again.
            _overlay?.BringToFront();
        }
        catch { }
    }

    private void EnsureHidden()
    {
        // The real cursor is the cursor now, so it must stay visible.
        if (RealCursorOnly) return;
        if (_cursorHidden) return;
        InputSim.HideCursor();
        _cursorHidden = true;
    }

    private void EnsureVisible()
    {
        // In one-cursor mode the real cursor IS the cursor and nothing of ours
        // ever hides it - but the system hides the pointer while a touch is
        // active, and _cursorHidden never became true, so the old guard made
        // this a no-op and the pointer could stay invisible for good
        // ("the mouse is not visible"). Restore unconditionally; the helper
        // returns at once when it is already showing.
        if (RealCursorOnly)
        {
            InputSim.RestoreCursor();
            return;
        }
        if (!_cursorHidden) return;
        InputSim.RestoreCursor();
        _cursorHidden = false;
    }

    private void ClampFake()
    {
        var (x0, y0, x1, y1) = ScreenBounds();
        // Real cursors stop at screen edges; the fake one must too, or it
        // roams off-screen and neither cursor is visible anywhere.
        _fakeX = Math.Max(x0, Math.Min(x1, _fakeX));
        _fakeY = Math.Max(y0, Math.Min(y1, _fakeY));
    }

    private void EnsureDpi()
    {
        try
        {
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget != null)
                _dpi = src.CompositionTarget.TransformToDevice.M11;
        }
        catch { }
    }

    private void ShowFake()
    {
        _overlay ??= new FakeCursorOverlay();
        _overlay.ApplyStyle(_s.CursorStyle);
        _overlay.MoveToPhysical(_fakeX, _fakeY);
    }

    public void ApplyCursorStyle() => _overlay?.ApplyStyle(_s.CursorStyle);

    /// <summary>
    /// Bring the fake cursor back to the primary screen center (it roams
    /// the whole virtual desktop by design, so it can end up on another
    /// monitor where the user isn't looking - actions then fire invisibly).
    /// </summary>
    public void CenterFake()
    {
        var (cx, cy) = PrimaryCenter();
        _fakeX = cx; _fakeY = cy;
        ClampFake();
        if (ArrowOn) ShowFake();
        else try { _overlay?.Hide(); } catch { }
        if (!_session)
        {
            InputSim.SetCursor((int)_fakeX, (int)_fakeY);
        ForgetRealCursor();
            _suppressPhysicalUntil = DateTime.Now.AddMilliseconds(250);
        }
        DebugLog.Write($"CENTER fake=({_fakeX:0},{_fakeY:0})");
        Status($"center ({_fakeX:0},{_fakeY:0})");
    }

    /// <summary>Physical-pixel bounds of the whole virtual screen.</summary>
    private static (double x0, double y0, double x1, double y1) ScreenBounds()
    {
        var v = System.Windows.Forms.SystemInformation.VirtualScreen;
        return (v.Left, v.Top, v.Right - 1, v.Bottom - 1);
    }

    private static (double x, double y) PrimaryCenter()
    {
        var p = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
            ?? System.Windows.Forms.SystemInformation.VirtualScreen;
        return (p.Left + p.Width / 2.0, p.Top + p.Height / 2.0);
    }

    /// <summary>
    /// The real system cursor position captured when the touch session
    /// opened - i.e. where the PHYSICAL mouse left it. Every synthetic
    /// click/wheel/drag has to move the real cursor (SendInput absolute),
    /// which is why the physical mouse used to be dragged along. In
    /// "preserve" mode we hand that position back, so the physical mouse
    /// never ends up somewhere the virtual pad chose.
    /// </summary>
    private int _physX = int.MinValue, _physY = int.MinValue;

    private bool UnifiedPhysical => _s.PhysicalMouseMode == "unified";

    private void RememberPhysicalCursor()
    {
        if (UnifiedPhysical) return;
        try
        {
            var (cx, cy) = InputSim.Cursor();
            // Never record OUR OWN position as the physical one. Right after a
            // release the cursor is still parked at the drop point - the hand-
            // back is deliberately deferred so a drop can be processed - and a
            // new session can start inside that window (measured: SESSION begin
            // phys=(161,252) 280ms after a release at (162,253)). Capturing it
            // made _physX/_physY the drop point, so every later hand-back moved
            // the physical mouse's cursor THERE instead of where the mouse
            // actually is, which reads as "the release always happens at the
            // real mouse, over the pad".
            if (DateTime.Now < _cursorFreeAt) return;
            if (Math.Abs(cx - _fakeX) <= 2 && Math.Abs(cy - _fakeY) <= 2) return;
            _physX = cx; _physY = cy;
        }
        catch { }
    }

    /// <summary>
    /// Put the real cursor back where the physical mouse left it - but only
    /// if it is still where WE put it. If it is somewhere else entirely,
    /// the physical mouse moved it and we must never fight that.
    /// </summary>
    private void RestorePhysicalCursor(string why)
    {
        // One cursor: there is no separate physical position to hand back to,
        // and moving the cursor here is exactly what used to make a drop land
        // at the mouse instead of where it was dragged to.
        if (RealCursorOnly) return;
        // A one-shot: the click that opens a drag_hold must NOT hand the
        // cursor back between itself and the hold. The application would see
        // the cursor leave the target and come back, which is exactly the
        // motion that cancels a drag.
        if (_keepCursorOnce)
        {
            _keepCursorOnce = false;
            DebugLog.Write($"PHYS keep cursor for {why} (drag_hold click)");
            return;
        }
        // A drop is in flight. The release has to land, and be read by the
        // application, at the virtual cursor - and a drop is not always
        // processed synchronously with the button-up. Handing the cursor back
        // at once (measured: the same millisecond as the BTN up line) is what
        // made a drag drop at the PHYSICAL mouse position instead of where it
        // was dragged to. Nothing may move the cursor until the grace has
        // passed.
        if (DateTime.Now < _cursorFreeAt)
        {
            DebugLog.Write($"PHYS defer restore for {why}"
                + $" ({(int)(_cursorFreeAt - DateTime.Now).TotalMilliseconds}ms left)");
            return;
        }
        if (UnifiedPhysical || _physX == int.MinValue) return;
        try
        {
            var (cx, cy) = InputSim.Cursor();
            if (Math.Abs(cx - _fakeX) > 2 || Math.Abs(cy - _fakeY) > 2)
            {
                DebugLog.Write($"PHYS skip restore ({cx},{cy}) not ours");
                return;
            }
            if (cx == _physX && cy == _physY) return;
            InputSim.SetCursor(_physX, _physY);
        ForgetRealCursor();
            _suppressPhysicalUntil = DateTime.Now.AddMilliseconds(120);
            DebugLog.Write($"PHYS restore ({_physX},{_physY}) from ({cx},{cy}) [{why}]");
        }
        catch { }
    }

    /// <summary>
    /// True while a touch gesture is in progress anywhere in the app. Shared
    /// so the mode strip can get out of the way: it is topmost and sits along
    /// the top of the screen, so a drag ending up there was dropped on it
    /// (logged: "DRAG end ... under [TouchPadClone ModeStrip]").
    /// </summary>
    public static bool SessionActive { get; private set; }

    private void BeginSession()
    {
        // A re-park left over from the previous press must die here: it would
        // keep yanking the real cursor back to a stale position while the new
        // touch is in progress.
        _rePark?.Stop();
        if (_session || !_s.FakeCursor) return;
        EnsureDpi();
        // Before anything of ours moves it: this is the spot to return to.
        RememberPhysicalCursor();
        _session = true;
        SessionActive = true;
        EnterPersistentFake();
        // One cursor: fight the system's pull onto the touch contact from the
        // first event, not only while a button is held - it moves the cursor
        // when it feels like it, and a plain move is just as vulnerable.
        // NOT with the pointer input path: the keeper exists to fight the system
// pulling the cursor onto the touch contact, and it does that by writing the
// cursor 125 times a second - with one cursor that is a tug of war (the
// system pulls to the finger, the keeper pulls back), which is exactly the
// "the gesture comes alive and then gets blocked" report. Without it the
// pointer path's single SetPhysicalCursorPos per event simply wins.
        if (RealCursorOnly && !UseRawPointer) StartDragKeeper();
        DebugLog.Write($"SESSION begin fake=({_fakeX:0},{_fakeY:0}) phys=({_physX},{_physY})");
    }

    private void EndSession()
    {
        if (!_session) return;
        _session = false;
        SessionActive = false;
        // Backstop: no finger is on the pad any more, so nothing can be
        // holding a synthetic button. A grab whose release was missed would
        // otherwise leave the left button down for the whole desktop (the
        // stream-balance check caught exactly that: 8 downs, 7 ups).
        if (_dragHold) ReleaseActionButton();
        if (ArrowOn)
        {
            // Single cursor identity: keep it hidden, arrow stays.
            EnsureHidden();
            ShowFake();
            // Hand the real cursor back to the physical mouse instead of
            // parking it under the fake one - UNLESS a menu is open.
            if (!_menuClick) RestorePhysicalCursor("session end");
        }
        else
        {
            EnsureVisible();
            if (!UnifiedPhysical && !_menuClick)
                RestorePhysicalCursor("session end (no arrow)");
        }
        _suppressPhysicalUntil = DateTime.Now.AddMilliseconds(250);
        DebugLog.Write($"SESSION end fake=({_fakeX:0},{_fakeY:0}) phys=({_physX},{_physY})");
        // Lift-yank: Windows moves the cursor onto the contact point as the
        // finger leaves, i.e. AFTER we handed it back - and the delay is not
        // fixed (measured anywhere from ~0 to several hundred ms), so a
        // single re-check is a coin flip. Poll a few times and stop as soon
        // as it is where it belongs.
        // (The original condition compared "moved since park" with
        // "distance from park" - the same number twice, so it could never be
        // true and the cursor stayed on the finger.)
        double wantX = UnifiedPhysical ? _fakeX : _physX;
        double wantY = UnifiedPhysical ? _fakeY : _physY;
        // One cursor: there is nothing to hand back, and re-parking is what
        // dragged the cursor (and a drop) off the target a moment after the
        // release. Leave the cursor where the gesture put it.
        if (RealCursorOnly) return;
        // A context menu is still up: the cursor must STAY on it. Every
        // SetCursorPos away from an open menu dismisses it, which is why a
        // right-click appeared to do nothing - the event fired, the menu
        // opened, and the hand-back closed it again a moment later.
        if (!_menuClick && wantX != int.MinValue)
        {
            int tries = 6;    // covers DropGraceMs plus a few polls
            _rePark?.Stop();
            _rePark = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100),
            };
            var timer = _rePark;
            timer.Tick += (_, _) =>
            {
                try
                {
                    if (--tries <= 0) { timer.Stop(); return; }
                    if (_session || !_s.FakeCursor) { timer.Stop(); return; }
                    // A drop is in flight: the release must land, and be
                    // SEEN, at the virtual cursor. Moving the cursor back
                    // before the application has processed it made the drop
                    // land at the physical mouse instead. Keep polling, just
                    // do not move yet.
                    if (DateTime.Now < _cursorFreeAt) return;
                    var (cx, cy) = InputSim.Cursor();
                    if (Math.Abs(cx - wantX) <= 2 && Math.Abs(cy - wantY) <= 2)
                    {
                        timer.Stop();
                        return;
                    }
                    // Only if nobody took it meanwhile: a mouse that moved
                    // in the meantime owns the cursor, not us.
                    InputSim.SetCursor((int)wantX, (int)wantY);
                    ForgetRealCursor();
                    _suppressPhysicalUntil = DateTime.Now.AddMilliseconds(120);
                    // Log only a real lift-yank. Every poll used to log - 71
                    // lines in one session - which buries everything else when
                    // the log is read back.
                    if (Math.Abs(cx - wantX) + Math.Abs(cy - wantY) > 40)
                    {
                        string tag = UnifiedPhysical ? "RE-PARK" : "PHYS re-park";
                        DebugLog.Write(
                            $"{tag} to ({wantX:0},{wantY:0}), was ({cx},{cy})");
                    }
                }
                catch { }
            };
            timer.Start();
        }
    }

    /// <summary>
    /// Hide/show the roaming virtual cursor. This used to have no gesture to
    /// bind to at all - the pad list had nothing but clicks, wheels and
    /// browser keys, and the strip list had "center_fake" but no toggle - so
    /// the only way to get it was to unbind something, and the natural
    /// candidate (a swipe) came pre-bound to wheel_up, i.e. a scroll.
    /// </summary>
    public void ToggleFakeCursor()
    {
        bool on = !(_s.ShowFakeArrow && _s.FakeCursor);
        _s.ShowFakeArrow = on;
        _s.FakeCursor = on;
        if (on)
        {
            ShowFake();
            DebugLog.Write("VIRTUAL CURSOR shown");
        }
        else
        {
            try { _overlay?.Hide(); } catch { }
            // With no virtual cursor on screen the system one must be back,
            // or the desktop is left with an invisible pointer.
            EnsureVisible();
            DebugLog.Write("VIRTUAL CURSOR hidden");
        }
        _s.Save();
    }

    /// <summary>
    /// A two-finger scroll ends with one finger usually still resting on the
    /// pad. That finger has travelled the whole scroll distance, and the
    /// scroll branch in OnTouchMove returns BEFORE the cursor is updated - so
    /// the travel was never applied to the virtual cursor but stayed in
    /// p - f.Start. The first move after the release then handed the entire
    /// scroll distance to the cursor at once, times the gain: the cursor
    /// jumped as soon as the scroll ended ("it does not move while scrolling,
    /// then it moves").
    ///
    /// Re-anchor the survivors to where the cursor actually is. PeakRate goes
    /// too: the scroll latched it (both fingers' rates are measured before the
    /// two-finger branch), which left the survivor permanently "a drag" - no
    /// long-press, and MoveOwnsSession refusing every further contact.
    ///
    /// Finger.Moved is deliberately NOT cleared. The finger really did travel,
    /// and letting a leftover finger click on lift would fire a click at the
    /// end of every scroll.
    /// </summary>
    private void RebaseAfterTwoFinger()
    {
        foreach (var f in _fingers.Values)
        {
            f.Start = f.Last;
            f.Fx0 = _fakeX; f.Fy0 = _fakeY;
            f.PeakRate = 0;
            f.Trail.Clear();
        }
        _twoAccX = _twoAccY = 0;
    }

    /// <summary>Call on nasty exits so the cursor is never left hidden.</summary>
    public void EmergencyRestore()
    {
        if (_fingers.Count > 0) return; // never yank mid-touch
        _session = false;
        try { _overlay?.Hide(); } catch { }
        EnsureVisible();
    }

    private void AdoptCursor()
    {
        var (cx, cy) = InputSim.Cursor();
        _fakeX = cx; _fakeY = cy;
        _chaseX = cx; _chaseY = cy;
        _fakeInit = true;
        ClampFake();
        if (ArrowOn && _overlay != null && _overlay.IsVisible)
            _overlay.MoveToPhysical(_fakeX, _fakeY);
    }

    private void HandoverToPhysicalMouse()
    {
        EnsureVisible();
        try { _overlay?.Hide(); } catch { }
        AdoptCursor();
    }

    public event Action? RequestSettings;
    public event Action? RequestAssist;
    public event Action? RequestFullscreenToggle;
    /// <summary>Fired on any press: lets the app dismiss popups (picker).</summary>
    public event Action? PressedAnywhere;

    private sealed class Finger
    {
        public WPoint Start, Last;
        public DateTime T0;
        public DateTime Active; // last down/move (ghost sweep)
        public bool Moved;
        public Tile? Tile;
        public string? HoldButton;
        public (int vk, int[] mods)? HoldCombo;
        public double Fx0, Fy0; // fake cursor at press-down (tap rewind anchor)
        /// <summary>Raw press point, never re-based (net-drag guard).</summary>
        public double OriginX, OriginY;
        /// <summary>
        /// Highest travel seen in any HoldWindowMs window during this press.
        /// The PEAK is what identifies a drag: a current-rate test forgets -
        /// park a finger after a fast drag and the window empties, the rate
        /// reads 0, and the hold would fire on the spot the drag ended.
        /// A drag has to stay a drag for the rest of the press.
        /// </summary>
        public double PeakRate;
        public readonly List<(DateTime t, double x, double y)> Trail = new();
    }

    /// <summary>
    /// Reap ghost fingers: contacts whose TouchUp never arrived (capture
    /// stolen, driver hiccup, palm-cancel). Without this _fingers only grows,
    /// Count stays >= 2 forever, and single-tap chains (double/triple) never
    /// arm again while plain taps keep working - the exact reported rot.
    /// A legit hold always moves or ends within seconds; 10s idle = ghost.
    /// </summary>
    private void SweepGhosts()
    {
        var now = DateTime.Now;
        var dead = new List<int>();
        foreach (var kv in _fingers)
            if ((now - kv.Value.Active).TotalSeconds > 10)
                dead.Add(kv.Key);
        foreach (int id in dead)
        {
            if (!_fingers.TryGetValue(id, out var f)) continue;
            _fingers.Remove(id);
            if (f.HoldButton != null) InputSim.Up(f.HoldButton);
            if (f.HoldCombo is { } c)
            {
                InputSim.HoldKey(c.vk, false);
                foreach (int m in c.mods) InputSim.HoldKey(m, false);
            }
            if (id == _longId) CancelLong();
            if (id == _holdId) CancelHold();
            if (id == _dragArmId) _dragArmId = -1;
            if (id == _dragId && _dragHold) ReleaseActionButton();
            if (id == _secondId) { _secondPress = false; CancelHold(); }
            if (id == _thirdId) _thirdPress = false;
            if (id == _twoId) { _twoId = -1; _twoCandidate = false; }
            DebugLog.Write($"SWEEP ghost id={id} (had {_fingers.Count + 1} tracked)");
        }
    }

    // ---- Per-event gesture state machine (original §Side/Float model) ----
    // tap / double-tap / long-press / 2nd-tap-hold / two-finger-tap / swipes,
    // each mapped to a configurable action, per layout family.
    private GestureMap G => _s.ActiveGestures(_layout?.Name ?? "");
    private System.Windows.Threading.DispatcherTimer? _longTimer;
    private System.Windows.Threading.DispatcherTimer? _holdTimer;
    private int _longId = -1;
    private string _longAction = "none";
    private DateTime _longDue = DateTime.MinValue;
    private bool _longFired;
    private bool _longGraceUsed;
    private int _holdId = -1;
    private DateTime _pendingTapUntil = DateTime.MinValue;
    private DateTime _pendingTripleUntil = DateTime.MinValue;
    private bool _secondPress;
    private int _secondId = -1;
    private bool _thirdPress;
    private int _thirdId = -1;
    private bool _secondConsumed;   // hold/gesture already resolved this press
    private bool _dragHold;         // action button currently held for drag
    private int _dragId = -1;
    private string? _actionHeld;
    /// <summary>
    /// A chained press (tap, then press again) waiting to be resolved into
    /// EITHER a drag (it moved) or a hold (it didn't). The button is NOT
    /// down yet: pressing it here is what used to make every chained press a
    /// drag and steal the long-press entirely.
    /// </summary>
    private int _dragArmId = -1;
    /// <summary>
    /// Whether the configured hold action can become a drag. The original
    /// distinguishes press-and-hold Drag from click-then-hold L-click and
    /// drag; both move, but only the latter sends its own click.
    /// </summary>
    private bool DragActionArmed() => G.SecondHold is "drag" or "drag_hold";
    /// <summary>
    /// A completed double/triple tap leaves the grab armed for one more
    /// press (see the plain-press branch in OnTouchDown). Twice MultiTapMs:
    /// the chain window itself is deliberately short so a slow third tap
    /// does not turn into a triple-click, but the human pause after a
    /// double-click is much longer than the gap between two taps.
    /// </summary>
    private DateTime _grabArmUntil = DateTime.MinValue;
    /// <summary>A click has been delivered since the current press began.</summary>
    private bool _clickSincePress;
    private bool _keepCursorOnce;

    /// <summary>
    /// Arm the grab for the next press. Called when a tap completes AND when a
    /// drag or a long-press ends.
    ///
    /// The end-of-action case is what made "double tap then drag, again" fail:
    /// the press after the first drag was a plain one (logged "GESTURE arm
    /// long" instead of "arm hold -> drag"), because the window opened by the
    /// preceding tap had closed - measured at 1.3s after the drag ended, inside
    /// no window at all. After manipulating one thing the user goes on
    /// manipulating, so the next press belongs to the drag again.
    /// </summary>
    private void ArmGrab(string why)
    {
        _grabArmUntil = DateTime.Now.AddMilliseconds(_s.MultiTapMs * 3);
        DebugLog.Write($"GESTURE grab armed for the next press ({why})");
    }
    /// <summary>
    /// How long the cursor must stay at the drop point after a synthetic
    /// release, before anything may hand it back to the physical mouse. A
    /// drop is not always processed synchronously with the button-up, so
    /// moving the cursor at once (it used to happen in the same millisecond)
    /// made the drop land at the PHYSICAL mouse position.
    /// </summary>
    private const int DropGraceMs = 300;
    /// <summary>Cursor may not be moved back to the physical mouse before this.</summary>
    private DateTime _cursorFreeAt = DateTime.MinValue;
    /// <summary>Drop point bookkeeping for the drag-end log.</summary>
    private double _dragFromX = -1e9, _dragFromY = -1e9;
    private double _dragPathDip;
    private int _twoId = -1;        // second concurrent finger
    private bool _twoCandidate;
    private bool _twoActive;        // a two-finger gesture is/was in flight
    private double _twoAccX, _twoAccY;


    private void CancelLong() { _longTimer?.Stop(); _longTimer = null; _longId = -1; }
    private void CancelHold() { _holdTimer?.Stop(); _holdTimer = null; _holdId = -1; }

    /// <summary>Drop the tap/triple chain: this press is not part of one.</summary>
    private void ClearChains()
    {
        _pendingTapUntil = DateTime.MinValue;
        _pendingTripleUntil = DateTime.MinValue;
    }

    /// <summary>
    /// NET travel from the press point (Manhattan). Used for tap/drag
    /// classification and for the cursor ramp, NOT for the hold decision.
    /// </summary>
    private static double NetTravel(Finger f, WPoint p) =>
        Math.Abs(p.X - f.Start.X) + Math.Abs(p.Y - f.Start.Y);

    /// <summary>
    /// Is this press a DRAG rather than a hold? Measured on the real panel by
    /// replaying the debug log (DIP of NET travel per 150ms window):
    ///   resting finger   ~0.06  (0.4 DIP per SECOND of drift)
    ///   slow deliberate  ~19    (0.13 DIP/ms - fine positioning)
    ///   real drag        104-150 (0.7-1.0 DIP/ms)
    ///
    /// The rate is measured as NET displacement between the oldest and newest
    /// sample in the window, so tremor cancels itself out - that is the whole
    /// reason a still finger reads as still. An earlier note here claimed the
    /// resting creep was ~0.4 DIP/ms (65 per 150ms), which is 1000x the
    /// measured value and put the default threshold (100) ABOVE ordinary slow
    /// movement: every press that was not a hard flick was classified as
    /// "still" and ended in a right-click.
    ///
    /// Any rule based on DISTANCE FROM THE PRESS POINT cannot work: it
    /// integrates the drift and cancels every hold. The PEAK RATE over a
    /// short window ignores how far the finger has drifted in total, and
    /// once the peak trips, the press is a drag for good - see
    /// Finger.PeakRate.
    /// </summary>
    private bool HoldIsDragging(Finger f)
    {
        if (f.PeakRate > _s.HoldCancelDip) return true;
        // Safety net for a big, slow move: a resting finger drifts ~0.4 DIP/s,
        // so a corner-to-corner travel is certainly deliberate.
        return Math.Abs(f.Last.X - f.OriginX) + Math.Abs(f.Last.Y - f.OriginY)
            > DragNetDip;
    }

    /// <summary>Peak rate in DIP/150ms, for logging.</summary>
    private static double RecentTravel(Finger f) => f.PeakRate;

    private int _realSentX = int.MinValue, _realSentY = int.MinValue;

    /// <summary>
    /// Drive the system cursor onto the virtual cursor while a button is
    /// held. Absolute, so it is idempotent: whoever wrote last wins and
    /// nothing accumulates. The cache is dropped whenever the cursor is
    /// parked or jumped by someone else, so the next drag re-syncs instead
    /// of assuming the cursor is already where we left it.
    /// </summary>
    private void SyncRealCursorToFake()
    {
        // Refresh before the "no change" guard: the surface has to stay
        // transparent to the mouse for the WHOLE gesture - the moves and the
        // release must reach the target, and the release is what a drop is
        // decided on. A 120ms tail is enough between events only because this
        // runs on every one.
        HoldMouseThrough();
        int tx = (int)_fakeX, ty = (int)_fakeY;
        if (tx == _realSentX && ty == _realSentY) return;
        _realSentX = tx; _realSentY = ty;
        // SetCursorPos, NOT a SendInput absolute move.
        //
        // This is the one difference between the two ways to move the cursor
        // that survived testing in a minimal harness (Notepad, same text line,
        // same timing, same button injection - only the move method changed):
        // a drag built from SendInput absolute moves selected nothing, while
        // the same drag built from SetCursorPos (and the same one built from
        // relative moves) selected the text. Clicks never showed it because a
        // click's down and up are one atomic batch at one position - there is
        // no motion for the application to track, so "press, and release at
        // the moved-to place" works for a click and silently fails for a drag.
        //
        // SetCursorPos is also exact (no pointer acceleration, which only
        // affects relative input) and self-correcting, unlike relative deltas
        // which drifted into the screen edge.
        InputSim.SetCursor(tx, ty);
        // SetCursorPos is invisible to the injection recorder, so say it here:
        // otherwise a whole drag logs "moves=0" and the harness is blind to
        // the very motion it exists to check.
        InputSim.NotePosition(tx, ty);
        // Read back often, and say which window is under the cursor. The log
        // line otherwise only records what we ASKED for; this records what the
        // system did and whether the drag is still over the target at all.
        // An external harness cannot answer this: SetForegroundWindow fails
        // from a non-foreground process, so a test that injects a drag into
        // another app is unreliable here (measured: focus lost after the first
        // attempt, and the clipboard/UIA reads came back empty or errored).
        if ((_syncProbeN++ % 6) == 0)
        {
            var (ax, ay) = InputSim.Cursor();
            bool off = Math.Abs(ax - tx) > 2 || Math.Abs(ay - ty) > 2;
            // No window lookup here: it costs three user32 calls plus string
            // work on a path that runs many times a second, and the pad felt
            // slow because of it. The divergence flag is the part that matters;
            // the window under the drop is reported once, at DRAG end.
            DebugLog.Write($"DRAG sync -> ({tx},{ty}) actual=({ax},{ay})"
                + (off ? " DIVERGED" : ""));
            // Put it straight back. The yank happens AFTER our SetCursorPos
            // (the system moves the cursor when it processes the touch), so
            // waiting for the next move event leaves the cursor - and, if the
            // release lands in that window, the drop - on the pad.
            if (off)
            {
                InputSim.SetCursor(tx, ty);
                ForgetRealCursor();
                DebugLog.Write($"DRAG re-assert -> ({tx},{ty})");
            }
        }
    }

    private int _syncProbeN;

    /// <summary>Forget where we last put the cursor (someone else moved it).</summary>
    private void ForgetRealCursor() { _realSentX = int.MinValue; _realSentY = int.MinValue; }

    /// <summary>
    /// Is any synthetic button down right now - a drag grab (_actionHeld) or
    /// a held button tile (Finger.HoldButton)? Only then does the OS cursor
    /// have to follow the virtual one; while merely hovering, the physical
    /// mouse keeps its own cursor.
    /// </summary>
    private bool ButtonHeld()
    {
        if (_actionHeld != null) return true;
        foreach (var f in _fingers.Values)
            if (f.HoldButton != null) return true;
        return false;
    }

    /// <summary>
    /// True while the single live press is already classified as MOVING, so
    /// the session belongs to it alone. Uses the same latched peak-rate rule
    /// as the hold/drag decision (HoldIsDragging), which is what separates a
    /// real drag from the pad's stationary creep.
    /// </summary>
    private bool MoveOwnsSession()
    {
        if (_fingers.Count != 1) return false;
        foreach (var held in _fingers.Values)
            if (HoldIsDragging(held)) return true;
        return false;
    }

    /// <summary>
    /// What the CURRENT press produced, for the status label. The release
    /// used to write a generic "up gesture Nms" that overwrote the long-press
    /// line printed a moment earlier, so the label read as if nothing had
    /// happened right after a right-click fired. Empty = nothing fired yet.
    /// Cleared on the next press, so the label keeps the last outcome.
    /// </summary>
    private string _pressNote = "";

    private const int HoldWindowMs = 150;
    /// <summary>
    /// Extra wait before a long-press commits, spent once per press (see
    /// OnLongTick). A press that is going to hold pays it; a press that
    /// pauses and then drags does not.
    /// </summary>
    private const int LongGraceMs = 300;
    /// <summary>Net travel that is certainly a drag (see HoldIsDragging).</summary>
    /// <summary>
    /// Net travel from the press point that is certainly deliberate, whatever
    /// the rate says. A resting finger drifts ~0.4 DIP/s, so even a 3s hold
    /// stays around 1 DIP; anything past this is the user pointing somewhere.
    /// </summary>
    private const double DragNetDip = 60;

    /// <summary>
    /// Travel RATE over the last <paramref name="ms"/>, expressed in DIP per
    /// that window: distance from the OLDEST sample inside the window to
    /// now, divided by the span the window actually covered.
    /// Both halves are load-bearing:
    ///   - measuring from the newest sample yields nothing (it IS "now");
    ///   - dividing by the NOMINAL window under-rates a stroke that lasted
    ///     less than it, and a fast flick is exactly such a stroke: a 100ms
    ///     drag divided by 150ms reads 0.67 of its real speed and slipped
    ///     through every threshold.
    /// Speed must never be taken between two consecutive events: when the
    /// UI thread stalls, queued moves arrive back-to-back and a per-event
    /// delta reads as a rocket.
    /// </summary>
    private static double WindowRate(Finger f, int ms)
    {
        var now = f.Active;
        double lx = f.Last.X, ly = f.Last.Y;
        double ageMax = 0;
        int n = 0;
        foreach (var (t, x, y) in f.Trail)
        {
            double age = (now - t).TotalMilliseconds;
            if (age > ms) continue;
            if (n == 0) { lx = x; ly = y; }      // oldest inside the window
            if (age > ageMax) ageMax = age;
            n++;
        }
        if (n < 3) return 0;
        double dx = f.Last.X - lx, dy = f.Last.Y - ly;
        // The floor is what makes this survive a busy UI thread: a stall
        // delivers queued moves back-to-back, and dividing a 5ms burst by 5ms
        // reads as a rocket - enough to release the hold pin on a genuinely
        // still finger (observed flaky). 80ms is below any real drag on this
        // panel (measured 145ms) and above any burst, so the discrimination
        // survives: creep 36, flick 234, either way.
        double span = Math.Max(80, ageMax);
        return Math.Sqrt(dx * dx + dy * dy) / span * ms;
    }

    /// <summary>
    /// Arm the hold timer. Polls every 40ms so a missed dwell check can
    /// RE-ARM instead of dying - a one-shot timer turned every wandering
    /// hold into silence (reported: long press did nothing at all).
    /// </summary>
    private void ArmLong(int id, string action)
    {
        CancelLong();
        _longId = id;
        _longAction = action;
        _longFired = false;
        _longGraceUsed = false;      // one grace per press, see OnLongTick
        _longDue = DateTime.Now.AddMilliseconds(
            Math.Max(200, _s.LongPressMs));
        if (_longTimer == null)
        {
            _longTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(40),
            };
            _longTimer.Tick += OnLongTick;
        }
        _longTimer.Start();
        DebugLog.Write($"GESTURE arm long id={id} -> {action} in {_s.LongPressMs}ms");
    }

    private void OnLongTick(object? sender, EventArgs e)
    {
        int id = _longId;
        if (id < 0) return;
        if (DateTime.Now < _longDue) return;
        if (!_fingers.TryGetValue(id, out var f) || _fingers.Count != 1)
        {
            CancelLong();
            DebugLog.Write("GESTURE long skipped (gone/pair)");
            return;
        }
        // ONE rule decides hold vs drag: the PEAK travel rate over 150ms
        // (see HoldIsDragging for the measurements). The move handler applies
        // the same rule on every event and latches it, so re-deriving it here
        // would only duplicate a decision already made.
        if (HoldIsDragging(f))
        {
            _longDue = DateTime.Now.AddMilliseconds(_s.LongPressMs);
            DebugLog.Write(
                $"GESTURE long deferred (peak {RecentTravel(f):0}DIP/{HoldWindowMs}ms)");
            return;
        }
        // Grace, ONCE per press - and only for a press that can still become
        // a drag. "Press, pause, move" and "press and hold" are
        // indistinguishable at the deadline: the finger is simply not moving
        // yet in both, so the deadline cannot be the commit point. Measured on
        // real hardware: the user presses, waits 405ms, then moves, and the
        // drag threshold is crossed at 504ms - four milliseconds after the
        // long-press came due (logged "long canceled (peak 12)" against a
        // threshold of 10). Move any slower and the drag silently became a
        // right-click.
        //
        // Only where it is needed: a press that can grab already has its
        // click delivered, so waiting costs nothing and it may yet be a
        // drag. A plain press has no click before it, so its long-press is
        // the primary action and must stay punctual.
        if (!_longGraceUsed && _dragArmId >= 0)
        {
            _longGraceUsed = true;
            _longDue = DateTime.Now.AddMilliseconds(LongGraceMs);
            DebugLog.Write($"GESTURE long grace {LongGraceMs}ms"
                + $" (peak {RecentTravel(f):0}DIP/{HoldWindowMs}ms)");
            return;
        }
        CancelLong();
        _longFired = true;
        // A grab-armed press can legitimately long-fire here (press and hold
        // IS a right-click), so nothing to assert.
        _dragArmId = -1;   // the press is resolved: no drag after a hold
        // After a right-click the user usually goes on manipulating, so the
        // next press is a drag again. See ArmGrab.
        ArmGrab("long-press fired");
        string note = $"long-press {(int)(DateTime.Now - f.T0).TotalMilliseconds}ms"
            + $" → {_longAction}";
        _pressNote = note;
        Status(note);
        // A menu click must wait for the LIFT. The context menu opens while
        // the finger is still on the pad, and Windows then synthesises a tap
        // at that contact when it lifts - a click outside the menu, which
        // dismisses it. The event was delivered all along (logged with its
        // target window); it simply died ~200ms later. Deferring to the
        // release also means nothing can dismiss it in between.
        if (_longAction is "right_click" or "middle_click")
        {
            _pendingMenuAction = _longAction;
            DebugLog.Write($"GESTURE long-press -> {_longAction} (deferred to lift,"
                + $" peak {RecentTravel(f):0}DIP/{HoldWindowMs}ms"
                + $" of {_s.HoldCancelDip:0} threshold)");
            return;
        }
        DebugLog.Write($"GESTURE long-press -> {_longAction}"
            + $" (peak {RecentTravel(f):0}DIP/{HoldWindowMs}ms"
            + $" of {_s.HoldCancelDip:0} threshold)");
        FirePressAction(_longAction, id);
    }

    private void ArmHold(int id, string action)
    {
        CancelHold();
        _holdId = id;
        _holdTimer = new System.Windows.Threading.DispatcherTimer
        {
            // The same deadline as the long press, not a shorter one of its
            // own. At 250ms a slightly slow TAP was hijacked into a chained
            // hold - measured: TOUCHDOWN, then "GESTURE second-hold" 251ms
            // later, so a tap of 251ms fired a click-and-hold and never
            // completed as a tap. That is the "click release is judged
            // wrongly" report, and it also broke the double tap, because a
            // second tap slower than the hold deadline resolved as a hold
            // instead of as the second tap of the chain. A movement still
            // grabs immediately, so this timer only decides how long a
            // STATIONARY chained press has to be held to become SecondHold.
            Interval = TimeSpan.FromMilliseconds(
                Math.Max(300, _s.LongPressMs)),
        };
        _holdTimer.Tick += (_, _) =>
        {
            CancelHold();
            if (!_fingers.TryGetValue(id, out var held)) return;
            // A finger that has already MOVED is a cursor move, not a hold:
            // the hold action (drag) must only claim a press that stayed put.
            // Without this, "touch and move" would grab mid-stroke and the
            // pointer could never be moved.
            if (held.Moved)
            {
                DebugLog.Write($"GESTURE hold skipped id={id} (the press moved)");
                return;
            }
            // The movement already grabbed this press (StartDrag). Firing
            // SecondHold now would press the button a SECOND time, and the
            // release would only undo one of them - a stuck / doubled button.
            if (_dragHold && _dragId == id) return;
            if (id == _longId) CancelLong(); // don't also fire long-press
            _secondConsumed = true;
            _dragArmId = -1;
            _pressNote = $"hold → {action}";
            Status(_pressNote);
            DebugLog.Write("GESTURE second-hold");
            FirePressAction(action, id);
        };
        _holdTimer.Start();
        // Log the deadline: a "second-hold" line arriving sooner than this
        // would mean the timer is not the one firing (seen once at 265ms while
        // the interval was 500).
        DebugLog.Write($"GESTURE arm hold id={id} -> {action} in"
            + $" {(int)(_holdTimer.Interval.TotalMilliseconds)}ms");
    }

    /// <summary>
    /// A chained press started moving: it is a drag, not a hold. The button
    /// goes down ATOMICALLY at the press-down aim point (see DownAt) - a
    /// bare Down() after a cursor park lost the race with the OS yank and
    /// landed on our own pad, so nothing ever moved.
    /// </summary>
    private void StartDrag(int id)
    {
        if (!DragEnabled)
        {
            DebugLog.Write($"DRAG disabled: press id={id} stays a cursor move");
            _dragArmId = -1;
            return;
        }
        if (_dragHold || !_fingers.TryGetValue(id, out var f)) return;
        CancelLong();
        _dragHold = true;
        _dragId = id;
        _actionHeld = "left";
        _secondConsumed = true;
        _dragArmId = -1;
        ClearChains();
        // The cursor was frozen while this press was unresolved, so the fake
        // position still IS the aim point. Rebase the travel baseline to the
        // current finger position so the drag continues without a jump.
        _fakeX = f.Fx0; _fakeY = f.Fy0;
        ClampFake();
        _overlay?.MoveToPhysical(_fakeX, _fakeY);
        f.Start = f.Last;
        // Both drag routes press and hold the same way; only an explicit
        // click-then-hold action adds its own click first. See GrabHold.
        GrabHold(id, G.SecondHold == "drag_hold");
        _pressNote = "drag (grabbed)";
        Status(_pressNote);
        // Who actually receives this press? If it is the overlay, or an
        // unfocused window (whose activation click eats the drag), the log
        // says so here instead of the symptom being "the drag event fired
        // and nothing was dragged".
        DebugLog.Write($"GESTURE drag armed id={id} @({_fakeX:0},{_fakeY:0})"
            + $" under {DescribeWindowAt(_fakeX, _fakeY)}"
            + $" | foreground {DescribeWindow(GetForegroundWindow())}"
            + $" | clickFirst={_clickSincePress}");
    }

    /// <summary>
    /// Begin a press-and-hold drag at the aim point. The held button is what
    /// carries the drag; its release stays with the lifting finger and is not
    /// part of the grab. The original maps "2nd tap and hold" to Drag, not to
    /// click-then-drag, so the default path sends no push click: the tap that
    /// armed it already clicked. Only an explicit "drag_hold" action reproduces
    /// the old click-then-hold behavior.
    /// </summary>
    private void GrabHold(int id, bool pushClick)
    {
        if (!DragEnabled)
        {
            DebugLog.Write($"DRAG disabled: hold id={id} clicks instead of grabbing");
            DoGesture("drag_hold");   // release-context click, see DoGesture
            return;
        }
        // Origin and path bookkeeping for the drag-end log, here rather than
        // in StartDrag: the hold-timer route never calls StartDrag, so its
        // "net" came out as the -1e9 sentinel squared (1.4 billion px).
        _dragFromX = _fakeX; _dragFromY = _fakeY;
        _dragPathDip = 0;
        // A press must not land on the pad's own rectangle: the pad is on top,
        // so the press or the drag would be delivered to us instead of the
        // target. The surface is made mouse-transparent for the injection
        // instead of moving the aim, so the press and the drop stay exactly
        // where the finger put them (moving the aim put them a pixel off the
        // pad's edge instead).
        HoldMouseThrough();
        if (pushClick)
        {
            _keepCursorOnce = true;    // no hand-back between click and hold
            SafeClick("left");
        }
        _dragHold = true;
        _dragId = id;
        _actionHeld = "left";
        _secondConsumed = true;
        PressDown("left");
        // The keeper exists to fight the system moving the cursor onto the
        // touch contact. The pointer path never promotes a touch, so nothing
        // moves it and 125 wakeups a second are pure cost - only the old
        // WPF-touch path needs the keeper.
        if (!UseRawPointer) StartDragKeeper();
    }

    private System.Windows.Threading.DispatcherTimer? _dragKeeper;

    /// <summary>
    /// While a button is held, keep putting the cursor back on the virtual
    /// one, about 66 times a second, independently of touch events.
    ///
    /// The system moves the cursor onto the live touch contact - which is on
    /// the pad - and it does so AFTER our own SetCursorPos, so correcting on
    /// the next move event (or even on every move event) leaves windows in
    /// which the cursor, and any release, is on the pad. Measured in the log
    /// as "DRAG sync DIVERGED, actual sitting on our own pad". Doing it on a
    /// timer closes those windows and is an OUTPUT-side measure: it cannot
    /// disturb how touch input is routed, which is what an attempt to block
    /// the promotion itself did (pad touches stopped arriving at all).
    /// </summary>
    private void StartDragKeeper()
    {
        _dragKeeper?.Stop();
        _dragKeeper = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(8),
        };
        _dragKeeper.Tick += (_, _) =>
        {
            if (!_session)
            {
                _dragKeeper?.Stop();
                return;
            }
            int tx = (int)_fakeX, ty = (int)_fakeY;
            // Keep the surface transparent for as long as a button is down:
            // the release has to reach the target too.
            if (ButtonHeld()) HoldMouseThrough();
            var (ax, ay) = InputSim.Cursor();
            if (Math.Abs(ax - tx) <= 1 && Math.Abs(ay - ty) <= 1) return;
            InputSim.SetCursor(tx, ty);
            ForgetRealCursor();
            // No window lookup and no log line per correction: this runs 125
            // times a second while a drag is in flight and the lookups alone
            // made it stutter.
        };
        _dragKeeper.Start();
    }

    private void FirePressAction(string action, int id)
    {
        // Rewind to the press-start aim point: tremor during the hold would
        // otherwise land timer-fired actions (long-press menu!) off-target.
        if (_fingers.TryGetValue(id, out var ff))
        {
            _fakeX = ff.Fx0;
            _fakeY = ff.Fy0;
            ClampFake();
            _overlay?.MoveToPhysical(_fakeX, _fakeY);
        }
        if (action == "drag")
        {
            GrabHold(id, pushClick: false);
        }
        else if (action == "drag_hold")
        {
            GrabHold(id, pushClick: true);
        }
        else DoGesture(action);
    }

    private void ReleaseActionButton()
    {
        if (_dragHold && _actionHeld != null)
        {
            PressUp(_actionHeld);
            _dragHold = false;
            _actionHeld = null;
            // A drag just finished: the next press belongs to the drag again,
            // so the user can move one thing and then another without tapping
            // in between. See ArmGrab.
            ArmGrab("drag ended");
        }
    }

    /// <summary>
    /// Button press/release delivered AT the fake cursor in one atomic
    /// batch. Without a session the system cursor is the cursor, so the
    /// plain relative calls are correct there.
    /// </summary>
    private void PressDown(string button)
    {
        if (_session)
        {
            InputSim.DownAt((int)_fakeX, (int)_fakeY, button);
            ForgetRealCursor();
        }
        else InputSim.Down(button);
        _suppressPhysicalUntil = DateTime.Now.AddMilliseconds(250);
    }

    private void PressUp(string button)
    {
        // In one-cursor mode the keeper runs for the whole session (a plain
        // move needs it too); otherwise it exists only for the drag.
        if (!RealCursorOnly)
        {
            _dragKeeper?.Stop();
            _dragKeeper = null;
        }
        // The release has to reach the target as well, and it is the event a
        // drop is decided on.
        HoldMouseThrough();
        if (_session)
        {
            // Pin the position with SetCursorPos immediately before the
            // release. UpAt's own absolute move is a SendInput move, and if
            // those are not being applied as cursor motion (see
            // SyncRealCursorToFake) then the release could register at the
            // previous position - the press point - instead of the place the
            // finger moved to, which drops the drag back where it started.
            InputSim.SetCursor((int)_fakeX, (int)_fakeY);
            InputSim.UpAt((int)_fakeX, (int)_fakeY, button);
            ForgetRealCursor();
        }
        else InputSim.Up(button);
        // The release just happened: give the application time to process it
        // (a drop in particular) at the virtual cursor BEFORE anything moves
        // the cursor back. See RestorePhysicalCursor.
        if (_session)
        {
            _cursorFreeAt = DateTime.Now.AddMilliseconds(DropGraceMs);
            DebugLog.Write($"RELEASE: holding the cursor at"
                + $" ({_fakeX:0},{_fakeY:0}) for {DropGraceMs}ms (drop in flight)");
        }
        // Where the drop actually lands, and how far the drag went. A file
        // drag that ends 70px from where it started, in the same folder, is a
        // no-op in Explorer - it looks exactly like a drag that never
        // happened, so the question "did it drag?" has to be answered with the
        // drop target, not the injection.
        double netDip = Math.Sqrt(
            Math.Pow(_fakeX - _dragFromX, 2) + Math.Pow(_fakeY - _dragFromY, 2));
        DebugLog.Write($"DRAG end @({_fakeX:0},{_fakeY:0}) net={netDip:0}px"
            + $" path={_dragPathDip:0}DIP under {DescribeWindowAt(_fakeX, _fakeY)}");
        _dragFromX = _dragFromY = -1e9;
        _suppressPhysicalUntil = DateTime.Now.AddMilliseconds(250);
        // The drag is over: give the real cursor back to the physical mouse
        // so a virtual drag never leaves it parked under the fake one.
        RestorePhysicalCursor("drag end");
    }

    public PadWindow(AppSettings settings)
    {
        _s = settings;
        InitializeComponent();
        // ShowCursor's counter is shared for the desktop: a previous run that
        // hid the cursor (or the system hiding it while a touch is active) can
        // leave it invisible for everything, not just for us. Repair it on the
        // way in - the helper loops until the cursor is actually showing.
        try { InputSim.RestoreCursor(); } catch { }
        Core.NoActivate.Apply(this);
        Core.TabletTweaks.DisableSystemGestures(this);
        // Make the pad's touch surface transparent to mouse input WHILE we are
        // injecting a synthetic click or drag. The pad is on top, so a press
        // aimed at the pad's rectangle is otherwise delivered to us and lost -
        // which is why the cursor used to be pushed out of the pad before a
        // press, and why a drop that landed there went nowhere.
        //
        // Only while injecting: normal mouse and touch routing is untouched,
        // so the title bar, the tile buttons and every touch handler behave
        // exactly as before. A blanket HTTRANSPARENT would be simpler but it
        // would also make the pad transparent to TOUCH, and touch is the whole
        // point of the window.
        SourceInitialized += (_, _) =>
        {
            try
            {
                var src = System.Windows.Interop.HwndSource.FromHwnd(
                    new System.Windows.Interop.WindowInteropHelper(this).Handle);
                src?.AddHook(PadWndProc);
                DebugLog.Write(src != null
                    ? "PAD hit-test hook installed"
                    : "PAD hit-test hook NOT installed (FromHwnd null)");
                if (UseRawPointer)
                {
                    // Registering for touch is what makes Windows send
                    // WM_TOUCH (and WPF promote it to a mouse). Unregister to
                    // get WM_POINTER instead - the pointer path needs the
                    // window NOT to be touch-registered.
                    var h = new System.Windows.Interop.WindowInteropHelper(this)
                        .Handle;
                    bool ok = Core.RawPointer.UnregisterTouchWindow(h);
                    DebugLog.Write($"PAD unregister touch window = {ok}"
                        + " (WM_POINTER expected)");
                }
                if (UseRawInput)
                {
                    _rawTouch.PadRectProvider = () =>
                    {
                        var tl = PointToScreen(new Point(0, 0));
                        var src = PresentationSource.FromVisual(this);
                        double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1;
                        double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1;
                        return (tl.X, tl.Y, ActualWidth * sx, ActualHeight * sy);
                    };
                    _rawTouch.Contact = (id, sx, sy, down) =>
                    {
                        // Ids are kept apart from the pointer bridge's, so the
                        // two sources cannot be mistaken for one contact.
                        uint dev = unchecked((uint)(id + 1000));
                        bool known = _rawTouchDown.ContainsKey(id);
                        var at = new Point(sx, sy);
                        if (down && !known)
                        {
                            _rawTouchDown[id] = true;
                            ForwardRawTouch(dev, down: true, up: false, at);
                        }
                        else if (down)
                        {
                            ForwardRawTouch(dev, down: false, up: false, at);
                        }
                        else if (known)
                        {
                            _rawTouchDown.Remove(id);
                            ForwardRawTouch(dev, down: false, up: true, at);
                        }
                    };
                    _rawTouch.Register(
                        new System.Windows.Interop.WindowInteropHelper(this).Handle);
                }
            }
            catch { }
            EnsureTopmost();
        };
        // Keep it there. Another application putting a topmost window up would
        // otherwise cover the pad permanently.
        _topTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000),
        };
        _topTimer.Tick += (_, _) => EnsureTopmost();
        _topTimer.Start();
        SyncWindowSize();
        Left = SystemParameters.PrimaryScreenWidth - Width - 40;
        Top = SystemParameters.PrimaryScreenHeight - Height - 120;
        Opacity = settings.Opacity;

        // Touch via PREVIEW (tunneling): promotion to emulated mouse events
        // is decided before bubbling handlers run. Handling (and marking
        // Handled) only in bubbling TouchDown let every touch ALSO land as a
        // real system click at the finger (measured M-DOWN stylus=True
        // mid-touch) - doubling/renaming/scattering everything. Preview gets
        // first shot, so promotion never happens for pad touches. (Chrome
        // buttons and picker buttons intentionally keep promotion for Click.)
        Surface.PreviewTouchDown += OnTouchDown;
        Surface.PreviewTouchMove += OnTouchMove;
        Surface.PreviewTouchUp += OnTouchUp;
        // A contact that vanishes without TouchUp (capture stolen, driver
        // cancel) must still release: treat capture loss as a release.
        Surface.LostTouchCapture += OnTouchCaptureLost;
        Surface.MouseDown += OnMouseDown;
        Surface.MouseMove += OnMouseMove;
        Surface.MouseUp += OnMouseUp;
        // A touch on the pad is promoted by WPF to a stylus event and then to
        // a MOUSE event, and the system moves the cursor onto the contact as
        // part of that (logged: M-DOWN stylus=True mid-touch, and the cursor
        // left at the touch, not at the pad's virtual position). Our handlers
        // already ignore those, but ignoring is not blocking - the event still
        // happens and the mouse still moves. Mark the promoted ones handled so
        // a touch cannot produce a mouse event at all. A real mouse has no
        // StylusDevice, so it is untouched.
        Surface.PreviewMouseDown += SwallowTouchMouse;
        Surface.PreviewMouseMove += SwallowTouchMouse;
        Surface.PreviewMouseUp += SwallowTouchMouse;
        SizeChanged += (_, _) => Render();
        // NOTE: window drags apply DIRECTLY per move event (absolute target
        // = pointer - grab offset). This was briefly a 30Hz/60Hz timer queue
        // and lagged: apply immediately, targets don't accumulate error.
        // Title-bar chrome: drag background to move, buttons act.
        TitleBar.PreviewTouchDown += OnChromeTouchDown;
        TitleBar.PreviewTouchMove += OnChromeTouchMove;
        TitleBar.PreviewTouchUp += OnChromeTouchUp;
        TitleBar.PreviewMouseDown += OnChromeMouseDown;
        TitleBar.PreviewMouseMove += OnChromeMouseMove;
        TitleBar.PreviewMouseUp += OnChromeMouseUp;
        ChromeMenu.Click += (_, _) => RequestSettings?.Invoke();
        IsVisibleChanged += (_, _) =>
        {
            // Fresh show: normal cursor, fake silently adopted. Persistent
            // fake mode engages on first touch (BeginSession), not at launch
            // (launching cursor-free is disorienting).
            if (IsVisible) AdoptCursor();
            else EmergencyRestore();
        };
        Loaded += (_, _) =>
        {
            // Launch anchor: PRIMARY screen center in physical px.
            // Never the system cursor (touch yanks it to garbage like x=0),
            // never the virtual-screen middle (that can sit on the external
            // monitor or in the gap between monitors).
            var (ccx, ccy) = PrimaryCenter();
            _fakeX = ccx; _fakeY = ccy;
            ClampFake();
            _fakeInit = true;
            DebugLog.Write($"INIT fake=({_fakeX:0},{_fakeY:0}) dpi={_dpi}");
        };
    }

    /// <summary>
    /// Trackpad mode: fake cursor persistently visible, system cursor parked
    /// under it and hidden. Called on show/load and after settings apply.
    /// With ShowFakeArrow off there is no arrow and no hiding: the chased
    /// real cursor is the only cursor.
    /// </summary>
    private bool ArrowOn => !RealCursorOnly && _s.FakeCursor && _s.ShowFakeArrow;

    public void EnterPersistentFake()
    {
        if (!_s.FakeCursor) return;
        EnsureDpi();
        if (!_fakeInit)
        {
            var (cx, cy) = InputSim.Cursor();
            _fakeX = cx; _fakeY = cy;
            _fakeInit = true;
        }
        ClampFake();
        if (ArrowOn)
        {
            ShowFake();
            EnsureHidden();
        }
        else
        {
            try { _overlay?.Hide(); } catch { }
            EnsureVisible();
        }
        _suppressPhysicalUntil = DateTime.Now.AddMilliseconds(250);
    }

    private double _grabDX, _grabDY; // chrome press: screen pos minus window
    private double _gripDX, _gripDY; // grip tile press: same, separate finger
    private bool _chromeTouch;
    private int _chromeTouchId = -1;
    private bool _chromeMouse;

    /// <summary>
    /// Screen-space point from a window-relative one, using the CURRENT
    /// window position. Invariant to window motion by construction:
    /// screen = Left/Top + relative always equals the true screen point,
    /// so deltas never contain our own moves (no feedback - this is what
    /// broke left-edge drags, where the origin itself moves).
    /// </summary>
    private WPoint ScreenOf(WPoint rel) => new(rel.X + Left, rel.Y + Top);

    /// <summary>Place the window, clamped to the virtual screen.</summary>
    private void PlaceWindow(double tx, double ty)
    {
        double vx = SystemParameters.VirtualScreenLeft;
        double vy = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth;
        double vh = SystemParameters.VirtualScreenHeight;
        Left = Math.Max(vx, Math.Min(vx + vw - Width, tx));
        Top = Math.Max(vy, Math.Min(vy + vh - Height, ty));
    }

    /// <summary>Touches on the title bar must not fall through to tiles.</summary>
    private bool IsChrome(object? src)
    {
        DependencyObject? d = src as DependencyObject;
        while (d != null)
        {
            if (ReferenceEquals(d, TitleBar)) return true;
            d = (d as FrameworkElement)?.Parent as DependencyObject
                ?? (d as FrameworkContentElement)?.Parent as DependencyObject;
        }
        return false;
    }

    private void OnChromeTouchDown(object sender, TouchEventArgs e)
    {
        if (Core.WpfHit.IsButton(e.OriginalSource)) return; // button owns it
        _chromeTouch = true;
        _chromeTouchId = e.TouchDevice.Id;
        var sp = ScreenOf(e.GetTouchPoint(Surface).Position);
        _grabDX = sp.X - Left;
        _grabDY = sp.Y - Top;
        TitleBar.CaptureTouch(e.TouchDevice);
        e.Handled = true;
    }

    private void OnChromeTouchMove(object sender, TouchEventArgs e)
    {
        if (!_chromeTouch || e.TouchDevice.Id != _chromeTouchId) return;
        var p = ScreenOf(e.GetTouchPoint(Surface).Position);
        PlaceWindow(p.X - _grabDX, p.Y - _grabDY);
        e.Handled = true;
    }

    private void OnChromeTouchUp(object sender, TouchEventArgs e)
    {
        if (!_chromeTouch || e.TouchDevice.Id != _chromeTouchId) return;
        _chromeTouch = false;
        _chromeTouchId = -1;
        TitleBar.ReleaseTouchCapture(e.TouchDevice);
        e.Handled = true;
    }

    private void OnChromeMouseDown(object sender, WMouseButtonEventArgs e)
    {
        if (e.StylusDevice != null || Core.WpfHit.IsButton(e.OriginalSource)) return;
        _chromeMouse = true;
        var sp = ScreenOf(e.GetPosition(Surface));
        _grabDX = sp.X - Left;
        _grabDY = sp.Y - Top;
        TitleBar.CaptureMouse();
        e.Handled = true;
    }

    private void OnChromeMouseMove(object sender, WMouseEventArgs e)
    {
        if (e.StylusDevice != null || !_chromeMouse) return;
        var p = ScreenOf(e.GetPosition(Surface));
        PlaceWindow(p.X - _grabDX, p.Y - _grabDY);
        e.Handled = true;
    }

    private void OnChromeMouseUp(object sender, WMouseButtonEventArgs e)
    {
        if (e.StylusDevice != null || !_chromeMouse) return;
        _chromeMouse = false;
        TitleBar.ReleaseMouseCapture();
        e.Handled = true;
    }

    public void RefreshChrome() =>
        ChromeLabel.Text = $"{_layout?.Name ?? _s.Layout} · {_s.Speed:0.0}x";

    /// <summary>
    /// Tile area = FULL window. The title bar is an overlay attached on top,
    /// not tile space. Window height = tile setting + 30 chrome.
    /// </summary>
    public const double ChromeH = 30;

    private (double w, double h) TileArea() =>
        (ActualWidth > 0 ? ActualWidth : Width,
         ActualHeight > 0 ? ActualHeight : Height);

    public void SyncWindowSize()
    {
        Width = _s.PadWidth;
        Height = _s.PadHeight + ChromeH;
    }

    // ---------------- corner resizers (bottom-left / bottom-right) ----------------
    private const double GripZone = 26;
    private string? _resizeMode; // "left", "right", or null
    private double _rsW, _rsH, _rsX, _rsY, _rsL;
    private int _rsTouchId = -1;
    private bool _rsMouse;

    /// <summary>Which corner zone (if any) holds this window point.</summary>
    private string? ResizerAt(double x, double y)
    {
        var (w, h) = TileArea();
        if (y < h - GripZone) return null;
        if (x < GripZone) return "left";
        if (x > w - GripZone) return "right";
        return null;
    }

    private void BeginResize(string mode, double x, double y)
    {
        _resizeMode = mode;
        _rsW = Width; _rsH = Height;
        _rsX = x; _rsY = y; _rsL = Left;
        Status($"resize {mode}");
    }

    private void MoveResize(double x, double y)
    {
        if (_resizeMode == null) return;
        double dx = x - _rsX, dy = y - _rsY;
        if (_resizeMode == "right")
        {
            Width = Math.Max(220, _rsW + dx);
            Height = Math.Max(200, _rsH + dy);
        }
        else
        {
            double nw = Math.Max(220, _rsW - dx);
            Left = _rsL + (_rsW - nw);
            Width = nw;
            Height = Math.Max(200, _rsH + dy);
        }
    }

    private void EndResize()
    {
        if (_resizeMode == null) return;
        _resizeMode = null;
        _rsTouchId = -1;
        _rsMouse = false;
        // Persist TILE area (window minus chrome).
        _s.PadWidth = Width;
        _s.PadHeight = Height - ChromeH;
        _s.Save();
        Status($"size {Width:0}x{Height - ChromeH:0}");
        DebugLog.Write($"RESIZE to {Width:0}x{Height - ChromeH:0}");
    }

    private void DrawAdornments()
    {
        Adorn.Children.Clear();
        var (w, h) = TileArea();
        var brush = new SolidColorBrush(WColor.FromRgb(0x7C, 0x8A, 0xA5));
        foreach (var side in new[] { "left", "right" })
        {
            double gx = side == "left" ? 4 : w - 20;
            double gy = h - 20;
            var grip = new TextBlock
            {
                Text = side == "left" ? "◣" : "◢",
                Foreground = brush, FontSize = 15,
                Width = 20, Height = 20,
                IsHitTestVisible = false,
            };
            Adorn.Children.Add(grip);
            Canvas.SetLeft(grip, gx);
            Canvas.SetTop(grip, gy);
        }
    }

    public void SetLayout(Layout layout)
    {
        _layout = layout;
        RefreshChrome();
        DebugLog.Write($"SETLAYOUT {layout.Name} tiles={layout.Tiles.Count} " +
            $"win={ActualWidth:0}x{ActualHeight:0}/{Width}x{Height}");
        Render();
        DebugLog.Write($"RENDERED children={Tiles.Children.Count}");
    }

    public void RefreshOpacity() => Opacity = _s.Opacity;

    // ---------------- rendering ----------------
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["left"] = "◀ Left", ["right"] = "Right ▶", ["middle"] = "● Mid",
    };

    private void Render()
    {
        Tiles.Children.Clear();
        if (_layout == null) return;
        var (w, h) = TileArea();
        foreach (var t in _layout.Tiles)
        {
            double x0 = t.X / 100 * w, y0 = t.Y / 100 * h;
            double x1 = (t.X + t.W) / 100 * w, y1 = (t.Y + t.H) / 100 * h;
            if (t.Action == TileAction.Frame)
            {
                Tiles.Children.Add(new Rectangle
                {
                    Width = x1 - x0, Height = y1 - y0,
                    Fill = new SolidColorBrush(WColor.FromRgb(0x10, 0x13, 0x1A)),
                    Stroke = new SolidColorBrush(WColor.FromRgb(0x5A, 0x64, 0x78)),
                    StrokeThickness = 1, IsHitTestVisible = false,
                });
                Canvas.SetLeft(Tiles.Children[^1], x0);
                Canvas.SetTop(Tiles.Children[^1], y0);
                continue;
            }
            var (fill, label) = TileLook(t);
            var rect = new Rectangle
            {
                Width = Math.Max(0, x1 - x0), Height = Math.Max(0, y1 - y0),
                Fill = new SolidColorBrush(fill),
                Stroke = new SolidColorBrush(WColor.FromRgb(0x4A, 0x52, 0x65)),
                StrokeThickness = 1, IsHitTestVisible = false,
            };
            Tiles.Children.Add(rect);
            Canvas.SetLeft(rect, x0); Canvas.SetTop(rect, y0);
            if (label.Length > 0)
            {
                var tb = new TextBlock
                {
                    Text = label, Foreground = Brushes.White,
                    FontSize = Math.Max(8, Math.Min(t.W, t.H) * 0.28),
                    TextAlignment = TextAlignment.Center,
                    Width = Math.Max(10, x1 - x0 - 4),
                    IsHitTestVisible = false,
                };
                Tiles.Children.Add(tb);
                Canvas.SetLeft(tb, x0 + 2);
                Canvas.SetTop(tb, (y0 + y1) / 2 - 9);
            }
        }
        DrawAdornments();
    }

    private static (WColor fill, string label) TileLook(Tile t) => t.Action switch
    {
        // The pad is a TRANSPARENT input layer (like the original): it must
        // never paint over the buttons beneath it. Rendering it opaque hid
        // every tile (measured: only title + pad hint visible on screen).
        TileAction.Pad => (WColor.FromArgb(0, 0, 0, 0), ""),
        TileAction.Click or TileAction.Drag =>
            (WColor.FromRgb(0x2E, 0x6B, 0xE6),
             t.Action == TileAction.Drag ? $"⠿ {t.ClickButton}" :
             Labels.TryGetValue(t.ClickButton, out var l) ? l : t.RawKind),
        TileAction.Wheel => (WColor.FromRgb(0x3A, 0x41, 0x50),
            t.WheelHorizontal ? "⟷ Wheel" : "⟳ Wheel"),
        TileAction.Key => (WColor.FromRgb(0x31, 0x38, 0x45), KeyLabel(t)),
        TileAction.Grip => (WColor.FromRgb(0x45, 0x4E, 0x60), "⠿ move"),
        TileAction.Sys => (WColor.FromRgb(0x3A, 0x3F, 0x4B), t.Kind switch
        {
            "menu" => "☰", "minimize" => "–", "closebtn" => "✕",
            "assistpad" => "Ctrl+", "tabtip" => "⌨", _ => t.RawKind,
        }),
        _ => (WColor.FromRgb(0x24, 0x29, 0x32), ""),
    };

    private static string KeyLabel(Tile t)
    {
        if (t.KeyVk == 0x11) return "Ctrl";
        if (t.KeyVk == 0x10) return "Shift";
        if (t.KeyVk == 0x12) return "Alt";
        if (t.KeyVk == 0x20) return "Space";
        if (t.KeyVk == 0x0D) return "Enter";
        if (t.KeyVk == 0x08) return "⌫";
        if (t.KeyVk == 0x09) return "Tab";
        if (t.KeyVk == 0x1B) return "Esc";
        if (t.KeyVk is >= 0x41 and <= 0x5A) return ((char)t.KeyVk).ToString();
        return t.RawKind.Replace("VK_", "");
    }

    private void Status(string msg) => StatusLabel.Text =
        msg.Length > 90 ? msg[^90..] : msg;

    private void SafeClick(string button)
    {
        // Click target is the fake cursor in a session, else the cursor.
        // Delivered as ONE atomic absolute batch (see ClickAt): between a
        // parked Down and its Up the system yanks the cursor back onto the
        // live touch contact (measured 900px jumps), scattering the pair
        // outside the OS 4x4px double-click box (singles pile up, doubles
        // fire twice, folders rename).
        double px, py;
        if (_session) { px = _fakeX; py = _fakeY; }
        else { var c = InputSim.Cursor(); px = c.X; py = c.Y; }
        string b = (button == "left" && _s.SwapButtons) ? "right"
            : (button == "right" && _s.SwapButtons) ? "left" : button;
        DebugLog.Write($"BTN {b} -> ({px:0},{py:0}) under: {DescribeWindowAt(px, py)}");
        // A right/middle click leaves a context menu open at the cursor.
        // Remember that, or the physical-mouse hand-back will move the cursor
        // off the menu and dismiss it a moment later - which is exactly how a
        // working right-click looks like a no-op.
        _menuClick = b != "left";
        // A synthetic click landing on our OWN window re-enters as a fresh
        // tap and self-sustains (~1ms press/release flood, measured), so the
        // pad's surface is made mouse-transparent for the injection and the
        // click goes to the application beneath it. That replaces the old
        // nudge-to-the-nearest-pixel-outside, which moved the AIM: the click
        // landed a pixel off the pad's edge rather than where the finger was.
        HoldMouseThrough();
        InputSim.ClickAt((int)px, (int)py, b);
        ForgetRealCursor();
        _clickSincePress = true;
        _suppressPhysicalUntil = DateTime.Now.AddMilliseconds(250);
        // Show exactly where it landed (settles aim disputes at a glance).
        try
        {
            _flash ??= new FlashOverlay();
            _flash.Flash(px, py);
        }
        catch { }
        // A left tap has no reason to keep the real cursor: hand it straight
        // back so N taps do not walk the physical mouse across the screen.
        // Never for a menu click - see _menuClick.
        if (!_menuClick) RestorePhysicalCursor("click");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint p);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport(
        "user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [System.Runtime.InteropServices.DllImport(
        "user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    /// <summary>
    /// What a click at this point would actually hit. Aiming disputes are
    /// settled by reading the log, not by guessing: "BTN right -&gt; (x,y)
    /// under: [class] title" says immediately whether the event went where
    /// the user was pointing.
    /// </summary>
    private string DescribeWindowAt(double x, double y)
    {
        try
        {
            return DescribeWindow(WindowFromPoint(
                new NativePoint { X = (int)x, Y = (int)y }));
        }
        catch (Exception e) { return "(?" + e.GetType().Name + ")"; }
    }

    private string DescribeWindow(IntPtr h)
    {
        try
        {
            if (h == IntPtr.Zero) return "(no window)";
            var cls = new System.Text.StringBuilder(128);
            GetClassName(h, cls, cls.Capacity);
            var txt = new System.Text.StringBuilder(128);
            GetWindowText(h, txt, txt.Capacity);
            string t = txt.ToString();
            if (t.Length > 40) t = t[..40];
            return $"[{cls}] '{t}'"
                + (h == _overlayHandle ? " <<< OUR CURSOR OVERLAY" : "")
                + (h == new System.Windows.Interop.WindowInteropHelper(this).Handle
                    ? " <<< OUR OWN PAD" : "");
        }
        catch (Exception e) { return "(?" + e.GetType().Name + ")"; }
    }

    private IntPtr _overlayHandle =>
        _overlay == null ? IntPtr.Zero
            : new System.Windows.Interop.WindowInteropHelper(_overlay).Handle;

    private FlashOverlay? _flash;
    /// <summary>Polls the real cursor back after the lift-yank.</summary>
    private System.Windows.Threading.DispatcherTimer? _rePark;
    /// <summary>
    /// A right/middle click was delivered, so a context menu is (probably)
    /// open at the fake cursor. While set, the real cursor must not be moved
    /// anywhere: yanking it off an open menu dismisses it, which made
    /// right-clicks look like no-ops even though the event fired.
    /// Cleared on the next press.
    /// </summary>
    private bool _menuClick;
    /// <summary>
    /// A long-press mapped to a menu-opening click waits here for the lift
    /// (see OnLongTick): the menu must not exist while the touch contact is
    /// still down, or Windows' own tap on release dismisses it.
    /// </summary>
    private string _pendingMenuAction = "";

    // ---------------- touch (primary path) ----------------
    private void OnTouchDown(object sender, TouchEventArgs e)
    {
        if (IsChrome(e.OriginalSource)) return;
        SweepGhosts();
        // A press that is already MOVING owns the session until it lifts.
        // Without this, a second contact arriving mid-drag was free to act:
        // two fingers turned the drag into a scroll/tap/swipe, and a button
        // or key tile fired on top of it - so one drag produced a scroll AND
        // a keystroke AND a click. Creep does not count as movement (a parked
        // finger sits under HoldCancelDip), so a still first finger never
        // takes the lock and two-finger gestures keep working.
        if (MoveOwnsSession())
        {
            DebugLog.Write(
                $"TOUCHDOWN id={e.TouchDevice.Id} REFUSED: the moving press owns "
                + $"the session until release (n={_fingers.Count})");
            e.Handled = true;
            return;
        }
        PressedAnywhere?.Invoke();
        var p = e.GetTouchPoint(Surface).Position;
        // Corner resizers beat tiles (zone test in window coords, drag in
        // screen coords so window motion can't feed back into deltas).
        string? rz = ResizerAt(p.X, p.Y);
        if (rz != null)
        {
            var sp0 = ScreenOf(p);
            BeginResize(rz, sp0.X, sp0.Y);
            _rsTouchId = e.TouchDevice.Id;
            Surface.CaptureTouch(e.TouchDevice);
            e.Handled = true;
            return;
        }
        var (tw, th) = TileArea();
        var tile = _layout == null ? null :
            PresetParser.HitTest(_layout, p.X, p.Y, tw, th);
        var f = new Finger
        {
            Start = p, Last = p, T0 = DateTime.Now, Active = DateTime.Now,
            OriginX = p.X, OriginY = p.Y,
            Tile = tile,
            Fx0 = _fakeX, Fy0 = _fakeY,
        };
        _fingers[e.TouchDevice.Id] = f;
        if (_fingers.Count == 1)
        {
            _pressNote = "";
            _menuClick = false;
            _pendingMenuAction = "";
        }
        Status($"touch {e.TouchDevice.Id} {tile?.RawKind ?? "-"} n={_fingers.Count}");
        var (ccx, ccy) = InputSim.Cursor();
        DebugLog.Write($"TOUCHDOWN id={e.TouchDevice.Id} @{p.X:0},{p.Y:0} tile={tile?.RawKind} n={_fingers.Count} cursor=({ccx},{ccy})");
        if (tile?.Action == TileAction.Grip)
        {
            var gp = ScreenOf(p);
            _gripDX = gp.X - Left;
            _gripDY = gp.Y - Top;
        }
        if (_fingers.Count == 1)
            BeginSession();

        // ---- gesture arming (pad tile, touch only here; buttons/keys above) ----
        if (tile?.Action == TileAction.Pad)
        {
            if (_fingers.Count == 1)
            {
                if (DateTime.Now < _pendingTapUntil)
                {
                    // Second press of a would-be double tap: NOTHING is
                    // pressed yet. It resolves on the first movement (drag,
                    // StartDrag) or on the hold timer (SecondHold). The
                    // button used to go down at this instant, which turned
                    // every chained press into a drag and made a plain
                    // hold-then-right-click impossible.
                    _secondPress = true;
                    _secondId = e.TouchDevice.Id;
                    _secondConsumed = false;
                    _thirdPress = false;
                    _thirdId = -1;
                    // Move -> grab immediately.
                    if (DragActionArmed())
                        _dragArmId = e.TouchDevice.Id;
                    // Hold still -> the configured SecondHold, on the hold timer.
                    // With the default drag that IS the press-and-drag the
                    // report asked for: the button goes down and stays down, so
                    // moving drags and releasing drops. Like the original's
                    // "2nd tap and hold: Drag", it sends no extra click; the
                    // tap that armed it already clicked. The old click-then-hold
                    // remains available as the explicit "drag_hold" action.
                    ArmHold(e.TouchDevice.Id, G.SecondHold);
                    // No ArmLong here. A press that follows a tap belongs to
                    // the drag; the long-press (right click) is the gesture of
                    // a FRESH press and a fresh press still arms it.
                }
                else if (DateTime.Now < _pendingTripleUntil)
                {
                    // Third press: same resolution rule as the second.
                    _thirdPress = true;
                    _thirdId = e.TouchDevice.Id;
                    _secondPress = false;
                    _secondId = -1;
                    _secondConsumed = false;
                    if (DragActionArmed())
                        _dragArmId = e.TouchDevice.Id;
                    ArmHold(e.TouchDevice.Id, G.SecondHold);
                    // No ArmLong: same reason as the second press above.
                }
                else
                {
                    _secondPress = false;
                    _thirdPress = false;
                    _secondId = -1;
                    _thirdId = -1;
                    // Two ways in, both wanted:
                    //  - a press right after a tap (grab armed) grabs on
                    //    movement, which is the tap-then-drag flow the report
                    //    chose;
                    //  - a BARE press that stays put grabs on the HOLD timer
                    //    (SecondHold, "drag"), which is the reference's model:
                    //    its log shows L-down arriving after the hold, then the
                    //    moves, then L-up at the drop. Its right click is the
                    //    two-finger tap, not a single-finger long press.
                    // Movement alone never grabs, or the pointer could not be
                    // moved at all: the hold timer bails when the finger has
                    // moved (see ArmHold).
                    bool tapArmed = DateTime.Now < _grabArmUntil;
                    if (DragActionArmed())
                    {
                        _dragArmId = tapArmed ? e.TouchDevice.Id : -1;
                        ArmHold(e.TouchDevice.Id, G.SecondHold);
                    }
                    else
                    {
                        _dragArmId = tapArmed ? e.TouchDevice.Id : -1;
                        if (_dragArmId >= 0)
                            ArmHold(e.TouchDevice.Id, G.SecondHold);
                        else
                            ArmLong(e.TouchDevice.Id, G.LongPress);
                    }
                }
            }
            else if (_fingers.Count == 2)
            {
                // Second concurrent finger: every press-class gesture is off.
                CancelLong();
                CancelHold();
                _dragArmId = -1;
                ClearChains();
                _twoId = e.TouchDevice.Id;
                _twoCandidate = true;
                _twoActive = true;
                _twoAccX = _twoAccY = 0;
            }
        }

        if (tile != null)
        {
            switch (tile.Action)
            {
                case TileAction.Click:
                case TileAction.Drag:
                {
                    string b = tile.ClickButton;
                    if (_s.SwapButtons && (b == "left" || b == "right"))
                        b = b == "left" ? "right" : "left";
                    PressDown(b);
                    f.HoldButton = b;
                    break;
                }
                case TileAction.Key when tile.KeyVk != 0:
                {
                    var (vk, ctrl, shift, alt) = PresetParser.KeyCombo(tile);
                    var mods = new List<int>();
                    if (ctrl) mods.Add(0x11);
                    if (shift) mods.Add(0x10);
                    if (alt) mods.Add(0x12);
                    foreach (int m in mods) InputSim.HoldKey(m, true);
                    InputSim.HoldKey(vk, true);
                    f.HoldCombo = (vk, mods.ToArray());
                    break;
                }
            }
        }
        Surface.CaptureTouch(e.TouchDevice);
        e.Handled = true; // block promotion to mouse events
    }

    private void OnTouchCaptureLost(object sender, TouchEventArgs e)
    {
        // Same cleanup as a release, minus tap/swipe/click semantics:
        // the finger is gone, but no gesture completes from it.
        if (!_fingers.TryGetValue(e.TouchDevice.Id, out var f)) return;
        _fingers.Remove(e.TouchDevice.Id);
        if (e.TouchDevice.Id == _longId) CancelLong();
        if (e.TouchDevice.Id == _holdId) CancelHold();
        if (e.TouchDevice.Id == _dragArmId) _dragArmId = -1;
        _pendingMenuAction = "";   // the contact is gone, no lift will come
        if (_dragHold && e.TouchDevice.Id == _dragId) ReleaseActionButton();
        if (f.HoldButton != null) PressUp(f.HoldButton);
        if (f.HoldCombo is { } c)
        {
            InputSim.HoldKey(c.vk, false);
            foreach (int m in c.mods) InputSim.HoldKey(m, false);
        }
        _secondPress = false;
        _thirdPress = false;
        if (e.TouchDevice.Id == _twoId) { _twoId = -1; _twoCandidate = false; }
        if (_fingers.Count == 0) EndSession();
        DebugLog.Write($"CAPTURE-LOST id={e.TouchDevice.Id} cleaned");
        e.Handled = true;
    }

    private void OnTouchMove(object sender, TouchEventArgs e)
    {
        long t0 = Environment.TickCount64;
        try
        {
            OnTouchMoveInner(sender, e);
        }
        finally
        {
            // Perf probe: handler cost over a drag (avg/max per 200 events).
            // If avg grows with drag length, something in here accumulates.
            _pmSum += Environment.TickCount64 - t0;
            _pmN++;
            if (_pmN >= 200)
            {
                DebugLog.Write($"PERF move x{_pmN} avg={_pmSum / (double)_pmN:0.00}ms max={_pmMax}ms");
                _pmSum = 0; _pmN = 0; _pmMax = 0;
            }
            else if (Environment.TickCount64 - t0 > _pmMax)
                _pmMax = Environment.TickCount64 - t0;
        }
    }

    private long _pmSum;
    private int _pmN;
    private long _pmMax;

    private void OnTouchMoveInner(object sender, TouchEventArgs e)
    {
        if (IsChrome(e.OriginalSource)) return;
        if (_resizeMode != null && e.TouchDevice.Id == _rsTouchId)
        {
            var rp = ScreenOf(e.GetTouchPoint(Surface).Position);
            MoveResize(rp.X, rp.Y);
            e.Handled = true;
            return;
        }
        if (!_fingers.TryGetValue(e.TouchDevice.Id, out var f))
        {
            // A contact we refused (see MoveOwnsSession) must be swallowed
            // for its whole life: consuming the Down but letting the
            // Move/Up bubble makes WPF promote that touch to a MOUSE, and the
            // stray press lands on whatever tile is under it - leaking into
            // the next gesture as a phantom click.
            e.Handled = true;
            return;
        }
        var p = e.GetTouchPoint(Surface).Position;
        double dx = p.X - f.Last.X, dy = p.Y - f.Last.Y;
        // Count the whole path, before any early return. The drag-end log
        // reported a path SHORTER than the net distance (36DIP against 184px),
        // which is impossible - it was only seeing part of the movement.
        if (ButtonHeld()) _dragPathDip += Math.Sqrt(dx * dx + dy * dy);
        f.Last = p;
        f.Active = DateTime.Now;
        f.Trail.Add((f.Active, p.X, p.Y));
        while (f.Trail.Count > 2 &&
               (f.Active - f.Trail[0].t).TotalMilliseconds > 1200)
            f.Trail.RemoveAt(0);
        double net = NetTravel(f, p);
        bool was = f.Moved;
        if (net > TapMoveDip)
            f.Moved = true;
        if (f.Moved && !was && _fingers.Count >= 2) _twoCandidate = false;

        // A press that is TRAVELLING ends every tap-class gesture for the
        // rest of that press: once the finger moves, nothing may fire until
        // it lifts. Judged as a PEAK RATE over 150ms, never as distance from
        // the press point - this panel reports a constant ~0.4 DIP/ms creep
        // for a resting finger, so a distance rule cancelled EVERY hold
        // (logged at 114ms) while the rate tells creep (65 DIP/150ms) from a
        // real drag (375 DIP/150ms). The peak LATCHES: a fast drag that
        // parks must not turn back into a hold.
        double rate = WindowRate(f, HoldWindowMs);
        if (rate > f.PeakRate) f.PeakRate = rate;
        if (HoldIsDragging(f))
        {
            if (e.TouchDevice.Id == _longId)
            {
                CancelLong();
                DebugLog.Write(
                    $"GESTURE long canceled (peak {f.PeakRate:0}DIP/{HoldWindowMs}ms)");
            }
            if (e.TouchDevice.Id == _holdId) CancelHold();
            // A hold already recognised, then the user starts moving: they
            // changed their mind, so the deferred menu must not appear.
            if (_pendingMenuAction.Length > 0)
            {
                DebugLog.Write(
                    $"GESTURE menu {_pendingMenuAction} abandoned (became a drag)");
                _pendingMenuAction = "";
            }
        }
        // A chained press that starts moving is a drag, not a hold.
        if (net > TapMoveDip && _dragArmId == e.TouchDevice.Id)
            StartDrag(e.TouchDevice.Id);

        // Gesture policy: EVERY gesture action needs TWO contacts. A single
        // finger is the pointer and nothing else - one-finger swipe actions
        // were tried and removed: scratching hard across the pad fired a
        // scroll instead of moving the cursor, which is the one thing the
        // user must be able to do with one finger.
        // Two contacts: vertical = live scroll, horizontal = accumulated for
        // release-time swipe-nav.
        if (_fingers.Count >= 2 && f.Tile?.Action == TileAction.Pad)
        {
            _twoAccX += dx;
            _twoAccY += dy;
            if (Math.Abs(dy) >= Math.Abs(dx))
            {
                double step = _s.WheelStep / 4.0;
                DoWheel(-dy * step / 8, horizontal: false);
            }
            return;
        }

        if (f.Tile == null) return;
        switch (f.Tile.Action)
        {
            case TileAction.Pad when (dx != 0 || dy != 0):
            {
                // A chained press used to be FROZEN here until it resolved,
                // "so the grab lands on what the user actually pressed". That
                // is already guaranteed a different way - both grab routes
                // rewind the aim to the press point (StartDrag sets
                // _fakeX/_fakeY to f.Fx0/Fy0, FirePressAction rewinds the
                // same) - so the freeze only delayed the cursor. Measured as
                // "after a double touch, moving does nothing": the cursor sat
                // still for the ~135ms until the grab and then jumped, which
                // reads as unresponsive, not as aiming.
                // Deltas are touch positions in our own surface space -
                // fully independent of the OS cursor, so no feedback loop.
                if (_session)
                {
                    // Absolute from press baseline (like window dragging):
                    // fake = press-start fake + finger travel × gain.
                    // Recomputed fresh every event - nothing accumulates,
                    // nothing drifts, nothing stutters.
                    // Deadzone first: sub-threshold tremor never moves fake.
                    double tx = p.X - f.Start.X, ty = p.Y - f.Start.Y;
                    // OPTIONAL creep damping while a hold is pending. The
                    // panel reports ~0.4 DIP/ms of motion for a resting
                    // finger, which at speed 3 carries the cursor hundreds of
                    // px during a hold. Full stop (pinning the pointer) is
                    // NOT an option: measured normal movement is the same
                    // speed as the creep (0.33-0.81 DIP/ms), so a pin
                    // threshold either never releases - the pad feels dead for
                    // 500ms - or releases on the creep anyway. So this only
                    // softens the creep (HoldDamp < 1) and blends back to
                    // full gain as the stroke turns into a real drag. Both
                    // factors vary continuously, so the pointer path has no
                    // jump. Default 1.0 = no damping, immediate movement.
                    double gain = _s.Speed;
                    if (e.TouchDevice.Id == _longId && _s.HoldDamp < 1)
                    {
                        double fast = Math.Clamp(
                            f.PeakRate / Math.Max(1, _s.HoldCancelDip), 0, 1);
                        gain *= _s.HoldDamp + (1 - _s.HoldDamp) * fast;
                    }
                    if (RealCursorOnly)
                    {
                        // The MODEL is the state, not the cursor read-back.
                        //
                        // Reading the cursor and adding the finger delta looks
                        // like "real control", but the system moves the cursor
                        // onto the live touch contact, and every read then
                        // starts from that yanked position - the deltas feed
                        // the yank back into the cursor and it ends up pinned
                        // near where the finger is ("the boundary is the x,y
                        // of the touch"). Measured in the log as DIVERGED with
                        // actual sitting on our own pad.
                        //
                        // Press-start + travel x gain cannot be poisoned that
                        // way: it never reads the cursor. The yank is fought
                        // on the output side instead - every event re-asserts
                        // this position, and a keeper timer re-asserts it
                        // between events (see StartDragKeeper).
                        if (Math.Abs(tx) + Math.Abs(ty) >= DeadDip)
                        {
                            _fakeX = f.Fx0 + tx * _dpi * gain;
                            _fakeY = f.Fy0 + ty * _dpi * gain;
                        }
                        ClampFake();
                        int ix = (int)_fakeX, iy = (int)_fakeY;
                        InputSim.SetCursor(ix, iy);
                        InputSim.NotePosition(ix, iy);
                        _realSentX = ix; _realSentY = iy;
                    }
                    else
                    {
                        if (Math.Abs(tx) + Math.Abs(ty) >= DeadDip)
                        {
                            _fakeX = f.Fx0 + tx * _dpi * gain;
                            _fakeY = f.Fy0 + ty * _dpi * gain;
                        }
                        ClampFake();
                        // While a button is down the OS cursor IS the drag
                        // cursor, so it has to follow the virtual one. It used
                        // to stay parked at the press point for the whole
                        // gesture, and the drag was carried by whatever else
                        // wrote the cursor - the OS touch-to-mouse promotion,
                        // unscaled and on a different baseline. That is why a
                        // touch drag came out as a shaky circle while the same
                        // drag with the physical mouse was smooth.
                        if (ButtonHeld()) SyncRealCursorToFake();
                    }
                    // NOTE: the real cursor is deliberately NEVER chased here.
                    // It stays (hidden) where it was; only the fake roams.
                    // Clicks carry their own absolute position (see ClickAt).
                    // Draw at most on visible change AND 60Hz max.
                    long nowT = Environment.TickCount64;
                    if (ArrowOn && (Math.Abs(_fakeX - _drawnX) >= 2.5 ||
                        Math.Abs(_fakeY - _drawnY) >= 2.5) &&
                        nowT - _lastDrawTick >= 16)
                    {
                        _lastDrawTick = nowT;
                        _drawnX = _fakeX; _drawnY = _fakeY;
                        ShowFake();
                    }
                    // Move lines sampled: per-event file I/O is the heaviest
                    // thing left in this path and backs the queue up.
                    // (On-pad readout skips moves entirely by request: too noisy.)
                    if ((_moveLogN++ % 8) == 0)
                        DebugLog.Write($"FAKE d=({dx:0},{dy:0}) -> ({_fakeX:0},{_fakeY:0})");
                }
                else
                {
                    int ax = ClampStep((int)(dx * _dpi * _s.Speed));
                    int ay = ClampStep((int)(dy * _dpi * _s.Speed));
                    InputSim.Move(ax, ay);
                    DebugLog.Write($"TOUCHMOVE id={e.TouchDevice.Id} d=({dx:0},{dy:0}) -> ({ax},{ay})");
                }
                break;
            }
            case TileAction.Wheel when (dx != 0 || dy != 0):
            {
                double step = _s.WheelStep / 4.0;
                if (f.Tile.WheelHorizontal) DoWheel(dx * step / 8, true);
                else DoWheel(-dy * step / 8, false);
                break;
            }
            case TileAction.Grip:
            {
                // Legacy movegrip tile: absolute target like chrome drag.
                var sp = ScreenOf(e.GetTouchPoint(Surface).Position);
                PlaceWindow(sp.X - _gripDX, sp.Y - _gripDY);
                break;
            }
        }
        e.Handled = true;
    }

    private double _wheelCarry;
    private double _wheelCarryH;
    private void DoWheel(double amount, bool horizontal)
    {
        // Wheel messages route to the window under the SYSTEM cursor, which
        // mid-touch is yanked onto our own pad (scrolls nothing). Move and
        // wheel in ONE atomic batch so nothing can interleave.
        //
        // Proportional, NOT quantised into WheelStep chunks. Measured on the
        // reference: its two-finger scroll emits a stream of deltas that vary
        // with the movement (-156, -168, -132, -108, -300 ...) rather than
        // repeating one fixed 120 step, which is what makes it feel smooth.
        // Ours accumulated to a full 120 before sending anything.
        double carry = horizontal ? _wheelCarryH : _wheelCarry;
        carry += amount;
        int d = (int)carry;
        if (d != 0)
        {
            carry -= d;
            FireWheel(d, horizontal);
        }
        if (horizontal) _wheelCarryH = carry; else _wheelCarry = carry;
        // The wheel burst is done: the real cursor goes back to the physical
        // mouse (the wheel itself was routed by the atomic move+wheel pair).
        RestorePhysicalCursor("wheel");
    }

    private static int ClampStep(int v) => Math.Max(-256, Math.Min(256, v));

    private void OnTouchUp(object sender, TouchEventArgs e)
    {
        if (IsChrome(e.OriginalSource)) return;
        if (_resizeMode != null && e.TouchDevice.Id == _rsTouchId)
        {
            Surface.ReleaseTouchCapture(e.TouchDevice);
            EndResize();
            e.Handled = true;
            return;
        }
        if (!_fingers.TryGetValue(e.TouchDevice.Id, out var f))
        {
            // Refused contact: swallow it, see OnTouchMove. Bubbling the Up
            // while the Down was consumed hands WPF a one-off mouse press.
            DebugLog.Write($"UP-IN id={e.TouchDevice.Id} UNKNOWN keys=[{string.Join(",", _fingers.Keys)}]");
            e.Handled = true;
            return;
        }
        _fingers.Remove(e.TouchDevice.Id);
        if (e.TouchDevice.Id == _longId) CancelLong();
        if (e.TouchDevice.Id == _holdId) CancelHold();
        if (e.TouchDevice.Id == _dragArmId) _dragArmId = -1;
        double ms = (DateTime.Now - f.T0).TotalMilliseconds;
        // Classify by the UP position as well: a fast flick may deliver
        // zero TouchMove events, and move-flags alone would call it a tap.
        var end = e.GetTouchPoint(Surface).Position;
        if (Math.Abs(end.X - f.Start.X) + Math.Abs(end.Y - f.Start.Y) > TapMoveDip)
            f.Moved = true;
            // No dead zone. `ms < TapMs` alone left a silent window between
            // TapMs (220) and LongPressMs (500): a 331ms press matched neither
            // the tap nor the (still pending) long-press and did NOTHING -
            // half of the original "long press does nothing" report. A press
            // that never moved and whose long-press has not fired is a tap,
            // however long it was held.
            // A press that is still HOLDING a button is not a tap: the hold
            // action (SecondHold = drag) put the left button down, and calling
            // this a tap skipped the release path entirely, leaving the button
            // down for the whole desktop. Measured by the stream-balance check:
            // 10 downs against 8 ups.
            bool tap = !f.Moved && !_longFired && !_dragHold;
        if (tap && f.Tile?.Action == TileAction.Pad)
        {
            // Tap rewind: contact centroid jitters several px during even a
            // still press, but the OS pairs double-clicks only inside a 4x4px
            // box (measured). Rewind the fake to its press-start value so all
            // taps of a multi-tap land on the SAME pixel and pair reliably.
            _fakeX = f.Fx0;
            _fakeY = f.Fy0;
            _overlay?.MoveToPhysical(_fakeX, _fakeY);
            DebugLog.Write($"REWIND fake=({_fakeX:0},{_fakeY:0})");
        }
        // A held drag ends here UNLESS this release is itself a quick tap
        // (2nd-press tap completes an OS double-click in the branch below -
        // releasing early would eat the extra click).
        if (_dragHold && e.TouchDevice.Id == _dragId && !tap)
        {
            ReleaseActionButton();
            _secondPress = false;
            CancelHold();
        }
        var (ucx, ucy) = InputSim.Cursor();
        DebugLog.Write($"TOUCHUP id={e.TouchDevice.Id} {(tap ? "tap" : "gesture")} {(int)ms}ms n={_fingers.Count} cursor=({ucx},{ucy})");
        // Only report a plain outcome when the press produced none. Writing
        // "up gesture" unconditionally erased the long-press line that was
        // printed while the finger was still down, so lifting after a
        // right-click made the label claim nothing had happened. The note
        // survives until the next press.
        if (_pressNote.Length == 0)
            Status(tap ? $"tap {(int)ms}ms" : $"move {(int)ms}ms");
        // Release capture BEFORE firing actions: windows opened from here
        // (settings/assist) must activate normally.
        Surface.ReleaseTouchCapture(e.TouchDevice);

        if (f.Tile != null)
        {
            switch (f.Tile.Action)
            {
                case TileAction.Click:
                case TileAction.Drag:
                    // The finger stayed down since Down() and Windows yanked
                    // the system cursor onto the touch point meanwhile, so
                    // the Up is delivered atomically AT the virtual mouse.
                    if (f.HoldButton != null) PressUp(f.HoldButton);
                    break;
                case TileAction.Key when f.HoldCombo is { } c:
                    InputSim.HoldKey(c.vk, false);
                    foreach (int m in c.mods) InputSim.HoldKey(m, false);
                    break;
                case TileAction.Sys:
                    SysAction(f.Tile.Kind);
                    break;
                case TileAction.Pad when tap:
                {
                    int id = e.TouchDevice.Id;
                    if (id == _longId) CancelLong();
                    if (id == _thirdId && _thirdPress)
                    {
                        // Third press released right away: triple-tap event -
                        // UNLESS the hold timer already resolved this press.
                        // A hold ends silently on release; firing the triple
                        // click on top of a held drag would add a stray click.
                        _thirdPress = false;
                        ClearChains();
                        if (_dragHold && id == _dragId)
                            ReleaseActionButton();
                        if (!_secondConsumed)
                        {
                            DoGesture(G.TripleTap);
                            // Leave the grab armed for the next press: a
                            // triple-click is a "select this line" intent, and
                            // what the user does next is usually grab and move.
                            _grabArmUntil = DateTime.Now.AddMilliseconds(
                                _s.MultiTapMs * 2);
                        }
                    }
                    else if (id == _secondId && _secondPress)
                    {
                        // Second press released quickly: an OS-level double
                        // click (the first tap already clicked).
                        CancelHold();
                        _secondPress = false;
                        if (_dragHold && id == _dragId)
                        {
                            // Release only. The grab already pressed the button
                            // and the release must NOT carry a click of its own:
                            // it would fire at the drop point, which is not part
                            // of a drag.
                            ReleaseActionButton();
                        }
                        else if (!_secondConsumed)
                        {
                            DoGesture(G.DoubleTap);
                        }
                        // Multi-tap windows are generous: slow tappers
                        // otherwise fall out of the double/triple chain
                        // and every tap degrades to a single click
                        // (measured user report: "needs three touches").
                        _pendingTripleUntil = DateTime.Now.AddMilliseconds(_s.MultiTapMs);
                        _pendingTapUntil = DateTime.MinValue;
                        // Same as above: after a double-click the next press
                        // is a grab, even after the chain window has closed.
                        _grabArmUntil = DateTime.Now.AddMilliseconds(
                            _s.MultiTapMs * 2);
                        // else: hold timer already consumed it.
                    }
                    else if (_longFired)
                    {
                        _longFired = false; // long-press already fired
                    }
            else if (_s.TapToClick)
            {
                DoGesture(G.Tap);
                _pendingTapUntil = DateTime.Now.AddMilliseconds(_s.MultiTapMs);
                // A single tap arms the grab too, not just a double/triple
                // tap. The flow is "tap the thing, then press and move it",
                // and those two are a second or more apart at human speed: the
                // tap-chain window has closed by then, so the press was read
                // as a plain one, no grab was armed, and the drag produced NO
                // button at all - the cursor roamed and nothing was pressed or
                // released. Logged: TOUCHDOWN, long canceled, cursor moves,
                // TOUCHUP gesture, with no BTN down and no DRAG end anywhere.
                _grabArmUntil = DateTime.Now.AddMilliseconds(_s.MultiTapMs * 2);
            }
                    break;
                }
                case TileAction.Pad when f.Moved:
                    // A drag owns the press from here to release: drop the
                    // chain so the NEXT tap is read as a fresh press, not as
                    // a continuation of this drag.
                    ClearChains();
                    if (_longFired)
                    {
                        _longFired = false; // long-press owned this press
                    }
                    else if (e.TouchDevice.Id == _thirdId && _thirdPress)
                    {
                        // 3rd press that moved: triple-drag ends, no event.
                        _thirdPress = false;
                        if (_dragHold && e.TouchDevice.Id == _dragId)
                            ReleaseActionButton();
                    }
                    else if (e.TouchDevice.Id == _secondId && _secondPress)
                    {
                        // 2nd press that moved: it became a drag (button went
                        // down at StartDrag) - release it, no click, no double.
                        CancelHold();
                        _secondPress = false;
                        if (_dragHold && e.TouchDevice.Id == _dragId)
                            ReleaseActionButton();
                    }
                    // NOTE: a single-finger release NEVER fires a gesture,
                    // however fast the drag ended. Every gesture action is
                    // two-finger (see the two-contact branch in Move).
                    break;
                case TileAction.Pad:
                    // A still hold released: too slow for a tap, never moved,
                    // so it matched neither case above. Whatever fired at
                    // hold time is done - only flag hygiene is left, or the
                    // NEXT press inherits a stale _longFired and stays mute.
                    _longFired = false;
                    _secondConsumed = false;
                    _dragArmId = -1;
                    break;
            }
        }

        // Explicit two-finger tap: 2nd finger tapped while 1st stayed still.
        if (tap && e.TouchDevice.Id == _twoId && _twoCandidate &&
            f.Tile?.Action == TileAction.Pad)
        {
            _twoCandidate = false;
            DebugLog.Write("GESTURE two-finger-tap");
            _pressNote = $"2-finger tap → {G.TwoFingerTap}";
            Status(_pressNote);
            DoGesture(G.TwoFingerTap);
        }
        // Two-finger release: horizontal-dominant total = swipe-nav.
        if (_twoActive && _fingers.Count >= 1)
        {
            double ax = _twoAccX, ay = _twoAccY;
            if (Math.Abs(ax) > Math.Abs(ay) &&
                Math.Sqrt(ax * ax + ay * ay) > SwipeDip)
            {
                string act = ax > 0 ? G.SwipeRight : G.SwipeLeft;
                DebugLog.Write($"GESTURE two-swipe ({ax:0},{ay:0})");
                // Fires after the status line above, so set it here too.
                _pressNote = $"2-finger swipe ({ax:0},{ay:0}) → {act}";
                Status(_pressNote);
                DoGesture(act);
            }
        }
        if (_fingers.Count < 2)
        {
            // Only a two-finger gesture leaves travel owed to the survivors.
            // Doing this on every lift would clear the drag state of an
            // unrelated single-finger press that a stray second finger
            // happened to brush.
            if (_twoActive) RebaseAfterTwoFinger();
            _twoActive = false;
        }
        if (e.TouchDevice.Id == _twoId) { _twoId = -1; _twoCandidate = false; }

        // Deferred menu click (see OnLongTick): fire it NOW, while the
        // session is still open so it aims at the virtual cursor, and
        // before EndSession - which would otherwise hand the real cursor
        // back to the physical mouse and close the menu we just opened.
        if (_pendingMenuAction.Length > 0)
        {
            string act = _pendingMenuAction;
            _pendingMenuAction = "";
            DebugLog.Write($"ACTION {act} (deferred long-press, on lift)");
            _pressNote = $"long-press → {act} (on lift)";
            Status(_pressNote);
            DoGesture(act);
        }

        e.Handled = true;
        if (_fingers.Count == 0)
            EndSession();
    }

    /// <summary>
    /// Wheel delivered at the virtual cursor. In preserve mode the system
    /// cursor belongs to the physical mouse, so a bare Wheel() would scroll
    /// whatever window the mouse happens to be over.
    /// </summary>
    private void FireWheel(int delta, bool horizontal = false)
    {
        if (_session) InputSim.WheelAt((int)_fakeX, (int)_fakeY, delta, horizontal);
        else InputSim.Wheel(delta, horizontal);
        _suppressPhysicalUntil = DateTime.Now.AddMilliseconds(60);
    }

    private void DoGesture(string name)
    {
        DebugLog.Write($"ACTION {name}");
        switch (name)
        {
            case "left_click": SafeClick("left"); break;
            case "right_click": SafeClick("right"); break;
            case "middle_click": SafeClick("middle"); break;
            case "double_click": SafeClick("left"); SafeClick("left"); break;
            case "triple_click":
                SafeClick("left"); SafeClick("left"); SafeClick("left"); break;
            case "drag": SafeClick("left"); break; // release-context fallback
            case "drag_hold": SafeClick("left"); break; // release-context fallback
            case "wheel_up": FireWheel(_s.WheelStep); break;
            case "wheel_down": FireWheel(-_s.WheelStep); break;
            case "browser_back": InputSim.TapKey(0xA6); break;
            case "browser_forward": InputSim.TapKey(0xA7); break;
            case "assist_pad": RequestAssist?.Invoke(); break;
            case "toggle_fake": ToggleFakeCursor(); break;
        }
    }

    private void SysAction(string kind)
    {
        switch (kind)
        {
            case "menu": RequestSettings?.Invoke(); break;
            case "minimize":
            case "closebtn": Hide(); break;
            case "assistpad": RequestAssist?.Invoke(); break;
            case "tabtip":
                try { Process.Start("TabTip.exe"); }
                catch { InputSim.TapKey(VkTabTip); }
                break;
        }
    }

    // ---------------- mouse fallback (physical mouse) ----------------
    // "unified" mode (legacy): a real mouse press hands control back to the
    // system cursor and the virtual one adopts it - the two are one cursor.
    // "preserve" mode (default): the physical mouse is NEVER disturbed and
    // never disturbs us - no handover, no adopt, no cursor mode flip. The
    // pad tiles still work for a user who drives them with a mouse.
    // Synthetic output right after our own park/click is suppressed via
    // _suppressPhysicalUntil, never mistaken for physical.
    /// <summary>
    /// Block a mouse event that came from a touch (WPF promotes touch to
    /// stylus, then to mouse). A physical mouse has StylusDevice == null and
    /// goes through untouched; a promoted touch is marked handled so nothing -
    /// ours or WPF's - acts on it.
    /// </summary>
    private static void SwallowTouchMouse(object sender, MouseEventArgs e)
    {
        if (e.StylusDevice == null) return;
        e.Handled = true;
    }

    private void OnMouseDown(object sender, WMouseButtonEventArgs e)
    {
        DebugLog.Write($"M-DOWN stylus={e.StylusDevice != null} fingers={_fingers.Count} suppressed={DateTime.Now < _suppressPhysicalUntil}");
        if (IsChrome(e.OriginalSource)) return;
        // Everything below is the touch surface: no mouse event that lands
        // here may act on anything else, so every path marks it handled. A
        // real mouse drag in the pad's area therefore does nothing, and a
        // mouse event promoted from a touch does nothing either.
        if (e.StylusDevice != null || _fingers.Count > 0)
        {
            e.Handled = true;
            return;
        }
        if (DateTime.Now < _suppressPhysicalUntil)
        {
            e.Handled = true;
            return;
        }
        if (UnifiedPhysical) HandoverToPhysicalMouse();
        else RememberPhysicalCursor();   // this spot belongs to the mouse
        PressedAnywhere?.Invoke();
        var mp0 = e.GetPosition(Surface);
        string? mrz = ResizerAt(mp0.X, mp0.Y);
        if (mrz != null)
        {
            var msp0 = ScreenOf(mp0);
            BeginResize(mrz, msp0.X, msp0.Y);
            _rsMouse = true;
            _mouseDown = true;
            Surface.CaptureMouse();
            e.Handled = true;
            return;
        }
        _mouseDown = true;
        _mouseLast = _mouseStart = e.GetPosition(Surface);
        _mouseT0 = DateTime.Now;
        var tile = HitMouse(_mouseStart);
        if (tile?.Action is TileAction.Click or TileAction.Drag)
        {
            PressDown(tile.ClickButton);
            _mouseDownButton = tile.ClickButton;
        }
        Surface.CaptureMouse();
        e.Handled = true;
    }
    private string? _mouseDownButton;

    private Tile? HitMouse(WPoint p)
    {
        var (w, h) = TileArea();
        return _layout == null ? null : PresetParser.HitTest(_layout, p.X, p.Y, w, h);
    }

    private void OnMouseMove(object sender, WMouseEventArgs e)
    {
        if (IsChrome(e.OriginalSource)) return;
        // The touch surface swallows every mouse event it receives: a real
        // mouse drag here must not do anything, and neither must a mouse event
        // promoted from a touch.
        if (e.StylusDevice != null || _fingers.Count > 0)
        {
            e.Handled = true;
            return;
        }
        if (DateTime.Now < _suppressPhysicalUntil)
        {
            e.Handled = true;
            return;
        }
        var hp = e.GetPosition(Surface);
        // Resize drag in progress (screen coords: window motion must not
        // feed back into the deltas).
        if (_rsMouse)
        {
            var hps = ScreenOf(hp);
            MoveResize(hps.X, hps.Y);
            e.Handled = true;
            return;
        }
        if (e.LeftButton != MouseButtonState.Released ||
            e.RightButton != MouseButtonState.Released ||
            e.MiddleButton != MouseButtonState.Released)
            DebugLog.Write($"M-MOVE buttons stylus={e.StylusDevice != null} fingers={_fingers.Count}");
        // Hover cursor over the grips.
        string? hz = ResizerAt(hp.X, hp.Y);
        Cursor = hz == "left" ? Cursors.SizeNESW
            : hz == "right" ? Cursors.SizeNWSE : Cursors.Arrow;
        if (_mouseDown)
        {
            e.Handled = true;   // no drag from a press on the surface
            return;             // handover already happened on MouseDown
        }
        // Hover: in unified mode the fake cursor tracks the mouse so the two
        // read as one cursor. In preserve mode the two are separate: the
        // physical mouse must not steer the virtual one.
        if (UnifiedPhysical &&
            e.LeftButton == MouseButtonState.Released &&
            e.RightButton == MouseButtonState.Released &&
            e.MiddleButton == MouseButtonState.Released)
            AdoptCursor();
        e.Handled = true;
    }

    private void OnMouseUp(object sender, WMouseButtonEventArgs e)
    {
        if (IsChrome(e.OriginalSource)) return;
        // Swallow: nothing outside the pad may act on a mouse event that
        // landed on the touch surface.
        if (e.StylusDevice != null || !_mouseDown)
        {
            e.Handled = true;
            return;
        }
        if (_rsMouse)
        {
            Surface.ReleaseMouseCapture();
            EndResize();
            _mouseDown = false;
            e.Handled = true;
            return;
        }
        if (DateTime.Now < _suppressPhysicalUntil) { _mouseDown = false; e.Handled = true; return; }
        if (UnifiedPhysical) HandoverToPhysicalMouse();
        _mouseDown = false;
        if (_mouseDownButton != null)
        {
            InputSim.Up(_mouseDownButton);
            _mouseDownButton = null;
        }
        else
        {
            var p = e.GetPosition(Surface);
            bool moved = Math.Abs(p.X - _mouseStart.X) + Math.Abs(p.Y - _mouseStart.Y) > TapMoveDip;
            // Same no-dead-zone rule as touch: a mouse press that never moved
            // is a click, however long it was held. Waiting for TapMs only
            // made a slow, deliberate press silently do nothing.
            bool tap = !moved;
            var tile = HitMouse(_mouseStart);
            if (tile?.Action == TileAction.Pad)
            {
                // The pad area is a TOUCH surface. Pressed with the physical
                // mouse it must stay inert: the mouse is a complete input
                // device in its own right and its click already went to the
                // OS, so adding a synthetic one fired a second, unrelated
                // click at the virtual cursor - the pad and the mouse both
                // acting on one press. Button/key/sys tiles are unaffected:
                // those exist to be operated.
                DebugLog.Write(
                    $"MOUSE pad press: no synthetic click (mouse=({p.X:0},{p.Y:0})"
                    + (tap ? ", tap" : ", moved") + ")");
            }
            else if (tile?.Action == TileAction.Sys && tile != null)
                SysAction(tile.Kind);
        }
        Surface.ReleaseMouseCapture();
        e.Handled = true;
    }
}
