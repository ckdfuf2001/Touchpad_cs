using System;
using System.IO;
using System.Text.Json;

namespace TouchPadCloneV2.Core;

public sealed class GestureMap
{
    public string Tap { get; set; } = "left_click";
    public string DoubleTap { get; set; } = "double_click";
    public string TripleTap { get; set; } = "triple_click";
    public string LongPress { get; set; } = "right_click";
    public string SecondHold { get; set; } = "drag_hold";
    public string TwoFingerTap { get; set; } = "right_click";
    public string SwipeUp { get; set; } = "wheel_up";
    public string SwipeDown { get; set; } = "wheel_down";
    public string SwipeLeft { get; set; } = "browser_back";
    public string SwipeRight { get; set; } = "browser_forward";

    public static readonly string[] Actions =
    [
        "none", "left_click", "right_click", "middle_click", "double_click",
        "triple_click", "drag_hold", "wheel_up", "wheel_down",
        "browser_back", "browser_forward", "assist_pad",
    ];
}

public sealed class StripGestureMap
{
    public string Tap { get; set; } = "toggle_modes";
    public string SwipeLeft { get; set; } = "prev_layout";
    public string SwipeRight { get; set; } = "next_layout";
    public string SwipeUp { get; set; } = "show_modes";
    public string SwipeDown { get; set; } = "show_modes";

    public static readonly string[] Actions =
    [
        "none", "prev_layout", "next_layout", "show_modes", "toggle_modes",
        "open_settings", "toggle_fullscreen", "show_assist", "toggle_pad",
        "center_fake",
    ];
}

public sealed class AppSettings
{
    public double Speed { get; set; } = 1.6;
    public int WheelStep { get; set; } = 120;
    public double Opacity { get; set; } = 0.6;
    public double PadWidth { get; set; } = 340;
    public double PadHeight { get; set; } = 260;
    public string Layout { get; set; } = "floatpad";
    public string PresetFile { get; set; } = "";
    public bool TapToClick { get; set; } = true;
    public bool SwapButtons { get; set; } = false;
    public bool FakeCursor { get; set; } = true;
    public string CursorStyle { get; set; } = "cyan";
    public bool ShowFakeArrow { get; set; } = true;
    public int LongPressMs { get; set; } = 500;
    /// <summary>
    /// Hold vs drag threshold: travel in DIP within the last 150ms above
    /// which a press counts as a drag and its long-press dies. It MUST be a
    /// rate, not a distance from the press point: this panel reports a
    /// steady ~0.4 DIP/ms creep for a resting finger (65 DIP/150ms) while a
    /// real drag reaches ~2.5 DIP/ms (375 DIP/150ms). A distance rule
    /// integrates the creep and cancels every hold within ~150ms.
    /// </summary>
    public double HoldCancelDip { get; set; } = 100;
    /// <summary>
    /// Pointer gain while a hold is pending, 1.0 = off (immediate movement,
    /// the default). Below 1.0 the panel's resting creep (~0.4 DIP/ms, which
    /// drags the cursor hundreds of px mid-hold) is softened, blending back
    /// to full gain as the stroke becomes a real drag.
    /// NOTE: do not "fix" the creep by pinning the pointer instead - normal
    /// movement on this panel is the same speed as the creep (0.33-0.81
    /// DIP/ms measured), so any pin threshold either never releases (the pad
    /// feels dead for 500ms) or releases on the creep anyway.
    /// </summary>
    public double HoldDamp { get; set; } = 1.0;
    /// <summary>
    /// How the real system cursor relates to the virtual one.
    /// "preserve" (default): the physical mouse is never displaced - every
    ///   synthetic click/drag/wheel hands the real cursor back to where the
    ///   mouse left it, and mouse input never steers the virtual cursor.
    ///   Hover/tooltip feedback follows the physical cursor between actions.
    /// "unified": legacy behaviour - the real cursor is parked under the
    ///   fake one and a mouse press hands control back to the system cursor
    ///   (one cursor identity, hover follows the virtual cursor).
    /// </summary>
    public string PhysicalMouseMode { get; set; } = "preserve";
    /// <summary>Multi-tap chain window (tap-tap, tap-tap-tap).</summary>
    public int MultiTapMs { get; set; } = 600;
    /// <summary>Gesture profile per layout family (settings tabs).</summary>
    public GestureMap Gestures { get; set; } = new();
    public GestureMap ArtistGestures { get; set; } = new();
    public GestureMap VirtualGestures { get; set; } = new();

    public GestureMap ActiveGestures(string layoutName)
    {
        if (layoutName.Contains("Artist", StringComparison.OrdinalIgnoreCase))
            return ArtistGestures;
        if (layoutName.Contains("virtual", StringComparison.OrdinalIgnoreCase))
            return VirtualGestures;
        return Gestures;
    }

    /// <summary>Top-strip gestures, user-mappable (General tab).</summary>
    public StripGestureMap StripGestures { get; set; } = new();

    public static string Path =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TouchPadClone", "settings.json");

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path));
                if (s != null)
                {
                    s.Gestures ??= new();
                    s.ArtistGestures ??= new();
                    s.VirtualGestures ??= new();
                    s.StripGestures ??= new();
                    return s;
                }
            }
        }
        catch { }
        return new AppSettings();
    }
}
