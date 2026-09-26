using System;
using System.IO;
using System.Linq;
using TouchPadCloneV2.Core;

// Smoke test: v2 PresetParser against the ORIGINAL TouchMousePointer INIs.
string dir = @"C:\Program Files\TouchMousePointer";
var files = Directory.GetFiles(dir, "Preset *.ini");
int ok = 0, totalTiles = 0;
foreach (var f in files.OrderBy(x => x))
{
    try
    {
        var layouts = PresetParser.ParseFile(f);
        totalTiles += layouts.Values.Sum(l => l.Tiles.Count);
        ok++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {Path.GetFileName(f)}: {ex.Message}");
    }
}
Console.WriteLine($"parsed {ok}/{files.Length} files, {totalTiles} tiles");

// Spot checks against measured ground truth.
var def = PresetParser.ParseFile(Path.Combine(dir, "Preset Default.ini"));
Assert(def.ContainsKey("floatpad"), "Default has floatpad");
Assert(def["floatpad"].Tiles.Count == 5, "floatpad has 5 tiles");
Assert(def["floatpad"].Tiles.Last().RawKind == "pad", "pad tile is last");

var ps = PresetParser.ParseFile(Path.Combine(dir, "Preset Photoshop CC.ini"));
Assert(ps["ArtistPad"].Tiles.Count == 58, "Photoshop ArtistPad has 58 tiles");

var wasd = PresetParser.ParseFile(Path.Combine(dir, "Preset WASD gaming.ini"));
Assert(wasd["fullscreen"].Tiles.Count == 31, "WASD fullscreen has 31 tiles");

// Key-combo decoding: ArtistPad C/V/X/Z carry extra '7' = Ctrl.
var artist = def["ArtistPad"];
foreach (char c in new[] { 'C', 'V', 'X', 'Z' })
{
    var t = artist.Tiles.FirstOrDefault(t =>
        t.RawKind == c.ToString() && t.Extras.Contains("7"));
    Assert(t != null, $"ArtistPad {c} with flag 7 exists");
    var combo = PresetParser.KeyCombo(t!);
    Assert(combo.ctrl && combo.vk == c, $"ArtistPad {c} decodes to Ctrl+{c}");
}

// WASD 'W' carries 'shift%20alt'.
var w = wasd["fullscreen"].Tiles.First(t => t.RawKind == "W");
var wc = PresetParser.KeyCombo(w);
Assert(wc.shift && wc.alt && !wc.ctrl, "WASD W decodes to Shift+Alt+W");

// Hit-test priority: concrete tiles win over the fullscreen pad layer.
var lay = def["floatpad"];
var hit = PresetParser.HitTest(lay, 25, 10, 340, 260); // over lbtn tile
Assert(hit != null && hit.RawKind == "lbtn", "lbtn hit, not pad");
var hitPad = PresetParser.HitTest(lay, 170, 200, 340, 260); // pad area (50%, 77%)
Assert(hitPad != null && hitPad.RawKind == "pad", "pad area hits pad");

Console.WriteLine(ok == files.Length ? "ALL PRESET CHECKS PASSED" : "FAILURES PRESENT");
return ok == files.Length ? 0 : 1;

static void Assert(bool cond, string what)
{
    Console.WriteLine((cond ? "PASS " : "FAIL ") + what);
    if (!cond) Environment.ExitCode = 1;
}
