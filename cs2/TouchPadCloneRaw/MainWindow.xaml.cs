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
using TouchPadRaw.Core;

namespace TouchPadRaw;

/// <summary>
/// Fresh observation rig (no gesture logic, no output).
/// Touch intake + zones + window ops + HUD + logs only.
/// Gestures arrive per dictation; every one funnels through Out.
/// </summary>
public partial class MainWindow : Window
{
    private const double ChromeH = 30;
    private const double GripZone = 26;
    private const double TapMoveDip = 12;
    private const double MergeDip = 40;

    private readonly Settings _s = Settings.Load();
    private double _dpi = 1;
    private double _fakeX, _fakeY;

    private sealed class Finger
    {
        public Point Start, Last;
        public DateTime T0;
        public bool Moved;
        public string Zone = "";
    }

    private readonly Dictionary<int, Finger> _fingers = new();
    // Tap-train anchor (double pairing across slow taps): last tap time +
    // finger pos + zone. Re-snapshot only outside window/zone/scatter.
    private DateTime _anchorT = DateTime.MinValue;
    private double _anchorFX, _anchorFY;
    private string _anchorZone = "";
    // Hold-to-right timer (pad zone): still held LongPressMs -> R click.
    private System.Windows.Threading.DispatcherTimer? _holdTimer;
    private int _holdId = -1;
    private int _lastFreeX = int.MinValue, _lastFreeY = int.MinValue;
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

    [DllImport("user32.dll")]
    private static extern int GetMessagePos();

    private static (int x, int y) MessagePos()
    {
        int v = GetMessagePos();
        return ((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
    }

    private System.Windows.Threading.DispatcherTimer? _actualTimer;

    public MainWindow()
    {
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
            Log.Write($"INIT fake=({_fakeX:0},{_fakeY:0}) dpi={_dpi}");
        };

        StatusLabel.MouseLeftButtonUp += (_, _) => OpenLog();
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
                if (_fingers.Count == 0)
                {
                    var (lx, ly) = Out.Cursor();
                    if (lx != 0 || ly != 0) { _lastFreeX = lx; _lastFreeY = ly; }
                }
                string s = Out.ActualState();
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
                    return IntPtr.Zero;
                });
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

    private void Status(string msg) =>
        StatusLabel.Text = msg.Length > 90 ? msg[^90..] : msg;

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

    // ---------------- zones (fractions of the tile area below title) ----

    private (double w, double h) TileArea() =>
        (ActualWidth > 0 ? ActualWidth : Width,
         (ActualHeight > 0 ? ActualHeight : Height) - ChromeH);

