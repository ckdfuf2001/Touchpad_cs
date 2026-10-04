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

    /// <summary>Strip menu (StripLayout cells) routing shared by the
    /// picker popup and the strip swipe navigation. UI wiring only.</summary>
    public static class StripMenu
    {
        /// <summary>Routes a cell: layout select, aux toggle, custom
        /// action, strip gesture, or direct cmd/program/shortcut.</summary>
        public static void Fire(StripCell cell, AppSettings s,
            Action<string> onLayout, Action<string> onAux, Action<string> onAction)
        {
            if (cell.Kind != "기능")
            {
                if (cell.Kind == "cmd") onAction("cmd:" + cell.Value);
                else if (cell.Kind == "프로그램") onAction("program:" + cell.Value);
                else onAction("shortcut:" + cell.Value);   // 단축키
                return;
            }
            if (cell.Value.StartsWith("layout:", StringComparison.OrdinalIgnoreCase))
            {
                string name = cell.Value.Substring(7);
                if (name.Contains("artist", StringComparison.OrdinalIgnoreCase)) { onAux("artist"); return; }
                if (name.Contains("virtual", StringComparison.OrdinalIgnoreCase)) { onAux("virtual"); return; }
                onLayout(name);
                return;
            }
            onAction(cell.Value);   // custom registry key or strip gesture
        }

        /// <summary>Steps the menu selection through the cell grid and
        /// returns the landed cell. Left/right wrap inside the row;
        /// up/down change rows (column clamped). False when empty.</summary>
        public static bool MoveSelection(AppSettings s,
            ref int row, ref int col, int dRow, int dCol, out StripCell cell)
        {
            cell = new StripCell();
            try
            {
                var rows = NonEmptyRows(s);
                if (rows.Count == 0) return false;
                if (row < 0 || row >= rows.Count) row = 0;
                if (dRow != 0)
                {
                    // Rows wrap like columns do: edge rows must visibly
                    // move too (a clamped no-op reads as "swipe broken").
                    row = (row + dRow + rows.Count) % rows.Count;
                    col = Math.Min(Math.Max(col, 0), rows[row].Count - 1);
                }
                else
                {
                    int n = rows[row].Count;
                    if (col < 0 || col >= n) col = dCol < 0 ? 0 : n - 1;
                    col = (col + dCol + n) % n;
                }
                cell = rows[row][col];
                return true;
            }
            catch { return false; }
        }

        /// <summary>Reads the selected cell (for the strip display).</summary>
        public static bool GetCell(AppSettings s, int row, int col,
            out StripCell cell)
        {
            cell = new StripCell();
            try
            {
                var rows = NonEmptyRows(s);
                if (row < 0 || row >= rows.Count) return false;
                if (col < 0 || col >= rows[row].Count) return false;
                cell = rows[row][col];
                return true;
            }
            catch { return false; }
        }

        /// <summary>Finds the cell selecting a layout (swipe continuity
        /// after picker taps). Indexes match MoveSelection's filtered
        /// rows. Returns false when absent.</summary>
        public static bool LocateLayout(AppSettings s, string layout,
            out int row, out int col)
        {
            row = -1; col = -1;
            try
            {
                int r = 0;
                foreach (var raw in NonEmptyRows(s))
                {
                    int c = 0;
                    foreach (var cell in raw)
                    {
                        if (cell.Value.Equals("layout:" + layout,
                            StringComparison.OrdinalIgnoreCase))
                        { row = r; col = c; return true; }
                        c++;
                    }
                    r++;
                }
            }
            catch { }
            return false;
        }

        private static List<List<StripCell>> NonEmptyRows(AppSettings s)
        {
            var rows = new List<List<StripCell>>();
            foreach (var r in s.StripLayout)
            {
                var cs = new List<StripCell>();
                foreach (var raw in r.Cells)
                {
                    var c = StripCell.Parse(raw);
                    if (!c.IsEmpty) cs.Add(c);
                }
                if (cs.Count > 0) rows.Add(cs);
            }
            return rows;
        }
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
