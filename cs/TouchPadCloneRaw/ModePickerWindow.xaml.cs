using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TouchPadCloneV2.Core;

namespace TouchPadCloneV2;

/// <summary>
/// Mode buttons popup: the strip's second tap opens the configured
/// StripLayout cells as big touch buttons (our row layout kept).
/// Selecting switches layout and closes.
/// </summary>
public partial class ModePickerWindow : Window
{
    private readonly System.Windows.Threading.DispatcherTimer _autoClose;

    public ModePickerWindow(AppSettings s, string current,
        Action<string> onLayout, Action<string> onAux, Action<string> onAction,
        Func<string, bool> auxOn, Action onSettings, string? selectedValue = null)
    {
        InitializeComponent();
        // Docked flush under the top strip: reads as one expanded area.
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 30;
        foreach (var row in s.StripLayout)
        {
            // One config row = one visual row (never wraps: a 4-cell
            // row stays 4 across, the window grows to fit).
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            bool any = false;
            foreach (var raw in row.Cells)
            {
                var cell = StripCell.Parse(raw);
                if (cell.IsEmpty) continue;
                bool on = IsOn(cell, current, auxOn);
                var b = new Button
                {
                    Content = (on ? "● " : "○ ") + cell.Label,
                    FontSize = 16,
                    Margin = new Thickness(2),
                    Padding = new Thickness(8),
                    MinHeight = 48,
                    MinWidth = 100,
                };
                // Strip swipe selection shows as a bold frame.
                if (!string.IsNullOrEmpty(selectedValue)
                    && cell.Value.Equals(selectedValue,
                        StringComparison.OrdinalIgnoreCase))
                {
                    b.BorderBrush = Brushes.White;
                    b.BorderThickness = new Thickness(3);
                }
                try
                {
                    if (ColorPalettes.IsNone(cell.Color))
                        b.Background = Brushes.Transparent;
                    else if (!string.IsNullOrWhiteSpace(cell.Color))
                        b.Background = new SolidColorBrush(
                            (Color)ColorConverter.ConvertFromString(cell.Color));
                }
                catch { }
                if (!string.IsNullOrWhiteSpace(cell.Image))
                    b.ToolTip = cell.Image;
                // Touch first (promotion-independent), mouse Click as fallback.
                b.PreviewTouchDown += (_, e) =>
                {
                    Fire(cell, s, onLayout, onAux, onAction);
                    Close();
                    e.Handled = true;
                };
                b.Click += (_, _) => { Fire(cell, s, onLayout, onAux, onAction); Close(); };
                panel.Children.Add(b);
                any = true;
            }
            if (any) List.Children.Add(panel);
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

    /// <summary>Radio/on state: layout cells match current layout,
    /// artist/virtual cells follow the aux toggles.</summary>
    private static bool IsOn(StripCell cell, string current,
        Func<string, bool> auxOn)
    {
        if (cell.Value.StartsWith("layout:", StringComparison.OrdinalIgnoreCase))
        {
            string name = cell.Value.Substring(7);
            if (name.Contains("artist", StringComparison.OrdinalIgnoreCase))
                return auxOn("artist");
            if (name.Contains("virtual", StringComparison.OrdinalIgnoreCase))
                return auxOn("virtual");
            return string.Equals(name, current, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>Routes a cell through the shared menu router.</summary>
    private static void Fire(StripCell cell, AppSettings s,
        Action<string> onLayout, Action<string> onAux, Action<string> onAction)
    {
        DebugLog.Write($"PICKER fire {cell.Label} [{cell.Kind}] -> {cell.Value}");
        Core.ActionRunner.StripMenu.Fire(cell, s, onLayout, onAux, onAction);
    }

    protected override void OnClosed(EventArgs e)
    {
        _autoClose.Stop();
        base.OnClosed(e);
    }
}
