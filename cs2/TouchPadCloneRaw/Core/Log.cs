using System;
using System.IO;

namespace TouchPadRaw.Core;

/// <summary>Timestamped file log. Raw intake + HUD clicks read this.</summary>
public static class Log
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetEnvironmentVariable("TEMP") ?? ".",
        "touchpad_raw.log");

    private static readonly object _gate = new();

    public static void Write(string line)
    {
        try
        {
            lock (_gate)
                File.AppendAllText(Path,
                    $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch { }
    }
}
