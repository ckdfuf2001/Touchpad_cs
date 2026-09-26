using System;
using System.IO;

namespace TouchPadCloneV2.Core;

/// <summary>File log, active only with TOUCHPAD_DEBUG=1.</summary>
public static class DebugLog
{
    private static readonly bool On =
        Environment.GetEnvironmentVariable("TOUCHPAD_DEBUG") == "1" ||
        File.Exists(System.IO.Path.Combine(
            Environment.GetEnvironmentVariable("TEMP") ?? ".",
            "TOUCHPAD_DEBUG"));

    private static string Path =>
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
        try { File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); }
        catch { }
    }
}
