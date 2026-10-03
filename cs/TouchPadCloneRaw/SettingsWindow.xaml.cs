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
    private readonly Action<string, bool>? _showAux;

    /// <summary>Label, stored value, explanation (shown under the box).</summary>
    private static readonly (string label, string value, string hint)[] PhysModes =
    [
        ("가상 마우스 존중 (물리 마우스 그대로)", "preserve",
            "가상 클릭·드래그가 실제 커서를 옮긴 뒤 원래 물리 마우스 위치로 되돌립니다. "
          + "물리 마우스는 가상 커서를 조종하지 않습니다. 대신 툴팁 등 호버 반응은 "
          + "동작 사이에는 물리 커서를 따릅니다."),
        ("물리 마우스와 통합 (기존 동작)", "unified",
            "실제 커서를 가상 커서 아래에 붙여 둡니다. 호버 반응이 항상 가상 커서를 "
          + "따르지만, 물리 마우스 커서가 가상 커서 위치로 끌려갑니다."),
    ];

    private void ApplyPhysHint()
    {
        string v = PhysBox.SelectedItem as string ?? "preserve";
        foreach (var m in PhysModes)
            if (m.value == v) { PhysHint.Text = m.hint; return; }
        PhysHint.Text = "";
    }

    private static readonly (string key, string label, Func<GestureMap, string> get, Action<GestureMap, string> set)[] Slots =
    [
        ("tap", "짧게 탭", m => m.Tap, (m, v) => m.Tap = v),
        ("double_tap", "더블탭", m => m.DoubleTap, (m, v) => m.DoubleTap = v),
        ("triple_tap", "세 번 탭", m => m.TripleTap, (m, v) => m.TripleTap = v),
        ("long_press", "길게 누르기", m => m.LongPress, (m, v) => m.LongPress = v),
        ("second_hold", "두번째 누른채", m => m.SecondHold, (m, v) => m.SecondHold = v),
        ("two_finger_tap", "두손가락 탭", m => m.TwoFingerTap, (m, v) => m.TwoFingerTap = v),
        ("swipe_up", "두손가락 위로", m => m.SwipeUp, (m, v) => m.SwipeUp = v),
        ("swipe_down", "두손가락 아래로", m => m.SwipeDown, (m, v) => m.SwipeDown = v),
        ("swipe_left", "두손가락 왼쪽으로", m => m.SwipeLeft, (m, v) => m.SwipeLeft = v),
        ("swipe_right", "두손가락 오른쪽으로", m => m.SwipeRight, (m, v) => m.SwipeRight = v),
    ];

    public SettingsWindow(AppSettings s, List<string> layouts, Action onApply,
        Action<string, bool>? showAux = null)
    {
        _s = s;
        _onApply = onApply;
        _showAux = showAux;
        InitializeComponent();
        Speed.Value = s.Speed;
        OpacityS.Value = s.Opacity;
        TapJudgeMs.Value = s.TapJudgeMs;
        HoldSlop.Value = s.HoldCancelDip;
        HoldDampS.Value = s.HoldDamp;
        MultiMs.Value = s.MultiTapMs;
        LayoutBox.ItemsSource = layouts;
        LayoutBox.SelectedItem = layouts.Contains(s.Layout) ? s.Layout : layouts.FirstOrDefault();
        TapClick.IsChecked = s.TapToClick;
        ScrollInv.IsChecked = s.ScrollInvert;
        ScrollInv.Click += (_, _) => { _s.ScrollInvert = ScrollInv.IsChecked == true; ApplySave(); };
        PhysBox.ItemsSource = PhysModes.Select(m => m.label).ToList();
        PhysBox.SelectedItem = PhysModes.FirstOrDefault(
            m => m.value == s.PhysicalMouseMode).label ?? PhysModes[0].label;
        PhysBox.SelectionChanged += (_, _) =>
        {
            string lbl = PhysBox.SelectedItem as string ?? "";
            foreach (var m in PhysModes)
                if (m.label == lbl) { _s.PhysicalMouseMode = m.value; break; }
            ApplyPhysHint();
            _s.Save();
            _onApply();
        };
        ApplyPhysHint();
        SwapBtn.IsChecked = s.SwapButtons;
        DebugLbl.IsChecked = s.DebugLabels;
        DebugLbl.Click += (_, _) => { _s.DebugLabels = DebugLbl.IsChecked == true; ApplySave(); };
        Speed.ValueChanged += (_, _) => Live();
        OpacityS.ValueChanged += (_, _) => Live();
        TapJudgeMs.ValueChanged += (_, _) => Live();
        HoldSlop.ValueChanged += (_, _) => Live();
        HoldDampS.ValueChanged += (_, _) => Live();
        MultiMs.ValueChanged += (_, _) => Live();

        BuildGestureGrid(FloatGrid, s.Gestures, "float");
        BuildGestureGrid(ArtistGrid, s.ArtistGestures, "artist");
        BuildGestureGrid(VirtualGrid, s.VirtualGestures, "virtual");
        InitAuxTabs();
        InitStripTab();
        InitPadsTab();
        InitActionsTab();

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

    private void Live()
    {
        _s.Speed = Speed.Value;
        _s.Opacity = OpacityS.Value;
        _s.TapJudgeMs = (int)TapJudgeMs.Value;
        _s.HoldCancelDip = HoldSlop.Value;
        _s.HoldDamp = HoldDampS.Value;
        _s.MultiTapMs = (int)MultiMs.Value;
        _onApply();
    }

    private void ApplySave()
    {
        _s.Speed = Speed.Value;
        _s.Opacity = OpacityS.Value;
        _s.TapJudgeMs = (int)TapJudgeMs.Value;
        _s.HoldCancelDip = HoldSlop.Value;
        _s.HoldDamp = HoldDampS.Value;
        _s.MultiTapMs = (int)MultiMs.Value;
        if (LayoutBox.SelectedItem is string l) _s.Layout = l;
        _s.TapToClick = TapClick.IsChecked == true;
        _s.ScrollInvert = ScrollInv.IsChecked == true;
        _s.SwapButtons = SwapBtn.IsChecked == true;
        _s.DebugLabels = DebugLbl.IsChecked == true;
        SaveStripTab();
        foreach (var (key, _, _, set) in Slots)
        {
            if (_gestures.TryGetValue("float:" + key, out var a)) set(_s.Gestures, Sel(a));
            if (_gestures.TryGetValue("artist:" + key, out var b)) set(_s.ArtistGestures, Sel(b));
            if (_gestures.TryGetValue("virtual:" + key, out var c2)) set(_s.VirtualGestures, Sel(c2));
        }
        _s.Save();
        _onApply();
    }

    // ---------------- strip menu tab -------------------------------

    private void InitStripTab()
    {
        StripVisible.IsChecked = _s.StripVisible;
        foreach (var p in new[] { "top", "bottom", "left", "right" }) StripPos.Items.Add(p);
        SelBox(StripPos, _s.StripPosition);
        foreach (var k in new[] { "radio", "toggle" }) StripKind.Items.Add(k);
        StripKind.SelectedIndex = 0;
        StripW.Text = _s.StripWidth.ToString();
        StripH.Text = _s.StripHeight.ToString();
        RefreshStripLists();
        StripAdd1.Click += (_, _) => AddStripItem(_s.StripRow1);
        StripAdd2.Click += (_, _) => AddStripItem(_s.StripRow2);
        StripDel.Click += (_, _) => DelStripItem();
    }

    private static void SelBox(WComboBox c, string v)
    {
        for (int i = 0; i < c.Items.Count; i++)
            if ((c.Items[i] as string) == v) { c.SelectedIndex = i; return; }
        if (c.Items.Count > 0) c.SelectedIndex = 0;
    }

    private void RefreshStripLists()
    {
        StripVisible.IsChecked = _s.StripVisible;
        StripRow1.Items.Clear();
        foreach (var it in _s.StripRow1)
            StripRow1.Items.Add($"{it.Name} [{it.Kind}] -> {it.Target}{(it.Fixed ? " (고정)" : "")}");
        StripRow2.Items.Clear();
        foreach (var it in _s.StripRow2)
            StripRow2.Items.Add($"{it.Name} [{it.Kind}] -> {it.Target}{(it.Fixed ? " (고정)" : "")}");
    }

    private void SaveStripTab()
    {
        _s.StripVisible = StripVisible.IsChecked == true;
        string p = StripPos.SelectedItem as string ?? "top";
        if (p.Length > 0) _s.StripPosition = p;
        if (double.TryParse(StripW.Text, out double w)) _s.StripWidth = w;
        if (double.TryParse(StripH.Text, out double h)) _s.StripHeight = h;
    }

    private void AddStripItem(List<StripRowItem> row)
    {
        SaveStripTab();
        if (StripName.Text.Length == 0) return;
        row.Add(new StripRowItem
        {
            Name = StripName.Text,
            Kind = StripKind.SelectedItem as string ?? "toggle",
            Target = StripTarget.Text.Length > 0 ? StripTarget.Text : StripName.Text,
        });
        _s.Save();
        RefreshStripLists();
        _onApply();
    }

    private void DelStripItem()
    {
        if (StripRow1.SelectedIndex >= 0)
        {
            var it = _s.StripRow1[StripRow1.SelectedIndex];
            if (!it.Fixed) _s.StripRow1.RemoveAt(StripRow1.SelectedIndex);
        }
        else if (StripRow2.SelectedIndex >= 0)
        {
            var it = _s.StripRow2[StripRow2.SelectedIndex];
            if (!it.Fixed) _s.StripRow2.RemoveAt(StripRow2.SelectedIndex);
        }
        _s.Save();
        RefreshStripLists();
        _onApply();
    }

    // ---------------- per-pad tab ------------------------------------

    private static readonly string[] PadNames =
        { "floatpad", "leftpad", "rightpad", "fullscreen", "artist", "virtual" };

    private void InitPadsTab()
    {
        foreach (var p in PadNames) PadSel.Items.Add(p);
        PadSel.SelectedIndex = 0;
        foreach (var a in new[] { "default", "full", "half-left", "half-right" }) PadArea.Items.Add(a);
        // Tri-state as an explicit combo (a 3-state checkbox cycles
        // null->false on first click, which silently wrote "off" instead
        // of "follow global" and killed taps).
        foreach (var c in new[] { PadScroll, PadSwap, PadTap })
            foreach (var o in new[] { "전역 따름", "켜기", "끄기" }) c.Items.Add(o);
        PadSel.SelectionChanged += (_, _) => LoadPadTab();
        PadArea.SelectionChanged += (_, _) => SavePadTab();
        PadVisible.Click += (_, _) => SavePadTab();
        PadScroll.SelectionChanged += (_, _) => SavePadTab();
        PadSwap.SelectionChanged += (_, _) => SavePadTab();
        PadTap.SelectionChanged += (_, _) => SavePadTab();
        PadPreview.Click += (_, _) => PreviewPad();
        LoadPadTab();
    }

    /// <summary>null(global) / true / false to combo index.</summary>
    private static int TriToIndex(bool? v) => v == null ? 0 : (v == true ? 1 : 2);

    /// <summary>Combo index to null / true / false.</summary>
    private static bool? IndexToTri(int i) => i == 1 ? true : i == 2 ? (bool?)false : null;

    private void LoadPadTab()
    {
        string name = PadSel.SelectedItem as string ?? "floatpad";
        var p = _s.Pad(name);
        SelBox(PadArea, p.AreaMode);
        PadVisible.IsChecked = p.Visible;
        PadVisible.IsEnabled = name != "fullscreen";
        PadOpacity.Text = p.Opacity.ToString();
        PadSpeed.Text = p.Speed.ToString();
        PadJudge.Text = p.TapJudgeMs.ToString();
        PadScroll.SelectedIndex = TriToIndex(p.ScrollInvert);
        PadSwap.SelectedIndex = TriToIndex(p.SwapButtons);
        PadTap.SelectedIndex = TriToIndex(p.TapToClick);
    }

    private void SavePadTab()
    {
        string name = PadSel.SelectedItem as string ?? "floatpad";
        if (!_s.Pads.TryGetValue(name, out var p))
        { p = new PadConfig(); _s.Pads[name] = p; }
        string area = PadArea.SelectedItem as string ?? "default";
        if (area.Length > 0) p.AreaMode = area;
        if (name != "fullscreen") p.Visible = PadVisible.IsChecked == true;
        if (double.TryParse(PadOpacity.Text, out double op)) p.Opacity = op;
        if (double.TryParse(PadSpeed.Text, out double sp)) p.Speed = sp;
        if (int.TryParse(PadJudge.Text, out int jg)) p.TapJudgeMs = jg;
        p.ScrollInvert = IndexToTri(PadScroll.SelectedIndex);
        p.SwapButtons = IndexToTri(PadSwap.SelectedIndex);
        p.TapToClick = IndexToTri(PadTap.SelectedIndex);
        _s.Save();
        LoadPadTab();
        _onApply();
    }

    private void PreviewPad()
    {
        SavePadTab();
        string name = PadSel.SelectedItem as string ?? "floatpad";
        if (name == "artist" || name == "virtual")
        {
            _showAux?.Invoke(name, true);
            return;
        }
        _s.Layout = name;
        _s.Save();
        _onApply();
    }

    // ---------------- artist / virtual tabs --------------------------

    private void InitAuxTabs()
    {
        foreach (var l in new[] { "top", "bottom", "left", "right", "hidden" }) ArtPos.Items.Add(l);
        SelBox(ArtPos, _s.Artist.Position);
        foreach (var l in new[] { "grid", "top", "bottom", "left", "right", "hidden" }) VirtPos.Items.Add(l);
        SelBox(VirtPos, _s.Virtual.Position);
        ArtPos.SelectionChanged += (_, _) =>
        {
            _s.Artist.Position = ArtPos.SelectedItem as string ?? "bottom";
            _s.Save(); RefreshAuxLists(); _onApply();
        };
        VirtPos.SelectionChanged += (_, _) =>
        {
            _s.Virtual.Position = VirtPos.SelectedItem as string ?? "hidden";
            _s.Save(); RefreshAuxLists(); _onApply();
        };
        ArtAdd.Click += (_, _) => AddAuxBtn(_s.Artist.Buttons, ArtLabel, ArtAction);
        ArtDel.Click += (_, _) => DelAuxBtn(_s.Artist.Buttons, ArtBtns);
        VirtAdd.Click += (_, _) => AddAuxBtn(_s.Virtual.Buttons, VirtLabel, VirtAction);
        VirtDel.Click += (_, _) => DelAuxBtn(_s.Virtual.Buttons, VirtBtns);
        RefreshAuxLists();
    }

    private void RefreshAuxLists()
    {
        ArtBtns.Items.Clear();
        foreach (var b in _s.Artist.Buttons) ArtBtns.Items.Add($"{b.Label} = {b.Action}");
        VirtBtns.Items.Clear();
        foreach (var b in _s.Virtual.Buttons) VirtBtns.Items.Add($"{b.Label} = {b.Action}");
    }

    private void AddAuxBtn(List<PadButton> list, TextBox label, TextBox action)
    {
        if (label.Text.Length == 0) return;
        list.Add(new PadButton { Label = label.Text, Action = action.Text });
        label.Text = ""; action.Text = "";
        _s.Save(); RefreshAuxLists(); _onApply();
    }

    private void DelAuxBtn(List<PadButton> list, ListBox box)
    {
        if (box.SelectedIndex >= 0) list.RemoveAt(box.SelectedIndex);
        _s.Save(); RefreshAuxLists(); _onApply();
    }

    // ---------------- actions tab ------------------------------------

    private void InitActionsTab()
    {
        foreach (var k in new[] { "mouse", "program", "cmd" }) ActKind.Items.Add(k);
        ActKind.SelectedIndex = 0;
        ActAdd.Click += (_, _) => AddAction();
        ActDel.Click += (_, _) => DelAction();
        RefreshActions();
    }

    private void RefreshActions()
    {
        ActList.Items.Clear();
        foreach (var kv in _s.Actions)
            ActList.Items.Add($"{kv.Key} [{kv.Value.Kind}] {kv.Value.Value} {kv.Value.Path} {kv.Value.Args}");
    }

    private void AddAction()
    {
        if (ActKey.Text.Length == 0) return;
        _s.Actions[ActKey.Text] = new ActionDef
        {
            Kind = ActKind.SelectedItem as string ?? "mouse",
            Value = ActValue.Text,
            Path = ActPath.Text,
            Args = ActArgs.Text,
        };
        _s.Save(); RefreshActions(); _onApply();
    }

    private void DelAction()
    {
        if (ActList.SelectedIndex < 0) return;
        string key = (ActList.SelectedItem as string ?? "").Split(' ')[0];
        _s.Actions.Remove(key);
        _s.Save(); RefreshActions(); _onApply();
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
