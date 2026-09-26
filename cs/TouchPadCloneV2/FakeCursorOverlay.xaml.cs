using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TouchPadCloneV2;

/// <summary>
/// The roaming (fake) cursor: a tiny click-through tool window positioned
/// with SetWindowPos in PHYSICAL pixels. No fullscreen window, no DIP math,
/// so mixed-DPI multi-monitor setups cannot misplace it. Same answer the
/// original ships as "Fake arrow cursor".
/// </summary>
public partial class FakeCursorOverlay : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr h, int n);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr h, int n, int v);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after,
        int x, int y, int cx, int cy, uint flags);

    private IntPtr _hwnd;
    private double _pendX, _pendY;
    private bool _hasPend;

    public FakeCursorOverlay()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int st = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE,
                st | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            if (_hasPend) Place(_pendX, _pendY);
        };
        var arrow = new Polygon
        {
            Fill = Brushes.White,
            Stroke = Brushes.Black,
            StrokeThickness = 1.5,
            Points = new PointCollection([new Point(2, 2), new Point(2, 24),
                new Point(8, 19), new Point(11, 26), new Point(14, 24),
                new Point(11, 17), new Point(17, 17)]),
            IsHitTestVisible = false,
        };
        Paper.Children.Add(arrow);
        _arrow = arrow;
        ApplyStyle(_style);
        IsHitTestVisible = false;
    }

    private readonly Polygon _arrow;
    private string _style = "cyan";

    /// <summary>Cursor theme. Default is the classic arrow in cyan.</summary>
    public void ApplyStyle(string name)
    {
        _style = string.IsNullOrWhiteSpace(name) ? "cyan" : name.ToLowerInvariant();
        var (fill, stroke) = _style switch
        {
            "white" => ((Brush)Brushes.White, (Brush)Brushes.Black),
            "black" => ((Brush)Brushes.Black, (Brush)Brushes.White),
            "green" => (new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x7F)),
                        (Brush)Brushes.Black),
            "yellow" => (new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)),
                         (Brush)Brushes.Black),
            "magenta" => (new SolidColorBrush(Color.FromRgb(0xF0, 0x4C, 0xF0)),
                          (Brush)Brushes.Black),
            _ => (new SolidColorBrush(Color.FromRgb(0x35, 0xC4, 0xFF)),
                  (Brush)Brushes.Black), // "cyan" default
        };
        _arrow.Fill = fill;
        _arrow.Stroke = stroke;
    }

    /// <summary>Hotspot (arrow tip) goes exactly to the physical pixel.</summary>
    public void MoveToPhysical(double physX, double physY)
    {
        _pendX = physX; _pendY = physY; _hasPend = true;
        if (!IsVisible)
        {
            try { base.Show(); } catch { }
        }
        if (_hwnd == IntPtr.Zero) return; // SourceInitialized flushes pending
        Place(physX, physY);
    }

    private void Place(double physX, double physY)
    {
        SetWindowPos(_hwnd, HWND_TOPMOST,
            (int)Math.Round(physX) - 2, (int)Math.Round(physY) - 2,
            0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public new void Hide()
    {
        try { base.Hide(); } catch { }
    }
}
