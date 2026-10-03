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
