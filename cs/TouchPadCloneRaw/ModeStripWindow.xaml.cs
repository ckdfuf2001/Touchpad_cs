using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TouchPadCloneV2.Core;
using WPoint = System.Windows.Point;
using WMouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace TouchPadCloneV2;

/// <summary>
/// Top-edge mode strip. Touch-first: TouchDown expands the bar for fingers,
/// TouchMove tracks the swipe, TouchUp decides tap / horizontal swipe /
/// swipe-down. Mouse is a fallback (stylus-promoted events ignored).
/// </summary>
public partial class ModeStripWindow : Window
{
    private const double TapMs = 300;

    /// <summary>
    /// Fires a configured strip action from StripGestureMap.Actions
    /// (none/prev_layout/next_layout/show_modes/open_settings/
    /// toggle_fullscreen/show_assist/toggle_pad).
    /// </summary>
    public event Action<string>? Gesture;

    /// <summary>Raw tap: the app decides by pad state (activate vs panel).</summary>
    public event Action? Tap;

    /// <summary>Close (X) button on the strip: hides the pad.</summary>
    public event Action? ClosePad;

    /// <summary>Mode (☰) button on the strip: opens the mode panel.</summary>
    public event Action? ModePressed;

    /// <summary>Fired on press so the app can tell strip-taps apart when
    /// the mode panel auto-closes on deactivation.</summary>
    public event Action? Pressed;

    /// <summary>Row item selected a preset layout.</summary>
    public event Action<string>? LayoutSelected;

    /// <summary>Row toggle for artist/virtual pads (not a layout).</summary>
    public event Action<string>? AuxToggled;

    private List<string> _knownLayouts = new();
    public List<string> KnownLayouts
    {
        get => _knownLayouts;
        set { _knownLayouts = value ?? new(); RefreshRows(); }
    }

    private string _current = "";
    private readonly Dictionary<string, bool> _toggleState = new();

    public void SetToggle(string name, bool on)
    {
        _toggleState[name] = on;
        RefreshRows();
    }
    private int _itId = -1;
    private WPoint _itStart;
    private DateTime _itT0;
    private StripRowItem? _itItem;

    private readonly AppSettings _s;

    private int _touchId = -1;
    private WPoint _start, _last;
    private DateTime _t0;
    private bool _moved, _mouseDown, _decided;
    private WPoint _mStart;
    private DateTime _mT0;
    private DateTime _lastTapUp = DateTime.MinValue;
    private System.Windows.Threading.DispatcherTimer? _tapTimer;
    private string _tapPending = "none";

