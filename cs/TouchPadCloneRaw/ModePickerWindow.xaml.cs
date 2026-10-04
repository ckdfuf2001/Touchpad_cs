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
    private AppSettings _ps = null!;
    private Action<string> _onLayout = _ => { };
    private Action<string> _onAux = _ => { };
    private Action<string> _onAction = _ => { };
    private Action _onSettings = () => { };
    private Action? _onStripClose;

    public ModePickerWindow(AppSettings s, string current,
        Action<string> onLayout, Action<string> onAux, Action<string> onAction,
        Func<string, bool> auxOn, Action onSettings, string? selectedValue = null,
        Action? onStripClose = null)
    {
        InitializeComponent();
        _ps = s;
        _onLayout = onLayout; _onAux = onAux; _onAction = onAction;
        _onSettings = onSettings;
        _onStripClose = onStripClose;
        BuildContent(selectedValue);
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

    /// <summary>Rebuilds the buttons live (strip swipe selection moves
    /// while open): same content, fresh highlight.</summary>
    public void Refresh(string? selectedValue)
    {
        try
        {
            List.Children.Clear();
            BuildContent(selectedValue);
        }
        catch { }
    }

    private void BuildContent(string? selectedValue)
    {
        var s = _ps;
        var onLayout = _onLayout;
        var onAux = _onAux;
        var onAction = _onAction;
        var onSettings = _onSettings;
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
                var b = new Button
                {
                    Content = cell.Label,
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
        // Close + settings share one row. Close hides the STRIP.
        var brow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var close = new Button
        {
            Content = "닫기 ✕", FontSize = 14,
            Margin = new Thickness(2), Padding = new Thickness(6),
        };
        close.PreviewTouchDown += (_, e) =>
        {
            _onStripClose?.Invoke();
            Close();
            e.Handled = true;
        };
        close.Click += (_, _) => { _onStripClose?.Invoke(); Close(); };
        brow.Children.Add(close);
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
        brow.Children.Add(settings);
        List.Children.Add(brow);
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
