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
    private bool _fakeInit;
    private DateTime _suppressPhysicalUntil = DateTime.MinValue;
    // ShowCursor is a global counter: HideCursor must be called exactly
    // once per hide (a settings-slider Apply loop once drained it by
    // hundreds and the cursor could never come back). Gate it here.
    private bool _cursorHidden;

    private void EnsureHidden()
    {
        if (_cursorHidden) return;
        InputSim.HideCursor();
        _cursorHidden = true;
    }

    private void EnsureVisible()
    {
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
        ShowFake();
        if (!_session)
        {
            InputSim.SetCursor((int)_fakeX, (int)_fakeY);
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

    private void BeginSession()
    {
        if (_session || !_s.FakeCursor) return;
        EnsureDpi();
        _session = true;
        EnterPersistentFake();
        DebugLog.Write($"SESSION begin fake=({_fakeX:0},{_fakeY:0})");
    }

    private void EndSession()
    {
        if (!_session) return;
        _session = false;
        // Park the system cursor exactly under the fake one and STAY hidden:
        // trackpad mode shows a single cursor identity (the fake one).
        InputSim.SetCursor((int)_fakeX, (int)_fakeY);
        EnsureHidden();
        _suppressPhysicalUntil = DateTime.Now.AddMilliseconds(250);
        ShowFake();
        DebugLog.Write($"SESSION end fake=({_fakeX:0},{_fakeY:0})");
        // ... (lift-yank re-park timer below)
        double parkX = _fakeX, parkY = _fakeY;
        var (lx, ly) = InputSim.Cursor();
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                if (_session || !_s.FakeCursor) return;
                var (cx, cy) = InputSim.Cursor();
                double dLift = Math.Sqrt((cx - lx) * (cx - lx) + (cy - ly) * (cy - ly));
                double dPark = Math.Sqrt((cx - parkX) * (cx - parkX) + (cy - parkY) * (cy - parkY));
                if (dLift < 80 && dPark > 80)
                {
                    InputSim.SetCursor((int)parkX, (int)parkY);
                    DebugLog.Write($"RE-PARK to ({parkX:0},{parkY:0}), was ({cx},{cy})");
                }
            }
            catch { }
        };
        timer.Start();
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
        _fakeInit = true;
        ClampFake();
        if (_overlay != null && _overlay.IsVisible)
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
        public bool Moved;
        public Tile? Tile;
        public string? HoldButton;
        public (int vk, int[] mods)? HoldCombo;
    }

    // ---- Per-event gesture state machine (original §Side/Float model) ----
    // tap / double-tap / long-press / 2nd-tap-hold / two-finger-tap / swipes,
    // each mapped to a configurable action, per layout family.
    private GestureMap G => _s.ActiveGestures(_layout?.Name ?? "");
    private System.Windows.Threading.DispatcherTimer? _longTimer;
    private System.Windows.Threading.DispatcherTimer? _holdTimer;
    private int _longId = -1;
    private bool _longFired;
    private DateTime _pendingTapUntil = DateTime.MinValue;
    private DateTime _pendingTripleUntil = DateTime.MinValue;
    private bool _secondPress;
    private int _secondId = -1;
    private bool _thirdPress;
    private int _thirdId = -1;
    private bool _secondConsumed;   // hold timer already fired
    private bool _dragHold;         // action button currently held for drag
    private int _dragId = -1;
    private string? _actionHeld;
    private int _twoId = -1;        // second concurrent finger
    private bool _twoCandidate;
    private bool _twoActive;        // a two-finger gesture is/was in flight
    private double _twoAccX, _twoAccY;

    private void CancelLong() { _longTimer?.Stop(); _longTimer = null; _longId = -1; }
    private void CancelHold() { _holdTimer?.Stop(); _holdTimer = null; }

    private void ArmLong(int id)
    {
        CancelLong();
        _longId = id;
        _longFired = false;
        _longTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(200, _s.LongPressMs)),
        };
        _longTimer.Tick += (_, _) =>
        {
            CancelLong();
            if (!_fingers.TryGetValue(id, out var f) || f.Moved) return;
            if (_fingers.Count != 1) return;
            _longFired = true;
            Status("long-press");
            DebugLog.Write("GESTURE long-press");
            FirePressAction(G.LongPress, id);
        };
        _longTimer.Start();
    }

    private void ArmHold(int id)
    {
        CancelHold();
        _holdTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _holdTimer.Tick += (_, _) =>
        {
            CancelHold();
            if (!_fingers.TryGetValue(id, out var f)) return;
            _secondConsumed = true;
            Status("second-hold");
            DebugLog.Write("GESTURE second-hold");
            FirePressAction(G.SecondHold, id);
        };
        _holdTimer.Start();
    }

    /// <summary>
    /// Actions that trigger while the finger is DOWN (long-press, 2nd hold).
    /// drag_hold presses the button until release; the rest fire once.
    /// </summary>
    private void FirePressAction(string action, int id)
    {
        if (action == "drag_hold")
        {
            _dragHold = true;
            _dragId = id;
            _actionHeld = "left";
            ParkAtFake();
            InputSim.Down("left");
            _suppressPhysicalUntil = DateTime.Now.AddMilliseconds(250);
        }
        else DoGesture(action);
    }

    private void ReleaseActionButton()
    {
        if (_dragHold && _actionHeld != null)
        {
            ParkAtFake();
            InputSim.Up(_actionHeld);
            _dragHold = false;
            _actionHeld = null;
        }
    }

    public PadWindow(AppSettings settings)
    {
        _s = settings;
        InitializeComponent();
        Core.NoActivate.Apply(this);
        Core.TabletTweaks.DisableSystemGestures(this);
        SyncWindowSize();
        Left = SystemParameters.PrimaryScreenWidth - Width - 40;
        Top = SystemParameters.PrimaryScreenHeight - Height - 120;
        Opacity = settings.Opacity;

        Surface.TouchDown += OnTouchDown;
        Surface.TouchMove += OnTouchMove;
        Surface.TouchUp += OnTouchUp;
        Surface.MouseDown += OnMouseDown;
        Surface.MouseMove += OnMouseMove;
        Surface.MouseUp += OnMouseUp;
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
    /// </summary>
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
        ShowFake();
        EnsureHidden();
        _suppressPhysicalUntil = DateTime.Now.AddMilliseconds(250);
    }

    /// <summary>
    /// A real physical mouse spoke: hand control back to the system cursor
    /// and adopt its position into the fake one. Own synthetic output right
    /// after park/click/restore is suppressed, never treated as physical.
    /// Returns false when the event must be ignored entirely.
    /// </summary>
    private bool NotePhysicalMouse()
    {
        if (DateTime.Now < _suppressPhysicalUntil) return false;
        if (!_session && _s.FakeCursor)
        {
            EnsureVisible();
            try { _overlay?.Hide(); } catch { }
            var (cx, cy) = InputSim.Cursor();
            _fakeX = cx; _fakeY = cy;
            _fakeInit = true;
        }
        return true;
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
        ParkAtFake();
        // A synthetic click landing on our own window would re-enter as a
        // new tap and self-sustain (~1ms press/release flood, measured).
        try
        {
            var (px, py) = _session ? (_fakeX, _fakeY) : ToPair(InputSim.Cursor());
            var src = PresentationSource.FromVisual(this);
            double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1;
            double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1;
            double rx = Left * sx, ry = Top * sy;
            if (px >= rx && px <= rx + ActualWidth * sx &&
                py >= ry && py <= ry + ActualHeight * sy)
            {
                Status("self-click suppressed (cursor over pad)");
                return;
            }
        }
        catch { }
        string b = (button == "left" && _s.SwapButtons) ? "right"
            : (button == "right" && _s.SwapButtons) ? "left" : button;
        InputSim.Click(b);
    }

    private static (double, double) ToPair((int X, int Y) p) => (p.X, p.Y);

    /// <summary>
    /// Park the system cursor exactly under the fake cursor before ANY
    /// synthetic button press. Without this, tile-button downs (which used
    /// to skip SafeClick) land at the yanked touch point instead of the
    /// virtual mouse - measured: right-click menus popping at the finger.
    /// </summary>
    private void ParkAtFake()
    {
        if (!_session) return;
        InputSim.SetCursor((int)_fakeX, (int)_fakeY);
        _suppressPhysicalUntil = DateTime.Now.AddMilliseconds(250);
    }

    // ---------------- touch (primary path) ----------------
    private void OnTouchDown(object sender, TouchEventArgs e)
    {
        if (IsChrome(e.OriginalSource)) return;
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
        var f = new Finger { Start = p, Last = p, T0 = DateTime.Now, Tile = tile };
        _fingers[e.TouchDevice.Id] = f;
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
                    // Second press of a would-be double tap.
                    _secondPress = true;
                    _secondId = e.TouchDevice.Id;
                    _secondConsumed = false;
                    _thirdPress = false;
                    if (G.SecondHold == "drag_hold")
                    {
                        // Click-and-hold state IMMEDIATELY (no 250ms dead
                        // zone): the button is down from this instant, moves
                        // drag at once. Quick release completes an OS-level
                        // double-click naturally (see release path).
                        ParkAtFake();
                        InputSim.Down("left");
                        _suppressPhysicalUntil = DateTime.Now.AddMilliseconds(250);
                        _dragHold = true;
                        _dragId = e.TouchDevice.Id;
                        _actionHeld = "left";
                        _secondConsumed = true;
                        DebugLog.Write("GESTURE second-hold (immediate drag)");
                    }
                    else ArmHold(e.TouchDevice.Id);
                }
                else if (DateTime.Now < _pendingTripleUntil)
                {
                    // Third press: triple-tap candidate (or triple-drag).
                    _thirdPress = true;
                    _thirdId = e.TouchDevice.Id;
                    _secondPress = false;
                    if (G.SecondHold == "drag_hold")
                    {
                        ParkAtFake();
                        InputSim.Down("left");
                        _suppressPhysicalUntil = DateTime.Now.AddMilliseconds(250);
                        _dragHold = true;
                        _dragId = e.TouchDevice.Id;
                        _actionHeld = "left";
                        DebugLog.Write("GESTURE third-hold (immediate drag)");
                    }
                }
                else
                {
                    _secondPress = false;
                    _thirdPress = false;
                    ArmLong(e.TouchDevice.Id);
                }
            }
            else if (_fingers.Count == 2)
            {
                // Second concurrent finger: long-press is off; maybe tap/scroll.
                CancelLong();
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
                    ParkAtFake();
                    InputSim.Down(b);
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

    private void OnTouchMove(object sender, TouchEventArgs e)
    {
        if (IsChrome(e.OriginalSource)) return;
        if (_resizeMode != null && e.TouchDevice.Id == _rsTouchId)
        {
            var rp = ScreenOf(e.GetTouchPoint(Surface).Position);
            MoveResize(rp.X, rp.Y);
            e.Handled = true;
            return;
        }
        if (!_fingers.TryGetValue(e.TouchDevice.Id, out var f)) return;
        var p = e.GetTouchPoint(Surface).Position;
        double dx = p.X - f.Last.X, dy = p.Y - f.Last.Y;
        f.Last = p;
        bool was = f.Moved;
        if (Math.Abs(p.X - f.Start.X) + Math.Abs(p.Y - f.Start.Y) > TapMoveDip)
            f.Moved = true;
        if (f.Moved && !was)
        {
            // Any real motion kills tap-class gestures for everyone held.
            if (e.TouchDevice.Id == _longId) CancelLong();
            if (_fingers.Count >= 2) _twoCandidate = false;
            // (drag_hold begins at 2nd-press DOWN now, not here.)
        }

        // Two concurrent contacts: vertical = live scroll, horizontal =
        // accumulated for release-time swipe-nav. Single finger NEVER swipes.
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
                // Deltas are touch positions in our own surface space -
                // fully independent of the OS cursor, so no feedback loop.
                if (_session)
                {
                    // Roam the fake cursor; the hidden system cursor is
                    // being yanked toward the finger by Windows, ignore it.
                    _fakeX += dx * _dpi * _s.Speed;
                    _fakeY += dy * _dpi * _s.Speed;
                    ClampFake();
                    ShowFake();
                    DebugLog.Write($"FAKE d=({dx:0},{dy:0}) -> ({_fakeX:0},{_fakeY:0})");
                    Status($"fake ({_fakeX:0},{_fakeY:0})");
                }
                else
                {
                    int ax = ClampStep((int)(dx * _dpi * _s.Speed));
                    int ay = ClampStep((int)(dy * _dpi * _s.Speed));
                    InputSim.Move(ax, ay);
                    DebugLog.Write($"TOUCHMOVE id={e.TouchDevice.Id} d=({dx:0},{dy:0}) -> ({ax},{ay})");
                    Status($"move d=({dx:0},{dy:0}) -> ({ax},{ay})");
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
    private void DoWheel(double amount, bool horizontal)
    {
        // Wheel messages route to the window under the SYSTEM cursor, which
        // mid-touch is yanked onto our own pad (scrolls nothing). Park it at
        // the fake cursor first so the wheel lands in the real target app.
        ParkAtFake();
        _wheelCarry += amount;
        while (Math.Abs(_wheelCarry) >= _s.WheelStep)
        {
            int d = _wheelCarry > 0 ? _s.WheelStep : -_s.WheelStep;
            InputSim.Wheel(d, horizontal);
            _wheelCarry -= d;
        }
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
        if (!_fingers.TryGetValue(e.TouchDevice.Id, out var f)) return;
        _fingers.Remove(e.TouchDevice.Id);
        if (e.TouchDevice.Id == _longId) CancelLong();
        double ms = (DateTime.Now - f.T0).TotalMilliseconds;
        // Classify by the UP position as well: a fast flick may deliver
        // zero TouchMove events, and move-flags alone would call it a tap.
        var end = e.GetTouchPoint(Surface).Position;
        if (Math.Abs(end.X - f.Start.X) + Math.Abs(end.Y - f.Start.Y) > TapMoveDip)
            f.Moved = true;
        bool tap = !f.Moved && ms < TapMs;
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
        Status($"up {(tap ? "tap" : "gesture")} {(int)ms}ms n={_fingers.Count}");
        // Release capture BEFORE firing actions: windows opened from here
        // (settings/assist) must activate normally.
        Surface.ReleaseTouchCapture(e.TouchDevice);

        if (f.Tile != null)
        {
            switch (f.Tile.Action)
            {
                case TileAction.Click:
                case TileAction.Drag:
                    // Re-park: the finger stayed down since Down(), and Windows
                    // yanked the system cursor onto the touch point meanwhile.
                    // Without this, Up (and the browser menu) lands at the
                    // finger instead of the virtual mouse.
                    if (f.HoldButton != null) { ParkAtFake(); InputSim.Up(f.HoldButton); }
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
                        // Third press released right away: triple-tap event.
                        // (Held-down was a triple-drag; Up ends it silently.)
                        _thirdPress = false;
                        _pendingTripleUntil = DateTime.MinValue;
                        _pendingTapUntil = DateTime.MinValue;
                        if (_dragHold && id == _dragId)
                            ReleaseActionButton();
                        DoGesture(G.TripleTap);
                    }
                    else if (id == _secondId && _secondPress)
                    {
                        // Second press released (Up always ends a held drag,
                        // even with TapToClick off - else the button sticks).
                        CancelHold();
                        _secondPress = false;
                        if (_dragHold && id == _dragId)
                        {
                            // Was click-and-hold (button down since 2nd DOWN):
                            // release it. If released right away, one extra
                            // click completes an OS-level double-click
                            // naturally - the DoubleTap slot is NOT fired
                            // on top (that would triple-click).
                            ReleaseActionButton();
                            if (_s.TapToClick) SafeClick("left");
                            // Multi-tap windows are generous (450ms): slow
                            // tappers otherwise fall out of the double/triple
                            // chain and every tap degrades to a single click
                            // (measured user report: "needs three touches").
                            _pendingTripleUntil = DateTime.Now.AddMilliseconds(450);
                            _pendingTapUntil = DateTime.MinValue;
                        }
                        else if (!_secondConsumed)
                        {
                            DoGesture(G.DoubleTap);
                        }
                        // else: hold timer already consumed it.
                    }
                    else if (_longFired)
                    {
                        _longFired = false; // long-press already fired
                    }
                    else if (_s.TapToClick)
                    {
                        DoGesture(G.Tap);
                        _pendingTapUntil = DateTime.Now.AddMilliseconds(450);
                    }
                    break;
                }
                case TileAction.Pad when f.Moved:
                    if (_longFired)
                    {
                        _longFired = false; // long-press owned this press
                    }
                    else if (e.TouchDevice.Id == _thirdId && _thirdPress)
                    {
                        // 3rd press that moved: triple-drag ends, no event.
                        _thirdPress = false;
                        _pendingTripleUntil = DateTime.MinValue;
                        if (_dragHold && e.TouchDevice.Id == _dragId)
                            ReleaseActionButton();
                    }
                    else if (e.TouchDevice.Id == _secondId && _secondPress)
                    {
                        // 2nd press that moved: it was a drag (button down
                        // since 2nd DOWN) - release it, no click, no double.
                        CancelHold();
                        _secondPress = false;
                        if (_dragHold && e.TouchDevice.Id == _dragId)
                            ReleaseActionButton();
                    }
                    // NOTE: single-finger release NEVER swipes. A repositioning
                    // drag ending fast used to fire browser back/forward.
                    // Swipe-nav lives on TWO fingers now (see below).
                    break;
            }
        }

        // Explicit two-finger tap: 2nd finger tapped while 1st stayed still.
        if (tap && e.TouchDevice.Id == _twoId && _twoCandidate &&
            f.Tile?.Action == TileAction.Pad)
        {
            _twoCandidate = false;
            DebugLog.Write("GESTURE two-finger-tap");
            DoGesture(G.TwoFingerTap);
        }
        // Two-finger release: horizontal-dominant total = swipe-nav.
        if (_twoActive && _fingers.Count >= 1)
        {
            double ax = _twoAccX, ay = _twoAccY;
            if (Math.Abs(ax) > Math.Abs(ay) &&
                Math.Sqrt(ax * ax + ay * ay) > SwipeDip)
            {
                DebugLog.Write($"GESTURE two-swipe ({ax:0},{ay:0})");
                DoGesture(ax > 0 ? G.SwipeRight : G.SwipeLeft);
            }
        }
        if (_fingers.Count < 2) _twoActive = false;
        if (e.TouchDevice.Id == _twoId) { _twoId = -1; _twoCandidate = false; }

        e.Handled = true;
        if (_fingers.Count == 0)
            EndSession();
    }

    private void DoSwipe(double dx, double dy)
    {
        if (Math.Sqrt(dx * dx + dy * dy) < SwipeDip) return;
        DoGesture(Math.Abs(dx) > Math.Abs(dy)
            ? (dx > 0 ? G.SwipeRight : G.SwipeLeft)
            : (dy > 0 ? G.SwipeDown : G.SwipeUp));
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
            case "drag_hold": SafeClick("left"); break; // release-context fallback
            case "wheel_up": InputSim.Wheel(_s.WheelStep); break;
            case "wheel_down": InputSim.Wheel(-_s.WheelStep); break;
            case "browser_back": InputSim.TapKey(0xA6); break;
            case "browser_forward": InputSim.TapKey(0xA7); break;
            case "assist_pad": RequestAssist?.Invoke(); break;
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

    // ---------------- mouse fallback (non-touch testing only) ----------------
    // Physical mouse input hands control back to the system cursor
    // (Adopt/Handover). Synthetic output right after our own park/click is
    // suppressed via _suppressPhysicalUntil, never mistaken for physical.
    private void OnMouseDown(object sender, WMouseButtonEventArgs e)
    {
        if (IsChrome(e.OriginalSource)) return;
        if (e.StylusDevice != null || _fingers.Count > 0) return;
        if (DateTime.Now < _suppressPhysicalUntil) return;
        HandoverToPhysicalMouse();
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
            ParkAtFake();
            InputSim.Down(tile.ClickButton);
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
        if (e.StylusDevice != null || _fingers.Count > 0) return;
        if (DateTime.Now < _suppressPhysicalUntil) return;
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
        // Hover cursor over the grips.
        string? hz = ResizerAt(hp.X, hp.Y);
        Cursor = hz == "left" ? Cursors.SizeNESW
            : hz == "right" ? Cursors.SizeNWSE : Cursors.Arrow;
        if (_mouseDown) return; // handover already happened on MouseDown
        // Hover: silently adopt so the fake cursor tracks the mouse while
        // hidden. Never moves the cursor from here (no feedback possible).
        if (e.LeftButton == MouseButtonState.Released &&
            e.RightButton == MouseButtonState.Released &&
            e.MiddleButton == MouseButtonState.Released)
            AdoptCursor();
        e.Handled = true;
    }

    private void OnMouseUp(object sender, WMouseButtonEventArgs e)
    {
        if (IsChrome(e.OriginalSource)) return;
        if (e.StylusDevice != null || !_mouseDown) return;
        if (_rsMouse)
        {
            Surface.ReleaseMouseCapture();
            EndResize();
            _mouseDown = false;
            e.Handled = true;
            return;
        }
        if (DateTime.Now < _suppressPhysicalUntil) { _mouseDown = false; return; }
        HandoverToPhysicalMouse();
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
            double mms = (DateTime.Now - _mouseT0).TotalMilliseconds;
            bool tap = !moved && mms < TapMs;
            var tile = HitMouse(_mouseStart);
            if (tap && tile?.Action == TileAction.Pad && _s.TapToClick)
                SafeClick("left");
            else if (tile?.Action == TileAction.Sys && tile != null)
                SysAction(tile.Kind);
        }
        Surface.ReleaseMouseCapture();
        e.Handled = true;
    }
}
