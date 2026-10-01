using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TouchPadRaw.Core;

/// <summary>Minimal settings (file-adjustable). No UI yet.</summary>
public sealed class Settings
{
    public double Speed { get; set; } = 1.6;
    public int LongPressMs { get; set; } = 500;
    public int MultiTapMs { get; set; } = 900;
    public int DragStartDip { get; set; } = 30;

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TouchPadRaw", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path));
                if (s != null) return s;
            }
        }
        catch { }
        return new Settings();
    }
}
