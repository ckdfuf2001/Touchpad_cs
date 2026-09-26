using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TouchPadCloneV2.Core;
using WComboBox = System.Windows.Controls.ComboBox;

namespace TouchPadCloneV2;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _s;
    private readonly Action _onApply;
    private readonly Dictionary<string, WComboBox> _gestures = new();
    private static readonly string[] CursorStyles =
        ["cyan", "white", "black", "green", "yellow", "magenta"];

    private static readonly (string key, string label, Func<GestureMap, string> get, Action<GestureMap, string> set)[] Slots =
    [
        ("tap", "짧게 탭", m => m.Tap, (m, v) => m.Tap = v),
        ("double_tap", "더블탭", m => m.DoubleTap, (m, v) => m.DoubleTap = v),
        ("long_press", "길게 누르기", m => m.LongPress, (m, v) => m.LongPress = v),
        ("second_hold", "두번째 누른채", m => m.SecondHold, (m, v) => m.SecondHold = v),
        ("two_finger_tap", "두손가락 탭", m => m.TwoFingerTap, (m, v) => m.TwoFingerTap = v),
        ("swipe_up", "두손가락 위로", m => m.SwipeUp, (m, v) => m.SwipeUp = v),
        ("swipe_down", "두손가락 아래로", m => m.SwipeDown, (m, v) => m.SwipeDown = v),
        ("swipe_left", "두손가락 왼쪽으로", m => m.SwipeLeft, (m, v) => m.SwipeLeft = v),
        ("swipe_right", "두손가락 오른쪽으로", m => m.SwipeRight, (m, v) => m.SwipeRight = v),
    ];

    public SettingsWindow(AppSettings s, List<string> layouts, Action onApply)
    {
        _s = s;
        _onApply = onApply;
        InitializeComponent();
        Speed.Value = s.Speed;
        OpacityS.Value = s.Opacity;
        LongMs.Value = s.LongPressMs;
        LayoutBox.ItemsSource = layouts;
        LayoutBox.SelectedItem = layouts.Contains(s.Layout) ? s.Layout : layouts.FirstOrDefault();
        CursorBox.ItemsSource = CursorStyles;
        CursorBox.SelectedItem = CursorStyles.Contains(s.CursorStyle) ? s.CursorStyle : "cyan";
        CursorBox.SelectionChanged += (_, _) =>
        {
            _s.CursorStyle = CursorBox.SelectedItem as string ?? "cyan";
            _s.Save();
            _onApply();
        };
        TapClick.IsChecked = s.TapToClick;
        FakeCur.IsChecked = s.FakeCursor;
        SwapBtn.IsChecked = s.SwapButtons;
        Speed.ValueChanged += (_, _) => Live();
        OpacityS.ValueChanged += (_, _) => Live();
        LongMs.ValueChanged += (_, _) => Live();

        BuildGestureGrid(FloatGrid, s.Gestures, "float");
        BuildGestureGrid(ArtistGrid, s.ArtistGestures, "artist");
        BuildGestureGrid(VirtualGrid, s.VirtualGestures, "virtual");
        BuildStripGrid();

        PresetBtn.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "TouchMousePointer preset (*.ini)|*.ini" };
            if (dlg.ShowDialog() == true) { _s.PresetFile = dlg.FileName; ApplySave(); }
        };
        SaveBtn.Click += (_, _) => ApplySave();

        VjoyStatus.Text = "상태: " + (VjoyInstalled() ? "설치됨" : "미설치");
        VjoyDl.Click += (_, _) => OpenUrl("https://sourceforge.net/projects/vjoystick/files/");
        VjoyFork.Click += (_, _) => OpenUrl("https://github.com/jshafer817/vJoy");
    }

    private void BuildGestureGrid(Grid grid, GestureMap map, string prefix)
    {
        for (int i = 0; i < Slots.Length; i++)
        {
            var (key, label, get, _) = Slots[i];
            var tb = new TextBlock
            {
                Text = label, VerticalAlignment = VerticalAlignment.Center,
            };
            var cb = new WComboBox
            {
                ItemsSource = GestureMap.Actions,
                SelectedItem = get(map),
                Margin = new Thickness(4, 2, 0, 2),
            };
            Grid.SetRow(tb, i); Grid.SetColumn(tb, 0);
            Grid.SetRow(cb, i); Grid.SetColumn(cb, 1);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(tb); grid.Children.Add(cb);
            _gestures[prefix + ":" + key] = cb;
        }
    }

    private static readonly (string key, string label)[] StripSlots =
    [
        ("tap", "스트립 탭"), ("swipe_left", "스트립 왼쪽으로"),
        ("swipe_right", "스트립 오른쪽으로"), ("swipe_up", "스트립 위로"),
        ("swipe_down", "스트립 아래로"),
    ];

    private void BuildStripGrid()
    {
        var m = _s.StripGestures;
        string Get(string k) => k switch
        {
            "tap" => m.Tap, "swipe_left" => m.SwipeLeft,
            "swipe_right" => m.SwipeRight, "swipe_up" => m.SwipeUp,
            _ => m.SwipeDown,
        };
        for (int i = 0; i < StripSlots.Length; i++)
        {
            var tb = new TextBlock
            {
                Text = StripSlots[i].label,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var cb = new WComboBox
            {
                ItemsSource = StripGestureMap.Actions,
                SelectedItem = Get(StripSlots[i].key),
                Margin = new Thickness(4, 2, 0, 2),
            };
            Grid.SetRow(tb, i); Grid.SetColumn(tb, 0);
            Grid.SetRow(cb, i); Grid.SetColumn(cb, 1);
            StripGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            StripGrid.Children.Add(tb); StripGrid.Children.Add(cb);
            _gestures["strip:" + StripSlots[i].key] = cb;
        }
    }

    private void Live()
    {
        _s.Speed = Speed.Value;
        _s.Opacity = OpacityS.Value;
        _s.LongPressMs = (int)LongMs.Value;
        _onApply();
    }

    private void ApplySave()
    {
        _s.Speed = Speed.Value;
        _s.Opacity = OpacityS.Value;
        _s.LongPressMs = (int)LongMs.Value;
        if (LayoutBox.SelectedItem is string l) _s.Layout = l;
        if (CursorBox.SelectedItem is string c) _s.CursorStyle = c;
        _s.TapToClick = TapClick.IsChecked == true;
        _s.FakeCursor = FakeCur.IsChecked == true;
        _s.SwapButtons = SwapBtn.IsChecked == true;
        var sg = _s.StripGestures;
        string SGet(string k) => _gestures.TryGetValue("strip:" + k, out var c)
            ? (c.SelectedItem as string ?? "none") : "none";
        sg.Tap = SGet("tap"); sg.SwipeLeft = SGet("swipe_left");
        sg.SwipeRight = SGet("swipe_right"); sg.SwipeUp = SGet("swipe_up");
        sg.SwipeDown = SGet("swipe_down");
        foreach (var (key, _, _, set) in Slots)
        {
            if (_gestures.TryGetValue("float:" + key, out var a)) set(_s.Gestures, Sel(a));
            if (_gestures.TryGetValue("artist:" + key, out var b)) set(_s.ArtistGestures, Sel(b));
            if (_gestures.TryGetValue("virtual:" + key, out var c2)) set(_s.VirtualGestures, Sel(c2));
        }
        _s.Save();
        _onApply();
    }

    private static string Sel(WComboBox cb) => cb.SelectedItem as string ?? "none";

    private static bool VjoyInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\vjoy");
            return key != null;
        }
        catch { return false; }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
