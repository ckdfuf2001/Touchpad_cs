using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TouchPadCloneV2;

/// <summary>
/// Click effect at the cursor (screen DIP): a small topmost click-through
/// window that flashes a ring where the click landed, then hides.
/// Reused (no per-click windows).
/// </summary>
public sealed class EffectOverlay : Window
{
    /// <summary>Fired after every Show (the app re-raises the strip,
    /// which a fresh topmost overlay would otherwise cover).</summary>
    public static Action? AfterShow;
    // Effect settings (wired to the settings UI later; alpha allowed).
    public static Color RingColor = Color.FromArgb(0xFF, 0x7F, 0xE0, 0xA8);
    public static double RingDiameter = 26;
    public static double RingStroke = 2;
    public static double RingGrowth = 6;

    /// <summary>Per-flash tint from settings (EffEffect). Null = RingColor.
    /// Set by the pad on every gesture so color picks apply live.</summary>
    public Color? EffectTint { get; set; }

    private Color TintOrDefault()
    {
        try
        {
            if (EffectTint is Color c)
            {
                // Fully transparent tint = keep default (invisible ring
                // would read as broken, not as a color choice).
                if (c.A != 0) return c;
            }
        }
        catch { }
        return RingColor;
    }

    private readonly Ellipse _ring;
    private readonly Ellipse _spin;
    private readonly Canvas _canvas;
    private readonly System.Windows.Threading.DispatcherTimer _t;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);    private int _steps;
    private bool _flashing;
    private bool _spinning;
    private double _spinAngle;

    public EffectOverlay()
    {
        Width = 120;
        Height = 120;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        // OS-level click-through (IsHitTestVisible is WPF-only):
        // WS_EX_TRANSPARENT + WS_EX_NOACTIVATE, other bits preserved.
        SourceInitialized += (_, _) =>
        {
            try
            {
                var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                long ex = (long)GetWindowLongPtr(h, -20);
                SetWindowLongPtr(h, -20, (IntPtr)(ex | 0x20L | 0x08000000L));
            }
            catch { }
        };
        _canvas = new Canvas();
        Content = _canvas;
        _ring = new Ellipse
        {
            Width = RingDiameter,
            Height = RingDiameter,
            Stroke = new SolidColorBrush(TintOrDefault()),
            StrokeThickness = RingStroke,
            Opacity = 1,
        };
        _canvas.Children.Add(_ring);
        _spin = new Ellipse
        {
            Width = RingDiameter,
            Height = RingDiameter,
            Stroke = new SolidColorBrush(TintOrDefault()),
            StrokeThickness = RingStroke,
            StrokeDashArray = new DoubleCollection { RingDiameter * 2.2, RingDiameter * 1.4 },
            StrokeDashCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(0),
            Opacity = 0,
        };
        _canvas.Children.Add(_spin);
        _t = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(30),
        };
        _t.Tick += (_, _) =>
        {
            try
            {
                if (_spinning)
                {
                    // One revolution per 12 ticks (~360ms): circular
                    // loading-bar while moving.
                    _spinAngle = (_spinAngle + 30) % 360;
                    ((RotateTransform)_spin.RenderTransform).Angle = _spinAngle;
                    _spin.Opacity = 1;
                }
                if (_flashing)
                {
                    _steps++;
                    _ring.Opacity = Math.Max(0, 1 - _steps * 0.2);
                    double s = RingDiameter + _steps * RingGrowth;
                    _ring.Width = s;
                    _ring.Height = s;
                    Canvas.SetLeft(_ring, 60 - s / 2);
                    Canvas.SetTop(_ring, 60 - s / 2);
                    if (_steps >= 5) _flashing = false;
                }
                if (!_flashing && !_spinning)
                {
                    _t.Stop();
                    Hide();
                }
            }
            catch { _t.Stop(); }
        };
        Hide();
    }

    public void Flash(double dipX, double dipY)
    {
        try
        {
            Left = dipX - 60;
            Top = dipY - 60;
            _ring.Width = RingDiameter;
            _ring.Height = RingDiameter;
            _ring.Stroke = new SolidColorBrush(TintOrDefault());
            _ring.StrokeThickness = RingStroke;
            _ring.Opacity = 1;
            Canvas.SetLeft(_ring, 60 - RingDiameter / 2);
            Canvas.SetTop(_ring, 60 - RingDiameter / 2);
            _spin.Opacity = 0;
            Show();
            try { AfterShow?.Invoke(); } catch { }
            _steps = 0;
            _flashing = true;
            _t.Start();
        }
        catch { }
    }

    /// <summary>Move spinner: dashed arc revolving at the cursor while the
    /// finger moves. Idempotent per call (repositions + ensures spinning).
    /// </summary>
    public void Spin(double dipX, double dipY)
    {
        try
        {
            Left = dipX - 60;
            Top = dipY - 60;
            _spin.Width = RingDiameter;
            _spin.Height = RingDiameter;
            _spin.Stroke = new SolidColorBrush(TintOrDefault());
            _spin.StrokeThickness = RingStroke;
            Canvas.SetLeft(_spin, 60 - RingDiameter / 2);
            Canvas.SetTop(_spin, 60 - RingDiameter / 2);
            if (!IsVisible) Show();
            try { AfterShow?.Invoke(); } catch { }
            _spinning = true;
            if (!_t.IsEnabled) _t.Start();
        }
        catch { }
    }

    public void StopSpin()
    {
        _spinning = false;
        try { _spin.Opacity = 0; } catch { }
    }
}
