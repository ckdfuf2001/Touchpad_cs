using System;
using System.Collections.Generic;
using System.Linq;

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

    /// <summary>Single shared placement used by the app and the layout
    /// preview alike: full/half fill the home monitor, custom clamps the
    /// stored rect into it, default anchors a WxH window bottom-right.</summary>
    public static (double l, double t, double w, double h) Place(
        string mode,
        (double l, double t, double w, double h) home,
        (double w, double h) size,
        (double x, double y, double w, double h) custom)
    {
        switch (mode)
        {
            case "full": return (home.l, home.t, home.w, home.h);
            case "half-left": return (home.l, home.t, home.w / 2, home.h);
            case "half-right":
                return (home.l + home.w / 2, home.t, home.w / 2, home.h);
            case "custom" when custom.w > 0 && custom.h > 0:
            {
                double w = System.Math.Min(custom.w, home.w);
                double h = System.Math.Min(custom.h, home.h);
                double l = System.Math.Max(home.l,
                    System.Math.Min(home.l + home.w - w, home.l + custom.x));
                double t = System.Math.Max(home.t,
                    System.Math.Min(home.t + home.h - h, home.t + custom.y));
                return (l, t, w, h);
            }
            default:
                return (home.l + home.w - size.w - 40,
                    home.t + home.h - size.h - 120, size.w, size.h);
        }
    }
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

/// <summary>Monitor picker backing (strip settings). DeviceName-keyed;
/// a vanished monitor resolves to primary, then to the first screen.</summary>
public static class MonitorList
{
    public static List<(string device, string label, bool primary)> All()
    {
        var list = new List<(string, string, bool)>();
        try
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            int n = 0;
            foreach (var sc in screens.OrderByDescending(s => s.Primary))
            {
                n++;
                string tag = sc.Primary ? "주" : n.ToString();
                list.Add((sc.DeviceName,
                    $"{tag} {sc.Bounds.Width}x{sc.Bounds.Height}", sc.Primary));
            }
        }
        catch { }
        if (list.Count == 0) list.Add(("", "주", true));
        return list;
    }

    public static System.Windows.Forms.Screen? Resolve(string? device)
    {
        try
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            if (!string.IsNullOrWhiteSpace(device))
                foreach (var sc in screens)
                    if (sc.DeviceName == device) return sc;
            foreach (var sc in screens)
                if (sc.Primary) return sc;
            if (screens.Length > 0) return screens[0];
        }
        catch { }
        return null;
    }
}
