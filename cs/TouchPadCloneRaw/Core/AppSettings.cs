using System;
using System.Collections.Generic;
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
    // Windows built-ins (specs in ActionRunner.WinActions).
    "win_show_desktop", "win_task_view", "win_close_window",
    "win_snap_left", "win_snap_right", "win_maximize", "win_minimize",
    "win_desk_prev", "win_desk_next", "win_desk_new", "win_desk_close",
    "win_explorer", "win_settings", "win_run", "win_search",
    "win_taskmgr", "win_notify", "win_quickset", "win_screenshot",
    "win_emoji", "win_lock", "win_min_all", "win_unmin_all",
    "win_alt_tab", "win_print", "win_menu",
    "win_vol_mute", "win_vol_up", "win_vol_down",
    "win_media_play", "win_media_next", "win_media_prev",
    ];
}

public sealed class StripGestureMap
{
    public string Tap { get; set; } = "toggle_modes";
    public string SwipeLeft { get; set; } = "menu_prev";
    public string SwipeRight { get; set; } = "menu_next";
    public string SwipeUp { get; set; } = "menu_up";
    public string SwipeDown { get; set; } = "menu_down";

    public static readonly string[] Actions =
    [
        "none", "prev_layout", "next_layout", "show_modes", "toggle_modes",
        "open_settings", "toggle_fullscreen", "show_assist", "toggle_pad",
        "center_fake", "toggle_fake",
        "menu_prev", "menu_next", "menu_up", "menu_down",
    ];
}

/// <summary>One configurable strip row item: a group radio (pad select)
/// or an individual toggle / custom (program/cmd).</summary>
public sealed class StripRowItem
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "radio";    // radio | toggle
    public string Target { get; set; } = "";       // pad name or action key
    public bool Fixed { get; set; } = false;      // fixed items can't be deleted
    public bool Visible { get; set; } = true;
}

/// <summary>Per-family color overrides (null/empty = follow General).</summary>
public sealed class PadColorSet
{
    public string? EffectColor { get; set; }
    public string? ZoneLeft { get; set; }
    public string? ZoneRight { get; set; }
    public string? ZoneWheel { get; set; }
    public string? ZonePad { get; set; }
    public string? ZoneBg { get; set; }
}

/// <summary>One strip menu cell. Serialized as
/// Label|Kind|Value|Color|Image|TextColor|W|H. Kind: 기능 | cmd |
/// 프로그램 | 단축키. Empty color = none, empty text color = gray,
/// 0 size = auto-fit to text.</summary>
public sealed class StripCell
{
    public string Label = "";
    public string Kind = "기능";
    public string Value = "";
    public string Color = "";
    public string Image = "";
    public string TextColor = "";
    public double CellW;
    public double CellH;
    public bool IsEmpty => Label == "" && Value == "";
    public static StripCell Parse(string? s)
    {
        var c = new StripCell();
        if (string.IsNullOrWhiteSpace(s)) return c;
        var p = s.Split("|");
        c.Label = p[0].Trim();
        if (p.Length > 1 && p[1].Trim() != "") c.Kind = p[1].Trim().ToLowerInvariant();
        if (p.Length > 2) c.Value = p[2].Trim();
        if (p.Length > 3) c.Color = p[3].Trim();
        if (p.Length > 4) c.Image = p[4].Trim();
        if (p.Length > 5) c.TextColor = p[5].Trim();
        if (p.Length > 6) double.TryParse(p[6].Trim(), out c.CellW);
        if (p.Length > 7) double.TryParse(p[7].Trim(), out c.CellH);
        if (c.Label == "") c.Label = c.Value;
        if (c.Value == "" && (c.Kind == "기능" || c.Kind == "layout")) c.Value = "layout:" + c.Label;
        return c;
    }
    public override string ToString() =>
        $"{Label}|{Kind}|{Value}|{Color}|{Image}|{TextColor}|{CellW}|{CellH}";
}

public sealed class StripRow
{
    public List<string> Cells { get; set; } = new() { "", "", "", "" };
}