    /// <summary>
    /// While a touch gesture is in progress, the strip lets mouse input
    /// through to whatever is beneath it - otherwise a drag whose release
    /// lands along the top of the screen is dropped on the strip and lost.
    /// The close button is excluded so it stays usable.
    /// </summary>
    private IntPtr StripWndProc(IntPtr hwnd, int msg, IntPtr wParam,
        IntPtr lParam, ref bool handled)
    {
        if (msg != 0x0084 || !TouchPadWindow.SessionActive) return IntPtr.Zero;
        try
        {
            int sx = unchecked((short)(long)lParam);
            int sy = unchecked((short)((long)lParam >> 16));
            var win = PointFromScreen(new Point(sx, sy));
            // A Button's content is a TextBlock, so hit testing returns that,
            // not the Button - walk up to see whether the point is inside one.
            // Testing `is Button` directly let the buttons fall through.
            System.Windows.DependencyObject? d = InputHitTest(win) as
                System.Windows.DependencyObject;
            while (d != null)
            {
                if (d is System.Windows.Controls.Button) return IntPtr.Zero;
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
            handled = true;
            return new IntPtr(-1);   // HTTRANSPARENT
        }
        catch { }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Block a mouse event promoted from a touch. A physical mouse has no
    /// StylusDevice and passes through.
    /// </summary>
    private static void SwallowTouchMouse(object sender, MouseEventArgs e)
    {
        if (e.StylusDevice == null) return;
        e.Handled = true;
    }

    public ModeStripWindow(AppSettings settings)
    {
        _s = settings;
        InitializeComponent();
        Core.NoActivate.Apply(this);
        Core.TabletTweaks.DisableSystemGestures(this);
        // The strip is a permanent control bar and has to stay visible. Its
        // Topmost="True" is only the initial state - another application that
        // raises its own topmost window afterwards covers it and nothing
        // brings it back, which is "the strip gets covered by a program".
        // One keeper only: the grip/mode buttons ride along in the same
        // tick. Separate keepers leapfrog each other (visible flicker).
        Core.TopmostKeeper.Attach(this, () =>
        {
            try
            {
                if (_gripWin != null) Core.TopmostKeeper.Raise(_gripWin);
                if (_modeWin != null) Core.TopmostKeeper.Raise(_modeWin);
            }
            catch { }
        });
        // And while a touch gesture runs the strip must get out of the way of
        // the mouse: it is topmost along the top of the screen, so a drag that
        // ended up there was dropped on it (logged: "DRAG end ... under
        // [TouchPadClone ModeStrip]") and the drop went nowhere. Its own close
        // button stays clickable.
        SourceInitialized += (_, _) =>
        {
            try
            {
                var src = System.Windows.Interop.HwndSource.FromHwnd(
                    new System.Windows.Interop.WindowInteropHelper(this).Handle);
                src?.AddHook(StripWndProc);
            }
            catch { }
        };
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
        ApplyStripLayout();
        RefreshRows();
        Surface.PreviewTouchDown += OnTouchDown;
        Surface.PreviewTouchMove += OnTouchMove;
        Surface.PreviewTouchUp += OnTouchUp;
        Surface.MouseDown += OnMouseDown;
        Surface.MouseMove += OnMouseMove;
        Surface.MouseUp += OnMouseUp;
        // NOT SwallowTouchMouse here, unlike the pad: this bar is made of
        // BUTTONS, and a WPF Button's Click comes from the mouse events that a
        // touch is promoted into. Swallowing them made the close button
        // unpressable by touch ("the close on the strip does not close"). The
        // rule that a touch must not produce mouse events is about the pad's
        // touch surface, not about this control bar.
        Surface.LostTouchCapture += (_, e) =>
        {
            // A touch that never reports Up (capture stolen/killed) must not
            // wedge the strip: drop the stroke, collapse the bar.
            ResetTouch();
        };
        SetPadActive(false);
    }

    /// <summary>Pad-active state (close X lives on the pad title bar
    /// now; the strip bar always shows the selection label).</summary>
    public void SetPadActive(bool on)
    {
        _padActive = on;
        PositionChrome();
    }

    public void SetLabel(string layout)
    {
        _current = layout;
        Label.Text = $"◀  {layout}  ▶";
        RefreshRows();
    }

    /// <summary>Strip shows the selected menu cell (swipe navigation).
    /// Falls back to the layout label when empty.</summary>
    public void SetSelection(string label)
    {
        Label.Text = string.IsNullOrWhiteSpace(label)
            ? $"◀  {_current}  ▶" : $"◀  {label.Trim()}  ▶";
    }

    /// <summary>Pad-open state: center text becomes the menu button
    /// (tap opens the mode panel). Doesn't touch _current.</summary>
    public void SetMenuMode()
    {
        Label.Text = "menu";
    }

    // NOTE: the bar never resizes itself on press anymore (that was
    // confusing). "Expansion" = the mode panel window below the bar.

    private void ResetTouch()
    {
        _touchId = -1;
        _decided = false;
    }

    private void OnTouchDown(object sender, TouchEventArgs e)
    {
        // Log entry FIRST (even rejections): silent drops are undebuggable.
        System.Windows.Point sp;
        try { sp = e.GetTouchPoint(this).Position; }
        catch (Exception ex)
        {
            // A bad point must never wedge _touchId (all future touches
            // would die silently): reset and report.
            _touchId = -1;
            DebugLog.Write($"STRIPDOWN-FAIL id={e.TouchDevice.Id} {ex.GetType().Name}");
            e.Handled = true;
            return;
        }
        DebugLog.Write($"STRIPDOWN-SEE id={e.TouchDevice.Id} @{sp.X:0},{sp.Y:0} src={e.OriginalSource?.GetType().Name}");
        if (Core.WpfHit.IsButton(e.OriginalSource)) return; // ✕ owns it
        if (InRows(e.OriginalSource)) return; // row items own it
        if (_touchId != -1)
        {
            DebugLog.Write($"STRIPDOWN-BUSY id={e.TouchDevice.Id} held={_touchId}");
            e.Handled = true;
            return;
        }
        _touchId = e.TouchDevice.Id;
        _start = _last = sp;
        _t0 = DateTime.Now;
        _moved = false;
        _decided = false;
        Pressed?.Invoke();
        DebugLog.Write($"STRIPDOWN id={e.TouchDevice.Id} @{_start.X:0},{_start.Y:0}");
        Surface.CaptureTouch(e.TouchDevice);
        e.Handled = true;
    }

    private void OnTouchMove(object sender, TouchEventArgs e)
    {
        if (e.TouchDevice.Id != _touchId) return;
        _last = e.GetTouchPoint(this).Position;
        if (Math.Abs(_last.X - _start.X) + Math.Abs(_last.Y - _start.Y) > 12)
            _moved = true;
        // Live recognition: fire as soon as travel passes the axis
        // threshold (proportional to bar length), so short-axis
        // swipes judge as fast as long-axis ones. UP stays fallback.
        if (!_decided)
        {
            string a = ClassifySwipe(_last.X - _start.X, _last.Y - _start.Y);
            if (a != "none")
            {
                DebugLog.Write($"STRIP live d=({_last.X - _start.X:0},{_last.Y - _start.Y:0}) -> {a}");
                Gesture?.Invoke(a);
                _decided = true;
            }
        }
        e.Handled = true;
    }

    /// <summary>Swipe classify shared by move (live) and up (fallback):
    /// dominant axis (1.5x) past a bar-proportional threshold.
    /// Under it (tap territory) returns none.</summary>
    private string ClassifySwipe(double dx, double dy)
    {
        var m = _s.StripGestures ?? new StripGestureMap();
        double ax = Math.Abs(dx), ay = Math.Abs(dy);
        if (ax + ay <= 8) return "none";
        double hx = Math.Min(48, Math.Max(16, Width * 0.06));
        double vx = Math.Min(48, Math.Max(10, Height * 0.06));
        if (ax >= ay * 1.5 && ax >= hx) return dx > 0 ? m.SwipeRight : m.SwipeLeft;
        if (ay > ax * 1.5 && ay >= vx) return dy > 0 ? m.SwipeDown : m.SwipeUp;
        return "none";
    }

    private void Decide(double dx, double dy, double ms)
    {
        var m = _s.StripGestures ?? new StripGestureMap();
        // Tap: quick and stationary. Anything else with a clear dominant
        // axis is a swipe - no absolute minimums (a top-edge bar gives
        // an upward flick ~10px of travel; a tall bar gives plenty).
        // Safe: swipes only move the menu selection, nothing fires.
        double ax = Math.Abs(dx), ay = Math.Abs(dy);
        if (ms < TapMs && ax + ay <= 8)
        {
            Tap?.Invoke();
            return;
        }
        string action = "none";
        if (ax >= ay * 1.5)
            action = dx > 0 ? m.SwipeRight : m.SwipeLeft;
        else if (ay > ax * 1.5)
            action = dy > 0 ? m.SwipeDown : m.SwipeUp;
        DebugLog.Write($"STRIP decide d=({dx:0},{dy:0}) {ms:0}ms -> {action}");
        if (action != "none") Gesture?.Invoke(action);
    }

    private void OnTouchUp(object sender, TouchEventArgs e)
    {
        if (e.TouchDevice.Id != _touchId) return;
        _touchId = -1;
        // Already fired live during the move: just release.
        if (_decided)
        {
            _decided = false;
            Surface.ReleaseTouchCapture(e.TouchDevice);
            Core.InputSim.ClearSuppression();
            e.Handled = true;
            return;
        }
        // Classify by the UP position, not the last move: a fast flick often
        // delivers zero TouchMove events, and using stale _last turns every
        // quick swipe into a tap.
        var end = e.GetTouchPoint(this).Position;
        double dx = end.X - _start.X, dy = end.Y - _start.Y;
        double ms = (DateTime.Now - _t0).TotalMilliseconds;
        DebugLog.Write($"STRIPUP d=({dx:0},{dy:0}) {ms:0}ms");
        // Release capture BEFORE firing actions: a window opened while we
        // still hold capture (picker/settings) may show without activation,
        // and then never receives Deactivated to auto-close.
        Surface.ReleaseTouchCapture(e.TouchDevice);
        Decide(dx, dy, ms);
        // A strip tap produces no mouse output, which leaves cursor
        // suppression stuck and the pointer invisible. Same self-heal as
        // the pad session end.
        Core.InputSim.ClearSuppression();
        e.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Core.WpfHit.IsButton(e.OriginalSource)) return; // ✕ owns it
        if (e.StylusDevice != null || _touchId != -1) return;
        _mouseDown = true;
        _decided = false;
        _mStart = e.GetPosition(this);
        _mT0 = DateTime.Now;
        Pressed?.Invoke();
        Surface.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, WMouseEventArgs e)
    {
        if (e.StylusDevice != null || !_mouseDown) return;
        if (!_decided)
        {
            var mp = e.GetPosition(this);
            string a = ClassifySwipe(mp.X - _mStart.X, mp.Y - _mStart.Y);
            if (a != "none")
            {
                DebugLog.Write($"STRIP live m=({mp.X - _mStart.X:0},{mp.Y - _mStart.Y:0}) -> {a}");
                Gesture?.Invoke(a);
                _decided = true;
            }
        }
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice != null || !_mouseDown) return;
        _mouseDown = false;
        if (_decided)
        {
            _decided = false;
            Surface.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        var p = e.GetPosition(this);
        double dx = p.X - _mStart.X, dy = p.Y - _mStart.Y;
        double ms = (DateTime.Now - _mT0).TotalMilliseconds;
        Surface.ReleaseMouseCapture();
        Decide(dx, dy, ms);
        e.Handled = true;
    }

    // ---------------- configurable rows -------------------------------

    /// <summary>Our home monitor in DIPs (dual monitors: the strip
    /// lives and clamps on its own screen, like the pad modes).</summary>
    private (double l, double t, double w, double h) HomeRect()
    {
        try
        {
            double d = 1.0;
            try
            {
                var s = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                if (s.DpiScaleX >= 0.5 && s.DpiScaleX <= 4) d = s.DpiScaleX;
            }
            catch { }
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            return Core.HomeMonitor.RectFor(h, d,
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        }
        catch
        {
            return (SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        }
    }

    /// <summary>Strip target monitor in DIPs: the configured monitor,
    /// or primary/first when it vanished. Used by the picker dock too.</summary>
    public (double l, double t, double w, double h) MonitorRect()
    {
        try
        {
            double d = 1.0;
            try
            {
                var s = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                if (s.DpiScaleX >= 0.5 && s.DpiScaleX <= 4) d = s.DpiScaleX;
            }
            catch { }
            var sc = Core.MonitorList.Resolve(_s.StripMonitor);
            if (sc != null)
            {
                var b = sc.Bounds;
                if (b.Width >= 100 && b.Height >= 100)
                    return (b.Left / d, b.Top / d, b.Width / d, b.Height / d);
            }
        }
        catch { }
        return HomeRect();
    }

    /// <summary>Position/size from settings: edge top|bottom|left|right,
    /// align left|center|right, px offset (px &lt; 0 = centered).
    /// Center align: +px toward right/bottom, -px toward left/top.
    /// Always clamped on-screen. Grip/close buttons dock outside the
    /// bar, overlapping it only when no outside space remains.</summary>
    public void ApplyStripLayout()
    {
        try
        {
            var home = MonitorRect();
            double pw = home.w, ph = home.h, ox = home.l, oy = home.t;
            string edge = (_s.StripEdge ?? "top").ToLowerInvariant();
            // Vertical edges run the bar tall: width/height swap.
            bool vertical = edge == "left" || edge == "right";
            double sw = vertical ? _s.StripHeight : _s.StripWidth;
            double sh = vertical ? _s.StripWidth : _s.StripHeight;
            Width = Math.Min(Math.Max(sw, 40), pw);
            Height = Math.Min(Math.Max(sh, 12), ph);
            // Tall bar: lay the mode label along it instead of clipping.
            try
            {
                Label.LayoutTransform = vertical
                    ? new System.Windows.Media.RotateTransform(-90) : null;
            }
            catch { }
            string align = (_s.StripSide ?? "left").ToLowerInvariant();
            int px = _s.StripPx;
            const double Chrome = 20;   // wedge width alongside the bar
            if (edge == "bottom" || edge == "top")
            {
                Top = oy + (edge == "bottom" ? ph - Height : 0);
                if (px < 0) Left = ox + (pw - Width) / 2;
                else if (align == "right") Left = ox + pw - Width - px;
                else if (align == "center") Left = ox + (pw - Width) / 2 + px;
                else Left = ox + px;
                // The bar always keeps the chrome buffer: shift, then
                // shrink, so the triangles never overlap it.
                Left = Math.Max(ox + Chrome, Math.Min(ox + pw - Width - Chrome, Left));
                if (Left + Width + Chrome > ox + pw)
                    Width = Math.Max(40, ox + pw - Chrome - Left);
                if (Left < ox + Chrome)
                    Left = ox + Chrome;
            }
            else
            {
                Left = ox + (edge == "right" ? pw - Width : 0);
                if (px < 0) Top = oy + (ph - Height) / 2;
                else if (align == "right") Top = oy + ph - Height - px;
                else if (align == "center") Top = oy + (ph - Height) / 2 + px;
                else Top = oy + px;
                Top = Math.Max(oy + Chrome, Math.Min(oy + ph - Height - Chrome, Top));
                if (Top + Height + Chrome > oy + ph)
                    Height = Math.Max(12, oy + ph - Chrome - Top);
                if (Top < oy + Chrome)
                    Top = oy + Chrome;
            }
            Visibility = _s.StripVisible ? Visibility.Visible : Visibility.Hidden;
            ApplyStripStyle();
            PositionChrome();
        }
        catch { }
    }

    private Window? _gripWin, _modeWin;

    /// <summary>External triangles: grip (drag to move) + mode (opens
    /// the mode panel, always visible like the grip). They ride with
    /// the bar; the close X lives inside the bar instead.</summary>
    private void EnsureChrome()
    {
        try
        {
            if (_gripWin == null)
            {
                _gripWin = MakeChromeBtn("스트립 이동 (드래그)", true);
                _gripWin.PreviewTouchDown += (_, e) =>
                {
                    var lp = e.GetTouchPoint(_gripWin).Position;
                    // Transparent corners belong to the bar (swipe starts).
                    if (!ChromeHit(true, _gripWin, lp)
                        && BarTouchDown(GripScreen(lp, _gripWin), e.TouchDevice))
                    { e.Handled = true; return; }
                    try { _gripWin.CaptureTouch(e.TouchDevice); } catch { }
                    StartGripDrag(lp, e.TouchDevice, true);
                    e.Handled = true;
                };
                _gripWin.PreviewTouchMove += (_, e) =>
                {
                    MoveGripDrag(e.GetTouchPoint(_gripWin).Position,
                        e.TouchDevice, true);
                    e.Handled = true;
                };
                _gripWin.PreviewTouchUp += (_, e) =>
                {
                    EndGripDrag(e.TouchDevice, true);
                    e.Handled = true;
                };
                // Preview (tunneling) for mouse too: the child Button
                // captures the press, so bubbling mouse events never
                // reach the window.
                _gripWin.PreviewMouseLeftButtonDown += (_, e) =>
                {
                    var lp = e.GetPosition(_gripWin);
                    if (!ChromeHit(true, _gripWin, lp)
                        && BarMouseDown(GripScreen(lp, _gripWin)))
                    { e.Handled = true; return; }
                    try { _gripWin.CaptureMouse(); } catch { }
                    StartGripDrag(lp, null, false);
                    e.Handled = true;
                };
                _gripWin.PreviewMouseMove += (_, e) =>
                {
                    if (!_gripMouse) return;
                    MoveGripDrag(e.GetPosition(_gripWin), null, false);
                    e.Handled = true;
                };
                _gripWin.PreviewMouseLeftButtonUp += (_, _) => EndGripDrag(null, false);
            }
            if (_modeWin == null)
            {
                _modeWin = MakeChromeBtn("모드 변경", false);
                _modeWin.PreviewTouchDown += (_, e) =>
                {
                    var lp = e.GetTouchPoint(_modeWin).Position;
                    if (!ChromeHit(false, _modeWin, lp)
                        && BarTouchDown(GripScreen(lp, _modeWin), e.TouchDevice))
                    { e.Handled = true; return; }
                    ModePressed?.Invoke(); e.Handled = true;
                };
                _modeWin.PreviewMouseLeftButtonDown += (_, e) =>
                {
                    var lp = e.GetPosition(_modeWin);
                    if (!ChromeHit(false, _modeWin, lp)
                        && BarMouseDown(GripScreen(lp, _modeWin)))
                    { e.Handled = true; return; }
                    e.Handled = true;
                };
                _modeWin.PreviewMouseLeftButtonUp += (_, e) => { ModePressed?.Invoke(); e.Handled = true; };
            }
        }
        catch { }
    }

    /// <summary>Sector frame: outward unit (ox,oy), center (cx,cy)
    /// on the bar edge, radius fitting the box.</summary>
    private static void SectorFrame(bool grip, bool vertical,
        double w, double h,
        out double ox, out double oy, out double cx, out double cy, out double r)
    {
        if (!vertical)
        {
            ox = grip ? -1 : 1; oy = 0;
            cx = grip ? w : 0; cy = h / 2;
            r = Math.Min(w, 0.7071 * h);
        }
        else
        {
            ox = 0; oy = grip ? -1 : 1;
            cx = w / 2; cy = grip ? h : 0;
            r = Math.Min(h, 0.7071 * w);
        }
        if (r < 4) r = 4;
    }

    /// <summary>Quarter-circle sector points for a W(x)H box: the
    /// sector center sits ON the bar edge, the 90-degree arc bulges
    /// outward (grip ▶/▼, mode ◀/▲). 10 samples approximate the arc.</summary>
    private static System.Windows.Media.PointCollection WedgePoints(
        bool grip, bool vertical, double w, double h)
    {
        var pts = new System.Windows.Media.PointCollection();
        try
        {
            SectorFrame(grip, vertical, w, h,
                out double ox, out double oy,
                out double cx, out double cy, out double r);
            // Perpendicular (either sign gives the symmetric fan).
            double ux = -oy, uy = ox;
            pts.Add(new Point(cx, cy));
            for (int i = 0; i <= 10; i++)
            {
                double t = (-45 + 90.0 * i / 10) * Math.PI / 180.0;
                double dx = ox * Math.Cos(t) + ux * Math.Sin(t);
                double dy = oy * Math.Cos(t) + uy * Math.Sin(t);
                pts.Add(new Point(cx + dx * r, cy + dy * r));
            }
        }
        catch { }
        return pts;
    }

    /// <summary>True when a point lands on the sector (dist <= R and
    /// within 45 degrees of the outward normal). Same frame as above;
    /// transparent corners belong to the bar.</summary>
    private static bool TriHit(bool grip, bool vertical,
        double w, double h, System.Windows.Point p)
    {
        try
        {
            SectorFrame(grip, vertical, w, h,
                out double ox, out double oy,
                out double cx, out double cy, out double r);
            double vx = p.X - cx, vy = p.Y - cy;
            double dist = Math.Sqrt(vx * vx + vy * vy);
            if (dist > r || dist < 0.5) return dist < 0.5;
            double cos = (vx * ox + vy * oy) / dist;
            return cos >= 0.7071;
        }
        catch { return true; }
    }

    /// <summary>Bar touch started on a chrome window's transparent area:
    /// adopt it as a bar press (bar captures the stream). False when
    /// the bar is busy (caller keeps its own behavior).</summary>
    private bool BarTouchDown(System.Windows.Point screen,
        System.Windows.Input.TouchDevice dev)
    {
        if (_touchId != -1) return false;
        try
        {
            _touchId = dev.Id;
            _start = _last = new System.Windows.Point(
                screen.X - Left, screen.Y - Top);
            _t0 = DateTime.Now;
            _moved = false;
            Pressed?.Invoke();
            DebugLog.Write($"STRIPDOWN id={dev.Id} @{_start.X:0},{_start.Y:0} (via chrome)");
            Surface.CaptureTouch(dev);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Same for the real mouse.</summary>
    private bool BarMouseDown(System.Windows.Point screen)
    {
        try
        {
            if (_touchId != -1 || _mouseDown) return false;
            _mouseDown = true;
            _mStart = new System.Windows.Point(screen.X - Left, screen.Y - Top);
            _mT0 = DateTime.Now;
            Pressed?.Invoke();
            Surface.CaptureMouse();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Hit test against the window's current wedge geometry
    /// (set by PositionChrome for the bar orientation).</summary>
    private bool ChromeHit(bool grip, Window w, System.Windows.Point lp)
    {
        try
        {
            string edge = (_s.StripEdge ?? "top").ToLowerInvariant();
            bool vertical = edge == "left" || edge == "right";
            return TriHit(grip, vertical, w.Width, w.Height, lp);
        }
        catch { return true; }
    }

    private System.Windows.Shapes.Polygon? _gripPoly, _modePoly;

    private Window MakeChromeBtn(string tip, bool grip)
    {
        var poly = new System.Windows.Shapes.Polygon
        {
            Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0xAA, 0x9A, 0xA6, 0xBD)),
            Stroke = System.Windows.Media.Brushes.Gray,
            StrokeThickness = 1,
        };
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Topmost = true, ShowInTaskbar = false, ShowActivated = false,
            ResizeMode = ResizeMode.NoResize, Width = 20, Height = 20,
            Content = poly, ToolTip = tip, Cursor = System.Windows.Input.Cursors.Hand,
        };
        Core.NoActivate.Apply(w);
        Core.TabletTweaks.DisableSystemGestures(w);
        // Press-and-hold (touch) must not steal slow/careful swipes:
        // the system would take the touch for a right-click gesture
        // and our stream dies with no UP (pad disables it too).
        System.Windows.Input.Stylus.SetIsPressAndHoldEnabled(w, false);
        // No own keeper: the strip's keeper raises these along (separate
        // keepers leapfrog = flicker).
        if (grip) _gripPoly = poly; else _modePoly = poly;
        ApplyStripStyle();
        w.Show();
        return w;
    }

    /// <summary>Strip color + opacity onto the bar and the corner
    /// triangles (render only).</summary>
    private void ApplyStripStyle()
    {
        try
        {
            string raw = (_s.StripColor ?? "#10131A").Trim();
            System.Windows.Media.Brush brush;
            if (ColorPalettes.IsNone(raw))
                brush = System.Windows.Media.Brushes.Transparent;
            else
            {
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                    .ConvertFromString(raw);
                double op = _s.StripOpacity;
                if (op < 0.05) op = 0.05;
                if (op > 1) op = 1;
                brush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb((byte)(op * 255), c.R, c.G, c.B));
            }
            Surface.Background = brush;
            if (_gripPoly != null) _gripPoly.Fill = brush;
            if (_modePoly != null) _modePoly.Fill = brush;
        }
        catch { }
    }

    private void PositionChrome()
    {
        try
        {
            EnsureChrome();
            if (_gripWin == null || _modeWin == null) return;
            bool show = Visibility == Visibility.Visible;
            // Wedges flush against the bar, bar-height tall (wide bar)
            // or bar-width wide (tall bar): [grip][bar][mode].
            bool vertical = ((_s.StripEdge ?? "top").ToLowerInvariant() == "left")
                || ((_s.StripEdge ?? "top").ToLowerInvariant() == "right");
            if (_gripPoly != null)
                _gripPoly.Points = WedgePoints(true, vertical,
                    _gripWin.Width, _gripWin.Height);
            if (_modePoly != null)
                _modePoly.Points = WedgePoints(false, vertical,
                    _modeWin.Width, _modeWin.Height);
            double gx, gy, mx, my;
            if (vertical)
            {
                _gripWin.Width = Width; _gripWin.Height = 20;
                _modeWin.Width = Width; _modeWin.Height = 20;
                if (_gripPoly != null)
                    _gripPoly.Points = WedgePoints(true, true, Width, 20);
                if (_modePoly != null)
                    _modePoly.Points = WedgePoints(false, true, Width, 20);
                gx = Left; gy = Top - 20;
                mx = Left; my = Top + Height;
            }
            else
            {
                _gripWin.Width = 20; _gripWin.Height = Height;
                _modeWin.Width = 20; _modeWin.Height = Height;
                if (_gripPoly != null)
                    _gripPoly.Points = WedgePoints(true, false, 20, Height);
                if (_modePoly != null)
                    _modePoly.Points = WedgePoints(false, false, 20, Height);
                gx = Left - 20; gy = Top;
                mx = Left + Width; my = Top;
            }
            _gripWin.Left = gx; _gripWin.Top = gy;
            _modeWin.Left = mx; _modeWin.Top = my;
            var gv = show ? Visibility.Visible : Visibility.Hidden;
            _gripWin.Visibility = gv;
            _modeWin.Visibility = gv;
        }
        catch { }
    }

    private bool _padActive;
    private bool _gripTouch;
    private System.Windows.Input.TouchDevice? _gripDevice;
    private bool _gripMouse;
    private System.Windows.Point _gripGrab;

    private System.Windows.Point GripScreen(System.Windows.Point p, Window w)
    {
        // PointToScreen returns DEVICE px; window coords are DIPs.
        // Without scaling, high-DPI screens overshoot the finger
        // (looks like the cursor speed gain leaking into strip moves).
        try
        {
            var s = w.PointToScreen(p);
            double d = 1.0;
            try
            {
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
                if (dpi.DpiScaleX >= 0.5 && dpi.DpiScaleX <= 4) d = dpi.DpiScaleX;
            }
            catch { }
            return new System.Windows.Point(s.X / d, s.Y / d);
        }
        catch { return p; }
    }

    private void StartGripDrag(System.Windows.Point p,
        System.Windows.Input.TouchDevice? dev, bool touch)
    {
        try
        {
            if (_gripWin == null) return;
            if (touch)
            {
                if (_gripTouch) return;
                _gripTouch = true; _gripDevice = dev;
            }
            else
            {
                if (_gripMouse) return;
                _gripMouse = true;
            }
            _gripGrab = GripScreen(p, _gripWin);
        }
        catch { }
    }

    private void MoveGripDrag(System.Windows.Point p,
        System.Windows.Input.TouchDevice? dev, bool touch)
    {
        try
        {
            if (touch && (!_gripTouch || !ReferenceEquals(dev, _gripDevice))) return;
            if (!touch && !_gripMouse) return;
            var s = GripScreen(p, _gripWin!);
            double dx = s.X - _gripGrab.X, dy = s.Y - _gripGrab.Y;
            if (Math.Abs(dx) + Math.Abs(dy) < 1) return;
            // Move along the current edge; the offset becomes explicit px.
            string edge = (_s.StripEdge ?? "top").ToLowerInvariant();
            string align = (_s.StripSide ?? "left").ToLowerInvariant();
            var home = MonitorRect();
            double pw = home.w, ph = home.h, ox = home.l, oy = home.t;
            if (edge == "top" || edge == "bottom")
            {
                double nl = Left + dx;
                nl = Math.Max(ox, Math.Min(ox + pw - Width, nl));
                if (align == "right") _s.StripPx = (int)Math.Round(ox + pw - Width - nl);
                else if (align == "center") _s.StripPx = (int)Math.Round(nl - (ox + (pw - Width) / 2));
                else _s.StripPx = (int)Math.Round(nl - ox);
            }
            else
            {
                double nt = Top + dy;
                nt = Math.Max(oy, Math.Min(oy + ph - Height, nt));
                if (align == "right") _s.StripPx = (int)Math.Round(oy + ph - Height - nt);
                else if (align == "center") _s.StripPx = (int)Math.Round(nt - (oy + (ph - Height) / 2));
                else _s.StripPx = (int)Math.Round(nt - oy);
            }
            _gripGrab = s;
            ApplyStripLayout();
        }
        catch { }
    }

    private void EndGripDrag(System.Windows.Input.TouchDevice? dev, bool touch)
    {
        try
        {
            if (touch && (!_gripTouch || !ReferenceEquals(dev, _gripDevice))) return;
            if (touch)
            {
                _gripTouch = false; _gripDevice = null;
                try
                {
                    if (_gripWin != null && dev != null)
                        _gripWin.ReleaseTouchCapture(dev);
                }
                catch { }
            }
            else
            {
                _gripMouse = false;
                try { _gripWin?.ReleaseMouseCapture(); } catch { }
            }
            _s.Save();
        }
        catch { }
    }

    /// <summary>Legacy in-bar menu retired (menu = picker popup).
    /// Kept as a clear-only stub so existing callers still compile.</summary>
    public void RefreshRows()
    {
        try { Rows?.Children.Clear(); }
        catch { }
    }

    private void AddRow(List<StripRowItem> items)
    {
        if (items == null) return;
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        foreach (var it in items)
        {
            if (!it.Visible) continue;
            bool on = it.Kind == "radio" ? it.Target == _current
                : _toggleState.TryGetValue(it.Target, out bool v) && v;
            row.Children.Add(MakeItem(it, on));
        }
        if (row.Children.Count > 0) Rows.Children.Add(row);
    }

    private Border MakeItem(StripRowItem it, bool on)
    {
        var tb = new TextBlock
        {
            Text = it.Name,
            Foreground = Brushes.LightGray,
            FontSize = 11,
            IsHitTestVisible = false,
        };
        var b = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = on
                ? new SolidColorBrush(Color.FromArgb(0xAA, 0x2E, 0x7F, 0xE0))
                : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(3, 2, 3, 2),
            Padding = new Thickness(8, 2, 8, 2),
            Child = tb,
            Tag = it,
        };
        b.TouchDown += ItemTouchDown;
        b.TouchUp += ItemTouchUp;
        b.MouseLeftButtonUp += (_, e) => { Activate(it); e.Handled = true; };
        return b;
    }

    private void ItemTouchDown(object sender, TouchEventArgs e)
    {
        try
        {
            if (_itId != -1) { e.Handled = true; return; }
            _itId = e.TouchDevice.Id;
            _itStart = e.GetTouchPoint(this).Position;
            _itT0 = DateTime.Now;
            _itItem = (sender as Border)?.Tag as StripRowItem;
            e.Handled = true;
        }
        catch { }
    }

    private void ItemTouchUp(object sender, TouchEventArgs e)
    {
        try
        {
            if (e.TouchDevice.Id != _itId) return;
            _itId = -1;
            var end = e.GetTouchPoint(this).Position;
            double ms = (DateTime.Now - _itT0).TotalMilliseconds;
            var it = _itItem;
            _itItem = null;
            if (ms < 300 && Math.Abs(end.X - _itStart.X) + Math.Abs(end.Y - _itStart.Y) <= 12
                && it != null)
                Activate(it);
            e.Handled = true;
        }
        catch { }
    }

    private void Activate(StripRowItem it)
    {
        try
        {
            if (_knownLayouts.Contains(it.Target))
            {
                _current = it.Target;
                LayoutSelected?.Invoke(it.Target);
            }
            else if (it.Target == "artist" || it.Target == "virtual")
                AuxToggled?.Invoke(it.Target);
            else if (Array.IndexOf(StripGestureMap.Actions, it.Target) >= 0)
                Gesture?.Invoke(it.Target);
            RefreshRows();
        }
        catch { }
    }

    private bool InRows(object? src)
    {
        var d = src as DependencyObject;
        while (d != null)
        {
            if (ReferenceEquals(d, Rows)) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }
}
