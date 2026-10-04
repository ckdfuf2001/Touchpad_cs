using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TouchPadCloneV2.Core;

namespace TouchPadCloneV2;

/// <summary>
/// Fresh observation rig (no gesture logic, no output).
/// Touch intake + zones + window ops + HUD + logs only.
/// Gestures arrive per dictation; every one funnels through Out.
/// </summary>
public partial class TouchPadWindow : Window
{
    private const double ChromeH = 30;

    /// <summary>Bottom event labels (zone/status/actual) only in debug.
    /// File-backed (AppSettings.DebugLabels); TOUCHPAD_DEBUG=1 also
    /// forces on. Env alone is unreliable: Explorer inherits a stale
    /// copy of deleted vars.</summary>
    private bool ShowEvents =>
        _s.DebugLabels
        || Environment.GetEnvironmentVariable("TOUCHPAD_DEBUG") == "1";
    private const double GripZone = 20;   // visual legs
    private const double GripHit = 36;    // touch legs (bigger than visual)
    private const double TapMoveDip = 12;
    private const double SecondTapDip = 120;   // re-landing scatter (~70 measured)
    private const double WheelDip = 30;        // DIP per wheel notch
    private const double MergeDip = 40;

    private readonly AppSettings _s;
    private double _dpi = 1;
    private double _fakeX, _fakeY;
    private bool _fakeInit;   // selftest anchor (unused by the engine)

    private sealed class Finger
    {
        public Point Start, Last;
        public DateTime T0;
        public bool Moved;
        public string Zone = "";
        public double WheelAccX, WheelAccY;
    }

    private readonly Dictionary<int, Finger> _fingers = new();

    /// <summary>True while any touch is live (strip click-through).</summary>
    public static bool SessionActive => _liveTouches > 0;
    private static int _liveTouches;
    // Gesture state machine (notebook-pad semantics): touch alone never
    // presses. Pending = untouched decision; Moving = cursor only;
    // TapHold = second tap pressed (double or drag start);
    // TripleHold = third tap pressed (triple).
    private enum G { None, Pending, Moving, TapHold, TripleHold, Dragging }
    private G _g = G.None;
    private int _primaryId = -1;
    // Tap chain across presses: 1 after a tap, 2 after a double. A third
    // quick press enters TripleHold instead of a fresh Pending.
    private int _tapChain;
    // Deferred single (TouchScript tap rule): a quick lift waits out
    // the judge window (TapJudgeMs setting) instead of firing. The next
    // press claims it first (double flushes, drag/hold flush, rolling
    // two-finger drops it); expiry fires the single.
    private System.Windows.Threading.DispatcherTimer? _deferTimer;
    private string _deferAct = "";
    private int _deferX, _deferY;
    // _asyncTwo = rolling two-finger (no overlap): a quick re-press
    // FAR from the last tap joins it; its lift fires TwoFingerTap.
    private bool _asyncTwo;
    // Two-contact session (single-contact flows never see these set):
    // _twoSeen = a second contact landed during this press;
    // _twoOk = every lift so far quick + still (gates the tap);
    // _twoMaxNet/_twoVX/_twoVY = largest joint travel + its vector;
    // _twoSpoiled = a third contact landed (silent, no gesture).
    // _twoT0 = session start (the join): tap-slowness is judged on the
    // PAIR's age, not each finger's total press (a finger held long
    // before the partner lands is still a live two-finger session).
    private bool _twoSeen;
    private bool _twoOk = true;
    private double _twoMaxNet;
    private double _twoVX, _twoVY;
    private bool _twoSpoiled;
    private long _twoT0;
    // Live two-finger scroll: centroid wheel accumulators (same notch
    // size as the scroll zone) + trailing-200ms travel (fling test).
    private double _twoWheelX, _twoWheelY;
    private long _twoSegT;
    private double _twoSegNet;
    // _twoScrolled = a wheel notch went out this session: the lift
    // stays silent (no tap/swipe after a scroll), far pair or not.
    private bool _twoScrolled;
    // Close-pair fallback (never opened a session): _wasTwo = two
    // contacts overlapped this press; _twoOverlap = overlapped ms
    // (flicker reads tiny and falls back to single).
    private bool _wasTwo;
    private long _twoJoinT;
    private long _twoOverlap;
    // _twoFired = tap already went out at the first lift (partner lift
    // stays silent). _twoDD = pair separation at join (same-blob
    // flicker reads ~0 and must never tap).
    private bool _twoFired;
    private double _twoDD;
    private int _gearId = -1;
    private int _xId = -1;
    private Point _xP;
    private DateTime _xT0;
    private Point _gearP;
    private DateTime _gearT0;
    private long _lastTapTick;
    private int _lastTapX, _lastTapY;
    private double _lastTapFX, _lastTapFY;
    // Tap window lives in settings (TapJudgeMs); no const here.
    // Hold-to-right timer (pad zone): still held LongPressMs -> R click.
    private System.Windows.Threading.DispatcherTimer? _holdTimer;
    private int _holdId = -1;
    private int _lastFreeX = int.MinValue, _lastFreeY = int.MinValue;
    private long _pollBlockUntil;
    private bool _cursorInPad;
    private string _lastWhat = "boot";
    private int _downOrigX = int.MinValue, _downOrigY = int.MinValue;

    /// <summary>Event position law: anchor cursor + finger travel (scaled).
    /// Never reads the live cursor (yanked/failing reads).</summary>
    private (int X, int Y) EventAt(Finger f, Point now)
    {
        if (_downOrigX == int.MinValue) return (int.MinValue, int.MinValue);
        double tx = (now.X - f.Start.X) * _dpi * _s.Speed;
        double ty = (now.Y - f.Start.Y) * _dpi * _s.Speed;
        return (_downOrigX + (int)tx, _downOrigY + (int)ty);
    }
    private bool _heldLeft;
    private EffectOverlay? _fx;

    private EffectOverlay Fx()
    {
        if (_fx == null)
        {
            _fx = new EffectOverlay();
            _fx.Owner = this;
        }
        // Effect tint follows settings live (render only). Unparsable
        // values fall back inside TintOrDefault.
        try
        {
            _fx.EffectTint = (Color)ColorConverter.ConvertFromString(
                _s.EffEffect(_layoutName));
        }
        catch { _fx.EffectTint = null; }
        return _fx;
    }
    private long _throughUntil;

    /// <summary>Mouse click-through while touches are live (plus a short
    /// tail so drops land on the target, not on us). Touch intake uses
    /// capture after DOWN, so it survives transparency; only a brand-new
    /// DOWN needs opacity (first contact finds us opaque).</summary>
    private bool MouseThroughActive =>
        _fingers.Count > 0 || Environment.TickCount64 < _throughUntil;

    // Chrome (title) touch/mouse drag state.
    private bool _chromeTouch;
    private int _chromeTouchId = -1;
    private bool _chromeMouse;
    private int _chromeMsgX, _chromeMsgY;
    private double _chromeGrabX, _chromeGrabY;

    // Touch resize state.
    private string? _resizeMode;
    private int _rsTouchId = -1;
    private double _rsW, _rsH, _rsX, _rsY, _rsL;
    private int _rsMsgX, _rsMsgY;
    private double _rsTX, _rsTY;
    private double _rsLastX, _rsLastY;   // last touch point, DIP
    private double _rsSX, _rsSY;         // low-passed point, DIP

    [DllImport("user32.dll")]
    private static extern int GetMessagePos();

