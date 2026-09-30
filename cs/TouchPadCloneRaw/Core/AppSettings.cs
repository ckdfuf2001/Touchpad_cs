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
    // Double-touch + hold (no release): click, then hold - moving drags.
    // The reference default ("2nd tap and hold: click then hold, drag on move").
    // Double-touch + hold (no release): press-and-hold, NO extra click -
    // moving drags. The reference "2nd tap and hold: Drag".
    public string SecondHold { get; set; } = "drag";
    public string TwoFingerTap { get; set; } = "right_click";
    public string SwipeUp { get; set; } = "wheel_up";
    public string SwipeDown { get; set; } = "wheel_down";
    public string SwipeLeft { get; set; } = "browser_back";
    public string SwipeRight { get; set; } = "browser_forward";

    public static readonly string[] Actions =
    [
    "none", "left_click", "right_click", "middle_click", "double_click",
    "triple_click", "drag", "drag_hold", "wheel_up", "wheel_down",
    "browser_back", "browser_forward", "assist_pad", "toggle_fake",
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
        "center_fake", "toggle_fake",
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
    public int LongPressMs { get; set; } = 650;
    /// <summary>
    /// Hold vs drag threshold: NET travel in DIP within the last 150ms above
    /// which a press counts as a drag and its long-press dies. It MUST be a
    /// rate, not a distance from the press point.
    ///
    /// Measured on this panel (log analysis of real presses, DIP per 150ms):
    ///   resting finger   ~0.06   (0.4 DIP per SECOND of drift)
    ///   slow deliberate  ~19     (0.13 DIP/ms - fine positioning)
    ///   real drag        104-150 (0.7-1.0 DIP/ms)
    /// The old default of 100 sat above the SLOW movement, so anything short
    /// of a real flick was read as "still" and every attempt ended in a
    /// right-click ("only a big move avoids the long press"). The default
    /// belongs in the log between the drift and the slow movement: 10 leaves
    /// ~165x over the noise floor and still catches the slowest move.
    /// </summary>
    public double HoldCancelDip { get; set; } = 10;
    /// <summary>
    /// Pointer gain while a hold is pending, 1.0 = off (immediate movement,
    /// the default). Below 1.0 a drifting finger is softened, blending back
    /// to full gain as the stroke becomes a real drag.
    /// NOTE: do not "fix" the drift by pinning the pointer instead - normal
    /// movement on this panel is faster than the drift, so any pin threshold
    /// either never releases (the pad feels dead for 500ms) or releases on
    /// the drift anyway.
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
    /// <summary>
    /// Multi-tap chain window (tap-tap, tap-tap-tap).
    ///
    /// 900 rather than the 600 it was: measured on the device, a natural
    /// double tap lands 700-900ms apart (UP of the first to DOWN of the
    /// second), so 600 missed almost every one and a double tap came out as
    /// two separate single clicks - "double touch produces no events at all",
    /// with not one double_click in a whole session's log. A touch pad should
    /// be more forgiving than a mouse button, which is what this window is.
    /// </summary>
    public int MultiTapMs { get; set; } = 900;
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

    /// <summary>
    /// <summary>
    /// Legacy "drag_hold" (click-then-hold) selections move forward to the
    /// press-and-hold Drag default: double-hold maintains left-down with no
    /// extra click. Explicit "drag_hold" stays available for click-then-hold.
    /// Chained multi-taps fired double_click (2) / triple_click (2+) on top
    /// of the first tap's click (triple/quadruple total): single (OS pairs)
    /// gives correct double/triple counts.
    /// </summary>
    private static void MigrateLegacySecondHold(AppSettings settings)
    {
        foreach (var map in new[]
            { settings.Gestures, settings.ArtistGestures, settings.VirtualGestures })
        {
            if (map != null && map.SecondHold == "drag_hold")
                map.SecondHold = "drag";
            if (map != null && map.DoubleTap == "double_click")
                map.DoubleTap = "left_click";
            if (map != null && map.TripleTap == "triple_click")
                map.TripleTap = "left_click";
        }
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
                    MigrateLegacySecondHold(s);
                    return s;
                }
            }
        }
        catch { }
        return new AppSettings();
    }
}
