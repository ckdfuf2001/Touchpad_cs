using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TouchPadCloneV2.Core;

/// <summary>
/// Makes utility windows non-activating (WS_EX_NOACTIVATE): touches and
/// clicks never steal focus, so wheel events and keystrokes keep going to
/// the app the user is actually working in. (The original and TKM both do
/// this; without it, scrolling and Ctrl+C land in our own pad window.)
/// The Settings window intentionally stays activatable.
/// </summary>
public static class NoActivate
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

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
                SetWindowLong(hwnd, GWL_EXSTYLE, st | WS_EX_NOACTIVATE);
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
/// Disables the OS tablet gestures that compete with our own:
/// "press and hold for right-click" synthesizes its OWN right down+up at
/// the TOUCH point (measured: our fake-position click followed by a stray
/// release/menu at the finger), and flicks inject scroll/nav we never asked
/// for. Handled per touch-surface window via WM_TABLET_QUERYSYSTEMGESTURESTATUS.
/// </summary>
public static class TabletTweaks
{
    private const int WM_TABLET_QUERYSYSTEMGESTURESTATUS = 0x02CC;
    private const long TABLET_DISABLE_PRESSANDHOLD = 0x00000001;
    private const long TABLET_DISABLE_FLICKS = 0x00010000;

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
                        return (IntPtr)(TABLET_DISABLE_PRESSANDHOLD |
                                        TABLET_DISABLE_FLICKS);
                    }
                    return IntPtr.Zero;
                });
            }
            catch { }
        };
    }
}