    private static (int x, int y) MessagePos()
    {
        int v = GetMessagePos();
        return ((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
    }

    private System.Windows.Threading.DispatcherTimer? _actualTimer;

    public TouchPadWindow(AppSettings s)
    {
        _s = s;
        InitializeComponent();
        var src = PresentationSource.FromVisual(this);
        _dpi = src?.CompositionTarget?.TransformToDevice.M11 ?? 1;

        Surface.TouchDown += OnTouchDown;
        Surface.TouchMove += OnTouchMove;
        Surface.TouchUp += OnTouchUp;
        TitleBar.PreviewTouchDown += OnChromeTouchDown;
        TitleBar.PreviewTouchMove += OnChromeTouchMove;
        TitleBar.PreviewTouchUp += OnChromeTouchUp;
        TitleBar.PreviewMouseDown += OnChromeMouseDown;
        TitleBar.PreviewMouseMove += OnChromeMouseMove;
        TitleBar.PreviewMouseUp += OnChromeMouseUp;
        // Real-mouse pass-through: anything on the bare surface that our
        // own chrome does not consume is forwarded to the window below
        // (PostMessage, translated). Touch-promoted mouse (stylus) stays
        // swallowed: touches are ours, the mouse is everybody's.
        Surface.PreviewMouseDown += ForwardMouse;
        Surface.PreviewMouseMove += ForwardMouse;
        Surface.PreviewMouseUp += ForwardMouse;
        Surface.PreviewMouseWheel += ForwardMouse;
        SizeChanged += (_, _) => { if (_resizeMode == null && !_chromeTouch && !_chromeMouse) RenderZones(); UpdateChrome(); };
        LocationChanged += (_, _) => UpdateChrome();
        Loaded += (_, _) =>
        {
            var (ccx, ccy) = (SystemParameters.PrimaryScreenWidth / 2,
                SystemParameters.PrimaryScreenHeight / 2);
            _fakeX = ccx; _fakeY = ccy;
            RenderZones();
            UpdateChrome();
            RefreshDebugLabels();
            Log.Write($"INIT fake=({_fakeX:0},{_fakeY:0}) dpi={_dpi}");
            Core.MouseTap.Start();   // system-wide tap: [ours]/[ext] in the log
            Core.Out.Below = BelowDeliver;   // our output over us goes below
        };

        StatusLabel.MouseLeftButtonUp += (_, _) => OpenLog();
        // Gear: own touch/mouse tap (element-level, Handled).
        GearLabel.TouchDown += (_, e) =>
        {
            _gearId = e.TouchDevice.Id;
            _gearP = e.GetTouchPoint(Surface).Position;
            _gearT0 = DateTime.Now;
            e.Handled = true;
        };
        GearLabel.TouchUp += (_, e) =>
        {
            if (e.TouchDevice.Id != _gearId) return;
            _gearId = -1;
            var end = e.GetTouchPoint(Surface).Position;
            double ms = (DateTime.Now - _gearT0).TotalMilliseconds;
            if (ms < 400 && Math.Abs(end.X - _gearP.X) + Math.Abs(end.Y - _gearP.Y) <= 14)
                RequestSettings?.Invoke();
            e.Handled = true;
        };
        GearLabel.MouseLeftButtonUp += (_, e) => { RequestSettings?.Invoke(); e.Handled = true; };
        // X (next to gear): own touch/mouse tap, hides the pad.
        XLabel.TouchDown += (_, e) =>
        {
            _xId = e.TouchDevice.Id;
            _xP = e.GetTouchPoint(Surface).Position;
            _xT0 = DateTime.Now;
            e.Handled = true;
        };
        XLabel.TouchUp += (_, e) =>
        {
            if (e.TouchDevice.Id != _xId) return;
            _xId = -1;
            var end = e.GetTouchPoint(Surface).Position;
            double ms = (DateTime.Now - _xT0).TotalMilliseconds;
            if (ms < 400 && Math.Abs(end.X - _xP.X) + Math.Abs(end.Y - _xP.Y) <= 14)
            {
                Log.Write("X tap -> RequestHide");
                RequestHide?.Invoke();
            }
            e.Handled = true;
        };
        XLabel.MouseLeftButtonUp += (_, e) => { Log.Write("X click -> RequestHide"); RequestHide?.Invoke(); e.Handled = true; };
        ActualLabel.MouseLeftButtonUp += (_, _) => OpenLog();
        _actualTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _actualTimer.Tick += (_, _) =>
        {
            try
            {
                RefreshDpi();
                if (_fingers.Count == 0
                    && Environment.TickCount64 >= _pollBlockUntil)
                {
                    var (lx, ly) = Out.Cursor();
                    // Never adopt pad coords as the next touch start:
                    // that would make the next press land on ourselves.
                    if ((lx != 0 || ly != 0) && !Core.MouseTap.InPad(lx, ly))
                    { _lastFreeX = lx; _lastFreeY = ly; }
                }
                if (_fingers.Count == 0 && _lastFreeX != int.MinValue)
                {
                    // Pad-intrusion watch: log the moment the cursor gets
                    // onto our window, plus whatever we did last.
                    double pl = Left * _dpi, pt = Top * _dpi;
                    double pr = pl + Width * _dpi, pb = pt + Height * _dpi;
                    Core.MouseTap.SetPadRect((int)pl, (int)pt, (int)pr, (int)pb);
                    bool inside = _lastFreeX >= pl && _lastFreeX < pr
                        && _lastFreeY >= pt && _lastFreeY < pb;
                    if (inside && !_cursorInPad)
                        Log.Write($"CURSOR entered pad @({_lastFreeX},{_lastFreeY}) after {_lastWhat}");
                    else if (!inside && _cursorInPad)
                        Log.Write($"CURSOR left pad @({_lastFreeX},{_lastFreeY}) after {_lastWhat}");
                    _cursorInPad = inside;
                }
                string s = ShowEvents ? Out.ActualState() : "";
                if (ShowEvents)
                    ActualLabel.Text = s.Length > 90 ? s[^90..] : s;
            }
            catch { }
        };
        _actualTimer.Start();
        SourceInitialized += (_, _) =>
        {
            try
            {
                var hsrc = new System.Windows.Interop.WindowInteropHelper(this);
                var src = System.Windows.Interop.HwndSource.FromHwnd(hsrc.Handle);
                src?.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp,
                    ref bool handled) =>
                {
                    const int WM_NCHITTEST = 0x0084;
                    if (msg == WM_NCHITTEST && MouseThroughActive)
                    {
                        handled = true;
                        return new IntPtr(-1);
                    }
                    // Phase 1: observe OS gestures only (never handled).
                    if (msg == OsGestures.WM_GESTURE)
                    {
                        try { Log.Write(OsGestures.Describe(lp)); } catch { }
                        return IntPtr.Zero;
                    }
                    if (msg == OsGestures.WM_GESTURENOTIFY)
                    {
                        Log.Write($"OSGESTURE NOTIFY wp=0x{wp.ToInt64():X} lp=0x{lp.ToInt64():X}");
                        try { OsGestures.Enable(hwnd, "notify-retry"); } catch { }
                        return IntPtr.Zero;
                    }
                    return IntPtr.Zero;
                });
                try { OsGestures.Enable(hsrc.Handle, "init"); } catch { }
            }
            catch { }
        };
    }

    private void OpenLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Log.Path,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private void Status(string msg)
    {
        if (!ShowEvents) return;
        StatusLabel.Text = msg.Length > 90 ? msg[^90..] : msg;
    }

    /// <summary>DPI factor with sanity clamp (a null transform or a
    /// virtualized 1.0 would scale drags wrong).</summary>
    private void RefreshDpi()
    {
        try
        {
            var src = PresentationSource.FromVisual(this);
            double f = src?.CompositionTarget?.TransformToDevice.M11 ?? 0;
            if (f >= 0.5 && f <= 4) _dpi = f;
        }
        catch { }
    }

    private void UpdateChrome()
    {
        try
        {
            if (_chromeTouch || _chromeMouse || _resizeMode != null) return;
            ChromeLabel.Text =
                $"rawpad {_s.Speed:0.0}x @({Left:0},{Top:0}) {ActualWidth:0}x{ActualHeight:0} dpi={_dpi:0.00}";
        }
        catch { }
    }

    // ---------------- cs app compatibility (replaces PadWindow) ----------

    public event Action? PressedAnywhere;
    public event Action? RequestSettings;
    public event Action? RequestHide;
    public event Action? RequestAssist;
    public event Action? RequestFullscreenToggle;

    private Core.Layout? _layout;
    private string _layoutName = "floatpad";
    private GestureMap ActiveMap() => _s.ActiveGestures(_layoutName);

    /// <summary>Called by the app on layout switch: gesture family +
    /// tile overlay refresh. Hit-testing stays cs2 zones.</summary>
    public void SetLayout(Core.Layout layout)
    {
        _layout = layout;
        _layoutName = layout?.Name ?? "floatpad";
        RenderZones();
    }

    /// <summary>Applies AppSettings.DebugLabels to the bottom labels
    /// (called at load and on every settings apply).</summary>
    public void RefreshDebugLabels()
    {
        var v = ShowEvents ? Visibility.Visible : Visibility.Hidden;
        ZoneLabel.Visibility = v;
        StatusLabel.Visibility = v;
        ActualLabel.Visibility = v;
    }

    /// <summary>Tile color for a role: settings Eff* wins; anything
    /// unparsable (including "없음") falls back to ZonePalette.
    /// Render only - no event behavior.</summary>
    private string RoleColor(string role)
    {
        try
        {
            string v = role switch
            {
                "left" => _s.EffZoneLeft(_layoutName),
                "right" => _s.EffZoneRight(_layoutName),
                "wheel" => _s.EffZoneWheel(_layoutName),
                "pad" => _s.EffZonePad(_layoutName),
                _ => "",
            };
            if (string.IsNullOrWhiteSpace(v)) return Core.ZonePalette.For(role);
            if (v.Equals("none", StringComparison.OrdinalIgnoreCase)
                || v == "없음") return Core.ZonePalette.For(role);
            var _ = (Color)ColorConverter.ConvertFromString(v);
            return v;
        }
        catch { return Core.ZonePalette.For(role); }
    }

