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
    private const double SwipePx = 32;
    private const double DownPx = 28;
    private const double UpPx = 18; // top screen edge: upward travel is short
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
        Core.TopmostKeeper.Attach(this);
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
        LocationChanged += (_, _) => PublishRect();
        SizeChanged += (_, _) => PublishRect();
        Loaded += (_, _) => PublishRect();
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
        PadClose.Click += (_, _) => ClosePad?.Invoke();
    }

    /// <summary>Show/hide the pad-close (✕) button: visible while pad is on.</summary>
    public void SetPadActive(bool on) =>
        PadClose.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    public void SetLabel(string layout)
    {
        _current = layout;
        Label.Text = $"◀  {layout}  ▶";
        RefreshRows();
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
        // Raw tap goes to the app: pad off -> activate, pad on -> mapped tap.
        if (ms < TapMs && Math.Abs(dx) + Math.Abs(dy) <= 12)
        {
            Tap?.Invoke();
            return;
        }
        string action = "none";
        if (Math.Abs(dx) >= SwipePx && Math.Abs(dx) >= Math.Abs(dy))
            action = dx > 0 ? m.SwipeRight : m.SwipeLeft;
        else if (Math.Abs(dy) > Math.Abs(dx))
        {
            // Top screen edge: upward travel is physically short, so the
            // up threshold is lenient (UpPx < DownPx).
            double need = dy > 0 ? DownPx : UpPx;
            if (Math.Abs(dy) >= need) action = dy > 0 ? m.SwipeDown : m.SwipeUp;
        }
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

    /// <summary>Position/size from settings. The bar itself stays one
    /// line (26px); StripHeight sizes the picker menu instead.</summary>
    public void ApplyStripLayout()
    {
        try
        {
            Width = _s.StripWidth;
            Height = 26;
            double pw = SystemParameters.PrimaryScreenWidth;
            double ph = SystemParameters.PrimaryScreenHeight;
            switch (_s.StripPosition)
            {
                case "bottom":
                    Left = (pw - Width) / 2; Top = ph - Height; break;
                case "left":
                    Left = 0; Top = (ph - Height) / 2; break;
                case "right":
                    Left = pw - Width; Top = (ph - Height) / 2; break;
                default:
                    Left = (pw - Width) / 2; Top = 0; break;
            }
            Visibility = _s.StripVisible ? Visibility.Visible : Visibility.Hidden;
            PublishRect();
        }
        catch { }
    }

    /// <summary>Publishes our bar rect (physical px) to the mouse tap hook:
    /// touch-promoted mouse over the bar is swallowed (the bar owns its
    /// touches), everywhere else it passes. Empty while hidden.</summary>
    private void PublishRect()
    {
        try
        {
            if (!IsVisible || Visibility != Visibility.Visible)
            { Core.MouseTap.SetStripRect(0, 0, 0, 0); return; }
            var src = PresentationSource.FromVisual(this);
            double d = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            if (d < 0.5 || d > 4) d = 1.0;
            Core.MouseTap.SetStripRect(
                (int)(Left * d), (int)(Top * d),
                (int)((Left + Width) * d), (int)((Top + Height) * d));
        }
        catch { }
    }

    /// <summary>Rebuild rows from settings (call after edits).</summary>
    public void RefreshRows()
    {
        try
        {
            if (Rows == null) return;
            Rows.Children.Clear();
            AddRow(_s.StripRow1);
            AddRow(_s.StripRow2);
        }
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
