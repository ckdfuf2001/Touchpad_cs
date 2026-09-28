using System;
using System.Runtime.InteropServices;

namespace TouchPadCloneV2;

/// <summary>Shared Win32 bits for the click-through overlay windows.</summary>
public static class NativeWin
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr h, int n);
    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr h, int n, int v);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after,
        int x, int y, int cx, int cy, uint flags);

    public static void PlaceTopmost(IntPtr hwnd, double physX, double physY)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST,
            (int)Math.Round(physX), (int)Math.Round(physY),
            0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
