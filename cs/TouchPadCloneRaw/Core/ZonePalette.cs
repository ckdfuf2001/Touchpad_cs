using System.Collections.Generic;

namespace TouchPadCloneV2.Core;

/// <summary>Per-zone fill colors (hex with alpha).
/// Centralized so Settings can bind per-zone customization later
/// (TODO: ZoneColor-&lt;role&gt; settings keys, editor row in the
/// appearance tab). Roles: left, right, middle, wheel, key, drag,
/// pad, other. Both preset tiles and zone guides resolve through here,
/// so every layout uses the fullscreen standard.</summary>
public static class ZonePalette
{
    public static readonly Dictionary<string, string> Colors = new()
    {
        ["left"] = "#40206040",    // green
        ["right"] = "#40402060",   // blue
        ["middle"] = "#40406080",  // purple
        ["wheel"] = "#40602020",   // red
        ["key"] = "#40206060",     // teal
        ["drag"] = "#40404020",    // olive
        ["pad"] = "#30404040",     // neutral
        ["other"] = "#30202020",   // dark (menu/grip/sys)
    };

    public static string For(string role) =>
        Colors.TryGetValue(role, out var c) ? c : Colors["other"];
}