/// <summary>Per-pad override: area mode, visibility, opacity, and the
/// touch globals (speed / judge time / scroll-invert / swap / tap-click).
/// Same-as-global by default; any set value overwrites for that pad.</summary>
public sealed class PadConfig
{
    public string AreaMode { get; set; } = "default"; // default | full | half-left | half-right
    public bool Visible { get; set; } = true;
    public double Opacity { get; set; } = -1;     // -1 = follow global
    public double Speed { get; set; } = -1;       // -1 = follow global
    public int TapJudgeMs { get; set; } = -1;     // -1 = follow global
    public bool? ScrollInvert { get; set; }       // null = follow global
    public bool? SwapButtons { get; set; }        // null = follow global
    public bool? TapToClick { get; set; }         // null = follow global
}

/// <summary>One custom button on an artist/virtual pad.</summary>
public sealed class PadButton
{
    public string Label { get; set; } = "";
    public string Action { get; set; } = "";       // key into Actions
}

public sealed class ArtistConfig
{
    public string Position { get; set; } = "bottom"; // top | bottom | left | right | hidden
    public List<PadButton> Buttons { get; set; } = new();
}

public sealed class VirtualConfig
{
    public string Position { get; set; } = "hidden"; // top | bottom | left | right | grid | hidden
    public List<PadButton> Buttons { get; set; } = new();
}

/// <summary>A runnable action: a mouse action, a program, or a cmd line.</summary>
public sealed class ActionDef
{
    public string Kind { get; set; } = "mouse";   // mouse | program | cmd
    public string Value { get; set; } = "left_click";
    public string Path { get; set; } = "";
    public string Args { get; set; } = "";
}

public sealed class AppSettings
{
    public double Speed { get; set; } = 1.6;
    public bool ScrollInvert { get; set; } = false;
    public int WheelStep { get; set; } = 120;
    public double Opacity { get; set; } = 0.6;
    public double PadWidth { get; set; } = 340;
    public double PadHeight { get; set; } = 260;
    public string Layout { get; set; } = "floatpad";
    public string PresetFile { get; set; } = "";
    public bool TapToClick { get; set; } = true;
    public bool SwapButtons { get; set; } = false;
    /// <summary>Bottom zone/status/actual labels. Default off: the launch
    /// environment is not reliable (Explorer keeps a stale copy of deleted
    /// env vars), so this file-backed flag owns the decision.</summary>
    public bool DebugLabels { get; set; } = false;
    public bool FakeCursor { get; set; } = true;
    public string CursorStyle { get; set; } = "cyan";
    public bool ShowFakeArrow { get; set; } = true;
    /// <summary>Legacy file-compat only: the engine now uses the single
    /// TapJudgeMs for both tap and hold. Kept so old files still load.</summary>
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
    /// <summary>
    /// Drag-start travel (pad DIP, Manhattan net from press point): a press
    /// must move this far before it becomes a move-grab (button down).
    /// Above the tap slide, below a deliberate stroke. Cursor tracking
    /// itself is unaffected - only the button waits.
    /// </summary>
    public int DragStartDip { get; set; } = 30;
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

    /// <summary>Tap judgment window ms (first touch to action decision).</summary>
    public int TapJudgeMs { get; set; } = 500;

    /// <summary>Legacy row model (superseded by StripLayout cells below).
    /// Kept so old files still load; nothing reads it anymore.</summary>
    public string StripPosition { get; set; } = "top";  // top | bottom | left | right
    public double StripWidth { get; set; } = 380;
    public double StripHeight { get; set; } = 26;
    public bool StripVisible { get; set; } = true;
    public List<StripRowItem> StripRow1 { get; set; } = new()
    {
        new StripRowItem { Name = "floatpad", Kind = "radio", Target = "floatpad", Fixed = true },
        new StripRowItem { Name = "leftpad", Kind = "radio", Target = "leftpad", Fixed = true },
        new StripRowItem { Name = "rightpad", Kind = "radio", Target = "rightpad", Fixed = true },
        new StripRowItem { Name = "fullscreen", Kind = "radio", Target = "fullscreen", Fixed = true },
    };
    public List<StripRowItem> StripRow2 { get; set; } = new()
    {
        new StripRowItem { Name = "artist", Kind = "toggle", Target = "artist", Fixed = true },
        new StripRowItem { Name = "virtual", Kind = "toggle", Target = "virtual", Fixed = true },
    };

    /// <summary>Strip placement (General tab): edge top|bottom|left|right,
    /// side left|right, px offset along the edge (-1 = centered).</summary>
    public string StripEdge { get; set; } = "top";
    public string StripSide { get; set; } = "left";
    public int StripPx { get; set; } = -1;

