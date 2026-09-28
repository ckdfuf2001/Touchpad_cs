using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TouchPadCloneV2.Core;

/// <summary>
/// Original parity (measured on TouchMousePointer.exe pad window):
/// WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE.
/// Plain popup, no border/caption. Takes all input hitting it by z-order
/// (that IS the blocking - no hooks, no touch registration anywhere in
/// the original), never steals focus, hides from taskbar/Alt-Tab.
/// </summary>
public static class NoActivate
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr h, int n);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr h, int n, int v);

    public static void Apply(Window w)
    {
        w.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                int st = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE,
                    st | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            }
            catch { }
        };
    }
}

/// <summary>
/// Visual-tree hit helpers shared by chrome/strip.
/// </summary>
public static class WpfHit
{
    /// <summary>
    /// A press on a Button usually reports its inner content (text/chrome),
    /// not the Button itself - walk up to find out.
    /// </summary>
    public static bool IsButton(object? src)
    {
        System.Windows.DependencyObject? d = src as System.Windows.DependencyObject;
        while (d != null)
        {
            if (d is System.Windows.Controls.Button) return true;
            d = (d as System.Windows.FrameworkElement)?.Parent
                    as System.Windows.DependencyObject
                ?? System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }
}
/// <summary>
/// Disables the OS tablet gestures that compete with our own. Every one of
/// them synthesizes its OWN click at the TOUCH point, which on our pad
/// means a stray press on ourselves plus a menu at the finger:
///  - press-and-hold / tap-to-long -> its own right down+up at the touch point
///  - double/triple tap          -> extra click pairs that break the tap chain
///  - flicks                     -> scroll/nav we never asked for
/// Handled per touch-surface window via WM_TABLET_QUERYSYSTEMGESTURESTATUS.
/// </summary>
public static class TabletTweaks
{
    private const int WM_TABLET_QUERYSYSTEMGESTURESTATUS = 0x02CC;
    private const long TABLET_DISABLE_PRESSANDHOLD = 0x00000001;
    private const long TABLET_DISABLE_DOUBLETAP = 0x00000002;
    private const long TABLET_DISABLE_TRIPLETAP = 0x00000004;
    private const long TABLET_DISABLE_TAPTOOLONG = 0x00000008;
    private const long TABLET_DISABLE_PENPRESSANDHOLD = 0x00000100;
    private const long TABLET_DISABLE_FLICKS = 0x00010000;

    private const long Blocked =
        TABLET_DISABLE_PRESSANDHOLD | TABLET_DISABLE_DOUBLETAP |
        TABLET_DISABLE_TRIPLETAP | TABLET_DISABLE_TAPTOOLONG |
        TABLET_DISABLE_PENPRESSANDHOLD | TABLET_DISABLE_FLICKS;

    public static void DisableSystemGestures(Window w)
    {
        w.SourceInitialized += (_, _) =>
        {
            try
            {
                var src = (System.Windows.Interop.HwndSource?)
                    System.Windows.PresentationSource.FromVisual(w);
                src?.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp,
                    ref bool handled) =>
                {
                    if (msg == WM_TABLET_QUERYSYSTEMGESTURESTATUS)
                    {
                        handled = true;
                        return (IntPtr)Blocked;
                    }
                    return IntPtr.Zero;
                });
            }
            catch { }
        };
    }
}
