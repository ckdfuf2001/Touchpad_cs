using System;
using System.Windows;
using System.Windows.Input;
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
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
        Surface.PreviewTouchDown += OnTouchDown;
        Surface.PreviewTouchMove += OnTouchMove;
        Surface.PreviewTouchUp += OnTouchUp;
        Surface.MouseDown += OnMouseDown;
        Surface.MouseMove += OnMouseMove;
        Surface.MouseUp += OnMouseUp;
        // Same as the pad: a touch here is promoted to a MOUSE event (and the
        // system moves the cursor onto the contact for it). Block the promoted
        // ones; a real mouse has StylusDevice == null and is untouched.
        Surface.PreviewMouseDown += SwallowTouchMouse;
        Surface.PreviewMouseMove += SwallowTouchMouse;
        Surface.PreviewMouseUp += SwallowTouchMouse;
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

    public void SetLabel(string layout) => Label.Text = $"◀  {layout}  ▶";

    // NOTE: the bar never resizes itself on press anymore (that was
    // confusing). "Expansion" = the mode panel window below the bar.

    private void ResetTouch()
    {
        _touchId = -1;
    }

    private void OnTouchDown(object sender, TouchEventArgs e)
    {
        if (Core.WpfHit.IsButton(e.OriginalSource)) return; // ✕ owns it
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
}
