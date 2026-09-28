using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;

namespace TouchPadCloneV2.Core;

/// <summary>File log, active only with TOUCHPAD_DEBUG=1 (or sentinel file).
/// Fire-and-forget: UI thread only enqueues (microseconds); a background
/// thread batches to disk. Synchronous per-event file I/O on the input path
/// visibly lags long drags (AV rescan per append). Ring-capped file.</summary>
public static class DebugLog
{
    private static readonly bool On =
        Environment.GetEnvironmentVariable("TOUCHPAD_DEBUG") == "1" ||
        File.Exists(System.IO.Path.Combine(
            Environment.GetEnvironmentVariable("TEMP") ?? ".",
            "TOUCHPAD_DEBUG"));

    private const int MaxLines = 1000;
    private static readonly ConcurrentQueue<string> _q = new();
    private static int _queued;

    static DebugLog()
    {
        var t = new Thread(FlushLoop) { IsBackground = true, Name = "dbglog" };
        t.Start();
    }

    public static string Path =>
        System.IO.Path.Combine(
            Environment.GetEnvironmentVariable("TEMP") ?? ".",
            "touchpad_v2.log");

    public static void Clear()
    {
        if (!On) return;
        try { File.WriteAllText(Path, $"=== V2 START {DateTime.Now:HH:mm:ss} ===\n"); }
        catch { }
    }

    public static void Write(string msg)
    {
        if (!On) return;
        _q.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {msg}");
        Interlocked.Increment(ref _queued);
    }

    private static void FlushLoop()
    {
        var buf = new System.Collections.Generic.List<string>(256);
        while (true)
        {
            try
            {
                Thread.Sleep(250);
                buf.Clear();
                while (_q.TryDequeue(out var line))
                {
                    buf.Add(line);
                    if (buf.Count >= 512) break;
                }
                if (buf.Count == 0) continue;
                File.AppendAllLines(Path, buf);
                _queued -= buf.Count;
                if (_queued > MaxLines) Trim();
            }
            catch { }
        }
    }

    private static void Trim()
    {
        try
        {
            var lines = File.ReadAllLines(Path);
            if (lines.Length > MaxLines)
                File.WriteAllLines(Path, lines.Skip(lines.Length - MaxLines / 2));
        }
        catch { }
    }
}