    /// <summary>Draws the active preset tiles (visual linkage).</summary>
    private void RenderTiles()
    {        try
        {
            if (_layout == null) return;
            var (w, h) = TileArea();
            // Background kinds first (pad tiles cover everything).
            var ordered = _layout.Tiles
                .OrderBy(t => t.Kind is "pad" or "padframe" or "blank" ? 0 : 1);
            foreach (var t in ordered)
            {
                double tx = t.X / 100 * w, ty = t.Y / 100 * h;
                double tw = t.W / 100 * w, th = t.H / 100 * h;
                // Role colors: settings Eff* wins, ZonePalette is the
                // fallback (render only - no event behavior here).
                string role;
                string color;
                if (t.Kind == "lbtn"
                    || (t.Kind == "click" && t.ClickButton.Equals("left", StringComparison.OrdinalIgnoreCase))) role = "left";
                else if (t.Kind == "rbtn"
                    || (t.Kind == "click" && t.ClickButton.Equals("right", StringComparison.OrdinalIgnoreCase))) role = "right";
                else if (t.Kind == "wheel") role = "wheel";
                else if (t.Kind == "drag") role = "drag";
                else if (t.Kind == "key" || t.Kind.StartsWith("vk_")
                    || t.Kind.Length == 1) role = "key";
                else if (t.Kind is "pad" or "padframe") role = "pad";
                else role = "other";
                color = RoleColor(role);
                var r = new Rectangle
                {
                    Width = Math.Max(0, tw),
                    Height = Math.Max(0, th),
                    Fill = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(color)),
                    Stroke = Brushes.Gray,
                    StrokeThickness = 1,
                };
                Canvas.SetLeft(r, tx);
                Canvas.SetTop(r, ty + ChromeH);
                Zones.Children.Add(r);
                string label = t.Kind.StartsWith("vk_",
                    StringComparison.OrdinalIgnoreCase)
                    ? t.Kind[3..].ToUpperInvariant()
                    : t.Kind switch
                    {
                        "lbtn" => "L",
                        "rbtn" => "R",
                        "wheel" => "Wheel",
                        "menu" => "Menu",
                        "movegrip" => "<->",
                        "minimize" => "_",
                        _ => "",
                    };
                if (label.Length == 0) continue;
                var tb = new TextBlock
                {
                    Text = label,
                    Foreground = Brushes.WhiteSmoke,
                    FontSize = 9,
                };
                Canvas.SetLeft(tb, tx + 3);
                Canvas.SetTop(tb, ty + ChromeH + 2);
                Zones.Children.Add(tb);
            }
        }
        catch { }
    }

    /// <summary>cs2 engine uses the real cursor: fake-cursor API is inert.</summary>
    public void EnterPersistentFake() { }
    public void EmergencyRestore() { }
    public void CenterFake() { }
    public void ApplyCursorStyle() { }

    public void SyncWindowSize()
    {
        try
        {
            if (_s.PadWidth > 0) Width = _s.PadWidth;
            if (_s.PadHeight > 0) Height = _s.PadHeight;
        }
        catch { }
    }

    public void RefreshOpacity()
    {
        try
        {
            double op = _s.Pad(_layoutName).Opacity;
            if (op < 0) op = _s.Opacity;
            Opacity = Math.Clamp(op, 0.2, 1.0);
        }
        catch { }
    }

    public void RefreshChrome() => UpdateChrome();

    /// <summary>Fires a gesture-map mouse action. True if it clicked.
    /// Single pairs journal as one "click" (old ClickAt semantics).
    /// SwapButtons swaps left/right globally.</summary>
    /// <summary>Drops a waiting single (rolling two-finger replaces it;
    /// nothing went out).</summary>
    private void CancelDefer()
    {
        try
        {
            if (_deferTimer != null)
            {
                _deferTimer.Stop();
                _deferTimer = null;
            }
            _deferAct = "";
        }
        catch { }
    }

    /// <summary>Fires a waiting single now (the next press proved it
    /// stands alone: double-second, drag, hold, far re-press).
    /// No-op when nothing is armed.</summary>
    private void FireDefer()
    {
        try
        {
            if (_deferTimer == null) return;
            _deferTimer.Stop();
            _deferTimer = null;
            string act = _deferAct;
            int fx = _deferX, fy = _deferY;
            _deferAct = "";
            if (act == "") return;
            if (DoMapAction(act, fx, fy))
            {
                Log.Write($"TAP fired -> {act}");
                Fx().Flash(fx / _dpi, fy / _dpi);
                _lastWhat = "tap-click";
            }
        }
        catch { }
    }

    /// <summary>Arms a quick lift: the click goes out at the judge-window
    /// end (TapJudgeMs setting) unless the next press joins it first.</summary>
    private void ArmDefer(string act, int fx, int fy, long now)
    {
        try
        {
            CancelDefer();
            _deferAct = act;
            _deferX = fx; _deferY = fy;
            _lastTapTick = now;
            _tapChain = 1;
            _lastTapX = fx; _lastTapY = fy;
            _deferTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(_s.TapJudgeMs),
            };
            _deferTimer.Tick += (_, _) => FireDefer();
            _deferTimer.Start();
            Log.Write($"TAP deferred ({_s.TapJudgeMs}ms) -> {act}");
            _lastWhat = "tap-deferred";
        }
        catch { }
    }

    /// <summary>Actions a waiting single may hold (it actually fires).</summary>
    private static bool Mappable(string a) => a is "left_click"
        or "right_click" or "middle_click" or "double_click"
        or "triple_click" or "wheel_up" or "wheel_down"
        or "browser_back" or "browser_forward";

    private bool DoMapAction(string action, int x, int y)
    {
        if (_s.SwapButtons)
            action = action == "left_click" ? "right_click"
                : action == "right_click" ? "left_click" : action;
        switch (action)
        {
            case "left_click":
                Out.ClickAt(x, y, "left"); return true;
            case "right_click":
                Out.ClickAt(x, y, "right"); return true;
            case "middle_click":
                Out.ClickAt(x, y, "middle"); return true;
            case "double_click":
                Out.ClickAt(x, y, "left");
                Out.ClickAt(x, y, "left"); return true;
            case "triple_click":
                Out.ClickAt(x, y, "left");
                Out.ClickAt(x, y, "left");
                Out.ClickAt(x, y, "left"); return true;
            case "wheel_up":
            case "wheel_down":
            {
                int dir = action == "wheel_up" ? 1 : -1;
                if (_s.ScrollInvert) dir = -dir;
                for (int i = 0; i < 3; i++) Out.Wheel(dir * 120);
                return true;
            }
            case "browser_back":
                InputSim.TapKey(0xA6); return true;
            case "browser_forward":
                InputSim.TapKey(0xA7); return true;
            default: return false;
        }
    }

    /// <summary>Opens the two-contact session (tap/swipe ledger).
    /// Once per session (guarded): the join starts a fresh travel
    /// ledger, so pre-join wiggle and a stale vector from the last
    /// session never vote tap-vs-swipe after the join.</summary>
    private void BeginTwo(string why, Finger f, Finger? other, double dd)
    {
        if (_twoSeen) return;
        _twoSeen = true;
        _twoOk = true;
        _twoDD = dd;
        _twoMaxNet = 0;
        _twoVX = 0; _twoVY = 0;
        _twoT0 = Environment.TickCount64;
        _twoWheelX = 0; _twoWheelY = 0;
        _twoSegT = _twoT0; _twoSegNet = 0;
        f.Start = f.Last; f.Moved = false;
        if (other != null) { other.Start = other.Last; other.Moved = false; }
        Log.Write($"2FINGER begin d={dd:0} ({why})");
    }

    /// <summary>Close-pair two-finger tap (never opened a session):
    /// real overlap (100ms+, flicker reads tiny), both calm, final
    /// lift quick. True = consumed.</summary>
    private bool CloseTwoTap(Finger f, int fx, int fy, double ms)
    {
        if (!_wasTwo || _twoSeen || _twoScrolled) return false;
        if (!_s.TapToClick) return false;
        if (f.Zone != "pad" && f.Zone != "left-click" && f.Zone != "right-click") return false;
        if (!_twoOk || _twoOverlap < 40 || _twoDD < 18 || ms > _s.TapJudgeMs || f.Moved) return false;
        string tact = f.Zone == "right-click" ? "right_click" : ActiveMap().TwoFingerTap;
        if (DoMapAction(tact, fx, fy))
        {
            Log.Write($"2FINGER tap-close -> {tact} overlap={(int)_twoOverlap}ms");
            Fx().Flash(fx / _dpi, fy / _dpi);
            _lastWhat = "twofinger-tap";
        }
        else
        {
            Log.Write("2FINGER tap-close unmapped (silent)");
            _lastWhat = "two-tap-none";
        }
        _lastTapTick = 0;
        _tapChain = 0;
        return true;
    }

    /// <summary>Fires the two-finger tap at the FIRST lift (never waits
    /// for the partner): both contacts quick + calm, pair young, no
    /// travel yet. True = fired (partner lift goes silent).</summary>
    private bool PairTapFire(Finger f, int fx, int fy, double ms)
    {
        if (_twoFired || _twoScrolled) return false;
        if (!_wasTwo) return false;
        if (!_s.TapToClick) return false;
        if (f.Zone != "pad" && f.Zone != "left-click" && f.Zone != "right-click") return false;
        if (fx == int.MinValue) return false;
        if (_twoDD < 18) return false;
        if (f.Moved || ms > _s.TapJudgeMs) return false;
        if (Environment.TickCount64 - _twoJoinT > _s.TapJudgeMs) return false;
        if (_twoMaxNet > TapMoveDip) return false;
        foreach (var kv in _fingers)
        {
            if (kv.Value.Moved) return false;
            if ((DateTime.Now - kv.Value.T0).TotalMilliseconds > _s.TapJudgeMs) return false;
        }
        string tact = f.Zone == "right-click" ? "right_click" : ActiveMap().TwoFingerTap;
        if (DoMapAction(tact, fx, fy))
        {
            Log.Write($"2FINGER tap-first -> {tact}");
            Fx().Flash(fx / _dpi, fy / _dpi);
            _lastWhat = "twofinger-tap";
        }
        else
        {
            Log.Write("2FINGER tap-first unmapped (silent)");
            _lastWhat = "two-tap-none";
        }
        _twoFired = true;
        _lastTapTick = 0;
        _tapChain = 0;
        _g = G.None;
        _primaryId = -1;
        if (_heldLeft) { Out.Up("left"); _heldLeft = false; }
        return true;
    }

    /// <summary>Two-contact lift decision. True = consumed (caller skips
    /// its normal single-contact landing). Fires the mapped TwoFingerTap
    /// (quick + still, TapToClick-gated like taps) or a directional
    /// swipe (moved). Anything else (spoiled/slow) lands silent.
    /// Single-contact sessions never reach here (_twoSeen false).</summary>
    private bool TwoFingerUp(Finger f, int fx, int fy, double ms)
    {
        if (!_twoSeen) return false;
        try
        {
            if (_heldLeft) { Out.Up("left"); _heldLeft = false; }
            if (fx == int.MinValue) return false;
            bool moved = _twoMaxNet > TapMoveDip || f.Moved;
            // Slow = the PAIR's age (ms is the single finger's press,
            // which includes pre-join holding and must not judge the tap).
            if (_twoSpoiled || (!moved && (!_twoOk || Environment.TickCount64 - _twoT0 > _s.TapJudgeMs)))
            {
                Log.Write("2FINGER idle (silent)");
                _lastWhat = "two-idle";
                _lastTapTick = 0;
                _tapChain = 0;
                return true;
            }
            if (moved)
            {
                // Slow/extended travel already scrolled live above: only
                // a fast fling (120+ DIP in the trailing 200ms) also
                // fires the mapped swipe. A stop-then-lift reads no
                // travel (stale window expires), so it stays scroll.
                double recent = Environment.TickCount64 - _twoSegT > 200 ? 0 : _twoSegNet;
                if (recent < 120)
                {
                    Log.Write("2FINGER scroll (silent)");
                    _lastWhat = "two-scroll";
                    _lastTapTick = 0;
                    _tapChain = 0;
                    return true;
                }
                string act = "";
                double ax = Math.Abs(_twoVX), ay = Math.Abs(_twoVY);
                if (ax >= ay * 1.5) act = _twoVX > 0 ? "swipe_right" : "swipe_left";
                else if (ay > ax * 1.5) act = _twoVY > 0 ? "swipe_down" : "swipe_up";
                var map = ActiveMap();
                string mapped = act switch
                {
                    "swipe_up" => map.SwipeUp,
                    "swipe_down" => map.SwipeDown,
                    "swipe_left" => map.SwipeLeft,
                    "swipe_right" => map.SwipeRight,
                    _ => "none",
                };
                if (mapped == "none" || !DoMapAction(mapped, fx, fy))
                {
                    Log.Write($"2FINGER swipe {act} unmapped (silent)");
                    _lastWhat = "two-swipe-none";
                }
                else
                {
                    Log.Write($"2FINGER swipe {act} -> {mapped}");
                    Fx().Flash(fx / _dpi, fy / _dpi);
                    _lastWhat = "two-swipe";
                }
                _lastTapTick = 0;
                _tapChain = 0;
                return true;
            }
            if (!_s.TapToClick)
            {
                Log.Write("2FINGER tap off (silent)");
                _lastWhat = "two-idle";
                _lastTapTick = 0;
                _tapChain = 0;
                return true;
            }
            // Same zones as single taps (wheel/resizer stay silent).
            if (f.Zone != "pad" && f.Zone != "left-click" && f.Zone != "right-click")
            {
                Log.Write($"2FINGER tap zone={f.Zone} (silent)");
                _lastWhat = "two-idle";
                _lastTapTick = 0;
                _tapChain = 0;
                return true;
            }
            string tact = f.Zone == "right-click"
                ? "right_click" : ActiveMap().TwoFingerTap;
            if (DoMapAction(tact, fx, fy))
            {
                Log.Write($"2FINGER tap -> {tact}");
                Fx().Flash(fx / _dpi, fy / _dpi);
                _lastWhat = "twofinger-tap";
            }
            else
            {
                Log.Write("2FINGER tap unmapped (silent)");
                _lastWhat = "two-tap-none";
            }
            _lastTapTick = 0;
            _tapChain = 0;
            return true;
        }
        catch { return true; }
    }

    // ---------------- zones (tile space sits below the title bar,
    // so top tiles are never covered) -----------------------------------

    private (double w, double h) TileArea() =>
        (ActualWidth > 0 ? ActualWidth : Width,
         (ActualHeight > 0 ? ActualHeight : Height) - ChromeH);

    private static string ZoneAt(double x, double y, double w, double h)
    {
        if (y > h - GripHit
            && (x + (h - y) < GripHit || (w - x) + (h - y) < GripHit))
            return "resizer";
        double ty = y;
        if (x < w * 0.5 && ty < h * 0.2) return "left-click";
        if (x >= w * 0.5 && ty < h * 0.2) return "right-click";
        if (x >= w * 0.8 && ty >= h * 0.2) return "wheel";
        return "pad";
    }

    private string ZoneOf(Point p)
    {
        var (w, h) = TileArea();
        if (w <= 0 || h <= 0) return "pad";
        // Layout tiles first (what you see is what you hit): last button
        // tile wins in file order, mirroring PresetParser.HitTest.
        if (_layout != null)
        {
            double fx = p.X / w, fy = (p.Y - ChromeH) / h;
            string? hit = null;
            foreach (var t in _layout.Tiles)
            {
                if (fx < t.X / 100 || fx > (t.X + t.W) / 100
                    || fy < t.Y / 100 || fy > (t.Y + t.H) / 100) continue;
                if (t.Kind == "lbtn"
                    || (t.Kind == "click"
                        && t.ClickButton.Equals("left", StringComparison.OrdinalIgnoreCase)))
                    hit = "left-click";
                else if (t.Kind == "rbtn"
                    || (t.Kind == "click"
                        && t.ClickButton.Equals("right", StringComparison.OrdinalIgnoreCase)))
                    hit = "right-click";
                else if (t.Kind == "wheel") hit = "wheel";
            }
            if (hit != null) return hit;
        }
        return ZoneAt(p.X, p.Y - ChromeH, w, h);
    }

    private void RenderZones()
    {
        try
        {
            Zones.Children.Clear();
            var (w, h) = TileArea();
            void rect(double x, double y, double rw, double rh,
                string color, string label)
            {
                var r = new Rectangle
                {
                    Width = Math.Max(0, rw),
                    Height = Math.Max(0, rh),
                    Fill = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(color)),
                    Stroke = Brushes.Gray,
                    StrokeThickness = 1,
                };
                Canvas.SetLeft(r, x);
                Canvas.SetTop(r, y + ChromeH);
                Zones.Children.Add(r);
                var t = new TextBlock
                {
                    Text = label,
                    Foreground = Brushes.WhiteSmoke,
                    FontSize = 10,
                };
                Canvas.SetLeft(t, x + 4);
                Canvas.SetTop(t, y + ChromeH + 4);
                Zones.Children.Add(t);
            }
            if (_layout != null)
            {
                // Layout set: preset tiles replace the cs2 zone overlay
                // (no double-draw). Hit-testing stays cs2 zones. Missing
                // button/wheel tiles get their zone guides drawn, so
                // layouts like fullscreen still show left/right/wheel.
                RenderTiles();
                bool hasL = false, hasR = false, hasW = false;
                foreach (var t in _layout.Tiles)
                {
                    if (t.Kind == "lbtn"
                        || (t.Kind == "click" && t.ClickButton == "left")) hasL = true;
                    else if (t.Kind == "rbtn"
                        || (t.Kind == "click" && t.ClickButton == "right")) hasR = true;
                    else if (t.Kind == "wheel") hasW = true;
                }
                if (!hasL) rect(0, 0, w * 0.5, h * 0.2, RoleColor("left"), "left");
                if (!hasR) rect(w * 0.5, 0, w * 0.5, h * 0.2, RoleColor("right"), "right");
                if (!hasW) rect(w * 0.8, h * 0.2, w * 0.2, h * 0.8, RoleColor("wheel"), "wheel");
            }
            else
            {
                rect(0, 0, w * 0.5, h * 0.2, RoleColor("left"), "left");
                rect(w * 0.5, 0, w * 0.5, h * 0.2, RoleColor("right"), "right");
                rect(w * 0.8, h * 0.2, w * 0.2, h * 0.8, RoleColor("wheel"), "wheel");
            }
            // Resize grips: faint filled triangles in the bottom corners
            // (hit-test matches shape, bigger legs).
            void tri(Point a, Point b, Point c)
            {
                var pg = new Polygon
                {
                    Points = new PointCollection { a, b, c },
                    Fill = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                    Stroke = Brushes.Gray,
                    StrokeThickness = 1,
                };
                Zones.Children.Add(pg);
            }
            double y0 = h + ChromeH;
            tri(new Point(0, y0 - GripZone), new Point(0, y0), new Point(GripZone, y0));
            tri(new Point(w, y0 - GripZone), new Point(w, y0), new Point(w - GripZone, y0));
        }
        catch { }
    }

    private int MergedCount()
    {
        var pts = _fingers.Values.Select(f => f.Last).ToList();
        int clusters = 0;
        var used = new bool[pts.Count];
        for (int i = 0; i < pts.Count; i++)
        {
            if (used[i]) continue;
            clusters++;
            for (int j = i + 1; j < pts.Count; j++)
            {
                if (!used[j] && Math.Abs(pts[j].X - pts[i].X)
                    + Math.Abs(pts[j].Y - pts[i].Y) <= MergeDip)
                    used[j] = true;
            }
        }
        return clusters;
    }

    // ---------------- touch intake (raw only, no gestures) --------------

    private void OnTouchDown(object sender, TouchEventArgs e)
    {
        try
        {
            _liveTouches++;
            var p = e.GetTouchPoint(Surface).Position;
            string zone = ZoneOf(p);
            PressedAnywhere?.Invoke();
            if (zone == "resizer")
            {
                if (_resizeMode != null)
                {
                    // Mid-resize re-touch: never re-anchor the start point
                    // (that jumps the window). Swallow it.
                    Log.Write($"TOUCHDOWN id={e.TouchDevice.Id} resizer-busy");
                    e.Handled = true;
                    return;
                }
                var (w0, h0) = TileArea();
                string mode = p.X < w0 / 2 ? "left" : "right";
                _resizeMode = mode;
                _rsW = Width; _rsH = Height;
                _rsX = Left + p.X; _rsY = Top + p.Y; _rsL = Left;
            _rsTouchId = e.TouchDevice.Id;
            RefreshDpi();
            (_rsMsgX, _rsMsgY) = MessagePos();
                _rsTX = _rsTY = 0;
                _rsLastX = p.X; _rsLastY = p.Y;
                _rsSX = p.X; _rsSY = p.Y;
                Surface.CaptureTouch(e.TouchDevice);
                if (ShowEvents) ZoneLabel.Text = "zone: resizer";
                Log.Write($"TOUCHDOWN id={e.TouchDevice.Id} resizer->{mode} L={Left:0} W={Width:0}");
                e.Handled = true;
                return;
            }
            var f = new Finger
            {
                Start = p,
                Last = p,
                T0 = DateTime.Now,
                Zone = zone,
            };
            _fingers[e.TouchDevice.Id] = f;
            if (_fingers.Count == 1)
            {
                // Fresh press: single-contact state machine starts clean
                // (tap chain across presses lives in _tapChain instead).
                _twoSeen = false;
                _twoOk = true;
                _twoMaxNet = 0;
                _twoSpoiled = false;
                _twoScrolled = false;
                _wasTwo = false;
                _twoOverlap = 0;
                _twoJoinT = 0;
                _twoFired = false;
                _twoDD = 0;
                // Stale hold from a lost UP first (safety net).
                if (_heldLeft) { Out.Up("left"); _heldLeft = false; }
                _primaryId = e.TouchDevice.Id;
                // Keep moves/ups flowing when the finger leaves our window.
                Surface.CaptureTouch(e.TouchDevice);

                long now = Environment.TickCount64;
                double tdist = Math.Abs(p.X - _lastTapFX) + Math.Abs(p.Y - _lastTapFY);
                // Unified judge: a re-touch inside the action-judge time
                // after a tap chains. Chain 2 (after a double) arms the
                // third tap; anything else chains a double. A quick
                // re-press FAR from the last tap (no overlap, other
                // finger) joins it as rolling two-finger instead.
                bool inWin = _lastTapTick != 0
                    && now - _lastTapTick <= _s.TapJudgeMs;
                bool near = inWin && tdist <= SecondTapDip;
                bool asyncTwo = inWin && !near && tdist <= 250 && _tapChain == 1;

                if (near && _tapChain >= 2)
                {
                    // Third tap: quick lift fires TripleTap, moving drags.
                    _downOrigX = _lastTapX; _downOrigY = _lastTapY;
                    Out.SetAnchor(_downOrigX, _downOrigY);
                    _g = G.TripleHold;
                    Fx().Flash(_downOrigX / _dpi, _downOrigY / _dpi);
                    Status($"triplehold @({_downOrigX},{_downOrigY})");
                    _lastWhat = "triplehold";
                }
                else if (near || asyncTwo)
                {
                    // Second tap: don't press yet. Quick lift completes
                    // the double-click, moving starts a drag. (Pressing
                    // here turned every quick re-touch into a drag and
                    // every tap pair into an extra double.)
                    // Rolling two-finger drops the waiting single (it
                    // joins this press) and fires TwoFingerTap at lift.
                    if (asyncTwo)
                    {
                        CancelDefer();
                        _asyncTwo = true;
                    }
                    _downOrigX = _lastTapX; _downOrigY = _lastTapY;
                    Out.SetAnchor(_downOrigX, _downOrigY);
                    _g = G.TapHold;
                    Fx().Flash(_downOrigX / _dpi, _downOrigY / _dpi);
                    Status(asyncTwo ? $"asynctwo @({_downOrigX},{_downOrigY})" : $"taphold @({_downOrigX},{_downOrigY})");
                    _lastWhat = asyncTwo ? "asynctwo" : "taphold";
                }
                else
                {
                    // Fresh press: the waiting single stands alone
                    // (flush it now), chain restarts.
                    FireDefer();
                    _asyncTwo = false;
                    // Fresh press: chain restarts (single tap ahead).
                    _tapChain = 0;
                    // Anchor = live cursor, not the polled cache: the cache
                    // freezes (poll-block + pad-skip + self-reinforcing
                    // release writes), teleporting every tap to a stale
                    // "previous anchor". Live is trustworthy now that
                    // promotion is swallowed globally.
                    var (liveX, liveY) = Out.Cursor();
                    if (liveX != 0 || liveY != 0)
                    { _downOrigX = liveX; _downOrigY = liveY; }
                    else
                    {
                        _downOrigX = _lastFreeX; _downOrigY = _lastFreeY;
                        if (_downOrigX == int.MinValue)
                        {
                            var (gx, gy) = Out.Logical();
                            _downOrigX = gx; _downOrigY = gy;
                        }
                    }
                    Out.SetAnchor(_downOrigX, _downOrigY);
                    // No cursor output on touch-down: promotion is swallowed
                    // globally, and snatching the cursor here is what threw
                    // the physical mouse off. The anchor below follows the
                    // live cursor on first move instead.
                    Fx().Flash(_downOrigX / _dpi, _downOrigY / _dpi);
                    _g = G.Pending;
                    Status($"pending @({_downOrigX},{_downOrigY})");
                    _lastWhat = "pending";
                }
                // Hold arms right-click (pad/left-click zones): still held
                // LongPressMs with no real movement -> R down+up at press pos.
                _holdTimer?.Stop();
                _holdId = -1;
                if (zone == "pad" || zone == "left-click")
                {
                    _holdId = e.TouchDevice.Id;
                    _holdTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(_s.LongPressMs),
                    };
                    _holdTimer.Tick += (_, _) =>
                    {
                        try
                        {
                            _holdTimer?.Stop();
                            _holdTimer = null;
                            if (_g != G.Pending) return;
                            if (!_fingers.TryGetValue(_holdId, out var hf)) return;
                            if (hf.Moved) return;
                            // Ownership (TouchScript rule): the moment a pair is
                            // live, the single-press pipeline is dead - any
                            // live second contact kills the hold, far or not.
                            if (_twoSeen || _fingers.Count >= 2) return;
                            // The waiting single stands alone (flush it):
                            // tap, then hold.
                            FireDefer();
                            DoMapAction(ActiveMap().LongPress, _downOrigX, _downOrigY);
                            Status($"hold right @({_downOrigX},{_downOrigY})");
                            _lastWhat = "hold-right";
                            _g = G.None;
                            _lastTapTick = 0;
                            _tapChain = 0;
                        }
                        catch { }
                    };
                    _holdTimer.Start();
                }
            }
            // Second live contact: two-contact session starts. A partner
            // landing within 40 DIP of the first is a split-blob ghost,
            // not a finger (measured whipsaw) - ignored, stays single.
            // Third contact spoils the gesture (silent, no output).
            if (_fingers.Count == 2)
            {
                _wasTwo = true;
                _twoJoinT = Environment.TickCount64;
                // Decisive switch: the single press dies HERE (hold timer
                // stops now, moves consume on live-n below) - the gesture
                // owns the session from this contact on.
                _holdTimer?.Stop();
                _holdTimer = null;
                _holdId = -1;
                Finger? other = null;
                foreach (var kv in _fingers)
                    if (kv.Key != e.TouchDevice.Id) { other = kv.Value; break; }
                double dd = other == null ? 999 :
                    Math.Abs(p.X - other.Start.X) + Math.Abs(p.Y - other.Start.Y);
                if (dd < 40)
                {
                    _twoDD = dd;
                    Log.Write($"2FINGER ghost-near d={dd:0} (scrolls, no tap ledger)");
                }
                else
                {
                    BeginTwo("down", f, other, dd);
                }
            }
            else if (_fingers.Count >= 3 && _twoSeen)
            {
                _twoSpoiled = true;
                Log.Write("2FINGER third contact (spoiled, silent)");
            }
            int rawN = _fingers.Count, mgN = MergedCount();
            Status($"touch {e.TouchDevice.Id} {zone} n={(rawN == mgN ? rawN.ToString() : mgN + "[raw" + rawN + "]")}");
            if (ShowEvents) ZoneLabel.Text = "zone: " + zone;
            _lastWhat = $"touchdown {zone}";
            var (cx, cy) = Out.Cursor();
            var tb = e.GetTouchPoint(Surface).Bounds;
            Log.Write($"TOUCHDOWN id={e.TouchDevice.Id} @{p.X:0},{p.Y:0} zone={zone} n={_fingers.Count} cursor=({cx},{cy}) blob={tb.Width:0.0}x{tb.Height:0.0}");
            e.Handled = true;
        }
        catch { }
    }

    private void OnTouchMove(object sender, TouchEventArgs e)
    {
        try
        {
            if (_resizeMode != null && e.TouchDevice.Id == _rsTouchId)
            {
                // Touch deltas directly (DIP): message positions don't track
                // fingers, so the old MessagePos math always read zero.
                // Low-pass the raw point: the device alternates between
                // split blobs (~+-24 DIP whipsaw in X), which shakes the
                // window faster than any quantization can hide.
                var rp = e.GetTouchPoint(Surface).Position;
                _rsSX += (rp.X - _rsSX) * 0.35;
                _rsSY += (rp.Y - _rsSY) * 0.35;
                double dx = _rsSX - _rsLastX, dy = _rsSY - _rsLastY;
                _rsLastX = _rsSX; _rsLastY = _rsSY;
                if (Math.Abs(dx) > 40 || Math.Abs(dy) > 40)
                {
                    e.Handled = true;
                    return;
                }
                _rsTX += dx; _rsTY += dy;
                // Apply in 2-DIP steps: raw 70Hz jitter otherwise shakes
                // the window, most visibly on the moving left edge.
                double ax = Math.Round(_rsTX / 2) * 2, ay = Math.Round(_rsTY / 2) * 2;
                ApplyResize(_rsX + ax, _rsY + ay);
                e.Handled = true;
                return;
            }
            if (!_fingers.TryGetValue(e.TouchDevice.Id, out var f)) return;
            var p = e.GetTouchPoint(Surface).Position;
            double dx2 = p.X - f.Last.X, dy2 = p.Y - f.Last.Y;
            f.Last = p;
            double net = Math.Abs(p.X - f.Start.X) + Math.Abs(p.Y - f.Start.Y);
            if (net > TapMoveDip) f.Moved = true;
            // Joint travel for two-contact sessions (any finger): the
            // biggest net + its vector classifies the swipe at lift.
            if (_twoSeen && net > _twoMaxNet)
            {
                _twoMaxNet = net;
                _twoVX = p.X - f.Start.X;
                _twoVY = p.Y - f.Start.Y;
            }
            // n == 2 drives wheel here (same detection, different action):
            // any two live contacts scroll, far apart or not. The far-pair
            // session (tap/swipe ledger) opens lazily when they spread.
            if (_fingers.Count >= 2)
            {
                if (!_twoSeen)
                {
                    Finger? o2 = null;
                    foreach (var kv in _fingers)
                        if (kv.Key != e.TouchDevice.Id) { o2 = kv.Value; break; }
                    double dd2 = o2 == null ? 999 :
                        Math.Abs(p.X - o2.Last.X) + Math.Abs(p.Y - o2.Last.Y);
                    if (dd2 >= 40) BeginTwo("spread", f, o2, dd2);
                }
                long nowW = Environment.TickCount64;
                if (nowW - _twoSegT > 200) { _twoSegNet = 0; _twoSegT = nowW; }
                _twoSegNet += (Math.Abs(dx2) + Math.Abs(dy2)) / _fingers.Count;
                _twoWheelX += dx2 / _fingers.Count;
                _twoWheelY += dy2 / _fingers.Count;
                int notch = _s.ScrollInvert ? -120 : 120;
                while (Math.Abs(_twoWheelY) >= WheelDip)
                {
                    int s = Math.Sign(_twoWheelY);
                    _twoWheelY -= s * WheelDip;
                    Out.Wheel(s * notch);
                    _twoScrolled = true;
                }
                while (Math.Abs(_twoWheelX) >= WheelDip)
                {
                    int s = Math.Sign(_twoWheelX);
                    _twoWheelX -= s * WheelDip;
                    Out.HWheel(s * notch);
                    _twoScrolled = true;
                }
                _lastWhat = "two-scroll";
            }
            if (e.TouchDevice.Id == _primaryId)
            {
                // Two live contacts: no cursor, no drag - wheel already
                // streamed above, the lift decides tap vs fling-swipe.
                // Single-contact flows never enter here.
                if (_fingers.Count >= 2)
                {
                    _lastWhat = "twofinger-hold";
                    e.Handled = true;
                    return;
                }
                if (f.Zone == "wheel")
                {
                    // Scroll zone: sliding = wheel notches, never a press.
                    f.WheelAccX += dx2; f.WheelAccY += dy2;
                    int notch = _s.ScrollInvert ? -120 : 120;
                    while (Math.Abs(f.WheelAccY) >= WheelDip)
                    {
                        int s = Math.Sign(f.WheelAccY);
                        f.WheelAccY -= s * WheelDip;
                        Out.Wheel(s * notch);
                    }
                    while (Math.Abs(f.WheelAccX) >= WheelDip)
                    {
                        int s = Math.Sign(f.WheelAccX);
                        f.WheelAccX -= s * WheelDip;
                        Out.HWheel(s * notch);
                    }
                    _lastWhat = "wheel";
                }
                else
                {
                if (f.Moved && _g == G.Pending)
                {
                    // Moving starts here: rebase onto the live cursor so the
                    // first step continues from it (never a jump), and zero
                    // the deadzone travel already covered.
                    var (ax0, ay0) = Out.Cursor();
                    if (ax0 != 0 || ay0 != 0) { _downOrigX = ax0; _downOrigY = ay0; }
                    Out.SetAnchor(_downOrigX, _downOrigY);
                    f.Start = p;
                    _g = G.Moving;
                }
                if (f.Moved && (_g == G.TapHold || _g == G.TripleHold))
                {
                    // The waiting single stands alone (flush it): tap,
                    // then drag - same as firing on lift.
                    FireDefer();
                    _asyncTwo = false;
                    // Drag starts now: "drag" presses and holds (no extra
                    // click), "drag_hold" clicks first, then holds.
                    // Rebase travel so the deadzone doesn't jump it.
                    string sh = ActiveMap().SecondHold;
                    string dbtn = _s.SwapButtons ? "right" : "left";
                    if (sh == "drag_hold")
                    {
                        Out.DownAt(_downOrigX, _downOrigY, dbtn);
                        Out.UpAt(_downOrigX, _downOrigY, dbtn);
                    }
                    if (sh == "drag" || sh == "drag_hold")
                    {
                        Out.DownAt(_downOrigX, _downOrigY, dbtn);
                        _heldLeft = true;
                    }
                    f.Start = p;
                    _g = G.Dragging;
                    _lastWhat = "drag-start";
                }
                if (_g == G.Moving || _g == G.Dragging)
                {
                    var (mx2, my2) = EventAt(f, p);
                    if (mx2 != int.MinValue)
                    {
                        // Skip no-op placements (stationary jitter): fewer
                        // inputs, no flicker. Self-heals when something else
                        // moved the cursor (target != live).
                        var (lx, ly) = Out.Cursor();
                        if (Math.Abs(mx2 - lx) + Math.Abs(my2 - ly) > 2)
                        {
                            Out.PlaceAt(mx2, my2);   // held button => drag
                            Fx().Spin(mx2 / _dpi, my2 / _dpi);
                        }
                        _lastWhat = _g == G.Dragging ? "drag-move" : "move";
                    }
                }
                }
            }
            // Model only (logs context, no output): press-start + travel.
            _fakeX = f.Start.X + (p.X - f.Start.X) * _dpi * _s.Speed;
            _fakeY = f.Start.Y + (p.Y - f.Start.Y) * _dpi * _s.Speed;
            // Zone is judged once at DOWN and kept until release:
            // drifting into another area mid-touch must not re-route
            // the gesture (no click<->wheel flips, no hold drops).
            e.Handled = true;
        }
        catch { }
    }

    private void OnTouchUp(object sender, TouchEventArgs e)
    {
        try
        {
            if (_resizeMode != null && e.TouchDevice.Id == _rsTouchId)
            {
                Surface.ReleaseTouchCapture(e.TouchDevice);
                EndResize();
                _liveTouches--;
                e.Handled = true;
                return;
            }
            if (!_fingers.TryGetValue(e.TouchDevice.Id, out var f))
            {
                Log.Write($"UP-IN id={e.TouchDevice.Id} UNKNOWN");
                _liveTouches--;
                e.Handled = true;
                return;
            }
            _fingers.Remove(e.TouchDevice.Id);
            long nowU = Environment.TickCount64;
            // Overlap ledger: 2 -> 1 banks the overlapped ms (re-joins
            // accumulate). Flicker banks almost nothing.
            if (_fingers.Count == 1 && _twoJoinT != 0)
            { _twoOverlap += nowU - _twoJoinT; _twoJoinT = 0; }
            if (e.TouchDevice.Id == _holdId)
            {
                _holdTimer?.Stop();
                _holdTimer = null;
                _holdId = -1;
            }
            double ms = (DateTime.Now - f.T0).TotalMilliseconds;
            var end = e.GetTouchPoint(Surface).Position;
            if (Math.Abs(end.X - f.Start.X) + Math.Abs(end.Y - f.Start.Y) > TapMoveDip)
                f.Moved = true;
            var (ux, uy) = Out.Cursor();
            Log.Write($"TOUCHUP id={e.TouchDevice.Id} {(f.Moved ? "moved" : "still")} {(int)ms}ms zone={f.Zone} n={_fingers.Count} cursor=({ux},{uy})");
            Status($"up {(int)ms}ms {(f.Moved ? "moved" : "still")}");
            _lastWhat = $"touchup {(f.Moved ? "moved" : "still")}";
            // State-machine landing (last finger up; primary gone with
            // others still down cancels the hold silently).
            if (e.TouchDevice.Id == _primaryId)
            {
                if (_fingers.Count > 0)
                {
                    if (_heldLeft) { Out.Up("left"); _heldLeft = false; }
                    // First lift decides now (never waits for the partner):
                    // a quick calm pair taps immediately.
                    int pfx, pfy;
                    if (!f.Moved && _downOrigX != int.MinValue)
                    { pfx = _downOrigX; pfy = _downOrigY; }
                    else (pfx, pfy) = EventAt(f, end);
                    PairTapFire(f, pfx, pfy, ms);
                    // Partner lift during a two-contact session: a slow or
                    // moved lift spoils only the tap (a swipe can complete).
                    // Slow = the PAIR's age (since the join), not this
                    // finger's total press.
                    if (_twoSeen && (f.Moved || Environment.TickCount64 - _twoT0 > _s.TapJudgeMs)) _twoOk = false;
                    if (_wasTwo && f.Moved) _twoOk = false;
                    _g = G.None;
                    _primaryId = -1;
                    _lastTapTick = 0;
                }
                else
                {
                    long now = Environment.TickCount64;
                    int fx, fy;
                    if (!f.Moved && _downOrigX != int.MinValue)
                    { fx = _downOrigX; fy = _downOrigY; }
                    else (fx, fy) = EventAt(f, end);
                    Fx().StopSpin();
                    if (fx == int.MinValue)
                    {
                        if (_heldLeft) { Out.Up("left"); _heldLeft = false; }
                    }
                    else if (_twoFired) { _lastWhat = "two-fired"; }
                    else if (_twoScrolled) { Log.Write("2FINGER scroll-end (silent)"); _lastWhat = "two-scroll-end"; }
                    else if (TwoFingerUp(f, fx, fy, ms)) { }
                    else if (CloseTwoTap(f, fx, fy, ms)) { }
                    else if (f.Zone == "wheel")
                    {
                        // Scroll only: the cursor never moved, keep it so.
                        _lastWhat = "wheel-end";
                    }
                    else switch (_g)
                    {
                        case G.Pending:                       // tap
                            // "Pad tap = click" gates the pad zone only:
                            // left/right zones always click.
                            if (ms <= _s.TapJudgeMs && (f.Zone == "left-click"
                                || f.Zone == "right-click"
                                || (f.Zone == "pad" && _s.TapToClick)))
                            {
                                string act = f.Zone == "right-click"
                                    ? "right_click" : ActiveMap().Tap;
                                // Detection wait (TapJudgeMs setting): the click
                                // goes out at the window end unless the next
                                // press joins it (double/drag/hold flush,
                                // rolling two-finger drops). Unmapped stays
                                // silent and chainless.
                                if (!Mappable(act))
                                { _lastTapTick = 0; _tapChain = 0; _lastWhat = "tap-none"; break; }
                                Fx().Flash(fx / _dpi, fy / _dpi);
                                // Roll survey: consecutive quick taps log gap
                                // + separation, so device tests reveal the
                                // natural rolling rhythm.
                                long gap0 = _lastTapTick == 0 ? -1 : now - _lastTapTick;
                                if (gap0 >= 0 && gap0 <= 1000)
                                {
                                    double rd0 = Math.Abs(fx - _lastTapX) + Math.Abs(fy - _lastTapY);
                                    Log.Write($"ROLL? gap={gap0}ms d={rd0:0} chain={_tapChain}");
                                }
                                _lastTapFX = f.Start.X; _lastTapFY = f.Start.Y;
                                ArmDefer(act, fx, fy, now);
                            }
                            else { _lastTapTick = 0; _tapChain = 0; _lastWhat = "long-idle"; }
                            break;
                        case G.Moving:                        // cursor only
                            Out.PlaceAt(fx, fy);
                            _lastTapTick = 0;
                            _tapChain = 0;
                            _lastWhat = "move-end";
                            break;
                        case G.TapHold:                       // 2nd tap lift
                            // Flush first: the waiting single stands alone
                            // (double = its pair + one more here). Rolling
                            // two-finger skips the flush (dropped at DOWN)
                            // and fires TwoFingerTap instead.
                            FireDefer();
                            if (_asyncTwo)
                            {
                                _asyncTwo = false;
                                if (ms <= _s.TapJudgeMs && (f.Zone == "left-click"
                                    || f.Zone == "right-click"
                                    || (f.Zone == "pad" && _s.TapToClick)))
                                {
                                    string tact2 = f.Zone == "right-click"
                                        ? "right_click" : ActiveMap().TwoFingerTap;
                                    if (DoMapAction(tact2, fx, fy))
                                    {
                                        Log.Write($"2FINGER tap-rolling -> {tact2}");
                                        Fx().Flash(fx / _dpi, fy / _dpi);
                                        _lastWhat = "twofinger-tap";
                                    }
                                    else
                                    {
                                        Log.Write("2FINGER tap-rolling unmapped (silent)");
                                        _lastWhat = "two-tap-none";
                                    }
                                }
                                else { _lastWhat = "async-idle"; }
                                _lastTapTick = 0;
                                _tapChain = 0;
                                break;
                            }
                            // Right zone stays right (right-double pairs).
                            // "double_click" fires one pair here (the first
                            // pair already went out), completing the double.
                            string dact = ActiveMap().DoubleTap;
                            if (f.Zone == "right-click" && dact != "none")
                                dact = "right_click";
                            else if (dact == "double_click") dact = "left_click";
                            DoMapAction(dact, fx, fy);
                            Fx().Flash(fx / _dpi, fy / _dpi);
                            _lastTapTick = now;
                            _tapChain = 2;
                            _lastTapX = fx; _lastTapY = fy;
                            _lastTapFX = f.Start.X; _lastTapFY = f.Start.Y;
                            _lastWhat = "second-tap";
                            break;
                        case G.TripleHold:                    // 3rd tap lift
                            // Same pairing rule as the double: the first two
                            // pairs already went out, one more completes
                            // the triple. A fresh "triple_click" still
                            // fires three pairs via DoMapAction.
                            string tact = ActiveMap().TripleTap;
                            if (f.Zone == "right-click" && tact != "none")
                                tact = "right_click";
                            else if (tact == "triple_click") tact = "left_click";
                            DoMapAction(tact, fx, fy);
                            Fx().Flash(fx / _dpi, fy / _dpi);
                            _lastTapTick = 0;
                            _tapChain = 0;
                            _lastWhat = "third-tap";
                            break;
                        case G.Dragging:                      // drag-drop
                            Out.UpAt(fx, fy, "left");
                            Fx().Flash(fx / _dpi, fy / _dpi);
                            _lastTapTick = 0;
                            _tapChain = 0;
                            _lastWhat = "drag-drop";
                            // Real drops land through our topmost window.
                            _throughUntil = Environment.TickCount64 + 300;
                            break;
                        default:
                            _lastTapTick = 0;
                            _tapChain = 0;
                            break;
                    }
                    _heldLeft = false;
                    _g = G.None;
                    _primaryId = -1;
                    if (fx != int.MinValue)
                    {
                        // Next touch starts where the cursor is now (never
                        // a stale anchor) - unless that's on our pad, which
                        // would anchor the whole next session (and its drags)
                        // at the touch area. Wheel never moved the cursor,
                        // so it adopts nothing. The yank window stays
                        // poll-blocked regardless.
                        if (f.Zone != "wheel" && !Core.MouseTap.InPad(fx, fy))
                        { _lastFreeX = fx; _lastFreeY = fy; }
                        _pollBlockUntil = now + 400;
                    }
                // Yank window: the OS pulls to the contact up to a few
                // hundred ms AFTER lift; hold the release point briefly
                // (stop early when stable or a new touch lands).
                if (fx != int.MinValue)
                {
                    // Defend where the cursor really is (snapshot now):
                    // wheel sessions never moved it, so defending fx
                    // would teleport the cursor to the finger path.
                    var (sx0, sy0) = Out.Cursor();
                    int hx = (sx0 != 0 || sy0 != 0) ? sx0 : fx;
                    int hy = (sx0 != 0 || sy0 != 0) ? sy0 : fy;
                    int tries = 5;
                    var holdT = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(40),
                    };
                    holdT.Tick += (_, _) =>
                    {
                        try
                        {
                            if (--tries <= 0) { holdT.Stop(); return; }
                            if (_fingers.Count > 0) { holdT.Stop(); return; }
                            var (hx2, hy2) = Out.Logical();
                            if (Math.Abs(hx2 - hx) + Math.Abs(hy2 - hy) <= 4)
                            {
                                holdT.Stop();
                                return;
                            }
                            Out.PlaceAt(hx, hy);
                            _lastWhat = "yank-hold";
                        }
                        catch { holdT.Stop(); }
                    };
                    holdT.Start();
                }
                }
            }
            else if (_twoSeen || _wasTwo)
            {
                // Partner lift (not the primary): a slow or moved lift
                // spoils only the tap (a swipe can still complete).
                // Slow = the PAIR's age (since the join).
                if (_twoSeen && (f.Moved || Environment.TickCount64 - _twoT0 > _s.TapJudgeMs)) _twoOk = false;
                if (_wasTwo && f.Moved) _twoOk = false;
                if (_fingers.Count > 0)
                {
                    // Partner lifts first: decide now, same as primary.
                    int qfx, qfy;
                    if (!f.Moved && _downOrigX != int.MinValue)
                    { qfx = _downOrigX; qfy = _downOrigY; }
                    else (qfx, qfy) = EventAt(f, end);
                    PairTapFire(f, qfx, qfy, ms);
                }
                if (_fingers.Count == 0)
                {
                    // Last lift wasn't the primary (it cancelled when the
                    // partner was still down): complete the two-contact
                    // session here instead of going silent. A scrolled
                    // session ends silent (no tap/swipe after a scroll).
                    if (_twoFired) { _lastWhat = "two-fired"; }
                    else if (_twoScrolled) { Log.Write("2FINGER scroll-end (silent)"); _lastWhat = "two-scroll-end"; }
                    else if (TwoFingerUp(f, _downOrigX, _downOrigY, ms)) { }
                    else
                    {
                        int fx2 = _downOrigX, fy2 = _downOrigY;
                        if (fx2 == int.MinValue)
                        { var (gx2, gy2) = Out.Logical(); fx2 = gx2; fy2 = gy2; }
                        CloseTwoTap(f, fx2, fy2, ms);
                    }
                    _heldLeft = false;
                    _g = G.None;
                    _primaryId = -1;
                }
            }
            if (_fingers.Count == 0)
            {
                // No click-through tail here: arming it eats fast re-taps
                // (a 2nd tap inside the tail falls to the window below and
                // never reaches us - the "needs 3 taps" bug). Only real
                // drops (Dragging UP above) arm it.
                Out.ClearAnchor();
                Log.Write($"SESSION raw fake=({_fakeX:0},{_fakeY:0})");
                _lastWhat = "session-end";
                _twoScrolled = false;
                _twoFired = false;
            }
            _liveTouches--;
            Surface.ReleaseTouchCapture(e.TouchDevice);
            e.Handled = true;
        }
        catch { }
    }

    // ---------------- window ops (no mouse output) -----------------------

    private void ApplyResize(double x, double y)
    {
        if (_resizeMode == null) return;
        double dx = x - _rsX, dy = y - _rsY;
        if (_resizeMode == "right")
        {
            double nw = Math.Max(220, _rsW + dx), nh = Math.Max(200, _rsH + dy);
            if (Math.Abs(nw - Width) >= 1) Width = nw;
            if (Math.Abs(nh - Height) >= 1) Height = nh;
        }
        else
        {
            double nw = Math.Max(220, _rsW - dx);
            if (Math.Abs(nw - Width) >= 1)
            {
                Left = _rsL + (_rsW - nw);
                Width = nw;
            }
            double nh = Math.Max(200, _rsH + dy);
            if (Math.Abs(nh - Height) >= 1) Height = nh;
        }
    }

    private void EndResize()
    {
        _resizeMode = null;
        _rsTouchId = -1;
        UpdateChrome();
        RenderZones();
    }

    /// <summary>True when the point (Surface coords) is on the gear.</summary>
    private bool OnGear(Point p)
    {
        try
        {
            var o = GearLabel.TranslatePoint(new Point(0, 0), Surface);
            return p.X >= o.X - 6 && p.X <= o.X + GearLabel.ActualWidth + 6
                && p.Y >= o.Y - 6 && p.Y <= o.Y + GearLabel.ActualHeight + 6;
        }
        catch { return false; }
    }

    /// <summary>True when the event source is the X label or inside
    /// it. Source-walk beats coordinates (no DPI/layout fragility).</summary>
    private bool FromX(object? src)
    {
        try
        {
            var d = src as DependencyObject;
            while (d != null)
            {
                if (ReferenceEquals(d, XLabel)) return true;
                d = (d as FrameworkElement)?.Parent as DependencyObject
                    ?? System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
        }
        catch { }
        return false;
    }

    /// <summary>True when the point (Surface coords) is on the X
    /// (chrome drag must not swallow it, like the gear).</summary>
    private bool OnX(Point p)
    {
        try
        {
            var o = XLabel.TranslatePoint(new Point(0, 0), Surface);
            return p.X >= o.X - 6 && p.X <= o.X + XLabel.ActualWidth + 6
                && p.Y >= o.Y - 6 && p.Y <= o.Y + XLabel.ActualHeight + 6;
        }
        catch { return false; }
    }

    private void OnChromeTouchDown(object sender, TouchEventArgs e)
    {
        if (FromX(e.OriginalSource)) return;
        var pp = e.GetTouchPoint(Surface).Position;
        if (OnGear(pp) || OnX(pp)) return;
        Log.Write($"CHROME down id={e.TouchDevice.Id} @{pp.X:0},{pp.Y:0}");
        _chromeTouch = true;
        _chromeTouchId = e.TouchDevice.Id;
        var rp = e.GetTouchPoint(Surface).Position;
        _chromeGrabX = rp.X; _chromeGrabY = rp.Y;
        TitleBar.CaptureTouch(e.TouchDevice);
        e.Handled = true;
    }

    private void OnChromeTouchMove(object sender, TouchEventArgs e)
    {
        if (!_chromeTouch || e.TouchDevice.Id != _chromeTouchId) return;
        // Same direct follow as the mouse: touch reports carry their own
        // position, no window coupling to go stale.
        var rp = e.GetTouchPoint(Surface).Position;
        Log.Write($"CHROME move @{rp.X:0},{rp.Y:0}");
        double tx = rp.X + Left - _chromeGrabX;
        double ty = rp.Y + Top - _chromeGrabY;
        if (Math.Abs(tx - Left) < 0.5 && Math.Abs(ty - Top) < 0.5) return;
        var vx = SystemParameters.VirtualScreenLeft;
        var vy = SystemParameters.VirtualScreenTop;
        var vw = SystemParameters.VirtualScreenWidth;
        var vh = SystemParameters.VirtualScreenHeight;
        Left = Math.Max(vx, Math.Min(vx + vw - Width, tx));
        Top = Math.Max(vy, Math.Min(vy + vh - Height, ty));
        e.Handled = true;
    }

    private void OnChromeTouchUp(object sender, TouchEventArgs e)
    {
        if (!_chromeTouch || e.TouchDevice.Id != _chromeTouchId) return;
        Log.Write("CHROME up");
        _chromeTouch = false;
        _chromeTouchId = -1;
        TitleBar.ReleaseTouchCapture(e.TouchDevice);
        UpdateChrome();
        e.Handled = true;
    }

    private void OnChromeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FromX(e.OriginalSource)) return;
        if (OnGear(e.GetPosition(Surface))) return;
        if (OnX(e.GetPosition(Surface))) return;
        _chromeMouse = true;
        var rp = e.GetPosition(Surface);
        var sp = new Point(rp.X + Left, rp.Y + Top);
        _chromeGrabX = sp.X - Left;
        _chromeGrabY = sp.Y - Top;
        TitleBar.CaptureMouse();
        e.Handled = true;
    }

    private void OnChromeMouseMove(object sender, MouseEventArgs e)
    {
        if (!_chromeMouse) return;
        // Mouse reports are synchronous (no touch latency), so direct
        // follow is exact: window at mouse minus grab offset. Small
        // deadband (0.5) filters sub-pixel churn without trailing.
        var rp = e.GetPosition(Surface);
        double tx = rp.X + Left - _chromeGrabX;
        double ty = rp.Y + Top - _chromeGrabY;
        if (Math.Abs(tx - Left) < 0.5 && Math.Abs(ty - Top) < 0.5) return;
        var vx = SystemParameters.VirtualScreenLeft;
        var vy = SystemParameters.VirtualScreenTop;
        var vw = SystemParameters.VirtualScreenWidth;
        var vh = SystemParameters.VirtualScreenHeight;
        Left = Math.Max(vx, Math.Min(vx + vw - Width, tx));
        Top = Math.Max(vy, Math.Min(vy + vh - Height, ty));
        e.Handled = true;
    }

    private void OnChromeMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_chromeMouse) return;
        _chromeMouse = false;
        TitleBar.ReleaseMouseCapture();
        UpdateChrome();
        e.Handled = true;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    private static extern int GetSystemMetrics2(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr ChildWindowFromPointEx(IntPtr parent, POINT pt, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    private const uint GA_ROOT = 2;

    /// <summary>Real-click parity for posted pass-through downs: posted
    /// messages never activate, so bring the target's top window
    /// forward and focus the exact control (clicks + keyboard work).
    /// Skips our own windows. Fully guarded.</summary>
    private static void ActivateTarget(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return;
            IntPtr root = GetAncestor(hwnd, GA_ROOT);
            if (root == IntPtr.Zero) root = hwnd;
            uint tid = 0;
            GetWindowThreadProcessId(root, out tid);
            uint ours = GetCurrentThreadId();
            if (tid == 0 || tid == ours) return;
            try { BringWindowToTop(root); } catch { }
            bool attached = false;
            try
            {
                attached = AttachThreadInput(ours, tid, true);
                try { SetForegroundWindow(root); } catch { }
                try { SetFocus(hwnd); } catch { }
            }
            finally
            {
                try { if (attached) AttachThreadInput(ours, tid, false); }
                catch { }
            }
        }
        catch { }
    }

    private static long _lastDownMs;
    private static int _lastDownX, _lastDownY;
    private static IntPtr _lastDownHwnd;
    private static string _lastDownBtn = "";
    private static int _downParity;

    private const int GCL_STYLE = -26;
    private const int CS_DBLCLKS = 0x0008;
    private const uint CWP_SKIPINVISIBLE = 0x1;
    private const uint CWP_SKIPDISABLED = 0x2;
    private const uint CWP_SKIPTRANSPARENT = 0x4;

    private static bool WantsDblClk(IntPtr h)
    {
        try
        {
            return ((long)GetClassLongPtr(h, GCL_STYLE) & CS_DBLCLKS) != 0;
        }
        catch { return false; }
    }

    /// <summary>Deepest child at the point (listviews, edits...).</summary>
    private static IntPtr DeepestChild(IntPtr top, int sx, int sy)
    {
        try
        {
            IntPtr cur = top;
            while (true)
            {
                var p = new POINT { X = sx, Y = sy };
                if (!ScreenToClient(cur, ref p)) return cur;
                IntPtr child = ChildWindowFromPointEx(cur, p,
                    CWP_SKIPINVISIBLE | CWP_SKIPDISABLED | CWP_SKIPTRANSPARENT);
                if (child == IntPtr.Zero || child == cur) return cur;
                cur = child;
            }
        }
        catch { return top; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private IntPtr _selfHwnd;

    /// <summary>Topmost visible window at the point, excluding ours.</summary>
    private IntPtr WindowBelow(int sx, int sy)
    {
        IntPtr found = IntPtr.Zero;
        if (_selfHwnd == IntPtr.Zero)
            _selfHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        EnumWindows((h, _) =>
        {
            if (found != IntPtr.Zero) return false;
            if (h == _selfHwnd) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == (uint)Environment.ProcessId) return true;  // ours (incl. EffectOverlay)
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            try
            {
                // Cloaked (hidden UWP) or click-through overlays never take input.
                if (DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0) return true;
                if (((long)GetWindowLongPtr(h, -20) & 0x20L) != 0) return true;
            }
            catch { }
            if (!GetWindowRect(h, out RECT r)) return true;
            if (sx >= r.Left && sx < r.Right && sy >= r.Top && sy < r.Bottom)
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private void ForwardMouse(object sender, MouseEventArgs e)
    {
        try
        {
            // Ours (chrome/labels/buttons) or touch-promoted: not forwarded.
            // (No own-input guard: PostMessage forwarding cannot loop back
            // through Preview, and stale ExtraInfo reads ate real downs.)
            if (e.StylusDevice != null) { e.Handled = true; return; }
            var src = e.OriginalSource as DependencyObject;
            if (src != null)
            {
                var d = src;
                while (d != null)
                {
                    if (ReferenceEquals(d, TitleBar)) return;
                    if (d is System.Windows.Controls.Button) return;
                    if (d is System.Windows.Controls.TextBlock) return;
                    d = (d as FrameworkElement)?.Parent as DependencyObject;
                }
            }
            // Screen coords from the message itself (physical, exact).
            var (sx, sy) = MessagePos();
            // Buttons go through the Fwd helpers (with press capture, so
            // drags survive crossing windows); moves/wheel use the matrix.
            if (e is MouseButtonEventArgs be2)
            {
                string? btn = be2.ChangedButton switch
                {
                    MouseButton.Left => "left",
                    MouseButton.Right => "right",
                    MouseButton.Middle => "middle",
                    _ => null,
                };
                if (btn != null)
                {
                    if (be2.ButtonState == MouseButtonState.Pressed)
                        FwdDown(btn, sx, sy);
                    else
                        FwdUp(btn, sx, sy);
                    e.Handled = true;
                    return;
                }
            }
            IntPtr target = WindowBelow(sx, sy);
            if (target == IntPtr.Zero) { e.Handled = true; return; }
            target = DeepestChild(target, sx, sy);
            POINT pt = new() { X = sx, Y = sy };
            if (!ScreenToClient(target, ref pt)) { e.Handled = true; return; }
            IntPtr lParam = (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF));
            uint msg = 0;
            UIntPtr wParam = UIntPtr.Zero;
            int buttons = 0;
            if (e.LeftButton == MouseButtonState.Pressed) buttons |= 0x0001;
            if (e.RightButton == MouseButtonState.Pressed) buttons |= 0x0002;
            if (e.MiddleButton == MouseButtonState.Pressed) buttons |= 0x0010;
            if (e is MouseButtonEventArgs be)
            {
                bool pressed = be.ButtonState == MouseButtonState.Pressed;
                // Pairing lives in FwdDown (L/R/M early-return above); X
                // buttons post plain downs here.
                switch (be.ChangedButton)
                {
                    case MouseButton.Left:
                        msg = pressed ? 0x0201u : 0x0202u; break;
                    case MouseButton.Right:
                        msg = pressed ? 0x0204u : 0x0205u; break;
                    case MouseButton.Middle:
                        msg = pressed ? 0x0207u : 0x0208u; break;
                    case MouseButton.XButton1:
                        msg = pressed ? 0x020Bu : 0x020Cu;
                        wParam = (UIntPtr)(uint)(1 << 16); break;
                    case MouseButton.XButton2:
                        msg = pressed ? 0x020Bu : 0x020Cu;
                        wParam = (UIntPtr)(uint)(2 << 16); break;
                }
                if (msg != 0 && msg < 0x020Bu) wParam = (UIntPtr)(uint)buttons;
            }
            else if (e is MouseWheelEventArgs we)
            {
                msg = 0x020Au;
                int delta = _s.ScrollInvert ? -we.Delta : we.Delta;
                wParam = (UIntPtr)(uint)(((delta << 16) & 0xFFFF0000) | (uint)buttons);
                lParam = Pack(sx, sy);   // WM_MOUSEWHEEL lParam = screen coords
            }
            else
            {
                msg = 0x0200u;
                wParam = (UIntPtr)(uint)buttons;
                // Drag capture: moves during a hold follow the down-window,
                // so crossing windows mid-drag doesn't strand the target.
                if (buttons != 0)
                {
                    foreach (var b in new[] { "left", "right", "middle" })
                        if (_fwdCap.TryGetValue(b, out IntPtr cap)) { target = cap; break; }
                    POINT cpt = new() { X = sx, Y = sy };
                    if (ScreenToClient(target, ref cpt))
                        lParam = Pack(cpt.X, cpt.Y);
                }
            }
            if (msg != 0) PostMessage(target, msg, wParam, lParam);
            _lastWhat = "mouse-fwd";
            e.Handled = true;
        }
        catch { }
    }

    // ---- Below-delivery button API (for gestures): L/R down, up, click,
    // double-click at a screen point, posted to the window below it.
    // Buttons capture like real input: down remembers its window, moves
    // and ups during the hold go there even if the cursor wandered off.
    private readonly Dictionary<string, IntPtr> _fwdCap = new();

    /// <summary>MK_* button flag for a pressed button (posted downs).</summary>
    private static uint BtnMask(string button) => button switch
    {
        "right" => 0x0002u,
        "middle" => 0x0010u,
        _ => 0x0001u,
    };

    private static uint BtnDownMsg(string button) => button switch
    {
        "right" => 0x0204u,
        "middle" => 0x0207u,
        _ => 0x0201u,
    };

    private static uint BtnUpMsg(string button) => button switch
    {
        "right" => 0x0205u,
        "middle" => 0x0208u,
        _ => 0x0202u,
    };

    private static IntPtr Pack(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

    private IntPtr BelowAt(int sx, int sy)
    {
        IntPtr target = WindowBelow(sx, sy);
        return target;
    }

    private void FwdDown(string button, int sx, int sy)
    {
        try
        {
            IntPtr target = BelowAt(sx, sy);
            if (target == IntPtr.Zero) return;
            target = DeepestChild(target, sx, sy);
            // Real-click parity: posted downs don't activate, so bring
            // the target forward + focus it (else clicks land dead and
            // keys go nowhere).
            ActivateTarget(target);
            // Manual double pairing: consecutive downs within the OS
            // time+box on the same window alternate single/double.
            // Windows rule: only the 2nd (even) down becomes DBLCLK,
            // and only for CS_DBLCLKS windows.
            long nowMs = Environment.TickCount64;
            int cx = GetSystemMetrics2(36) / 2;
            int cy = GetSystemMetrics2(37) / 2;
            if (button == _lastDownBtn
                && nowMs - _lastDownMs < GetDoubleClickTime()
                && Math.Abs(sx - _lastDownX) * 2 <= cx
                && Math.Abs(sy - _lastDownY) * 2 <= cy
                && target == _lastDownHwnd)
                _downParity++;
            else
            {
                if (_lastDownMs > 0 && nowMs - _lastDownMs < 5000)
                    Core.Log.Write($"NODBL button={button} dt={nowMs - _lastDownMs}ms dist={Math.Abs(sx - _lastDownX) + Math.Abs(sy - _lastDownY)} hwndSame={target == _lastDownHwnd} style={WantsDblClk(target)}");
                _downParity = 1;
            }
            _lastDownMs = nowMs;
            _lastDownX = sx; _lastDownY = sy;
            _lastDownHwnd = target;
            _lastDownBtn = button;
            uint msg = BtnDownMsg(button);
            bool dbl = (_downParity % 2 == 0) && WantsDblClk(target);
            if (dbl)
                msg = button switch
                {
                    "right" => 0x0206u,
                    "middle" => 0x0209u,
                    _ => 0x0203u,
                };
            _fwdCap[button] = target;
            var pt = new POINT { X = sx, Y = sy };
            if (!ScreenToClient(target, ref pt)) return;
            PostMessage(target, msg, (UIntPtr)BtnMask(button), Pack(pt.X, pt.Y));
            Core.Log.Write($"FWD {button} {(dbl ? "dblclk" : "down")} @({sx},{sy})");
            _lastWhat = $"fwd-{button}-down";
        }
        catch { }
    }

    private void FwdUp(string button, int sx, int sy)
    {
        try
        {
            IntPtr target;
            if (!_fwdCap.TryGetValue(button, out target))
                target = BelowAt(sx, sy);
            _fwdCap.Remove(button);
            if (target == IntPtr.Zero) return;
            var pt = new POINT { X = sx, Y = sy };
            if (!ScreenToClient(target, ref pt)) return;
            PostMessage(target, BtnUpMsg(button), UIntPtr.Zero, Pack(pt.X, pt.Y));
            Core.Log.Write($"FWD {button} up @({sx},{sy})");
            _lastWhat = $"fwd-{button}-up";
        }
        catch { }
    }

    /// <summary>Move posted to the captured/below window (for redirected
    /// gesture output crossing our own window).</summary>
    private void FwdMove(int sx, int sy)
    {
        try
        {
            IntPtr target = IntPtr.Zero;
            if (_heldLeft && _fwdCap.TryGetValue("left", out IntPtr cap)) target = cap;
            if (target == IntPtr.Zero)
            {
                target = BelowAt(sx, sy);
                if (target == IntPtr.Zero) return;
                target = DeepestChild(target, sx, sy);
            }
            var pt = new POINT { X = sx, Y = sy };
            if (!ScreenToClient(target, ref pt)) return;
            UIntPtr wParam = (UIntPtr)(uint)(_heldLeft ? 0x0001 : 0);
            PostMessage(target, 0x0200u, wParam, Pack(pt.X, pt.Y));
        }
        catch { }
    }

    /// <summary>Gesture output landing on our own window goes below
    /// (pass-through), like the physical mouse. The real cursor still
    /// follows the model, so it never strands at the drag start.
    /// Elsewhere: real input.</summary>
    private bool BelowDeliver(string kind, string button, int x, int y)
    {
        try
        {
            double pl = Left * _dpi, pt = Top * _dpi;
            if (x < pl || x >= pl + Width * _dpi || y < pt || y >= pt + Height * _dpi)
                return false;
            Core.Out.PlaceAtReal(x, y);
            if (kind == "down") FwdDown(button, x, y);
            else if (kind == "up") FwdUp(button, x, y);
            else FwdMove(x, y);
            return true;
        }
        catch { return false; }
    }

    private void FwdClick(string button, int sx, int sy)
    {
        FwdDown(button, sx, sy);
        FwdUp(button, sx, sy);
    }

    private void FwdDouble(string button, int sx, int sy)
    {
        FwdClick(button, sx, sy);
        System.Threading.Thread.Sleep(60);
        FwdClick(button, sx, sy);
    }
}
