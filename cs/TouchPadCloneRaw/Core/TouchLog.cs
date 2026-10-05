using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace TouchPadCloneV2.Core;

/// <summary>Timestamped file log. Raw intake + HUD clicks read this.
/// Async (queue + writer thread): Write must never block - it runs
/// inside the low-level mouse hook, where stalled file I/O times the
/// hook out and inputs slip through unswallowed/unlogged.</summary>
public static class Log
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetEnvironmentVariable("TEMP") ?? ".",
        "touchpad_raw.log");

    private static readonly ConcurrentQueue<string> _q = new();
    private static readonly AutoResetEvent _ev = new(false);

    static Log()
    {
        try
        {
            var w = new Thread(WriteLoop) { IsBackground = true, Name = "TouchLog" };
            w.Start();
        }
        catch { }
    }

    public static void Write(string line)
    {
        try
        {
            _q.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {line}");
            _ev.Set();
        }
        catch { }
    }

    private static void WriteLoop()
    {
        var sb = new StringBuilder();
        while (true)
        {
            try
            {
                _ev.WaitOne(250);
                sb.Clear();
                int n = 0;
                while (n < 2000 && _q.TryDequeue(out var s)) { sb.AppendLine(s); n++; }
                if (sb.Length > 0) File.AppendAllText(Path, sb.ToString());
            }
            catch { }
        }
    }
}
