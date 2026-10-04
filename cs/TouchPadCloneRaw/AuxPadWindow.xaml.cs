using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TouchPadCloneV2.Core;

namespace TouchPadCloneV2;

/// <summary>
/// Artist/virtual button bar: custom buttons from settings (touch + mouse).
/// Buttons are custom-drawn (touch tap handled directly, mouse click as
/// fallback). Position follows the pad Position setting.
/// </summary>
public partial class AuxPadWindow : Window
{
    private readonly AppSettings _s;
    private readonly string _pad;   // "artist" | "virtual"

    private int _touchId = -1;
    private Point _tStart;
    private DateTime _t0;
    private Action? _tAct;

    public AuxPadWindow(AppSettings s, string pad)
    {
        _s = s;
        _pad = pad;
        InitializeComponent();
        ShowActivated = false;
        // Own X: touch first, mouse Click as fallback. The app clears
        // its toggle state (RequestClose) so the next toggle re-opens.
        AuxCloseBtn.PreviewTouchDown += (_, e) => { RequestClose?.Invoke(); e.Handled = true; };
        AuxCloseBtn.Click += (_, _) => RequestClose?.Invoke();
        Refresh();
    }

    public event Action? RequestClose;

    private List<PadButton> Buttons =>
        _pad == "artist" ? _s.Artist.Buttons : _s.Virtual.Buttons;

    private string Position =>
        _pad == "artist" ? _s.Artist.Position : _s.Virtual.Position;

    public void Refresh()
    {
        try
        {
            Title.Text = _pad;
            Keys.Children.Clear();
            foreach (var b in Buttons)
            {
                string label = b.Label, action = b.Action;
                Keys.Children.Add(MakeKey(label, () => Fire(action)));
            }
            PositionSelf();
        }
        catch { }
    }

    public void PositionSelf()
    {
        try
        {
            double pw = SystemParameters.PrimaryScreenWidth;
            double ph = SystemParameters.PrimaryScreenHeight;
            switch (Position)
            {
                case "top":
                    Width = pw; Height = 110; Left = 0; Top = 0; break;
                case "bottom":
                    Width = pw; Height = 110; Left = 0; Top = ph - 110; break;
                case "left":
                    Width = 170; Height = ph; Left = 0; Top = 0; break;
                case "right":
                    Width = 170; Height = ph; Left = pw - 170; Top = 0; break;
                default: // grid: floating panel
                    Width = 340; Height = 220;
                    Left = (pw - Width) / 2; Top = (ph - Height) / 2; break;
            }
        }
        catch { }
    }

    private Border MakeKey(string label, Action act)
    {
        var tb = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF4)),
            FontSize = 13,
            IsHitTestVisible = false,
        };
        var b = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(4),
            Padding = new Thickness(12, 8, 12, 8),
            Child = tb,
        };
        b.TouchDown += (_, e) =>
        {
            if (_touchId != -1) { e.Handled = true; return; }
            _touchId = e.TouchDevice.Id;
            _tStart = e.GetTouchPoint(this).Position;
            _t0 = DateTime.Now;
            _tAct = act;
            e.Handled = true;
        };
        b.TouchUp += (_, e) =>
        {
            if (e.TouchDevice.Id != _touchId) return;
            _touchId = -1;
            var end = e.GetTouchPoint(this).Position;
            double ms = (DateTime.Now - _t0).TotalMilliseconds;
            var a = _tAct; _tAct = null;
            if (ms < 400 && Math.Abs(end.X - _tStart.X) + Math.Abs(end.Y - _tStart.Y) <= 14)
                a?.Invoke();
            e.Handled = true;
        };
        b.MouseLeftButtonUp += (_, e) => { act(); e.Handled = true; };
        return b;
    }

    private void Fire(string actionKey)
    {
        try
        {
            if (!_s.Actions.TryGetValue(actionKey, out var a))
            {
                // Bare mouse-action name also works directly.
                ActionRunner.ClickAtCursor(actionKey, out _);
                return;
            }
            if (a.Kind == "mouse") ActionRunner.ClickAtCursor(a.Value, out _);
            else ActionRunner.Run(a);
        }
        catch { }
    }
}
