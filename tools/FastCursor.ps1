# Fast cursor sampler (5ms) to catch sub-frame contact visits.
# Usage: powershell -ExecutionPolicy Bypass -File tools\FastCursor.ps1 [seconds]
# Output: %TEMP%\fast_cursor.log (only logs on change)
param([int]$Seconds = 25)

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;

public static class FastCur {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO {
        public int cbSize; public int flags; public IntPtr hCursor; public POINT pt;
    }
    [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO ci);

    public static void Run(string path, int seconds) {
        StreamWriter w = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        w.WriteLine("ms flags x y");
        long t0 = Environment.TickCount;
        int lastFlags = -1, lastX = -1, lastY = -1;
        while ((Environment.TickCount - t0) < seconds * 1000) {
            CURSORINFO ci = new CURSORINFO();
            ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            GetCursorInfo(ref ci);
            if (ci.flags != lastFlags || ci.pt.x != lastX || ci.pt.y != lastY) {
                w.WriteLine(string.Format("{0,6} 0x{1:X} {2} {3}",
                    Environment.TickCount - t0, ci.flags, ci.pt.x, ci.pt.y));
                w.Flush();
                lastFlags = ci.flags; lastX = ci.pt.x; lastY = ci.pt.y;
            }
            System.Threading.Thread.Sleep(5);
        }
        w.Close();
    }
}
'@

$log = Join-Path $env:TEMP "fast_cursor.log"
Write-Host "Fast sampling the cursor for $Seconds s to $log"
[FastCur]::Run($log, $Seconds)
Write-Host "Done."
