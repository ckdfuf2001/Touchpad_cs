using System;
using System.Drawing;
using System.Windows.Forms;

namespace TouchPadCloneV2;

/// <summary>System-tray icon (WinForms NotifyIcon hosted in the WPF app).</summary>
public sealed class TrayManager : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayManager(Action show, Action hide, Action settings, Action quit,
        Action restoreCursor)
    {
        _icon = new NotifyIcon
        {
            Text = "TouchPad Clone V2",
            Icon = BuildIcon(),
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        _icon.ContextMenuStrip.Items.Add("패드 보이기", null, (_, _) => show());
        _icon.ContextMenuStrip.Items.Add("패드 숨기기", null, (_, _) => hide());
        _icon.ContextMenuStrip.Items.Add("설정…", null, (_, _) => settings());
        _icon.ContextMenuStrip.Items.Add("커서 복구", null, (_, _) => restoreCursor());
        _icon.ContextMenuStrip.Items.Add("종료", null, (_, _) => quit());
        _icon.DoubleClick += (_, _) => show();
    }

    private static Icon BuildIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.FillRoundedRectangle(new SolidBrush(Color.FromArgb(46, 107, 230)), 3, 7, 26, 20, 5);
            g.FillRoundedRectangle(new SolidBrush(Color.FromArgb(27, 30, 36)), 7, 11, 18, 12, 3);
            g.FillEllipse(new SolidBrush(Color.FromArgb(53, 196, 255)), 13, 14, 6, 6);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

internal static class Gfx
{
    public static void FillRoundedRectangle(this Graphics g, Brush b,
        int x, int y, int w, int h, int r)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(x, y, r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        g.FillPath(b, path);
    }
}
