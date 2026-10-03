using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TouchPadCloneV2.Core;

/// <summary>Runs non-mouse custom actions (program/cmd). Mouse kinds are
/// handled by the caller in its own context (pad buttons click live).</summary>
public static class ActionRunner
{
    public static bool Run(ActionDef a)
    {
        try
        {
            if (a.Kind == "program" && a.Path.Length > 0)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = a.Path,
                    Arguments = a.Args,
                    UseShellExecute = true,
                });
                return true;
            }
            if (a.Kind == "cmd" && a.Path.Length > 0)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c " + a.Path,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Mouse click at the live cursor (aux pad buttons).</summary>
    public static void ClickAtCursor(string action, out bool clicked)
    {
        clicked = true;
        var (x, y) = InputSim.Cursor();
        switch (action)
        {
            case "left_click":
                InputSim.ClickAt(x, y, "left"); break;
            case "right_click":
                InputSim.ClickAt(x, y, "right"); break;
            case "middle_click":
                InputSim.ClickAt(x, y, "middle"); break;
            case "double_click":
                InputSim.ClickAt(x, y, "left");
                InputSim.ClickAt(x, y, "left"); break;
            default: clicked = false; break;
        }
    }

    /// <summary>Runs a "Ctrl+C" style shortcut: modifiers held while the
    /// main key taps. UI wiring only (settings strip cells).</summary>
    public static bool RunShortcut(string spec)
    {
        try
        {
            var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) return false;
            var mods = new List<int>();
            foreach (var p in parts[..^1])
            {
                int vk = p.ToLowerInvariant() switch
                {
                    "ctrl" or "control" => 0x11,
                    "shift" => 0x10,
                    "alt" => 0x12,
                    "win" or "windows" => 0x5B,
                    _ => 0,
                };
                if (vk == 0) return false;
                mods.Add(vk);
            }
            int main = ParseKey(parts[^1]);
            if (main == 0) return false;
            foreach (int m in mods) InputSim.HoldKey(m, true);
            try { InputSim.TapKey(main); }
            finally { foreach (int m in mods) InputSim.HoldKey(m, false); }
            return true;
        }
        catch { return false; }
    }

    private static int ParseKey(string name)
    {
        string n = name.Trim().ToUpperInvariant();
        if (n.Length == 1)
        {
            char c = n[0];
            if (c >= 'A' && c <= 'Z') return c;
            if (c >= '0' && c <= '9') return c;
        }
        if (n.StartsWith("F") && int.TryParse(n.Substring(1), out int f)
            && f >= 1 && f <= 24) return 0x6F + f;
        return n switch
        {
            "ESC" or "ESCAPE" => 0x1B,
            "TAB" => 0x09,
            "SPACE" => 0x20,
            "ENTER" or "RETURN" => 0x0D,
            "BACK" or "BACKSPACE" => 0x08,
            "DEL" or "DELETE" => 0x2E,
            "INS" or "INSERT" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PGUP" or "PAGEUP" => 0x21,
            "PGDN" or "PAGEDOWN" => 0x22,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            _ => 0,
        };
    }
}
