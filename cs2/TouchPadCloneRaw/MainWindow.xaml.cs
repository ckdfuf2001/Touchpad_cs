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

    // Chrome (title) touch/mouse drag state.
    private bool _chromeTouch;
    private int _chromeTouchId = -1;
    private bool _chromeMouse;
    private int _chromeMsgX, _chromeMsgY;
    private double _chromeGrabX, _chromeGrabY;
    // Touch-drag cumulative travel (like resize): per-event deltas jitter,
    // the anchor does not. Same stability as the resizer.
    private double _chAX, _chAY, _chTX, _chTY;

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
                string s = Out.ActualState();
                ActualLabel.Text = s.Length > 90 ? s[^90..] : s;
            }
            catch { }
        };
        _actualTimer.Start();
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
            double ms = (DateTime.Now - f.T0).TotalMilliseconds;
            var end = e.GetTouchPoint(Surface).Position;
            if (Math.Abs(end.X - f.Start.X) + Math.Abs(end.Y - f.Start.Y) > TapMoveDip)
                f.Moved = true;
            var (ux, uy) = Out.Cursor();
            Log.Write($"TOUCHUP id={e.TouchDevice.Id} {(f.Moved ? "moved" : "still")} {(int)ms}ms zone={f.Zone} n={_fingers.Count} cursor=({ux},{uy})");
            Status($"up {(int)ms}ms {(f.Moved ? "moved" : "still")}");
            if (_fingers.Count == 0)
                Log.Write($"SESSION raw fake=({_fakeX:0},{_fakeY:0})");
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
        (_chromeMsgX, _chromeMsgY) = MessagePos();
        RefreshDpi();
        _chAX = Left; _chAY = Top;
        _chTX = _chTY = 0;
        TitleBar.CaptureTouch(e.TouchDevice);
        e.Handled = true;
    }

    private void OnChromeTouchMove(object sender, TouchEventArgs e)
    {
        if (!_chromeTouch || e.TouchDevice.Id != _chromeTouchId) return;
        var (mx, my) = MessagePos();
        double dx = (mx - _chromeMsgX) / _dpi, dy = (my - _chromeMsgY) / _dpi;
        _chromeMsgX = mx; _chromeMsgY = my;
        if (Math.Abs(dx) > 40 || Math.Abs(dy) > 40) return;
        _chTX += dx; _chTY += dy;
        double tx = _chAX + _chTX, ty = _chAY + _chTY;
        if (Math.Abs(tx - Left) < 1 && Math.Abs(ty - Top) < 1) return;
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
        _chromeGrabX = rp.X; _chromeGrabY = rp.Y;
        TitleBar.CaptureMouse();
        e.Handled = true;
    }

    private void OnChromeMouseMove(object sender, MouseEventArgs e)
    {
        if (!_chromeMouse) return;
        var rp = e.GetPosition(Surface);
        double tx = Left + (rp.X - _chromeGrabX);
        double ty = Top + (rp.Y - _chromeGrabY);
        _chromeGrabX = rp.X; _chromeGrabY = rp.Y;
        Left = tx;
        Top = ty;
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
}
