using System;
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
}
