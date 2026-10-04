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

    /// <summary>Close (✕) button on the strip's right side.</summary>
    public event Action? ClosePad;

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
    private bool _moved, _mouseDown;
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
        // One keeper only: the grip/close buttons ride along in the same
        // tick. Separate keepers leapfrog each other (visible flicker).
        Core.TopmostKeeper.Attach(this, () =>
        {
            try
            {
                if (_gripWin != null) Core.TopmostKeeper.Raise(_gripWin);
                if (_closeWin != null) Core.TopmostKeeper.Raise(_closeWin);
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
        // In-bar close button retired: close lives outside the bar now
        // (PositionChrome). Kept in XAML collapsed for layout spacing.
        PadClose.Visibility = Visibility.Collapsed;
    }

    /// <summary>Pad-close availability: the external close button shows
    /// while the pad is on (and hides with the strip).</summary>
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
    }

    private void OnTouchDown(object sender, TouchEventArgs e)
    {
        if (Core.WpfHit.IsButton(e.OriginalSource)) return; // ✕ owns it
        if (InRows(e.OriginalSource)) return; // row items own it
        if (_touchId != -1) { e.Handled = true; return; }
        _touchId = e.TouchDevice.Id;
        _start = _last = e.GetTouchPoint(this).Position;
        _t0 = DateTime.Now;
        _moved = false;
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
        e.Handled = true;
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
        _mStart = e.GetPosition(this);
        _mT0 = DateTime.Now;
        Pressed?.Invoke();
        Surface.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, WMouseEventArgs e)
    {
        if (e.StylusDevice != null || !_mouseDown) return;
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice != null || !_mouseDown) return;
        _mouseDown = false;
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
            if (edge == "bottom" || edge == "top")
            {
                Top = oy + (edge == "bottom" ? ph - Height : 0);
                if (px < 0) Left = ox + (pw - Width) / 2;
                else if (align == "right") Left = ox + pw - Width - px;
                else if (align == "center") Left = ox + (pw - Width) / 2 + px;
                else Left = ox + px;
                Left = Math.Max(ox, Math.Min(ox + pw - Width, Left));
            }
            else
            {
                Left = ox + (edge == "right" ? pw - Width : 0);
                if (px < 0) Top = oy + (ph - Height) / 2;
                else if (align == "right") Top = oy + ph - Height - px;
                else if (align == "center") Top = oy + (ph - Height) / 2 + px;
                else Top = oy + px;
                Top = Math.Max(oy, Math.Min(oy + ph - Height, Top));
            }
            Visibility = _s.StripVisible ? Visibility.Visible : Visibility.Hidden;
            ApplyStripStyle();
            PositionChrome();
        }
        catch { }
    }

    private Window? _gripWin, _closeWin;

    /// <summary>External grip (drag to move) + close buttons. They dock
    /// outside the bar; only when that space is off-screen do they
    /// overlap the bar instead.</summary>
    private void EnsureChrome()
    {
        try
        {
            if (_gripWin == null)
            {
                _gripWin = MakeChromeBtn(TriPoints(close: false), "...", "스트립 이동 (드래그)", true);
                _gripWin.PreviewTouchDown += (_, e) =>
                {
                    try { _gripWin.CaptureTouch(e.TouchDevice); } catch { }
                    StartGripDrag(e.GetTouchPoint(_gripWin).Position,
                        e.TouchDevice, true);
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
                    try { _gripWin.CaptureMouse(); } catch { }
                    StartGripDrag(e.GetPosition(_gripWin), null, false);
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
            if (_closeWin == null)
            {
                _closeWin = MakeChromeBtn(TriPoints(close: true), "X", "패드 닫기", false);
                _closeWin.PreviewTouchDown += (_, e) => { ClosePad?.Invoke(); e.Handled = true; };
                _closeWin.PreviewMouseLeftButtonUp += (_, e) => { ClosePad?.Invoke(); e.Handled = true; };
            }
        }
        catch { }
    }

    /// <summary>Corner-grip triangle points (28x28 box): the window's
    /// outer corner IS the strip corner (top-left based / top-right
    /// based), diagonal edge like the pad resize triangles.</summary>
    private static System.Windows.Media.PointCollection TriPoints(bool close)
    {
        var pts = new System.Windows.Media.PointCollection();
        if (close)
        {
            // Right angle at top-right (= strip's top-right corner).
            pts.Add(new Point(4, 4));
            pts.Add(new Point(24, 4));
            pts.Add(new Point(24, 24));
        }
        else
        {
            // Right angle at top-left (= strip's top-left corner).
            pts.Add(new Point(4, 4));
            pts.Add(new Point(24, 4));
            pts.Add(new Point(4, 24));
        }
        return pts;
    }

    private System.Windows.Shapes.Polygon? _gripPoly, _closePoly;

    private Window MakeChromeBtn(
        System.Windows.Media.PointCollection tri, string glyph, string tip, bool grip)
    {
        var poly = new System.Windows.Shapes.Polygon
        {
            Points = tri,
            Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0xAA, 0x9A, 0xA6, 0xBD)),
            Stroke = System.Windows.Media.Brushes.Gray,
            StrokeThickness = 1,
        };
        var label = new System.Windows.Controls.TextBlock
        {
            Text = glyph, FontSize = 13, FontWeight = System.Windows.FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        var grid = new System.Windows.Controls.Grid();
        grid.Children.Add(poly);
        grid.Children.Add(label);
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Topmost = true, ShowInTaskbar = false, ShowActivated = false,
            ResizeMode = ResizeMode.NoResize, Width = 28, Height = 28,
            Content = grid, ToolTip = tip, Cursor = System.Windows.Input.Cursors.Hand,
        };
        Core.NoActivate.Apply(w);
        Core.TabletTweaks.DisableSystemGestures(w);
        // No own keeper: the strip's keeper raises these along (separate
        // keepers leapfrog = flicker).
        if (grip) _gripPoly = poly; else _closePoly = poly;
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
            if (_closePoly != null) _closePoly.Fill = brush;
        }
        catch { }
    }

    private void PositionChrome()
    {
        try
        {
            EnsureChrome();
            if (_gripWin == null || _closeWin == null) return;
            bool show = Visibility == Visibility.Visible;
            // Chrome rides ON the strip: grip overlaps the top-left
            // corner, close the top-right corner (their outer corners
            // are the strip corners). Narrow bars stack close below
            // grip instead of overlapping it.
            double gx = Left, gy = Top;
            double cx = Left + Width - _closeWin.Width, cy = Top;
            if (Width < _gripWin.Width + _closeWin.Width + 4)
            {
                cx = Left; cy = Top + _gripWin.Height;
            }
            _gripWin.Left = gx; _gripWin.Top = gy;
            _closeWin.Left = cx; _closeWin.Top = cy;
            var gv = show ? Visibility.Visible : Visibility.Hidden;
            _gripWin.Visibility = gv;
            // Close keeps its pad-active rule, and hides with the strip.
            _closeWin.Visibility = show && _padActive
                ? Visibility.Visible : Visibility.Hidden;
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
