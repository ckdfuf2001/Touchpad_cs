using System;
using System.Runtime.InteropServices;

namespace TouchPadCloneV2.Core;

/// <summary>OS gesture intake (Phase 1: receive + log only, no behavior).
/// Enables WM_GESTURE on our HWND and formats every gesture message for
/// the raw log, so device tests show which GIDs the pad actually
/// delivers (two-finger tap / pan / zoom / press-and-tap).</summary>
public static class OsGestures
{
    public const int WM_GESTURE = 0x0119;
    public const int WM_GESTURENOTIFY = 0x011A;

    private const int GC_ALLGESTURES = 0x00000001;

    // GID_* (winuser.h)
    private const int GID_BEGIN = 1;
    private const int GID_END = 2;
    private const int GID_ZOOM = 3;
    private const int GID_PAN = 4;
    private const int GID_ROTATE = 5;
    private const int GID_TWOFINGERTAP = 6;
    private const int GID_PRESSANDTAP = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct GESTURECONFIG
    {
        public int dwID;
        public int dwWant;
        public int dwBlock;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GESTUREINFO
    {
        public int cbSize;
        public int dwFlags;
        public int dwID;
        public IntPtr hwndTarget;
        public POINT ptsLocation;
        public int dwInstanceID;
        public int dwSequenceID;
        public ulong ullArguments;
        public uint cbExtraArgs;
    }

    [DllImport("user32.dll")]
    private static extern bool SetGestureConfig(IntPtr hwnd, int dwReserved,
        [In] GESTURECONFIG[] pGestureConfig, int cIDs,
        IntPtr pUnknown, int cbExtraArgs, IntPtr pExtraArgs);

    [DllImport("user32.dll")]
    private static extern bool GetGestureInfo(IntPtr hGestureInfo, ref GESTUREINFO pGestureInfo);

    [DllImport("user32.dll")]
    private static extern bool CloseGestureInfoHandle(IntPtr pGestureInfo);

    /// <summary>Opts the window into all OS gestures. Best-effort.</summary>
    public static void Enable(IntPtr hwnd)
    {
        try
        {
            var cfg = new[] { new GESTURECONFIG { dwID = 0, dwWant = GC_ALLGESTURES, dwBlock = 0 } };
            bool ok = SetGestureConfig(hwnd, 0, cfg, 1, IntPtr.Zero, 0, IntPtr.Zero);
            Log.Write($"OSGESTURE config all={ok}");
        }
        catch (Exception ex) { Log.Write("OSGESTURE config failed: " + ex.Message); }
    }

    private static string Name(int id) => id switch
    {
        GID_BEGIN => "BEGIN",
        GID_END => "END",
        GID_ZOOM => "ZOOM",
        GID_PAN => "PAN",
        GID_ROTATE => "ROTATE",
        GID_TWOFINGERTAP => "TWOFINGERTAP",
        GID_PRESSANDTAP => "PRESSANDTAP",
        _ => "GID" + id,
    };

    /// <summary>Formats a WM_GESTURE lParam (always closes the handle).</summary>
    public static string Describe(IntPtr lParam)
    {
        try
        {
            var gi = new GESTUREINFO { cbSize = Marshal.SizeOf<GESTUREINFO>() };
            if (!GetGestureInfo(lParam, ref gi)) return "OSGESTURE getinfo-failed";
            return $"OSGESTURE {Name(gi.dwID)} flags=0x{gi.dwFlags:X} at=({gi.ptsLocation.x},{gi.ptsLocation.y}) args=0x{gi.ullArguments:X} seq={gi.dwSequenceID}";
        }
        catch (Exception ex) { return "OSGESTURE describe-failed: " + ex.Message; }
        finally { try { CloseGestureInfoHandle(lParam); } catch { } }
    }
}