    private static string ZoneAt(double x, double y, double w, double h)
    {
        if (y > h - GripZone && (x < GripZone || x > w - GripZone))
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
            rect(0, 0, w * 0.5, h * 0.2, "#40206040", "left");
            rect(w * 0.5, 0, w * 0.5, h * 0.2, "#40402060", "right");
            rect(w * 0.8, h * 0.2, w * 0.2, h * 0.8, "#40602020", "wheel");
            rect(0, h - GripZone, GripZone, GripZone, "#50505050", "RS");
            rect(w - GripZone, h - GripZone, GripZone, GripZone, "#50505050", "RS");
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
            var p = e.GetTouchPoint(Surface).Position;
            string zone = ZoneOf(p);
            if (zone == "resizer")
            {
                var (w0, h0) = TileArea();
                string mode = p.X < w0 / 2 ? "left" : "right";
                _resizeMode = mode;
                _rsW = Width; _rsH = Height;
                _rsX = Left + p.X; _rsY = Top + p.Y; _rsL = Left;
            _rsTouchId = e.TouchDevice.Id;
            RefreshDpi();
            (_rsMsgX, _rsMsgY) = MessagePos();
                _rsTX = _rsTY = 0;
                Surface.CaptureTouch(e.TouchDevice);
                ZoneLabel.Text = "zone: resizer";
                Log.Write($"TOUCHDOWN id={e.TouchDevice.Id} resizer->{mode}");
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
                // Anchor persists across rapid same-zone taps (tap-train
                // for OS double pairing); re-snapshot outside window, zone
                // change, or far re-landing (120 DIP vs ~70 scatter).
                // Stale hold from a lost UP first (safety net).
                if (_heldLeft) { Out.Up("left"); _heldLeft = false; }
                double fdist = _downOrigX == int.MinValue ? double.MaxValue :
                    Math.Abs(p.X - _anchorFX) + Math.Abs(p.Y - _anchorFY);
                if (_downOrigX == int.MinValue
                    || (DateTime.Now - _anchorT).TotalMilliseconds >= _s.MultiTapMs
                    || zone != _anchorZone
                    || fdist > 120)
                {
                    _downOrigX = _lastFreeX; _downOrigY = _lastFreeY;
                    if (_downOrigX == int.MinValue)
                    {
                        var (gx, gy) = Out.Logical();
                        _downOrigX = gx; _downOrigY = gy;
                    }
                    _anchorFX = p.X; _anchorFY = p.Y;
                    _anchorZone = zone;
                }
                _anchorT = DateTime.Now;
                Out.SetAnchor(_downOrigX, _downOrigY);
                Out.DownAt(_downOrigX, _downOrigY, "left");
                _heldLeft = true;
                Fx().Flash(_downOrigX / _dpi, _downOrigY / _dpi);
                Status($"press down @({_downOrigX},{_downOrigY})");
                // Hold arms right-click (pad zone): still held LongPressMs
                // with no real movement -> Up safety + R down+up at anchor.
                _holdTimer?.Stop();
                _holdId = -1;
                if (zone == "pad" && _downOrigX != int.MinValue)
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
                            if (!_fingers.TryGetValue(_holdId, out var hf)) return;
                            double htravel = Math.Abs(hf.Last.X - hf.Start.X)
                                + Math.Abs(hf.Last.Y - hf.Start.Y);
                            if (htravel > _s.DragStartDip) return;
                            if (_heldLeft) { Out.Up("left"); _heldLeft = false; }
                            Out.DownAt(_downOrigX, _downOrigY, "right");
                            Out.UpAt(_downOrigX, _downOrigY, "right");
                            Status($"hold right @({_downOrigX},{_downOrigY})");
                        }
                        catch { }
                    };
                    _holdTimer.Start();
                }
            }
            int rawN = _fingers.Count, mgN = MergedCount();
            Status($"touch {e.TouchDevice.Id} {zone} n={(rawN == mgN ? rawN.ToString() : mgN + "[raw" + rawN + "]")}");
            ZoneLabel.Text = "zone: " + zone;
            var (cx, cy) = Out.Cursor();
            Log.Write($"TOUCHDOWN id={e.TouchDevice.Id} @{p.X:0},{p.Y:0} zone={zone} n={_fingers.Count} cursor=({cx},{cy})");
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
                var (mx, my) = MessagePos();
                double dx = (mx - _rsMsgX) / _dpi, dy = (my - _rsMsgY) / _dpi;
                _rsMsgX = mx; _rsMsgY = my;
                if (Math.Abs(dx) > 40 || Math.Abs(dy) > 40)
                {
                    e.Handled = true;
                    return;
                }
                _rsTX += dx; _rsTY += dy;
                ApplyResize(_rsX + _rsTX, _rsY + _rsTY);
                e.Handled = true;
                return;
            }
            if (!_fingers.TryGetValue(e.TouchDevice.Id, out var f)) return;
            var p = e.GetTouchPoint(Surface).Position;
            double dx2 = p.X - f.Last.X, dy2 = p.Y - f.Last.Y;
            f.Last = p;
            double net = Math.Abs(p.X - f.Start.X) + Math.Abs(p.Y - f.Start.Y);
            if (net > TapMoveDip) f.Moved = true;
            // Model only (logs context, no output): press-start + travel.
            _fakeX = f.Start.X + (p.X - f.Start.X) * _dpi * _s.Speed;
            _fakeY = f.Start.Y + (p.Y - f.Start.Y) * _dpi * _s.Speed;
            string z2 = ZoneOf(p);
            if (z2 != f.Zone)
            {
                f.Zone = z2;
                ZoneLabel.Text = "zone: " + z2;
            }
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
                e.Handled = true;
                return;
            }
            if (!_fingers.TryGetValue(e.TouchDevice.Id, out var f))
            {
                Log.Write($"UP-IN id={e.TouchDevice.Id} UNKNOWN");
                e.Handled = true;
                return;
            }
            _fingers.Remove(e.TouchDevice.Id);
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
            // Tap refresh: quick still pad lifts extend the tap-train
            // window (slow multi-taps keep pairing).
            if (!f.Moved && ms < 250 && f.Zone == "pad"
                && _downOrigX != int.MinValue)
            {
                _anchorT = DateTime.Now;
                _anchorFX = f.Start.X; _anchorFY = f.Start.Y;
                _anchorZone = f.Zone;
            }
            // Release on the last lift (at the model G, never at the touch).
            if (_heldLeft && _fingers.Count == 0)
            {
                _heldLeft = false;
                var (rx, ry) = EventAt(f, end);
                if (rx == int.MinValue)
                {
                    Out.Up("left");
                }
                else
                {
                    Out.UpAt(rx, ry, "left");
                    Fx().Flash(rx / _dpi, ry / _dpi);
                // Yank window: the OS pulls to the contact up to a few
                // hundred ms AFTER lift; hold the release point briefly
                // (stop early when stable or a new touch lands).
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
                        if (Math.Abs(hx2 - rx) + Math.Abs(hy2 - ry) <= 4)
                        {
                            holdT.Stop();
                            return;
                        }
                        Out.PlaceAt(rx, ry);
                    }
                    catch { holdT.Stop(); }
                };
                holdT.Start();
                }
            }
            if (_fingers.Count == 0)
            {
                _throughUntil = Environment.TickCount64 + 300;
                Out.ClearAnchor();
                Log.Write($"SESSION raw fake=({_fakeX:0},{_fakeY:0})");
            }
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

    private void OnChromeTouchDown(object sender, TouchEventArgs e)
    {
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
        _chromeTouch = false;
        _chromeTouchId = -1;
        TitleBar.ReleaseTouchCapture(e.TouchDevice);
        UpdateChrome();
        e.Handled = true;
    }

    private void OnChromeMouseDown(object sender, MouseButtonEventArgs e)
    {
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
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics2(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr ChildWindowFromPointEx(IntPtr parent, POINT pt, uint flags);

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
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
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
                wParam = (UIntPtr)(uint)(((we.Delta << 16) & 0xFFFF0000) | (uint)buttons);
            }
            else
            {
                msg = 0x0200u;
                wParam = (UIntPtr)(uint)buttons;
            }
            if (msg != 0) PostMessage(target, msg, wParam, lParam);
            e.Handled = true;
        }
        catch { }
    }

    // ---- Below-delivery button API (for gestures): L/R down, up, click,
    // double-click at a screen point, posted to the window below it.
    // Buttons capture like real input: down remembers its window, moves
    // and ups during the hold go there even if the cursor wandered off.
    private readonly Dictionary<string, IntPtr> _fwdCap = new();

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
            PostMessage(target, msg, UIntPtr.Zero, Pack(pt.X, pt.Y));
            Core.Log.Write($"FWD {button} {(dbl ? "dblclk" : "down")} @({sx},{sy})");
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
        }
        catch { }
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
