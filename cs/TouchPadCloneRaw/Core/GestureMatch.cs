using System;
using System.Collections.Generic;

namespace TouchPadCloneV2.Core;

/// <summary>Two-finger gesture matching ($1-lite, NO rotation: direction
/// matters here). A session's joint-centroid path is resampled to Size
/// points, translated to origin, uniformly scaled; the mean point
/// distance to a template decides. Single-finger paths never enter:
/// gestures are two fingers or more, always.</summary>
public static class GestureMatch
{
    public const int Size = 32;
    public const double Threshold = 0.30;
    public const double MinTravelDip = 80;

    public static double PathLength(List<(double x, double y)> pts)
    {
        double len = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            double dx = pts[i].x - pts[i - 1].x, dy = pts[i].y - pts[i - 1].y;
            len += Math.Sqrt(dx * dx + dy * dy);
        }
        return len;
    }

    public static List<(double x, double y)> Resample(List<(double x, double y)> pts, int n)
    {
        var out_ = new List<(double x, double y)>(n);
        if (pts.Count == 0) return out_;
        if (pts.Count == 1)
        {
            for (int i = 0; i < n; i++) out_.Add(pts[0]);
            return out_;
        }
        double total = PathLength(pts);
        if (total < 1e-9)
        {
            for (int i = 0; i < n; i++) out_.Add(pts[0]);
            return out_;
        }
        double step = total / (n - 1);
        double acc = 0;
        out_.Add(pts[0]);
        int j = 1;
        var prev = pts[0];
        while (out_.Count < n - 1 && j < pts.Count)
        {
            var cur = pts[j];
            double dx = cur.x - prev.x, dy = cur.y - prev.y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (acc + d >= step && d > 1e-9)
            {
                double t = (step - acc) / d;
                var np = (prev.x + dx * t, prev.y + dy * t);
                out_.Add(np);
                prev = np;
                acc = 0;
            }
            else
            {
                acc += d;
                prev = cur;
                j++;
            }
        }
        while (out_.Count < n) out_.Add(pts[pts.Count - 1]);
        return out_;
    }

    /// <summary>Centroid to origin, uniform scale by max extent.</summary>
    public static List<(double x, double y)> Normalize(List<(double x, double y)> pts)
    {
        var out_ = new List<(double x, double y)>(pts.Count);
        if (pts.Count == 0) return out_;
        double cx = 0, cy = 0;
        foreach (var p in pts) { cx += p.x; cy += p.y; }
        cx /= pts.Count; cy /= pts.Count;
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var p in pts)
        {
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.y < minY) minY = p.y;
            if (p.y > maxY) maxY = p.y;
        }
        double scale = Math.Max(maxX - minX, maxY - minY);
        if (scale < 1e-6) scale = 1;
        foreach (var p in pts)
            out_.Add(((p.x - cx) / scale, (p.y - cy) / scale));
        return out_;
    }

    public static List<double> Flatten(List<(double x, double y)> pts)
    {
        var out_ = new List<double>(pts.Count * 2);
        foreach (var p in pts) { out_.Add(p.x); out_.Add(p.y); }
        return out_;
    }

    public static List<(double x, double y)> Expand(List<double> flat)
    {
        var out_ = new List<(double x, double y)>(flat.Count / 2);
        for (int i = 0; i + 1 < flat.Count; i += 2)
            out_.Add((flat[i], flat[i + 1]));
        return out_;
    }

    public static double Score(List<(double x, double y)> a, List<(double x, double y)> b)
    {
        if (a.Count != b.Count || a.Count == 0) return double.MaxValue;
        double sum = 0;
        for (int i = 0; i < a.Count; i++)
        {
            double dx = a[i].x - b[i].x, dy = a[i].y - b[i].y;
            sum += Math.Sqrt(dx * dx + dy * dy);
        }
        return sum / a.Count;
    }

    /// <summary>Best template for a finished two-contact trail, or null
    /// when nothing is computable (too short, wrong fingers, empty lib).
    /// Caller fires when score &lt; Threshold; near-misses (&lt; 0.6)
    /// are logged for threshold tuning.</summary>
    public static (RecordedGesture g, double score)? MatchTwo(
        List<RecordedGesture> lib, List<(double x, double y)> trail, int maxN)
    {
        try
        {
            if (lib == null || lib.Count == 0) return null;
            if (maxN < 2 || trail.Count < 4) return null;
            if (PathLength(trail) < MinTravelDip) return null;
            var norm = Normalize(Resample(trail, Size));
            RecordedGesture? best = null;
            double bestScore = double.MaxValue;
            foreach (var g in lib)
            {
                if (g == null || g.Fingers != maxN) continue;
                if (g.Points == null || g.Points.Count != Size * 2) continue;
                double s = Score(norm, Expand(g.Points));
                if (s < bestScore) { bestScore = s; best = g; }
            }
            if (best == null) return null;
            return (best, bestScore);
        }
        catch { return null; }
    }
}

/// <summary>Gesture recording state: the settings tab arms it, the pad
/// feeds nothing itself - the window owns the trail (needed for matching
/// anyway) and hands it over at session end. One-shot callback.</summary>
public static class GestureRecorder
{
    public static bool IsRecording;
    public static Action<RecordedGesture?>? OnFinished;

    public static void Cancel()
    {
        IsRecording = false;
        OnFinished = null;
    }

    /// <summary>Called at every session end; completes only while armed.
/// Two fingers or more required (maxN), else the callback gets null.</summary>
    public static void CompleteIfRecording(List<(double x, double y)> trail, int maxN)
    {
        if (!IsRecording) return;
        IsRecording = false;
        RecordedGesture? g = null;
        try
        {
            if (maxN >= 2 && trail.Count >= 4
                && GestureMatch.PathLength(trail) >= GestureMatch.MinTravelDip)
            {
                var norm = GestureMatch.Normalize(GestureMatch.Resample(trail, GestureMatch.Size));
                g = new RecordedGesture
                {
                    Name = "",
                    Action = "none",
                    Fingers = Math.Min(maxN, 5),
                    Points = GestureMatch.Flatten(norm),
                };
            }
        }
        catch { g = null; }
        try
        {
            var cb = OnFinished;
            OnFinished = null;
            cb?.Invoke(g);
        }
        catch { }
    }
}
