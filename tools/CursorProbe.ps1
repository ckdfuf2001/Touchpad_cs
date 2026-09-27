# Samples the cursor state (visible? suppressed? where?) every 50ms so the
# reference's cursor behaviour can be read while it is actually being touched.
#
#   powershell -ExecutionPolicy Bypass -File tools\CursorProbe.ps1 [seconds]
# Output: %TEMP%\cursor_probe.log
#
# CURSOR_SHOWING = 0x1, CURSOR_SUPPRESSED = 0x2. A visible, moving cursor during
# a touch proves the system does not have to suppress it, which is what decides
# whether one real cursor is possible on this device.
param([int]$Seconds = 20)

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;

public static class CurProbe {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO {
        public int cbSize; public int flags; public IntPtr hCursor; public POINT pt;
    }
    [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO ci);

    public static void Run(string path, int seconds) {
        StreamWriter w = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        w.WriteLine("ms flags visible suppressed x y");
        long t0 = Environment.TickCount;
        int lastFlags = -1, lastX = -1, lastY = -1;
        while ((Environment.TickCount - t0) < seconds * 1000) {
            CURSORINFO ci = new CURSORINFO();
            ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            GetCursorInfo(ref ci);
            // Log any state change, and a heartbeat every second.
            bool changed = ci.flags != lastFlags || ci.pt.x != lastX || ci.pt.y != lastY;
            long el = Environment.TickCount - t0;
            if (changed || (el % 1000) < 60) {
                w.WriteLine(string.Format("{0,6} 0x{1:X} {2} {3} {4} {5}",
                    el, ci.flags,
                    (ci.flags & 1) != 0 ? "yes" : "no",
                    (ci.flags & 2) != 0 ? "yes" : "no",
                    ci.pt.x, ci.pt.y));
                w.Flush();
                lastFlags = ci.flags; lastX = ci.pt.x; lastY = ci.pt.y;
            }
            System.Threading.Thread.Sleep(50);
        }
        w.Close();
    }
}
'@

$log = Join-Path $env:TEMP "cursor_probe.log"
Write-Host "Sampling the cursor for $Seconds s to $log"
Write-Host "Touch / gesture on the REFERENCE app now."
[CurProbe]::Run($log, $Seconds)
Write-Host "Done."
