using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TouchPadCloneV2.Core;

/// <summary>
/// Tile kinds mirror the original TouchMousePointer INI vocabulary.
/// </summary>
public enum TileAction
{
    Pad, Frame, Click, Drag, Wheel, Key, Grip, Sys, Blank
}

public sealed class Tile
{
    public string Name = "";
    public string RawKind = "";
    public string Kind = "";          // lower-cased
    public double X, Y, W, H;         // 0..100 relative
    public List<string> Extras = new();
    public TileAction Action;
    public string ClickButton = "left"; // for Click/Drag
    public bool WheelHorizontal;
    public int KeyVk;                 // 0 = none
}

public sealed class Layout
{
    public string Name = "";
    public List<Tile> Tiles = new();
}

public static class Vk
{
    public static readonly Dictionary<string, int> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vk_control"] = 0x11, ["vk_shift"] = 0x10, ["vk_menu"] = 0x12,
        ["vk_space"] = 0x20, ["vk_return"] = 0x0D, ["vk_back"] = 0x08,
        ["vk_tab"] = 0x09, ["vk_escape"] = 0x1B,
        ["vk_left"] = 0x25, ["vk_up"] = 0x26, ["vk_right"] = 0x27, ["vk_down"] = 0x28,
        ["vk_f1"] = 0x70, ["vk_f2"] = 0x71, ["vk_f3"] = 0x72, ["vk_f4"] = 0x73,
        ["vk_f5"] = 0x74, ["vk_f6"] = 0x75, ["vk_f7"] = 0x76, ["vk_f8"] = 0x77,
        ["vk_f9"] = 0x78, ["vk_f10"] = 0x79, ["vk_f11"] = 0x7A, ["vk_f12"] = 0x7B,
        ["vk_lwin"] = 0x5B, ["vk_rwin"] = 0x5C,
        ["vk_numpad0"] = 0x60, ["vk_numpad1"] = 0x61, ["vk_numpad2"] = 0x62,
        ["vk_numpad3"] = 0x63, ["vk_numpad4"] = 0x64, ["vk_numpad5"] = 0x65,
        ["vk_numpad6"] = 0x66, ["vk_numpad7"] = 0x67, ["vk_numpad8"] = 0x68,
        ["vk_numpad9"] = 0x69, ["vk_multiply"] = 0x6A, ["vk_add"] = 0x6B,
        ["vk_subtract"] = 0x6D, ["vk_decimal"] = 0x6E, ["vk_divide"] = 0x6F,
        ["vk_home"] = 0x24, ["vk_end"] = 0x23, ["vk_prior"] = 0x21, ["vk_next"] = 0x22,
        ["vk_insert"] = 0x2D, ["vk_delete"] = 0x2E,
        ["vk_oem_5"] = 0xDC, ["vk_oem_plus"] = 0xBB, ["vk_oem_comma"] = 0xBC,
        ["vk_oem_minus"] = 0xBD, ["vk_oem_period"] = 0xBE,
        ["vk_media_play_pause"] = 0xB3, ["vk_volume_mute"] = 0xAD,
        ["vk_volume_down"] = 0xAE, ["vk_volume_up"] = 0xAF,
    };
}