    /// <summary>Strip monitor (WinForms DeviceName, e.g. \\.\DISPLAY1).
    /// Empty = primary. A vanished monitor falls back to primary live.</summary>
    public string StripMonitor { get; set; } = "";

    /// <summary>Strip bar color + opacity (General tab).</summary>
    public string StripColor { get; set; } = "#10131A";
    public double StripOpacity { get; set; } = 0.35;

    /// <summary>Windows logon auto-start (mirrors the Run registry key;
    /// the registry is the truth, this keeps the intent across moves).</summary>
    public bool AutoStart { get; set; } = false;

    /// <summary>Retired UI (was the reference's event-gap slider): the single
    /// TapJudgeMs is the judge time now. Kept so files still load.</summary>
    public int EventGapMs { get; set; } = 500;

    /// <summary>Pad colors (General tab). Stored here; pad render wiring
    /// follows (ZonePalette stays the live source until then).</summary>
    public string EffectColor { get; set; } = "#FF7FE0A8";
    /// <summary>Strip label text color.</summary>
    public string StripTextColor { get; set; } = "#FF9AA6BD";
    public string ZoneLeft { get; set; } = "#40206040";
    public string ZoneRight { get; set; } = "#40402060";
    public string ZoneWheel { get; set; } = "#40602020";
    public string ZonePad { get; set; } = "#30404040";
    public string ZoneBg { get; set; } = "#40204060";
    public List<string> CustomColors { get; set; } = new();
    public PadColorSet FloatColors { get; set; } = new();
    public PadColorSet ArtistColors { get; set; } = new();
    public PadColorSet VirtualColors { get; set; } = new();

    public string ZoneColor(string role) => role switch
    {
        "left" => ZoneLeft,
        "right" => ZoneRight,
        "wheel" => ZoneWheel,
        "pad" => ZonePad,
        "bg" => ZoneBg,
        _ => "",
    };

    public PadColorSet ForColors(string layoutName)
    {
        if (layoutName.Contains("Artist", StringComparison.OrdinalIgnoreCase))
            return ArtistColors;
        if (layoutName.Contains("virtual", StringComparison.OrdinalIgnoreCase))
            return VirtualColors;
        return FloatColors;
    }

    private static string PickColor(string? o, string d) =>
        string.IsNullOrWhiteSpace(o) ? d : o;

    public string EffEffect(string layoutName) => PickColor(ForColors(layoutName).EffectColor, EffectColor);
    public string EffZoneLeft(string layoutName) => PickColor(ForColors(layoutName).ZoneLeft, ZoneLeft);
    public string EffZoneRight(string layoutName) => PickColor(ForColors(layoutName).ZoneRight, ZoneRight);
    public string EffZoneWheel(string layoutName) => PickColor(ForColors(layoutName).ZoneWheel, ZoneWheel);
    public string EffZonePad(string layoutName) => PickColor(ForColors(layoutName).ZonePad, ZonePad);
    public string EffBackground(string layoutName) => PickColor(ForColors(layoutName).ZoneBg, ZoneBg);

    /// <summary>Strip menu cells (General tab editor, picker reads them).
    /// Format per cell: Label|Kind|Value|Color|Image.</summary>
    public System.Collections.ObjectModel.ObservableCollection<StripRow> StripLayout { get; set; } = DefaultStripLayout();

    public static System.Collections.ObjectModel.ObservableCollection<StripRow> DefaultStripLayout()
    {
        var rows = new System.Collections.ObjectModel.ObservableCollection<StripRow>();
        rows.Add(new StripRow { Cells = new() { "float|기능|layout:floatpad|", "Left|기능|layout:leftpad|", "Right|기능|layout:rightpad|", "Full Screen|기능|layout:fullscreen|" } });
        rows.Add(new StripRow { Cells = new() { "ArtistPad|기능|layout:ArtistPad|", "Virtual Ctrl|기능|layout:virtualctrls|", "", "" } });
        return rows;
    }

    /// <summary>Per-pad overrides (same-as-global + overwrite).</summary>
    public Dictionary<string, PadConfig> Pads { get; set; } = new()
    {
        ["floatpad"] = new PadConfig(),
        ["leftpad"] = new PadConfig { AreaMode = "half-left" },
        ["rightpad"] = new PadConfig { AreaMode = "half-right" },
        ["fullscreen"] = new PadConfig { AreaMode = "full" },
        ["artist"] = new PadConfig(),
        ["virtual"] = new PadConfig(),
    };

