using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TouchPadCloneV2.Core;

namespace TouchPadCloneV2;

/// <summary>
/// Mode buttons popup: swipe up/down on the top strip shows every layout
/// as a big touch button. Selecting switches layout and closes.
/// </summary>
public partial class ModePickerWindow : Window
{
    private readonly System.Windows.Threading.DispatcherTimer _autoClose;

    public ModePickerWindow(List<string> names, string current,
        Action<string> onSelect, Action onSettings)
    {
        InitializeComponent();
        // Docked flush under the top strip: reads as one expanded area.
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 30;
        foreach (string name in names)
        {
            var b = new Button
            {
                Content = (name == current ? "● " : "○ ") + name,
                FontSize = 16,
                Margin = new Thickness(2),
                Padding = new Thickness(8),
                MinHeight = 48,
            };
            string n = name;
            // Touch first (promotion-independent), mouse Click as fallback.
            b.PreviewTouchDown += (_, e) =>
            {
                DebugLog.Write($"PICKER select {n}");
                onSelect(n);
                Close();
                e.Handled = true;
            };
            b.Click += (_, _) => { onSelect(n); Close(); };
            List.Children.Add(b);
        }
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
        DebugLog.Write($"PICKER open n={names.Count} current={current}");
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

    protected override void OnClosed(EventArgs e)
    {
        _autoClose.Stop();
        base.OnClosed(e);
    }
}