/// <summary>
/// Original-syntax INI preset parser: [section] + tileNNN=TYPE,x,y,w,h[,extra…].
/// Values may contain '%' (e.g. shift%20alt), so no interpolation is applied.
/// </summary>
public static class PresetParser
{
    public static Dictionary<string, Layout> ParseFile(string path)
    {
        string text = Decode(File.ReadAllBytes(path)).TrimStart('﻿');
        var layouts = new Dictionary<string, Layout>(StringComparer.OrdinalIgnoreCase);
        Layout? cur = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r').Trim('﻿');
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                cur = new Layout { Name = line[1..^1].Trim().Trim('﻿') };
                layouts[cur.Name] = cur;
                continue;
            }
            if (cur == null) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();
            if (!key.StartsWith("tile", StringComparison.OrdinalIgnoreCase))
                continue; // scale=/aspect= hints are not needed for rendering
            var parts = val.Split(',');
            if (parts.Length < 5) continue;
            if (!double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double x)) x = 0;
            if (!double.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double y)) y = 0;
            if (!double.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double w)) w = 0;
            if (!double.TryParse(parts[4].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double h)) h = 0;
            var tile = new Tile
            {
                Name = key,
                RawKind = parts[0].Trim(),
                Kind = parts[0].Trim().ToLowerInvariant(),
                X = x, Y = y, W = w, H = h,
                Extras = parts.Skip(5).Select(p => p.Trim()).ToList(),
            };
            Classify(tile);
            cur.Tiles.Add(tile);
        }
        return layouts;
    }

    private static string Decode(byte[] data)
    {
        // Originals are UTF-16; ours are UTF-8. No code-page lookup
        // (CodePagesEncodingProvider is not referenced).
        foreach (var enc in new Encoding[] { Encoding.Unicode, Encoding.UTF8 })
        {
            try
            {
                string s = enc.GetString(data);
                if (s.Contains("tile", StringComparison.OrdinalIgnoreCase) || s.Contains('['))
                    return s;
            }
            catch { }
        }
        return Encoding.UTF8.GetString(data);
    }

    private static void Classify(Tile t)
    {
        string k = t.Kind;
        if (k is "lbtn" or "rbtn" or "mbtn")
        {
            t.Action = TileAction.Click;
            t.ClickButton = k == "lbtn" ? "left" : k == "rbtn" ? "right" : "middle";
        }
        else if (k.StartsWith("dragframe_"))
        {
            t.Action = TileAction.Drag;
            t.ClickButton = k.Contains("rbtn") ? "right" : k.Contains("mbtn") ? "middle" : "left";
        }
        else if (k is "wheel" or "wheel_no_mbtn" or "hwheel")
        {
            t.Action = TileAction.Wheel;
            t.WheelHorizontal = k == "hwheel";
        }
        else if (k == "pad") t.Action = TileAction.Pad;
        else if (k == "padframe") t.Action = TileAction.Frame;
        else if (k == "movegrip") t.Action = TileAction.Grip;
        else if (k is "menu" or "minimize" or "closebtn" or "blank"
                 or "assistpad" or "tabtip") t.Action = TileAction.Sys;
        else if (Vk.Map.TryGetValue(k, out int vk))
        {
            t.Action = TileAction.Key; t.KeyVk = vk;
        }
        else if (t.RawKind.Length == 1 && char.IsLetterOrDigit(t.RawKind[0]))
        {
            t.Action = TileAction.Key; t.KeyVk = char.ToUpperInvariant(t.RawKind[0]);
        }
        else if (k.StartsWith("vk_")) { t.Action = TileAction.Key; t.KeyVk = 0; }
        else t.Action = TileAction.Blank;
    }

    /// <summary>
    /// Modifier combo for a key tile. extras[0]=="7" is the original's
    /// Ctrl-combo style flag; extras may hold URL-encoded words (shift%20alt).
    /// </summary>
    public static (int vk, bool ctrl, bool shift, bool alt) KeyCombo(Tile t)
    {
        bool ctrl = false, shift = false, alt = false;
        foreach (var ex in t.Extras)
        {
            string low = Uri.UnescapeDataString(ex).ToLowerInvariant()
                .Replace('+', ' ').Replace(',', ' ');
            if (low.Contains("ctrl") || low.Contains("control")) ctrl = true;
            if (low.Contains("shift")) shift = true;
            if (low == "alt" || low.Contains(" alt") || low.Contains("alt ")) alt = true;
        }
        if (t.Extras.Count > 0 && t.Extras[0].Trim() == "7") ctrl = true;
        return (t.KeyVk, ctrl, shift, alt);
    }

    /// <summary>
    /// Hit test. 'pad' is the transparent background layer: concrete tiles
    /// always win over it (original z-order semantics).
    /// </summary>
    public static Tile? HitTest(Layout layout, double x, double y, double w, double h)
    {
        Tile? pad = null, top = null;
        foreach (var t in layout.Tiles)
        {
            double x0 = t.X / 100 * w, y0 = t.Y / 100 * h;
            double x1 = (t.X + t.W) / 100 * w, y1 = (t.Y + t.H) / 100 * h;
            if (x < x0 || x > x1 || y < y0 || y > y1) continue;
            if (t.Action == TileAction.Frame) continue;
            if (t.Action == TileAction.Pad) pad = t;
            else top = t;
        }
        return top ?? pad;
    }

    public static List<string> OrderedNames(Dictionary<string, Layout> layouts)
    {
        string[] preferred = ["floatpad", "leftpad", "rightpad", "toppad", "bottompad",
            "fullscreen", "fullscreen_with_btns_horz", "ArtistPad",
            "ArtistPad_Medium", "virtualctrls"];
        return layouts.Keys.OrderBy(n =>
        {
            int i = Array.IndexOf(preferred, n);
            return (i < 0 ? 99 : i, n);
        }).ToList();
    }
}
