using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private readonly Action? _showPad;

    /// <summary>True while the constructor sets initial control values:
    /// those fire change events that must not Apply (opening settings
    /// used to pop the pad open via ApplyPad).</summary>
    private bool _loading = true;

    private void FireApply()
    {
        if (_loading) return;
        try { _onApply(); } catch { }
    }

    // Own-titlebar drag state (pad chrome pattern).
    private bool _chromeTouch, _chromeMouse;
    private int _chromeTouchId = -1;
    private double _grabX, _grabY;

    /// <summary>Keeps the grabbed bar point under the pointer.</summary>
    private void MoveChrome(Point rp)
    {
        double tx = rp.X + Left - _grabX;
        double ty = rp.Y + Top - _grabY;
        if (Math.Abs(tx - Left) < 0.5 && Math.Abs(ty - Top) < 0.5) return;
        var vx = SystemParameters.VirtualScreenLeft;
        var vy = SystemParameters.VirtualScreenTop;
        var vw = SystemParameters.VirtualScreenWidth;
        var vh = SystemParameters.VirtualScreenHeight;
        Left = Math.Max(vx, Math.Min(vx + vw - Width, tx));
        Top = Math.Max(vy, Math.Min(vy + vh - Height, ty));
    }

    private static readonly string[] StripEdges = ["상", "하", "좌", "우"];
    private static readonly string[] StripSides = ["왼쪽", "중앙", "오른쪽"];

    private static string SideLabel(string? v) => v switch
    {
        "right" => "오른쪽",
        "center" => "중앙",
        _ => "왼쪽",
    };

    private static string SideValue(string? l) => l switch
    {
        "오른쪽" => "right",
        "중앙" => "center",
        _ => "left",
    };

    private static string EdgeLabel(string? v) => v switch
    {
        "bottom" => "하",
        "left" => "좌",
        "right" => "우",
        _ => "상",
    };

    private static string EdgeValue(string? l) => l switch
    {
        "하" => "bottom",
        "좌" => "left",
        "우" => "right",
        _ => "top",
    };

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
        Action<string, bool>? showAux = null, Action? showPad = null)
    {
        _s = s;
        _onApply = onApply;
        _showAux = showAux;
        _showPad = showPad;
        InitializeComponent();
        // Own title bar: touch-reachable close + drag to move (mouse/touch).
        TitleCloseBtn.Click += (_, _) => Close();
        // Title drag, same as the pad chrome: grab-offset absolute
        // positioning (the grabbed point stays under the finger).
        // Absolute targets are idempotent, so touch and promoted-mouse
        // paths converge instead of doubling deltas.
        TitleBar.PreviewTouchDown += (_, e) =>
        {
            if (Core.WpfHit.IsButton(e.OriginalSource)) return;
            _chromeTouch = true;
            _chromeTouchId = e.TouchDevice.Id;
            var rp = e.GetTouchPoint(TitleBar).Position;
            _grabX = rp.X; _grabY = rp.Y;
            TitleBar.CaptureTouch(e.TouchDevice);
            e.Handled = true;
        };
        TitleBar.PreviewTouchMove += (_, e) =>
        {
            if (!_chromeTouch || e.TouchDevice.Id != _chromeTouchId) return;
            MoveChrome(e.GetTouchPoint(TitleBar).Position);
            e.Handled = true;
        };
        TitleBar.PreviewTouchUp += (_, e) =>
        {
            if (!_chromeTouch || e.TouchDevice.Id != _chromeTouchId) return;
            _chromeTouch = false;
            _chromeTouchId = -1;
            TitleBar.ReleaseTouchCapture(e.TouchDevice);
            e.Handled = true;
        };
        TitleBar.PreviewMouseDown += (_, e) =>
        {
            if (Core.WpfHit.IsButton(e.OriginalSource)) return;
            _chromeMouse = true;
            var rp = e.GetPosition(TitleBar);
            _grabX = rp.X; _grabY = rp.Y;
            TitleBar.CaptureMouse();
            e.Handled = true;
        };
        TitleBar.PreviewMouseMove += (_, e) =>
        {
            if (!_chromeMouse) return;
            MoveChrome(e.GetPosition(TitleBar));
            e.Handled = true;
        };
        TitleBar.PreviewMouseUp += (_, _) =>
        {
            _chromeMouse = false;
            try { TitleBar.ReleaseMouseCapture(); } catch { }
        };
        Speed.Value = s.Speed;
        OpacityS.Value = s.Opacity;
        TapJudgeMs.Value = s.TapJudgeMs;
        TapClick.IsChecked = s.TapToClick;
        TapClick.Click += (_, _) => ApplySave();
        ScrollInv.IsChecked = s.ScrollInvert;
        ScrollInv.Click += (_, _) => ApplySave();
        SwapBtn.IsChecked = s.SwapButtons;
        SwapBtn.Click += (_, _) => ApplySave();
        DebugLbl.IsChecked = s.DebugLabels;
        DebugLbl.Click += (_, _) => ApplySave();
        AutoStart.IsChecked = IsAutoStartOn();
        AutoStart.Click += (_, _) =>
        {
            SetAutoStart(AutoStart.IsChecked == true);
            _s.AutoStart = AutoStart.IsChecked == true;
            _s.Save();
        };
        // Strip placement + size (reference General).
        RefreshMonitorList();
        StripMonBox.SelectionChanged += (_, _) =>
        {
            if (StripMonBox.SelectedItem is ComboBoxItem it)
            {
                string dev = it.Tag as string ?? "";
                if (dev == PrimaryDevice()) dev = "";
                _s.StripMonitor = dev;
                _s.Save();
                FireApply();
            }
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnSysDisplayChanged;
        Closed += (_, _) =>
        {
            try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnSysDisplayChanged; }
            catch { }
        };
        StripEdgeBox.ItemsSource = StripEdges;
        StripEdgeBox.SelectedItem = EdgeLabel(s.StripEdge);
        StripEdgeBox.SelectionChanged += (_, _) =>
        {
            _s.StripEdge = EdgeValue(StripEdgeBox.SelectedItem as string);
            _s.Save();
            FireApply();
        };
        StripSideBox.ItemsSource = StripSides;
        StripSideBox.SelectedItem = SideLabel(s.StripSide);
        StripSideBox.SelectionChanged += (_, _) =>
        {
            _s.StripSide = SideValue(StripSideBox.SelectedItem as string);
            _s.Save();
            FireApply();
        };
        StripPxBox.Text = s.StripPx.ToString();
        StripWBox.Text = s.StripWidth.ToString();
        StripHBox.Text = s.StripHeight.ToString();
        StripOpacityS.Value = s.StripOpacity;
        StripOpacityS.ValueChanged += (_, _) => Live();
        WirePicker(StripColorPicker, ColorPalettes.Zones, false,
            () => _s.StripColor, v => _s.StripColor = v ?? "#10131A");
        WirePicker(StripTextPicker, ColorPalettes.Effects, false,
            () => _s.StripTextColor, v => _s.StripTextColor = v ?? "#FF9AA6BD");
        StripPxBox.LostFocus += (_, _) => CommitStripNumbers();
        StripWBox.LostFocus += (_, _) => CommitStripNumbers();
        StripHBox.LostFocus += (_, _) => CommitStripNumbers();
        CellKindBox.ItemsSource = KindItems;
        CellFuncGroupBox.ItemsSource = FuncGroups;
        CellFuncGroupBox.SelectedItem = "전체";
        RefilterFuncBox("전체");
        CellFuncGroupBox.SelectionChanged += (_, _) =>
        {
            string g = CellFuncGroupBox.SelectedItem as string ?? "전체";
            string keep = CellFuncBox.SelectedItem as string ?? "";
            RefilterFuncBox(g);
            var items = CellFuncBox.ItemsSource as System.Collections.IList;
            if (items != null && items.Contains(keep)) CellFuncBox.SelectedItem = keep;
            else if (items != null && items.Count > 0) CellFuncBox.SelectedItem = items[0];
        };
        CellKindBox.SelectionChanged += (_, _) => { SyncCellValueInput(); CommitDetail(); };
        CellFuncBox.SelectionChanged += (_, _) => CommitDetail();
        CellLabelBox.LostFocus += (_, _) => CommitDetail();
        CellValueBox.LostFocus += (_, _) => CommitDetail();
        CellIconPicker.BasePalette = ColorPalettes.Zones;
        CellIconPicker.CustomColors = _s.CustomColors;
        CellIconPicker.AllowFollow = false;
        CellIconPicker.Picked += _ => CommitDetail();
        CellTextPicker.BasePalette = ColorPalettes.Effects;
        CellTextPicker.CustomColors = _s.CustomColors;
        CellTextPicker.AllowFollow = false;
        CellTextPicker.Picked += _ => CommitDetail();
        CellWBox.LostFocus += (_, _) => CommitDetail();
        CellHBox.LostFocus += (_, _) => CommitDetail();
        CellDelBtn.Click += (_, _) => DeleteSelCell();
        CellClearBtn.Click += (_, _) => ClearSelCell();
        CellInsertBtn.Click += (_, _) => { ImgPathBox.Text = _selImage; InsertPopup.IsOpen = true; };
        ImgBrowseBtn.Click += (_, _) => { try { var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "image|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico|all|*.*" }; if (dlg.ShowDialog() == true) ImgPathBox.Text = dlg.FileName; } catch { } };
        ImgAttachBtn.Click += (_, _) => { _selImage = (ImgPathBox.Text ?? "").Trim(); CommitDetail(); InsertPopup.IsOpen = false; };
        BuildInsertGrids();
        StripRowAdd2.Click += (_, _) => { _s.StripLayout.Add(new StripRow()); _s.Save(); RebuildStripRows(); };
        SyncCellValueInput();
        RebuildStripRows();
        Speed.ValueChanged += (_, _) => Live();
        OpacityS.ValueChanged += (_, _) => Live();
        TapJudgeMs.ValueChanged += (_, _) => Live();
        UpdateValLabels();
        WirePicker(EffectPicker, ColorPalettes.Effects, false, () => _s.EffectColor, v => _s.EffectColor = v ?? ColorPalettes.Effects[0]);
        WirePicker(ZoneLPicker, ColorPalettes.Zones, false, () => _s.ZoneLeft, v => _s.ZoneLeft = v ?? "없음");
        WirePicker(ZoneRPicker, ColorPalettes.Zones, false, () => _s.ZoneRight, v => _s.ZoneRight = v ?? "없음");
        WirePicker(ZoneWPicker, ColorPalettes.Zones, false, () => _s.ZoneWheel, v => _s.ZoneWheel = v ?? "없음");
        WirePicker(ZonePPicker, ColorPalettes.Zones, false, () => _s.ZonePad, v => _s.ZonePad = v ?? "없음");
        WirePicker(ZoneBgPicker, ColorPalettes.Zones, false, () => _s.ZoneBg, v => _s.ZoneBg = v ?? "#8C1B1E24");

        BuildGestureGrid(FloatGrid, s.Gestures, "float");
        BuildGestureGrid(ArtistGrid, s.ArtistGestures, "artist");
        BuildGestureGrid(VirtualGrid, s.VirtualGestures, "virtual");
        InitAuxTabs();
        InitPadsTab();
        InitActionsTab();

        SaveBtn.Click += (_, _) => ApplySave();
        SaveBtnFloat.Click += (_, _) => ApplySave();
        SaveBtnArtist.Click += (_, _) => ApplySave();
        SaveBtnVirtual.Click += (_, _) => ApplySave();
        _loading = false;
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
        _s.StripOpacity = StripOpacityS.Value;
        UpdateValLabels();
        FireApply();
    }

    private void UpdateValLabels()
    {
        try
        {
            SpeedVal.Text = $"{Speed.Value:0.0}x";
            OpacityVal.Text = $"{(1 - OpacityS.Value) * 100:0}%";
            StripOpacityVal.Text = $"{(1 - StripOpacityS.Value) * 100:0}%";
            TapJudgeVal.Text = $"{(int)TapJudgeMs.Value} ms";
        }
        catch { }
    }

    private void ApplySave()
    {
        _s.Speed = Speed.Value;
        _s.Opacity = OpacityS.Value;
        _s.TapJudgeMs = (int)TapJudgeMs.Value;
        _s.StripOpacity = StripOpacityS.Value;
        _s.TapToClick = TapClick.IsChecked == true;
        _s.ScrollInvert = ScrollInv.IsChecked == true;
        _s.SwapButtons = SwapBtn.IsChecked == true;
        _s.DebugLabels = DebugLbl.IsChecked == true;
        _s.StripEdge = EdgeValue(StripEdgeBox.SelectedItem as string);
        _s.StripSide = SideValue(StripSideBox.SelectedItem as string);
        TryParseStripNumbers();
        foreach (var (key, _, _, set) in Slots)
        {
            if (_gestures.TryGetValue("float:" + key, out var a)) set(_s.Gestures, Sel(a));
            if (_gestures.TryGetValue("artist:" + key, out var b)) set(_s.ArtistGestures, Sel(b));
            if (_gestures.TryGetValue("virtual:" + key, out var c2)) set(_s.VirtualGestures, Sel(c2));
        }
        foreach (var r in _s.StripLayout) { while (r.Cells.Count < 4) r.Cells.Add(""); }
        _s.Save();
        FireApply();
    }

    /// <summary>Strip monitor dropdown: rebuilt live so plugged /
    /// unplugged monitors appear and vanish. A vanished selection
    /// falls back to primary (model resolves live too).</summary>
    private void RefreshMonitorList()
    {
        try
        {
            var mons = Core.MonitorList.All();
            StripMonBox.Items.Clear();
            foreach (var m in mons)
            {
                var item = new ComboBoxItem
                {
                    Content = m.label + $"  ({m.device})",
                    Tag = m.device,
                };
                StripMonBox.Items.Add(item);
            }
            ComboBoxItem? sel = null;
            foreach (ComboBoxItem it in StripMonBox.Items)
                if ((it.Tag as string) == _s.StripMonitor) sel = it;
            if (sel == null)
                foreach (ComboBoxItem it in StripMonBox.Items)
                    if (string.IsNullOrEmpty(it.Tag as string)) sel = it;
            if (sel == null && StripMonBox.Items.Count > 0)
                sel = StripMonBox.Items[0] as ComboBoxItem;
            StripMonBox.SelectedItem = sel;
        }
        catch { }
    }

    private void OnSysDisplayChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() => OnDisplayChanged());

    private void OnDisplayChanged()
    {
        try
        {
            // A vanished monitor: re-resolve now (attach to next).
            var sc = Core.MonitorList.Resolve(_s.StripMonitor);
            string prim = PrimaryDevice();
            if (sc != null && sc.DeviceName != _s.StripMonitor)
            {
                _s.StripMonitor = sc.DeviceName == prim ? "" : sc.DeviceName;
                _s.Save();
            }
            RefreshMonitorList();
            FireApply();
        }
        catch { }
    }

    private static string PrimaryDevice()
    {
        try
        {
            foreach (var sc in System.Windows.Forms.Screen.AllScreens)
                if (sc.Primary) return sc.DeviceName;
        }
        catch { }
        return "";
    }

    private void RememberCustom(string hex)
    {
        if (ColorPalettes.Effects.Contains(hex)) return;
        if (ColorPalettes.Zones.Contains(hex)) return;
        if (_s.CustomColors.Contains(hex)) return;
        _s.CustomColors.Add(hex);
        while (_s.CustomColors.Count > 12) _s.CustomColors.RemoveAt(0);
    }

    private void CommitStripNumbers()
    {
        if (TryParseStripNumbers())
        {
            _s.Save();
            FireApply();
        }
    }

    private bool TryParseStripNumbers()
    {
        if (!int.TryParse((StripPxBox.Text ?? "").Trim(), out int p)) { StripPxBox.Text = _s.StripPx.ToString(); return false; }
        if (!double.TryParse((StripWBox.Text ?? "").Trim(), out double w) || w < 40) { StripWBox.Text = _s.StripWidth.ToString(); return false; }
        if (!double.TryParse((StripHBox.Text ?? "").Trim(), out double h) || h < 12) { StripHBox.Text = _s.StripHeight.ToString(); return false; }
        _s.StripPx = p;
        _s.StripWidth = w;
        _s.StripHeight = h;
        return true;
    }

    private void WirePicker(PalettePicker box, IEnumerable<string> basePal, bool allowFollow, Func<string?> get, Action<string?> set)
    {
        box.BasePalette = basePal;
        box.CustomColors = _s.CustomColors;
        box.AllowFollow = allowFollow;
        box.Selected = get() ?? "";
        box.Picked += v => CommitPickerValue(box, v, allowFollow, get, set);
    }

    private void CommitPickerValue(PalettePicker box, string? v, bool allowFollow, Func<string?> get, Action<string?> set)
    {
        string val = (v ?? "").Trim();
        if (allowFollow && (val == ColorPalettes.Follow || val == "")) set(null);
        else if (ColorPalettes.IsNone(val)) set(ColorPalettes.None);
        else
        {
            try { var _ = (Color)ColorConverter.ConvertFromString(val); set(val); RememberCustom(val); }
            catch { box.Selected = get() ?? ""; return; }
        }
        _s.Save();
        FireApply();
    }

    private static readonly string[] KindItems = ["기능", "cmd", "프로그램", "단축키"];

    private static readonly string[] FuncGroups = ["전체", "레이아웃", "패드", "윈도우"];

    private static readonly (string group, string show, string val)[] FuncItems =
    [
        ("레이아웃", "레이아웃: float", "layout:floatpad"),
        ("레이아웃", "레이아웃: Left", "layout:leftpad"),
        ("레이아웃", "레이아웃: Right", "layout:rightpad"),
        ("레이아웃", "레이아웃: Full Screen", "layout:fullscreen"),
        ("레이아웃", "레이아웃: ArtistPad", "layout:ArtistPad"),
        ("레이아웃", "레이아웃: Virtual Ctrl", "layout:virtualctrls"),
        ("패드", "이전 레이아웃", "prev_layout"),
        ("패드", "다음 레이아웃", "next_layout"),
        ("패드", "모드 패널", "show_modes"),
        ("패드", "설정 열기", "open_settings"),
        ("패드", "전체화면 토글", "toggle_fullscreen"),
        ("패드", "보조패드", "show_assist"),
        ("패드", "패드 켜기/끄기", "toggle_pad"),
        ("윈도우", "바탕화면 보기", "shortcut:Win+D"),
        ("윈도우", "작업 보기", "shortcut:Win+Tab"),
        ("윈도우", "창 닫기", "window:close_window"),
        ("윈도우", "왼쪽 스냅", "shortcut:Win+Left"),
        ("윈도우", "오른쪽 스냅", "shortcut:Win+Right"),
        ("윈도우", "최대화", "window:maximize"),
        ("윈도우", "최소화/복원", "window:minimize"),
        ("윈도우", "이전 데스크톱", "shortcut:Win+Ctrl+Left"),
        ("윈도우", "다음 데스크톱", "shortcut:Win+Ctrl+Right"),
        ("윈도우", "새 데스크톱", "shortcut:Win+Ctrl+D"),
        ("윈도우", "데스크톱 닫기", "shortcut:Win+Ctrl+F4"),
        ("윈도우", "탐색기", "shortcut:Win+E"),
        ("윈도우", "설정", "shortcut:Win+I"),
        ("윈도우", "실행", "shortcut:Win+R"),
        ("윈도우", "검색", "shortcut:Win+S"),
        ("윈도우", "작업 관리자", "shortcut:Ctrl+Shift+Esc"),
        ("윈도우", "알림", "shortcut:Win+N"),
        ("윈도우", "빠른 설정", "shortcut:Win+A"),
        ("윈도우", "캡처", "shortcut:Win+Shift+S"),
        ("윈도우", "이모지", "shortcut:Win+Period"),
        ("윈도우", "화면 잠금", "shortcut:Win+L"),
        ("윈도우", "모두 최소화", "shortcut:Win+M"),
        ("윈도우", "최소화 취소", "shortcut:Win+Shift+M"),
        ("윈도우", "앱 전환", "shortcut:Alt+Tab"),
        ("윈도우", "PrintScreen", "shortcut:PRTSCN"),
        ("윈도우", "메뉴 키", "shortcut:APPS"),
        ("윈도우", "음소거", "shortcut:VOLMUTE"),
        ("윈도우", "볼륨 +", "shortcut:VOLUP"),
        ("윈도우", "볼륨 -", "shortcut:VOLDOWN"),
        ("윈도우", "재생/일시정지", "shortcut:PLAYPAUSE"),
        ("윈도우", "다음 트랙", "shortcut:NEXT"),
        ("윈도우", "이전 트랙", "shortcut:PREV"),
    ];

    private static string FuncGroupOf(string v) =>
        FuncItems.FirstOrDefault(f => f.val == v).group ?? "전체";

    private void RefilterFuncBox(string group)
    {
        CellFuncBox.ItemsSource = FuncItems
            .Where(f => group == "전체" || f.group == group)
            .Select(f => f.show).ToList();
    }

    private int _selRow = -1, _selCol = -1;
    private string _selImage = "";

    private static string FuncLabel(string v) => FuncItems.FirstOrDefault(f => f.val == v).show ?? FuncItems[0].show;

    private static string FuncValue(string? s) => FuncItems.FirstOrDefault(f => f.show == s).val ?? FuncItems[0].val;

    private void SyncCellValueInput()
    {
        bool fn = (CellKindBox.SelectedItem as string ?? "기능") == "기능";
        CellFuncPanel.Visibility = fn ? Visibility.Visible : Visibility.Collapsed;
        CellValueBox.Visibility = fn ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RebuildStripRows()
    {
        try
        {
            StripRows.Children.Clear();
            for (int ri = 0; ri < _s.StripLayout.Count; ri++)
            {
                int r = ri;
                var row = _s.StripLayout[r];
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                var del = new Button { Content = "행삭제", Width = 52, Margin = new Thickness(0, 0, 4, 0) };
                del.Click += (_, _) => { _s.StripLayout.RemoveAt(r); _selRow = -1; _selCol = -1; _s.Save(); RebuildStripRows(); };
                sp.Children.Add(del);
                for (int ci = 0; ci < row.Cells.Count && ci < 6; ci++)
                {
                    int c = ci;
                    var cell = StripCell.Parse(row.Cells[c]);
                    var b = new Button { Content = cell.IsEmpty ? "빈칸" : cell.Label, MinWidth = 64, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(6, 2, 6, 2), Foreground = ColorPalettes.CellTextBrush(cell.TextColor, _s.StripTextColor) };
                    try
                    {
                        if (string.IsNullOrWhiteSpace(cell.Color) || ColorPalettes.IsNone(cell.Color)) b.Background = Brushes.Transparent;
                        else b.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(cell.Color));
                    }
                    catch { }
                    if (r == _selRow && c == _selCol) { b.BorderBrush = Brushes.Black; b.BorderThickness = new Thickness(2); }
                    b.Click += (_, _) => SelectStripCell(r, c);
                    sp.Children.Add(b);
                }
                var add = new Button { Content = "+", Width = 30, ToolTip = "열추가" };
                add.Click += (_, _) => { row.Cells.Add(""); _s.Save(); RebuildStripRows(); };
                sp.Children.Add(add);
                StripRows.Children.Add(sp);
            }
        }
        catch { }
    }

    private void SelectStripCell(int r, int c)
    {
        try
        {
            _selRow = r; _selCol = c;
            var cell = StripCell.Parse(_s.StripLayout[r].Cells[c]);
            CellLabelBox.Text = cell.Label;
            _selImage = cell.Image;
            CellIconPicker.Selected = cell.Color;
            CellTextPicker.Selected = cell.TextColor;
            CellWBox.Text = cell.CellW > 0 ? cell.CellW.ToString() : "";
            CellHBox.Text = cell.CellH > 0 ? cell.CellH.ToString() : "";
            CellKindBox.SelectedItem = KindItems.Contains(cell.Kind) ? cell.Kind : "기능";
            SyncCellValueInput();
            CellValueBox.Text = cell.Value;
            CellValueBox.ToolTip = (CellKindBox.SelectedItem as string) == "cmd" ? "명령줄 (백그라운드 실행)" : ((CellKindBox.SelectedItem as string) == "프로그램" ? "exe 경로" : "예: Ctrl+C");
            // Show the cell's group (a window cell opens under 윈도우).
            string grp = FuncGroupOf(cell.Value);
            CellFuncGroupBox.SelectedItem = grp;
            RefilterFuncBox(grp);
            CellFuncBox.SelectedItem = FuncLabel(cell.Value);
            RebuildStripRows();
        }
        catch { }
    }

    private void CommitDetail()
    {
        try
        {
            if (_selRow < 0 || _selCol < 0 || _selRow >= _s.StripLayout.Count) return;
            var row = _s.StripLayout[_selRow];
            if (_selCol >= row.Cells.Count) return;
            string kind = CellKindBox.SelectedItem as string ?? "기능";
            string val = kind == "기능" ? FuncValue(CellFuncBox.SelectedItem as string) : (CellValueBox.Text ?? "").Trim();
            string col = (CellIconPicker.Selected ?? "").Trim();
            if (ColorPalettes.IsHex(col)) RememberCustom(col);
            string tcol = (CellTextPicker.Selected ?? "").Trim();
            if (ColorPalettes.IsHex(tcol)) RememberCustom(tcol);
            double cw = 0, ch = 0;
            double.TryParse((CellWBox.Text ?? "").Trim(), out cw);
            double.TryParse((CellHBox.Text ?? "").Trim(), out ch);
            if (cw < 0) cw = 0;
            if (ch < 0) ch = 0;
            row.Cells[_selCol] = (CellLabelBox.Text ?? "").Trim() + "|" + kind + "|" + val + "|" + col + "|" + _selImage
                + "|" + tcol + "|" + (cw > 0 ? cw.ToString() : "") + "|" + (ch > 0 ? ch.ToString() : "");
            _s.Save();
            RebuildStripRows();
        }
        catch { }
    }

    private void ClearSelCell()
    {
        try
        {
            if (_selRow < 0 || _selCol < 0 || _selRow >= _s.StripLayout.Count) return;
            var row = _s.StripLayout[_selRow];
            if (_selCol >= row.Cells.Count) return;
            row.Cells[_selCol] = "";
            _s.Save();
            RebuildStripRows();
            SelectStripCell(_selRow, _selCol);
        }
        catch { }
    }

    private void DeleteSelCell()
    {
        try
        {
            if (_selRow < 0 || _selCol < 0 || _selRow >= _s.StripLayout.Count) return;
            var row = _s.StripLayout[_selRow];
            if (_selCol >= row.Cells.Count) return;
            row.Cells.RemoveAt(_selCol);
            if (_selCol >= row.Cells.Count) _selCol = row.Cells.Count - 1;
            if (row.Cells.Count == 0) { _selRow = -1; _selCol = -1; }
            _s.Save();
            RebuildStripRows();
            if (_selRow >= 0 && _selCol >= 0) SelectStripCell(_selRow, _selCol);
        }
        catch { }
    }

    private static readonly string[] EmojiItems = ["\U0001F600", "\U0001F601", "\U0001F602", "\U0001F923", "\U0001F60A", "\U0001F60D", "\U0001F60E", "\U0001F914", "\U0001F44D", "\U0001F44E", "\U0001F44F", "\U0001F64F", "\U0001F4AA", "\U0001F525", "\U00002B50", "\U0001F319", "\U00002600", "\U0001F308", "\U0001F389", "\U0001F381", "\U000026BD", "\U0001F3E0", "\U0001F697", "\U00002708", "\U000026FA", "\U0001F338", "\U0001F355", "\U00002615", "\U0001F4A1", "\U0001F514", "\U0001F50B", "\U0001F4CC", "\U0001F4C1", "\U0001F4BE", "\U00002764", "\U0001F494", "\U00002705", "\U0000274C", "\U00002753", "\U00002757", "\U0001F4AF", "\U0001F512", "\U0001F513", "\U0001F3B5", "\U0001F4F7"];

    private static readonly string[] SymItems = ["\u2605", "\u2606", "\u25CF", "\u25CB", "\u25C6", "\u25C7", "\u25B2", "\u25B3", "\u25BC", "\u25BD", "\u25A0", "\u25A1", "\u25AA", "\u25AB", "\u2190", "\u2191", "\u2192", "\u2193", "\u2194", "\u2195", "\u2715", "\u2713", "\u2714", "\u2766", "\u25B6", "\u25B7", "\u2665", "\u2666", "\u2663", "\u2660", "\u266A", "\u266B", "\u00A9", "\u00AE", "\u2122", "\u00A7", "\u00B6", "\u00B0", "\u00B1", "\u00D7", "\u00F7", "\u2260", "\u2248", "\u221E", "\u03C0", "\u03A9", "\u03B1", "\u2026", "\u2014", "\u2500", "\u2502", "\u250C", "\u2510", "\u2514", "\u2518", "\u2550", "\u203C", "\u2049"];

    private void BuildInsertGrids()
    {
        try
        {
            foreach (var e in EmojiItems)
            {
                var b = new Button { Content = e, FontSize = 15, Width = 32, Height = 30, Margin = new Thickness(1), ToolTip = e };
                b.Click += (_, _) => { CellLabelBox.Text += e; };
                EmojiGrid.Children.Add(b);
            }
            foreach (var e in SymItems)
            {
                var b = new Button { Content = e, FontSize = 15, Width = 32, Height = 30, Margin = new Thickness(1), ToolTip = e };
                b.Click += (_, _) => { CellLabelBox.Text += e; };
                SymGrid.Children.Add(b);
            }
        }
        catch { }
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
        BuildGestureGrid(PadGestureGrid, _s.Gestures, "pad:");
        foreach (var (key, _, _, _) in Slots)
            if (_gestures.TryGetValue("pad:" + key, out var cb))
                cb.SelectionChanged += (_, _) => { if (!_padSync) SavePadGestures(); };
        PadGesturesOwn.Checked += (_, _) => { PadGestureGrid.IsEnabled = true; SavePadGestures(); };
        PadGesturesOwn.Unchecked += (_, _) => { PadGestureGrid.IsEnabled = false; SavePadGestures(); };
        PadSel.SelectionChanged += (_, _) => LoadPadTab();
        PadArea.SelectionChanged += (_, _) => SavePadTab();
        PadVisible.Click += (_, _) => SavePadTab();
        PadScroll.SelectionChanged += (_, _) => SavePadTab();
        PadSwap.SelectionChanged += (_, _) => SavePadTab();
        PadTap.SelectionChanged += (_, _) => SavePadTab();
        PadPreview.Click += (_, _) => PreviewPad();
        LoadPadTab();
    }

    /// <summary>Suppresses gesture-combo save-backs while LoadPadTab
    /// fills them programmatically.</summary>
    private bool _padSync;

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
        FillPadGestures(name, p);
    }

    /// <summary>Fills the per-pad gesture grid: the override when set,
    /// else the effective family map (shown disabled).</summary>
    private void FillPadGestures(string name, PadConfig p)
    {
        _padSync = true;
        try
        {
            var ov = p.Gestures;
            PadGesturesOwn.IsChecked = ov != null;
            var disp = ov ?? _s.ActiveGestures(name);
            foreach (var (key, _, get, _) in Slots)
                if (_gestures.TryGetValue("pad:" + key, out var cb))
                    cb.SelectedItem = get(disp);
            PadGestureGrid.IsEnabled = ov != null;
        }
        finally { _padSync = false; }
    }

    /// <summary>Writes the per-pad gesture override (or clears it when
    /// the box is off = follow family). Live save like the rest of
    /// this tab.</summary>
    private void SavePadGestures()
    {
        string name = PadSel.SelectedItem as string ?? "floatpad";
        if (!_s.Pads.TryGetValue(name, out var p))
        { p = new PadConfig(); _s.Pads[name] = p; }
        if (PadGesturesOwn.IsChecked == true)
        {
            var m = p.Gestures ?? new GestureMap();
            foreach (var (key, _, _, set) in Slots)
                if (_gestures.TryGetValue("pad:" + key, out var cb))
                    set(m, Sel(cb));
            p.Gestures = m;
        }
        else p.Gestures = null;
        _s.Save();
        FireApply();
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
        FireApply();
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
        if (_showPad != null) _showPad();
        else FireApply();
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
            _s.Save(); RefreshAuxLists(); FireApply();
        };
        VirtPos.SelectionChanged += (_, _) =>
        {
            _s.Virtual.Position = VirtPos.SelectedItem as string ?? "hidden";
            _s.Save(); RefreshAuxLists(); FireApply();
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
        _s.Save(); RefreshAuxLists(); FireApply();
    }

    private void DelAuxBtn(List<PadButton> list, ListBox box)
    {
        if (box.SelectedIndex >= 0) list.RemoveAt(box.SelectedIndex);
        _s.Save(); RefreshAuxLists(); FireApply();
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
        _s.Save(); RefreshActions(); FireApply();
    }

    private void DelAction()
    {
        if (ActList.SelectedIndex < 0) return;
        string key = (ActList.SelectedItem as string ?? "").Split(' ')[0];
        _s.Actions.Remove(key);
        _s.Save(); RefreshActions(); FireApply();
    }

    private static string Sel(WComboBox cb) => cb.SelectedItem as string ?? "none";

    private const string AutoStartKey = "TouchPadCloneRaw";
    private const string RunSubkey =
        @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Registry truth (the file flag mirrors it).</summary>
    internal static bool IsAutoStartOn()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunSubkey, false);
            return k?.GetValue(AutoStartKey) != null;
        }
        catch { return false; }
    }

    /// <summary>Registers/unregisters this exe for Windows logon.</summary>
    internal static void SetAutoStart(bool on)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunSubkey, true);
            if (k == null) return;
            if (on)
            {
                string exe = System.Diagnostics.Process.GetCurrentProcess()
                    .MainModule?.FileName ?? "";
                if (exe.Length > 0) k.SetValue(AutoStartKey, "\"" + exe + "\"");
            }
            else
            {
                try { k.DeleteValue(AutoStartKey, false); } catch { }
            }
        }
        catch { }
    }

    private static void SelBox(WComboBox c, string v)
    {
        for (int i = 0; i < c.Items.Count; i++)
            if ((c.Items[i] as string) == v) { c.SelectedIndex = i; return; }
        if (c.Items.Count > 0) c.SelectedIndex = 0;
    }
}
