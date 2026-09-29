using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TouchPadCloneV2;

/// <summary>
/// Click flash: a small ring shown exactly where a synthetic click lands,
/// fading in ~250ms. Makes invisible click routing visible - the user sees
/// WHERE each click went (fake pos, never the finger), which settles
/// aim-vs-fire disputes instantly. Click-through, never takes focus.
/// </summary>
public partial class FlashOverlay : Window
{
    private readonly Ellipse _ring;
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private int _frame;

    public FlashOverlay()
    {
        Width = 48; Height = 48;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        var canvas = new Canvas { Width = 48, Height = 48 };
        Content = canvas;
        _ring = new Ellipse
        {
            Width = 36, Height = 36,
            Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)),
            StrokeThickness = 3,
            IsHitTestVisible = false,
        };
        canvas.Children.Add(_ring);
        Canvas.SetLeft(_ring, 6);
        Canvas.SetTop(_ring, 6);
        IsHitTestVisible = false;
        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _timer.Tick += (_, _) =>
        {
            if (++_frame >= 5) { _timer.Stop(); Hide(); return; }
            _ring.Opacity = 1.0 - _frame / 5.0;
            double s = 36 + _frame * 2;
            _ring.Width = s; _ring.Height = s;
            Canvas.SetLeft(_ring, 24 - s / 2);
            Canvas.SetTop(_ring, 24 - s / 2);
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int st = NativeWin.GetWindowLong(hwnd, NativeWin.GWL_EXSTYLE);
            NativeWin.SetWindowLong(hwnd, NativeWin.GWL_EXSTYLE,
                st | NativeWin.WS_EX_TRANSPARENT | NativeWin.WS_EX_NOACTIVATE |
                NativeWin.WS_EX_TOOLWINDOW);
        };
    }

    private double _lastFX, _lastFY;
    private long _lastFTick;

    public void Flash(double physX, double physY)
    {
        // Coalesce same-spot flashes: a tap plus its chained grab land on
        // the same press point ~100-200ms apart (tap rewind), and every
        // re-fire reset the 250ms fade - pinning a permanent ring at the
        // first position through a whole move. A repeat at the same spot
        // while fading is skipped so the old fade runs out on schedule;
        // a new spot flashes normally.
        long now = Environment.TickCount64;
        double dx = physX - _lastFX, dy = physY - _lastFY;
        if (IsVisible && (now - _lastFTick) < 500
            && dx * dx + dy * dy < 24 * 24)
            return;
        _lastFX = physX; _lastFY = physY; _lastFTick = now;
        NativeWin.PlaceTopmost(Handle, physX - 24, physY - 24);
        _frame = 0;
        _ring.Opacity = 1.0;
        _ring.Width = _ring.Height = 36;
        Canvas.SetLeft(_ring, 6);
        Canvas.SetTop(_ring, 6);
        if (!IsVisible)
        {
            try { Show(); } catch { }
        }
        _timer.Stop();
        _timer.Start();
    }

    private IntPtr Handle
    {
        get
        {
            try
            {
                return new System.Windows.Interop.WindowInteropHelper(this).Handle;
            }
            catch { return IntPtr.Zero; }
        }
    }

    public new void Hide()
    {
        try { base.Hide(); } catch { }
    }
}