    public ArtistConfig Artist { get; set; } = new();
    public VirtualConfig Virtual { get; set; } = new();

    /// <summary>Named custom actions (program/cmd/mouse) for strip items
    /// and aux pad buttons.</summary>
    public Dictionary<string, ActionDef> Actions { get; set; } = new();

    public PadConfig Pad(string name) =>
        Pads.TryGetValue(name, out var p) ? p : new PadConfig();

    /// <summary>Layout name -&gt; Pads key (artist/virtual families).</summary>
    public static string PadFamilyKey(string layoutName)
    {
        if (layoutName.Contains("artist", StringComparison.OrdinalIgnoreCase))
            return "artist";
        if (layoutName.Contains("virtual", StringComparison.OrdinalIgnoreCase))
            return "virtual";
        return layoutName.ToLowerInvariant();
    }

    /// <summary>Settings live in Documents\Default Project\Touchpad_cs
    /// (user-visible folder, not AppData).</summary>
    public static string Path =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Default Project", "Touchpad_cs", "settings.json");

    /// <summary>Previous location (kept for one-time migration).</summary>
    private static string LegacyPath =>
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
        }
    }

    public static AppSettings Load()
    {
        try
        {
            // One-time migration from the old AppData location.
            if (!File.Exists(Path) && File.Exists(LegacyPath))
            {
                try
                {
                    Directory.CreateDirectory(
                        System.IO.Path.GetDirectoryName(Path)!);
                    File.Copy(LegacyPath, Path);
                }
                catch { }
            }
            if (File.Exists(Path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path));
                if (s != null)
                {
                    s.Gestures ??= new();
                    s.ArtistGestures ??= new();
                    s.VirtualGestures ??= new();
                    s.StripGestures ??= new();
                    // Strip swipes navigate the menu grid now (not preset
                    // order, not the mode panel): forward old defaults.
                    // (No UI ever set these deliberately.)
                    if (s.StripGestures.SwipeUp == "show_modes")
                        s.StripGestures.SwipeUp = "menu_up";
                    if (s.StripGestures.SwipeDown == "show_modes")
                        s.StripGestures.SwipeDown = "menu_down";
                    if (s.StripGestures.SwipeUp == "prev_layout")
                        s.StripGestures.SwipeUp = "menu_up";
                    if (s.StripGestures.SwipeDown == "next_layout")
                        s.StripGestures.SwipeDown = "menu_down";
                    if (s.StripGestures.SwipeLeft == "prev_layout")
                        s.StripGestures.SwipeLeft = "menu_prev";
                    if (s.StripGestures.SwipeRight == "next_layout")
                        s.StripGestures.SwipeRight = "menu_next";
                    s.StripRow1 ??= new();
                    s.StripRow2 ??= new();
                    s.Pads ??= new();
                    s.Artist ??= new();
                    s.Virtual ??= new();
                    s.Actions ??= new();
                    s.EffectColor ??= "#FF7FE0A8";
                    s.ZoneLeft ??= "#40206040";
                    s.ZoneRight ??= "#40402060";
                    s.ZoneWheel ??= "#40602020";
                    s.ZonePad ??= "#30404040";
                    if (s.ZoneBg == null || s.ZoneBg == "#8C1B1E24") s.ZoneBg = "#40204060";
                    s.CustomColors ??= new();
                    s.FloatColors ??= new();
                    s.ArtistColors ??= new();
                    s.VirtualColors ??= new();
                    if (s.StripLayout == null || s.StripLayout.Count == 0) s.StripLayout = DefaultStripLayout();

                    foreach (var r in s.StripLayout)
                    {
                        while (r.Cells.Count < 4) r.Cells.Add("");
                        for (int i = 0; i < r.Cells.Count; i++)
                        {
                            var c = StripCell.Parse(r.Cells[i]);
                            if (c.IsEmpty) continue;
                            bool ch = false;
                            if (c.Kind == "layout") { c.Kind = "기능"; c.Value = "layout:" + c.Value; ch = true; }
                            else if (c.Kind == "hotkey") { c.Kind = "단축키"; ch = true; }
                            else if (c.Kind == "run") { c.Kind = "프로그램"; ch = true; }
                            if (ch) r.Cells[i] = c.ToString();
                        }
                    }
                    MigrateLegacySecondHold(s);
                    return s;
                }
            }
        }
        catch { }
        return new AppSettings();
    }
}
