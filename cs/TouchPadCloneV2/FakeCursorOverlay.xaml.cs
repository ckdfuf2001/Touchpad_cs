using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using TouchPadCloneV2.Core;

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

    private const int WM_NCHITTEST = 0x0084;
    private static readonly IntPtr HTTRANSPARENT = new(-1);

    public FakeCursorOverlay()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int st = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE,
                st | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            // WS_EX_TRANSPARENT alone does NOT reliably pass mouse input
            // through a top-level window: the default hit test still claims
            // every point of the window rectangle. The arrow is drawn exactly
            // where the OS cursor is parked during a drag, so any point that
            // landed on it was swallowed and the application under the
            // cursor saw a button press with no motion - the drag "event"
            // fired and nothing was dragged.
            if (HwndSource.FromHwnd(_hwnd) is { } src)
            {
                src.AddHook(NcHitTest);
                DebugLog.Write("OVERLAY hit-test hook installed");
            }
            else
            {
                DebugLog.Write("OVERLAY hit-test hook NOT installed"
                    + " (HwndSource.FromHwnd returned null)");
            }
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
        if (_suspended) return;      // a button is held, see Suspend
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

    /// <summary>
    /// Click-through, for real. HTTRANSPARENT tells the system to keep
    /// looking for the window underneath, so the overlay can sit on top of
    /// the cursor without ever being the thing that receives a click.
    /// </summary>
    private IntPtr NcHitTest(IntPtr hwnd, int msg, IntPtr wParam,
        IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST) { handled = true; return HTTRANSPARENT; }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Stop drawing the arrow without forgetting where it was (Hide() is the
    /// window). Used while a button is held: the arrow is placed exactly on
    /// the cursor's hotspot by design, so for as long as it is up it is a
    /// topmost window sitting under the cursor - and mouse input pass-through
    /// for a layered window applies to its transparent area, not to the opaque
    /// arrow glyph. A click survives that (down and up are injected in one
    /// batch), a drag does not: the arrow is re-placed on every move, so the
    /// moves and the release land on the arrow instead of the application.
    /// </summary>
    public void Suspend() { try { base.Hide(); } catch { } _suspended = true; }

    public void Resume()
    {
        _suspended = false;
        if (_hasPend) MoveToPhysical(_pendX, _pendY);
    }

    private bool _suspended;

    public new void Hide()
    {
        try { base.Hide(); } catch { }
    }
}
