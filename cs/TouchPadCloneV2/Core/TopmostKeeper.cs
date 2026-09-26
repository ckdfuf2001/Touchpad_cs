using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TouchPadCloneV2.Core;

/// <summary>
/// Keeps one of our always-on-top windows at the top of the topmost band.
///
/// Topmost="True" is only the INITIAL state. An application that puts its own
/// topmost window up afterwards goes above ours and nothing brings it back -
/// measured on both the pad and the mode strip ("it gets covered by a
/// program"). Re-asserting on a timer is the usual answer, and it is what the
/// cursor overlay already gets implicitly by re-placing itself on every move.
///
/// A hidden window is left alone: the pad can be toggled off, and forcing
/// SWP_SHOWWINDOW would resurrect it a second later.
/// </summary>
public static class TopmostKeeper
{
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001,
        SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after,
        int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// Re-assert <paramref name="w"/>'s topmost place every
    /// <paramref name="intervalMs"/>. <paramref name="alsoAfter"/> runs right
    /// after the raise - the pad uses it to bring the cursor arrow forward
    /// again, since raising the pad otherwise lifts it above the arrow and the
    /// arrow vanishes whenever it passes over the pad.
    /// </summary>
    public static void Attach(Window w, Action? alsoAfter = null,
        int intervalMs = 1000)
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(intervalMs),
        };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (!w.IsVisible) return;
                var h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return;
                SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                alsoAfter?.Invoke();
            }
            catch { }
        };
        timer.Start();
    }
}
