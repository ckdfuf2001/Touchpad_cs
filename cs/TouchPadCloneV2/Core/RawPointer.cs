using System;
using System.Runtime.InteropServices;

namespace TouchPadCloneV2.Core;

/// <summary>
/// Touch input straight from the pointer API, instead of WPF's touch pipeline.
///
/// Why this exists: a WPF touch is promoted by the framework to a STYLUS and
/// then to a MOUSE event, and the system moves the cursor onto the contact
/// point as part of that. Everything hard about this project came from it - the
/// cursor being dragged onto the pad mid-gesture, phantom mouse events, drags
/// split between the window and the application, and the "fake cursor" the
/// whole design is built around.
///
/// The working reference on this machine (TouchMousePointer) avoids it the same
/// way in spirit: it reads the digitizer with Raw Input so no promotion ever
/// happens. The pointer API is the simpler equivalent - a window that has NOT
/// registered for touch receives WM_POINTER instead of WM_TOUCH, so
/// UnregisterTouchWindow plus handling WM_POINTER gives us the touches with no
/// promotion at all.
///
/// Geometry note: the reference moves the cursor with SetPhysicalCursorPos
/// (physical pixels, no DPI virtualisation) and issues clicks with mouse_event.
/// Both are worth copying if the pointer path needs them.
/// </summary>
public static class RawPointer
{
    public const int WM_POINTERUPDATE = 0x0245;
    public const int WM_POINTERDOWN = 0x0246;
    public const int WM_POINTERUP = 0x0247;
    public const int WM_POINTERCAPTURECHANGED = 0x024C;

    public const int PT_TOUCH = 2;
    public const int PT_PEN = 3;
    public const int PT_MOUSE = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public uint ButtonChangeType;
    }

    public const uint POINTER_FLAG_INCONTACT = 0x00000004;
    public const uint POINTER_FLAG_DOWN = 0x00010000;
    public const uint POINTER_FLAG_UPDATE = 0x00020000;
    public const uint POINTER_FLAG_UP = 0x00040000;

    [DllImport("user32.dll")]
    public static extern bool GetPointerInfo(uint pointerId, out POINTER_INFO info);
    [DllImport("user32.dll")]
    public static extern bool GetPointerType(uint pointerId, out int pointerType);
    [DllImport("user32.dll")]
    public static extern bool UnregisterTouchWindow(IntPtr hwnd);

    /// <summary>Pointer id lives in the low word of wParam.</summary>
    public static uint IdFromWParam(IntPtr wParam) =>
        (uint)((long)wParam & 0xFFFF);
}
