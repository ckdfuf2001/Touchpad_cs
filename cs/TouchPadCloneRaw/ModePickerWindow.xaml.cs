using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TouchPadCloneV2.Core;

namespace TouchPadCloneV2;

/// <summary>
/// Mode buttons popup: the strip's second tap opens the configured
/// StripRow1/StripRow2 items as big touch buttons, docked under the bar.
/// Selecting switches layout and closes.
/// </summary>
public partial class ModePickerWindow : Window
{
    private readonly System.Windows.Threading.DispatcherTimer _autoClose;

    public ModePickerWindow(AppSettings s, string current,
        Action<string> onLayout, Action<string> onAux, Action<string> onAction,
        Func<string, bool> auxOn, Action onSettings)
    {
        InitializeComponent();
        // Docked flush under the top strip: reads as one expanded area.
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 30;
        AddRow(s.StripRow1, s, current, onLayout, onAux, onAction, auxOn);
        AddRow(s.StripRow2, s, current, onLayout, onAux, onAction, auxOn);
        var close = new Button
        {
            Content = "닫기 ✕", FontSize = 14,
            Margin = new Thickness(2), Padding = new Thickness(6),
        };
        close.Click += (_, _) => Close();
        List.Children.Add(close);
        var settings = new Button
        {
            Content = "⚙ 설정 열기", FontSize = 14,
            Margin = new Thickness(2), Padding = new Thickness(6),
        };
        settings.PreviewTouchDown += (_, e) =>
        {
            onSettings();
            Close();
            e.Handled = true;
        };
        settings.Click += (_, _) => { onSettings(); Close(); };
        List.Children.Add(settings);
        DebugLog.Write($"PICKER open current={current}");
        _autoClose = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10),
        };
        _autoClose.Tick += (_, _) =>
        {
            try { Close(); }
            catch (InvalidOperationException) { }
        };
        _autoClose.Start();
        // Window-level touch sniffer: proves whether fingers reach us at all.
        PreviewTouchDown += (_, e) =>
        {
            var p = e.GetTouchPoint(this).Position;
            DebugLog.Write($"PICKER touch @{p.X:0},{p.Y:0} src={e.Source?.GetType().Name}");
        };
    }

    /// <summary>One configured strip row as big touch buttons.</summary>
    private void AddRow(List<StripRowItem> items, AppSettings s, string current,
        Action<string> onLayout, Action<string> onAux, Action<string> onAction,
        Func<string, bool> auxOn)
    {
        if (items == null) return;
        var row = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        bool any = false;
        foreach (var it in items)
        {
            if (it == null || !it.Visible) continue;
            bool on = it.Kind == "radio" ? it.Target == current : auxOn(it.Target);
            var b = new Button
            {
                Content = (on ? "● " : "○ ") + it.Name,
                FontSize = 16,
                Margin = new Thickness(2),
                Padding = new Thickness(8),
                MinHeight = 48,
                MinWidth = 100,
                Tag = it,
            };
            // Touch first (promotion-independent), mouse Click as fallback.
            b.PreviewTouchDown += (_, e) =>
            {
                Fire(it, s, onLayout, onAux, onAction);
                Close();
                e.Handled = true;
            };
            b.Click += (_, _) => { Fire(it, s, onLayout, onAux, onAction); Close(); };
            row.Children.Add(b);
            any = true;
        }
        if (any) List.Children.Add(row);
    }

    private static void Fire(StripRowItem it, AppSettings s,
        Action<string> onLayout, Action<string> onAux, Action<string> onAction)
    {
        DebugLog.Write($"PICKER fire {it.Name} -> {it.Target}");
        if (it.Target == "artist" || it.Target == "virtual") onAux(it.Target);
        else if (Array.IndexOf(StripGestureMap.Actions, it.Target) >= 0
            || (s.Actions != null && s.Actions.ContainsKey(it.Target)))
            onAction(it.Target);
        else onLayout(it.Target);
    }

    protected override void OnClosed(EventArgs e)
    {
        _autoClose.Stop();
        base.OnClosed(e);
    }
}
