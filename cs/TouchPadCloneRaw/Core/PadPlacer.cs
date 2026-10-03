using System;

namespace TouchPadCloneV2.Core;

/// <summary>Pad area geometry (DIP). Pure function so the selftest can
/// verify every mode without opening windows. NaN = keep current
/// (default mode never moves the window).</summary>
public static class PadPlacer
{
    public static (double l, double t, double w, double h) RectFor(
        string mode, double vx, double vy, double vw, double vh) => mode switch
        {
            "full" => (vx, vy, vw, vh),
            "half-left" => (vx, vy, vw / 2, vh),
            "half-right" => (vx + vw / 2, vy, vw / 2, vh),
            _ => (double.NaN, double.NaN, double.NaN, double.NaN),
        };
}

/// <summary>Home monitor (DIP): the screen containing a window handle.
/// Pad area modes split THIS monitor, never the whole virtual desktop
/// (dual monitors). Physical px from WinForms, scaled by the given
/// per-monitor DPI into DIPs. Falls back to the virtual screen.</summary>
public static class HomeMonitor
{
    public static (double l, double t, double w, double h) RectFor(
        IntPtr hwnd, double dpi,
        double vx, double vy, double vw, double vh)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return (vx, vy, vw, vh);
            var sc = System.Windows.Forms.Screen.FromHandle(hwnd);
            if (sc == null) return (vx, vy, vw, vh);
            if (dpi < 0.5 || dpi > 4) dpi = 1.0;
            var b = sc.Bounds;
            if (b.Width < 100 || b.Height < 100) return (vx, vy, vw, vh);
            return (b.Left / dpi, b.Top / dpi, b.Width / dpi, b.Height / dpi);
        }
        catch { return (vx, vy, vw, vh); }
    }
}
