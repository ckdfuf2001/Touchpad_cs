using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using TouchPadCloneV2.Core;
using WComboBox = System.Windows.Controls.ComboBox;

namespace TouchPadCloneV2;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _s;
    private readonly Action _onApply;
    private Dictionary<string, Layout> _presets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WComboBox> _gestures = new();
    private readonly Action<string, bool>? _showAux;
    private readonly Action? _showPad;
    private readonly Action<Action<RecordedGesture?>>? _showRec;

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

    // Single-finger slots only. Two-finger gestures are recorded
    // templates now (FloatPad list below), not fixed rows: the saved
    // TwoFingerTap/swipe values in existing files keep working, they
    // just have no editor anymore.
    private static readonly (string key, string label, Func<GestureMap, string> get, Action<GestureMap, string> set)[] Slots =
    [
        ("tap", "짧게 탭", m => m.Tap, (m, v) => m.Tap = v),
        ("double_tap", "더블탭", m => m.DoubleTap, (m, v) => m.DoubleTap = v),
        ("triple_tap", "세 번 탭", m => m.TripleTap, (m, v) => m.TripleTap = v),
        ("long_press", "길게 누르기", m => m.LongPress, (m, v) => m.LongPress = v),
        ("second_hold", "두번째 누른채", m => m.SecondHold, (m, v) => m.SecondHold = v),
    ];

    public SettingsWindow(AppSettings s, List<string> layouts, Action onApply,
        Action<string, bool>? showAux = null, Action? showPad = null,
        Dictionary<string, Layout>? presets = null,
        Action<Action<RecordedGesture?>>? showRec = null)
    {
        _s = s;
        _onApply = onApply;
        _showAux = showAux;
        _showPad = showPad;
        _showRec = showRec;
        _presets = presets ?? new Dictionary<string, Layout>(StringComparer.OrdinalIgnoreCase);
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

        BuildGestureGrid(ArtistGrid, s.ArtistGestures, "artist");
        BuildGestureGrid(VirtualGrid, s.VirtualGestures, "virtual");
        InitAuxTabs();
        InitPadsTab();
        InitActionsTab();
        InitLayoutTab();
        InitRecTab();

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
        // Default two-finger strokes live outside Slots (FloatPad only).
        if (_gestures.TryGetValue("float:swipe_up", out var su)) _s.Gestures.SwipeUp = Sel(su);
        if (_gestures.TryGetValue("float:swipe_down", out var sd)) _s.Gestures.SwipeDown = Sel(sd);
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

    // ---------------- layout tab (preview + geometry) ----------------

    private void InitLayoutTab()
    {
        foreach (var a in new[] { "default", "full", "half-left", "half-right", "custom" })
            LayoutAreaBox.Items.Add(a);
        RefreshPresetList();
        FillLayoutSel();
        LayoutSelBox.SelectionChanged += (_, _) => { LoadLayoutEditors(); DrawPreview(); };
        PresetSelBox.SelectionChanged += (_, _) => { if (!_loading) SwitchPresetFile(); };
        LayoutAreaBox.SelectionChanged += (_, _) =>
        {
            if (!_loading && !_layoutSync) { SaveLayoutTab(); DrawPreview(); }
        };
        foreach (var b in new[] { LayoutXBox, LayoutYBox, LayoutWBox, LayoutHBox })
            b.LostFocus += (_, _) =>
            {
                if (_loading) return;
                // Editing the rect means custom: adopt it, then save.
                if ((LayoutAreaBox.SelectedItem as string) != "custom")
                    LayoutAreaBox.SelectedItem = "custom";
                SaveLayoutTab();
                DrawPreview();
            };
        LayoutSaveBtn.Click += (_, _) => { SaveLayoutTab(); DrawPreview(); };
        LayoutShowBtn.Click += (_, _) => ShowLayoutPad();
        CustomPickBtn.Click += (_, _) => ArmPick("custom");
        PadZonePickBtn.Click += (_, _) => ArmPick("pad");
        LayoutPreview.MouseMove += LayoutPreview_MouseMove;
        LayoutPreview.MouseLeftButtonDown += LayoutPreview_MouseDown;
        LayoutPreview.SizeChanged += (_, _) => { try { DrawPreview(); } catch { } };
        PadZoneDef.Checked += (_, _) => { if (!_loading) { SaveLayoutTab(); DrawPreview(); } };
        PadZoneDef.Unchecked += (_, _) => { if (!_loading) { SaveLayoutTab(); DrawPreview(); } };
        BuildZoneRows();
        // Live mouse dot: polled while settings is open.
        _mouseTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _mouseTimer.Tick += MouseTick;
        _mouseTimer.Start();
        // Display change: redraw the preview on the new topology (the
        // strip repositions the same way). Unhook on close (SystemEvents
        // holds strong refs).
        _onLayoutDisplayChanged = (_, _) =>
            Dispatcher.InvokeAsync(() => { try { DrawPreview(); } catch { } });
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += _onLayoutDisplayChanged;
        Closed += (_, _) =>
        {
            try
            {
                if (_onLayoutDisplayChanged != null)
                    Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= _onLayoutDisplayChanged;
            }
            catch { }
            try { _mouseTimer?.Stop(); _mouseTimer = null; } catch { }
        };
        if (LayoutSelBox.Items.Contains(_s.Layout)) LayoutSelBox.SelectedItem = _s.Layout;
        else if (LayoutSelBox.Items.Count > 0) LayoutSelBox.SelectedIndex = 0;
        LoadLayoutEditors();
        DrawPreview();
    }

    private string LayoutKey() => LayoutSelBox.SelectedItem as string ?? _s.Layout;

    /// <summary>Settings-window DPI (all preview math is DIPs; raw
    /// Screen.Bounds pixels are divided by this).</summary>
    private double WinDpi()
    {
        try
        {
            var s = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            if (s.DpiScaleX >= 0.5 && s.DpiScaleX <= 4) return s.DpiScaleX;
        }
        catch { }
        return 1.0;
    }

    /// <summary>Pad home monitor in DIPs (mirrors App.HomeRect: the
    /// configured strip monitor, primary fallback).</summary>
    private (double l, double t, double w, double h) LayoutHome()
    {
        double d = WinDpi();
        try
        {
            var sc = Core.MonitorList.Resolve(_s.StripMonitor);
            if (sc != null)
            {
                var b = sc.Bounds;
                if (b.Width >= 100 && b.Height >= 100)
                    return (b.Left / d, b.Top / d, b.Width / d, b.Height / d);
            }
        }
        catch { }
        return (SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
    }

    private static string TrimNum(double v) => v.ToString("0.##");

    private void RefreshPresetList()
    {
        try
        {
            var files = new List<(string show, string path)>();
            string bundled = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "presets");
            if (System.IO.Directory.Exists(bundled))
                foreach (var f in System.IO.Directory.GetFiles(bundled, "*.ini"))
                    files.Add((System.IO.Path.GetFileName(f) + " (내장)", f));
            string userDir = System.IO.Path.GetDirectoryName(AppSettings.Path) ?? "";
            if (System.IO.Directory.Exists(userDir))
                foreach (var f in System.IO.Directory.GetFiles(userDir, "*.ini"))
                    if (!files.Exists(x => x.path.Equals(f, StringComparison.OrdinalIgnoreCase)))
                        files.Add((System.IO.Path.GetFileName(f), f));
            PresetSelBox.Items.Clear();
            PresetSelBox.Items.Add("기본 내장");
            foreach (var (show, _) in files) PresetSelBox.Items.Add(show);
            PresetSelBox.Tag = files;
            string cur = _s.PresetFile ?? "";
            int sel = 0;
            for (int i = 0; i < files.Count; i++)
                if (files[i].path.Equals(cur, StringComparison.OrdinalIgnoreCase)) sel = i + 1;
            PresetSelBox.SelectedIndex = sel;
            PresetPathLbl.Text = sel == 0 ? "내장 Default.ini" : files[sel - 1].path;
        }
        catch { }
    }

    private string PresetPathFor(int sel)
    {
        if (sel <= 0) return "";
        try
        {
            if (PresetSelBox.Tag is List<(string show, string path)> files
                && sel - 1 < files.Count)
                return files[sel - 1].path;
        }
        catch { }
        return "";
    }

    private void SwitchPresetFile()
    {
        try
        {
            string path = PresetPathFor(PresetSelBox.SelectedIndex);
            var parsed = string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)
                ? null : Core.PresetParser.ParseFile(path);
            if (parsed == null || parsed.Count == 0)
            {
                LayoutInfoLbl.Text = "프리셋을 읽지 못했습니다.";
                RefreshPresetList();
                return;
            }
            _s.PresetFile = path;
            _presets = parsed;
            _s.Save();
            FillLayoutSel();
            if (!_presets.ContainsKey(_s.Layout)) _s.Layout = Core.PresetParser.OrderedNames(_presets)[0];
            if (LayoutSelBox.Items.Contains(_s.Layout)) LayoutSelBox.SelectedItem = _s.Layout;
            else if (LayoutSelBox.Items.Count > 0) LayoutSelBox.SelectedIndex = 0;
            RefreshPresetList();
            LoadLayoutEditors();
            DrawPreview();
            FireApply();
        }
        catch { }
    }

    private void FillLayoutSel()
    {
        try
        {
            LayoutSelBox.Items.Clear();
            foreach (var n in Core.PresetParser.OrderedNames(_presets))
                LayoutSelBox.Items.Add(n);
        }
        catch { }
    }

    private void LoadLayoutEditors()
    {
        _layoutSync = true;
        try
        {
            string sec = LayoutKey();
            string key = AppSettings.PadFamilyKey(sec);
            _s.Pads.TryGetValue(key, out var p);
            string mode = p?.AreaMode ?? "default";
            if (!LayoutAreaBox.Items.Contains(mode)) mode = "default";
            LayoutAreaBox.SelectedItem = mode;
            // Boxes always show the EFFECTIVE placed rect (any mode);
            // editing them switches to custom (see LostFocus above).
            var (home, r, m0, k0, s0) = PreviewGeom();
            LayoutXBox.Text = TrimNum(r.l);
            LayoutYBox.Text = TrimNum(r.t);
            LayoutWBox.Text = TrimNum(r.w);
            LayoutHBox.Text = TrimNum(r.h);
        }
        catch { }
        finally { _layoutSync = false; }
    }

    /// <summary>Suppresses save-backs while LoadLayoutEditors fills.</summary>
    private bool _layoutSync;

    private sealed class ZoneRow
    {
        public string Key = "";
        public CheckBox Def = null!;
        public TextBox X = null!, Y = null!, X2 = null!, Y2 = null!;
        public Button Pick = null!;
    }

    private readonly Dictionary<string, ZoneRow> _zoneRows = new();
    private System.Windows.Threading.DispatcherTimer? _mouseTimer;
    private System.Windows.Shapes.Ellipse? _mouseDot;

    /// <summary>Per-zone custom rows (Left/Right/wheel): checkbox =
    /// default, unchecked = editable override. Built once.</summary>
    private void BuildZoneRows()
    {
        try
        {
            ZoneRows.Children.Clear();
            _zoneRows.Clear();
            foreach (var (key, label) in new[]
                { ("left-click", "Left"), ("right-click", "Right"), ("wheel", "wheel") })
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                var def = new CheckBox
                {
                    Content = label + " 기본값 사용",
                    Width = 150,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var xb = new TextBox { Width = 56, Margin = new Thickness(4, 0, 4, 0) };
                var yb = new TextBox { Width = 56, Margin = new Thickness(0, 0, 4, 0) };
                var sep = new TextBlock { Text = "~", VerticalAlignment = VerticalAlignment.Center };
                var x2b = new TextBox { Width = 56, Margin = new Thickness(4, 0, 4, 0) };
                var y2b = new TextBox { Width = 56 };
                var pick = new Button { Content = "화면선택", Width = 80, Margin = new Thickness(8, 0, 0, 0) };
                pick.Click += (_, _) => ArmPick(key);
                def.Checked += (_, _) => { if (!_loading) { SaveLayoutTab(); DrawPreview(); } };
                def.Unchecked += (_, _) => { if (!_loading) { SaveLayoutTab(); DrawPreview(); } };
                foreach (var b in new[] { xb, yb, x2b, y2b })
                    b.LostFocus += (_, _) => { if (!_loading) { SaveLayoutTab(); DrawPreview(); } };
                line.Children.Add(def);
                line.Children.Add(xb);
                line.Children.Add(yb);
                line.Children.Add(sep);
                line.Children.Add(x2b);
                line.Children.Add(y2b);
                line.Children.Add(pick);
                ZoneRows.Children.Add(line);
                _zoneRows[key] = new ZoneRow { Key = key, Def = def, X = xb, Y = yb, X2 = x2b, Y2 = y2b, Pick = pick };
            }
        }
        catch { }
    }

    /// <summary>Live mouse dot on the preview (physical cursor, blue),
    /// polled while settings is open.</summary>
    private void MouseTick(object? sender, EventArgs e)
    {
        try
        {
            if (_mouseDot == null)
            {
                _mouseDot = new System.Windows.Shapes.Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x35, 0xC4, 0xFF)),
                    Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                    StrokeThickness = 1,
                    Visibility = Visibility.Collapsed,
                };
            }
            if (_mouseDot.Parent == null) LayoutPreview.Children.Add(_mouseDot);
            if (_pvSc < 1e-9) return;
            var mp = System.Windows.Forms.Cursor.Position;
            double d = WinDpi();
            double mx = mp.X / d, my = mp.Y / d;
            double cx = _pvOx + mx * _pvSc, cy = _pvOy + my * _pvSc;
            if (cx < 0 || cy < 0 || cx > LayoutPreview.ActualWidth || cy > LayoutPreview.ActualHeight)
            {
                _mouseDot.Visibility = Visibility.Collapsed;
                return;
            }
            _mouseDot.Visibility = Visibility.Visible;
            Canvas.SetLeft(_mouseDot, cx - 5);
            Canvas.SetTop(_mouseDot, cy - 5);
        }
        catch { }
    }

    /// <summary>Fixed-value areas on the preview: pad zone + per-zone
    /// overrides, drawn in their zone colors.</summary>
    private void DrawCustomZones(
        (double l, double t, double w, double h) r, string sec, string key)
    {
        try
        {
            System.Windows.Media.Color ZC(string hex)
            {
                try
                {
                    return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                }
                catch
                {
                    return System.Windows.Media.Color.FromArgb(0xFF, 0xAA, 0xAA, 0xAA);
                }
            }
            void Box(double x1, double y1, double x2, double y2, System.Windows.Media.Color c)
            {
                var rr = new System.Windows.Shapes.Rectangle
                {
                    Width = Math.Max(2, (x2 - x1) * _pvSc),
                    Height = Math.Max(2, (y2 - y1) * _pvSc),
                    Fill = System.Windows.Media.Brushes.Transparent,
                    Stroke = new SolidColorBrush(c),
                    StrokeThickness = 1.5,
                };
                LayoutPreview.Children.Add(rr);
                Canvas.SetLeft(rr, _pvOx + x1 * _pvSc);
                Canvas.SetTop(rr, _pvOy + y1 * _pvSc);
            }
            _s.Pads.TryGetValue(key, out var p);
            if (p != null)
            {
                if (p.ZonePadCustom && p.ZonePadW > 0 && p.ZonePadH > 0)
                {
                    var c = ZC(_s.EffZonePad(sec));
                    Box(r.l + p.ZonePadX, r.t + p.ZonePadY,
                        r.l + p.ZonePadX + p.ZonePadW, r.t + p.ZonePadY + p.ZonePadH,
                        System.Windows.Media.Color.FromArgb(0xFF, c.R, c.G, c.B));
                }
                if (p.ZoneRects != null)
                {
                    foreach (var kv in p.ZoneRects)
                    {
                        var a = kv.Value;
                        if (a == null || a.Length < 4 || a[2] <= 0 || a[3] <= 0) continue;
                        System.Windows.Media.Color c = kv.Key switch
                        {
                            "left-click" => ZC(_s.EffZoneLeft(sec)),
                            "right-click" => ZC(_s.EffZoneRight(sec)),
                            "wheel" => ZC(_s.EffZoneWheel(sec)),
                            _ => ZC(_s.EffZonePad(sec)),
                        };
                        Box(r.l + a[0], r.t + a[1], r.l + a[0] + a[2], r.t + a[1] + a[3],
                            System.Windows.Media.Color.FromArgb(0xFF, c.R, c.G, c.B));
                    }
                }
            }
        }
        catch { }
    }

    private void SaveLayoutTab()
    {
        try
        {
            string sec = LayoutKey();
            string key = AppSettings.PadFamilyKey(sec);
            if (!_s.Pads.TryGetValue(key, out var p))
            { p = new PadConfig(); _s.Pads[key] = p; }
            p.AreaMode = LayoutAreaBox.SelectedItem as string ?? "default";
            if (!double.TryParse(LayoutXBox.Text, out double x)
                || !double.TryParse(LayoutYBox.Text, out double y)
                || !double.TryParse(LayoutWBox.Text, out double w)
                || !double.TryParse(LayoutHBox.Text, out double h))
            {
                LoadLayoutEditors();
                return;
            }
            p.RectX = x; p.RectY = y; p.RectW = w; p.RectH = h;
            // Pad zone override (screen DIP boxes -> window DIP).
            var (zh, zr, zm, zk, zs) = PreviewGeom();
            if (PadZoneDef.IsChecked != true)
            {
                if (!double.TryParse(ZoneXBox.Text, out double zx1)
                    || !double.TryParse(ZoneYBox.Text, out double zy1)
                    || !double.TryParse(ZoneX2Box.Text, out double zx2)
                    || !double.TryParse(ZoneY2Box.Text, out double zy2)
                    || zx2 <= zx1 || zy2 <= zy1)
                {
                    UpdateZoneDisplay();
                    return;
                }
                p.ZonePadCustom = true;
                p.ZonePadX = zx1 - zr.l; p.ZonePadY = zy1 - zr.t;
                p.ZonePadW = zx2 - zx1; p.ZonePadH = zy2 - zy1;
            }
            else p.ZonePadCustom = false;
            // Per-zone overrides (screen DIP boxes -> window DIP).
            foreach (var (zkey, zrow) in _zoneRows)
            {
                if (zrow.Def.IsChecked != true)
                {
                    if (!double.TryParse(zrow.X.Text, out double ax1)
                        || !double.TryParse(zrow.Y.Text, out double ay1)
                        || !double.TryParse(zrow.X2.Text, out double ax2)
                        || !double.TryParse(zrow.Y2.Text, out double ay2)
                        || ax2 <= ax1 || ay2 <= ay1)
                    {
                        UpdateZoneDisplay();
                        return;
                    }
                    if (p.ZoneRects == null) p.ZoneRects = new Dictionary<string, double[]>();
                    p.ZoneRects[zkey] = new[] { ax1 - zr.l, ay1 - zr.t, ax2 - ax1, ay2 - ay1 };
                }
                else if (p.ZoneRects != null) p.ZoneRects.Remove(zkey);
            }
            _s.Save();
            FireApply();
        }
        catch { }
    }

    private void ShowLayoutPad()
    {
        try
        {
            SaveLayoutTab();
            _s.Layout = LayoutKey();
            _s.Save();
            if (_showPad != null) _showPad();
            else FireApply();
            DrawPreview();
        }
        catch { }
    }

    /// <summary>Shared preview geometry: home monitor, placed pad rect,
    /// area mode, pads key and ini section. Draw, hover and zone display
    /// all read this so the numbers always agree.</summary>
    private ((double l, double t, double w, double h) home,
        (double l, double t, double w, double h) r,
        string mode, string key, string sec) PreviewGeom()
    {
        var home = LayoutHome();
        string sec = LayoutKey();
        string key = AppSettings.PadFamilyKey(sec);
        _s.Pads.TryGetValue(key, out var p);
        string mode = p?.AreaMode ?? "default";
        if (sec.StartsWith("fullscreen", StringComparison.OrdinalIgnoreCase))
            mode = "full";
        double pw = _s.PadWidth > 0 ? _s.PadWidth : 340;
        double ph = _s.PadHeight > 0 ? _s.PadHeight : 260;
        var r = Core.PadPlacer.Place(mode, home, (pw, ph),
            (p?.RectX ?? 0, p?.RectY ?? 0, p?.RectW ?? 0, p?.RectH ?? 0));
        return (home, r, mode, key, sec);
    }

    // Preview mapping (hover readout): canvas px -> monitor DIP.
    // Forward is X(v) = ox + v*sc with ox already holding -vl*sc, so the
    // inverse is plain (px-ox)/sc - never add the origin again.
    private double _pvOx, _pvOy, _pvSc = 1;
    // Click-pinned area (single): odd clicks set the start dot, even
    // clicks close the rect and fill the custom boxes above. Clicking
    // replaces hover-lock: every layout click leaves a visible dot.
    private (double x, double y)? _pinStart;
    private (double x1, double y1, double x2, double y2)? _pinArea;
    private string _hoverText = "";
    private EventHandler? _onLayoutDisplayChanged;

    private void LayoutPreview_MouseMove(object sender, MouseEventArgs e)
    {
        try
        {
            var pp = e.GetPosition(LayoutPreview);
            if (_pvSc < 1e-9) return;
            double mx = (pp.X - _pvOx) / _pvSc;
            double my = (pp.Y - _pvOy) / _pvSc;
            _hoverText = $"X={mx:0} Y={my:0}";
            LayoutHoverLbl.Text = _hoverText;
        }
        catch { }
    }

    private void LayoutPreview_MouseDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            PinClick(e);
        }
        catch { }
    }

    /// <summary>Pin target: "custom" (window rect), "pad" or a zone key
    /// (left-click/right-click/wheel). Null = disarmed. One area per
    /// arming: start dot, end dot + rect, then the target boxes are
    /// filled and it disarms.</summary>
    private string? _pinTarget;

    private void ArmPick(string target)
    {
        _pinTarget = target;
        _pinStart = null;
        _pinArea = null;
        LayoutHoverLbl.Text = $"영역 찍기({target}): 시작점을 클릭";
        DrawPreview();
    }

    /// <summary>Pin clicks: first sets the start dot, second closes the
    /// rect into the armed target's boxes. Disarms after one area.</summary>
    private void PinClick(MouseEventArgs e)
    {
        try
        {
            if (_pinTarget == null) return;
            if (_pvSc < 1e-9) return;
            var pp = e.GetPosition(LayoutPreview);
            double mx = (pp.X - _pvOx) / _pvSc;
            double my = (pp.Y - _pvOy) / _pvSc;
            if (_pinStart == null)
            {
                _pinStart = (mx, my);
                LayoutHoverLbl.Text = $"시작점: X={mx:0} Y={my:0} — 끝점을 클릭";
            }
            else
            {
                var (sx, sy) = _pinStart.Value;
                _pinStart = null;
                _pinArea = (Math.Min(sx, mx), Math.Min(sy, my),
                    Math.Max(sx, mx), Math.Max(sy, my));
                ApplyPinToTarget();
                _pinTarget = null;
            }
            DrawPreview();
        }
        catch { }
    }

    private void ApplyPinToTarget()
    {
        try
        {
            if (_pinArea == null || _pinTarget == null) return;
            var a = _pinArea.Value;
            string t = _pinTarget;
            if (t == "custom")
            {
                LayoutXBox.Text = TrimNum(a.x1);
                LayoutYBox.Text = TrimNum(a.y1);
                LayoutWBox.Text = TrimNum(a.x2 - a.x1);
                LayoutHBox.Text = TrimNum(a.y2 - a.y1);
                LayoutAreaBox.SelectedItem = "custom";
                LayoutHoverLbl.Text =
                    $"고정됨: [{TrimNum(a.x1)},{TrimNum(a.y1)}] ~ [{TrimNum(a.x2)},{TrimNum(a.y2)}] → custom 입력됨";
            }
            else if (t == "pad")
            {
                ZoneXBox.Text = TrimNum(a.x1);
                ZoneYBox.Text = TrimNum(a.y1);
                ZoneX2Box.Text = TrimNum(a.x2);
                ZoneY2Box.Text = TrimNum(a.y2);
                PadZoneDef.IsChecked = false;
                LayoutHoverLbl.Text =
                    $"고정됨: [{TrimNum(a.x1)},{TrimNum(a.y1)}] ~ [{TrimNum(a.x2)},{TrimNum(a.y2)}] → pad 입력됨";
            }
            else if (_zoneRows.TryGetValue(t, out var zrow))
            {
                zrow.X.Text = TrimNum(a.x1);
                zrow.Y.Text = TrimNum(a.y1);
                zrow.X2.Text = TrimNum(a.x2);
                zrow.Y2.Text = TrimNum(a.y2);
                zrow.Def.IsChecked = false;
                LayoutHoverLbl.Text =
                    $"고정됨: [{TrimNum(a.x1)},{TrimNum(a.y1)}] ~ [{TrimNum(a.x2)},{TrimNum(a.y2)}] → {t} 입력됨";
            }
        }
        catch { }
    }

    /// <summary>Pinned start dot + area rect on top of the preview.</summary>
    private void DrawPins()
    {
        try
        {
            if (_pvSc < 1e-9) return;
            if (_pinArea != null)
            {
                var a = _pinArea.Value;
                var col = System.Windows.Media.Color.FromArgb(0xFF, 0x7F, 0xE0, 0xA8);
                var rr = new System.Windows.Shapes.Rectangle
                {
                    Width = Math.Max(2, (a.x2 - a.x1) * _pvSc),
                    Height = Math.Max(2, (a.y2 - a.y1) * _pvSc),
                    Fill = System.Windows.Media.Brushes.Transparent,
                    Stroke = new SolidColorBrush(col),
                    StrokeThickness = 1.25,
                };
                LayoutPreview.Children.Add(rr);
                Canvas.SetLeft(rr, _pvOx + a.x1 * _pvSc);
                Canvas.SetTop(rr, _pvOy + a.y1 * _pvSc);
                foreach (var (px, py, dot) in new[] {
                    (a.x1, a.y1, System.Windows.Media.Color.FromArgb(0xFF, 0x7F, 0xE0, 0xA8)),
                    (a.x2, a.y2, System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0x7F, 0x7F)) })
                {
                    var d = new System.Windows.Shapes.Ellipse
                    {
                        Width = 7,
                        Height = 7,
                        Fill = new SolidColorBrush(dot),
                    };
                    LayoutPreview.Children.Add(d);
                    Canvas.SetLeft(d, _pvOx + px * _pvSc - 3.5);
                    Canvas.SetTop(d, _pvOy + py * _pvSc - 3.5);
                }
            }
            if (_pinStart != null)
            {
                var (sx, sy) = _pinStart.Value;
                var d = new System.Windows.Shapes.Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x7F, 0xE0, 0xA8)),
                };
                LayoutPreview.Children.Add(d);
                Canvas.SetLeft(d, _pvOx + sx * _pvSc - 3.5);
                Canvas.SetTop(d, _pvOy + sy * _pvSc - 3.5);
            }
        }
        catch { }
    }

    /// <summary>Pad custom zone editors (the zone text readout was
    /// removed per request; hover + preview carry the numbers).</summary>
    private void UpdateZoneDisplay()
    {
        try
        {
            var (home, r, mode, key, sec) = PreviewGeom();
            // Pad custom editors: stored window-DIP -> screen DIP display.
            _s.Pads.TryGetValue(key, out var p);
            bool custom = p != null && p.ZonePadCustom && p.ZonePadW > 0 && p.ZonePadH > 0;
            PadZoneDef.IsChecked = !custom;
            if (custom && p != null)
            {
                ZoneXBox.Text = TrimNum(r.l + p.ZonePadX);
                ZoneYBox.Text = TrimNum(r.t + p.ZonePadY);
                ZoneX2Box.Text = TrimNum(r.l + p.ZonePadX + p.ZonePadW);
                ZoneY2Box.Text = TrimNum(r.t + p.ZonePadY + p.ZonePadH);
            }
            else
            {
                var (zx1, zy1, zx2, zy2) = PadZoneScreen();
                ZoneXBox.Text = TrimNum(zx1);
                ZoneYBox.Text = TrimNum(zy1);
                ZoneX2Box.Text = TrimNum(zx2);
                ZoneY2Box.Text = TrimNum(zy2);
            }
            bool en = PadZoneDef.IsChecked != true;
            ZoneXBox.IsEnabled = en;
            ZoneYBox.IsEnabled = en;
            ZoneX2Box.IsEnabled = en;
            ZoneY2Box.IsEnabled = en;
            // Per-zone rows: stored override -> screen DIP, else the ini
            // tile rect (empty when the section has none).
            foreach (var (zkey, zrow) in _zoneRows)
            {
                double sx1 = 0, sy1 = 0, sx2 = 0, sy2 = 0;
                bool show = false;
                if (p != null && p.ZoneRects != null
                    && p.ZoneRects.TryGetValue(zkey, out var a)
                    && a != null && a.Length >= 4 && a[2] > 0 && a[3] > 0)
                {
                    sx1 = r.l + a[0]; sy1 = r.t + a[1];
                    sx2 = sx1 + a[2]; sy2 = sy1 + a[3];
                    show = true;
                    zrow.Def.IsChecked = false;
                }
                else
                {
                    zrow.Def.IsChecked = true;
                    var (tx1, ty1, tx2, ty2) = ZoneTileScreen(
                        zkey == "left-click" ? "lbtn" : zkey == "right-click" ? "rbtn" : "wheel",
                        r, sec, out bool found);
                    if (found) { sx1 = tx1; sy1 = ty1; sx2 = tx2; sy2 = ty2; show = true; }
                }
                zrow.X.Text = show ? TrimNum(sx1) : "";
                zrow.Y.Text = show ? TrimNum(sy1) : "";
                zrow.X2.Text = show ? TrimNum(sx2) : "";
                zrow.Y2.Text = show ? TrimNum(sy2) : "";
                bool zen = zrow.Def.IsChecked != true;
                zrow.X.IsEnabled = zen;
                zrow.Y.IsEnabled = zen;
                zrow.X2.IsEnabled = zen;
                zrow.Y2.IsEnabled = zen;
            }
        }
        catch { }
    }

    /// <summary>An ini tile rect in screen DIP (ChromeH mapping like the
    /// pad): role is lbtn/rbtn/wheel/pad. found=false when the section
    /// has no such tile.</summary>
    private (double x1, double y1, double x2, double y2) ZoneTileScreen(
        string role,
        (double l, double t, double w, double h) r, string sec, out bool found)
    {
        found = false;
        if (_presets.TryGetValue(sec, out var lay) && lay != null)
        {
            foreach (var t in lay.Tiles)
            {
                string k = (t.Kind ?? "").ToLowerInvariant();
                string btn = (t.ClickButton ?? "").ToLowerInvariant();
                bool hit = role == "pad" ? k == "pad"
                    : role == "wheel" ? k.Contains("wheel")
                    : role == "lbtn" ? (k.Contains("lbtn") || (k.Contains("click") && btn != "right"))
                    : (k.Contains("rbtn") || (k.Contains("click") && btn == "right"));
                if (!hit) continue;
                double tx = r.l + r.w * t.X / 100.0;
                double ty = r.t + 30 + (r.h - 30) * t.Y / 100.0;
                found = true;
                return (tx, ty, tx + r.w * t.W / 100.0, ty + (r.h - 30) * t.H / 100.0);
            }
        }
        return (0, 0, 0, 0);
    }

    /// <summary>Default pad zone in screen DIP: the ini pad tile, or the
    /// full tile area when the section has none.</summary>
    private (double x1, double y1, double x2, double y2) PadZoneScreen()
    {
        var (home, r, mode, key, sec) = PreviewGeom();
        if (_presets.TryGetValue(sec, out var lay) && lay != null)
        {
            foreach (var t in lay.Tiles)
            {
                if ((t.Kind ?? "").ToLowerInvariant() != "pad") continue;
                double tx = r.l + r.w * t.X / 100.0;
                double ty = r.t + 30 + (r.h - 30) * t.Y / 100.0;
                return (tx, ty, tx + r.w * t.W / 100.0, ty + (r.h - 30) * t.H / 100.0);
            }
        }
        return (r.l, r.t + 30, r.l + r.w, r.t + r.h);
    }

    private void DrawPreview()
    {
        try
        {
            LayoutPreview.Children.Clear();
            double cw = LayoutPreview.ActualWidth > 50 ? LayoutPreview.ActualWidth : 460;
            double ch = LayoutPreview.Height;
            if (ch < 50) ch = 250;
            double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
            double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
            if (vw < 100 || vh < 100 || cw < 50 || ch < 50) return;
            double sc = Math.Min(cw / vw, ch / vh);
            double ox = (cw - vw * sc) / 2 - vl * sc;
            double oy = (ch - vh * sc) / 2 - vt * sc;
            _pvOx = ox; _pvOy = oy; _pvSc = sc;
            double X(double v) => ox + v * sc;
            double Y(double v) => oy + v * sc;
            // Monitors (bounds are physical px: divide into DIPs first so
            // every layer shares one unit system).
            double dd = WinDpi();
            string homeDev = "";
            try
            {
                var hr = LayoutHome();
                foreach (var s in System.Windows.Forms.Screen.AllScreens)
                {
                    var b = s.Bounds;
                    double bl = b.Left / dd, bt = b.Top / dd;
                    double bw = b.Width / dd, bh = b.Height / dd;
                    bool isHome = bl <= hr.l + 1 && hr.l <= bl + bw
                        && bt <= hr.t + 1 && hr.t <= bt + bh;
                    if (isHome) homeDev = s.DeviceName;
                    var mr = new System.Windows.Shapes.Rectangle
                    {
                        Width = Math.Max(2, bw * sc),
                        Height = Math.Max(2, bh * sc),
                        Fill = new SolidColorBrush(isHome
                            ? System.Windows.Media.Color.FromArgb(0x22, 0xAA, 0xAA, 0xAA)
                            : System.Windows.Media.Color.FromArgb(0xFF, 0x1A, 0x22, 0x2C)),
                        Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x33, 0x5C, 0x6C)),
                        StrokeThickness = 1,
                    };
                    LayoutPreview.Children.Add(mr);
                    System.Windows.Controls.Canvas.SetLeft(mr, X(bl));
                    System.Windows.Controls.Canvas.SetTop(mr, Y(bt));
                    var ml = new TextBlock
                    {
                        Text = s.DeviceName.Replace(@"\\.\", "") + (s.Primary ? " (주)" : ""),
                        FontSize = 9,
                        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x9A, 0xA6, 0xBD)),
                    };
                    LayoutPreview.Children.Add(ml);
                    System.Windows.Controls.Canvas.SetLeft(ml, X(bl) + 3);
                    System.Windows.Controls.Canvas.SetTop(ml, Y(bt) + 2);
                }
            }
            catch { }
            // Pad rect (same math as the app: PadPlacer.Place).
            var (home, r, mode, key, sec) = PreviewGeom();
            var pr = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(2, r.w * sc),
                Height = Math.Max(2, r.h * sc),
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x18, 0x7F, 0xE0, 0xA8)),
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x7F, 0xE0, 0xA8)),
                StrokeThickness = 1.5,
            };
            LayoutPreview.Children.Add(pr);
            System.Windows.Controls.Canvas.SetLeft(pr, X(r.l));
            System.Windows.Controls.Canvas.SetTop(pr, Y(r.t));
            // Tiles in the pad's REAL zone colors (no more stacked cyan):
            // structural tiles stay quiet, interactive tiles pop.
            System.Windows.Media.Color ZC(string hex)
            {
                try
                {
                    return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                }
                catch
                {
                    return System.Windows.Media.Color.FromArgb(0x44, 0xAA, 0xAA, 0xAA);
                }
            }
            int shown = 0;
            if (_presets.TryGetValue(sec, out var lay) && lay != null)
            {
                foreach (var t in lay.Tiles)
                {
                    // Same mapping as the pad (TileArea + 30px title offset).
                    double tx = r.l + r.w * t.X / 100.0;
                    double ty = r.t + 30 + (r.h - 30) * t.Y / 100.0;
                    double tw = r.w * t.W / 100.0, th = (r.h - 30) * t.H / 100.0;
                    if (tw < 1 || th < 1) continue;
                    string kind = (t.Kind ?? "").ToLowerInvariant();
                    string zhex;
                    bool structural = false;
                    if (kind.Contains("frame") || kind.Contains("blank"))
                    { zhex = ""; structural = true; }
                    else if (kind.Contains("wheel")) zhex = _s.EffZoneWheel(sec);
                    else if (kind.Contains("lbtn")) zhex = _s.EffZoneLeft(sec);
                    else if (kind.Contains("rbtn")) zhex = _s.EffZoneRight(sec);
                    else if (kind.Contains("click") || kind.Contains("drag"))
                        zhex = (t.ClickButton ?? "").ToLowerInvariant() == "right"
                            ? _s.EffZoneRight(sec) : _s.EffZoneLeft(sec);
                    else if (kind.Contains("pad")) zhex = _s.EffZonePad(sec);
                    else zhex = _s.EffBackground(sec);
                    var zc = ZC(zhex);
                    var tr = new System.Windows.Shapes.Rectangle
                    {
                        Width = Math.Max(1, tw * sc),
                        Height = Math.Max(1, th * sc),
                        Fill = structural
                            ? System.Windows.Media.Brushes.Transparent
                            : new SolidColorBrush(zc),
                        Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(
                            structural ? (byte)0x66 : (byte)0xFF, zc.R, zc.G, zc.B)),
                        StrokeThickness = 0.75,
                    };
                    LayoutPreview.Children.Add(tr);
                    System.Windows.Controls.Canvas.SetLeft(tr, X(tx));
                    System.Windows.Controls.Canvas.SetTop(tr, Y(ty));
                    if (tw * sc > 44 && th * sc > 12 && !string.IsNullOrWhiteSpace(t.Kind))
                    {
                        var tl = new TextBlock
                        {
                            Text = t.Kind,
                            FontSize = 9,
                            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xE8, 0xEC, 0xF4)),
                        };
                        LayoutPreview.Children.Add(tl);
                        System.Windows.Controls.Canvas.SetLeft(tl, X(tx) + 2);
                        System.Windows.Controls.Canvas.SetTop(tl, Y(ty) + 1);
                    }
                    shown++;
                }
            }
            double cov = home.w * home.h > 0 ? r.w * r.h / (home.w * home.h) * 100.0 : 0;
            string homeName = (homeDev ?? "").Replace(@"\\.\", "");
            if (string.IsNullOrEmpty(homeName)) homeName = "주 모니터";
            LayoutInfoLbl.Text = $"모니터: {homeName} {home.w:0}x{home.h:0} | " +
                $"패드: {r.l:0},{r.t:0} {r.w:0}x{r.h:0} (모니터의 {cov:0}%) | " +
                $"모드 {mode} | 타일 {shown}개";
            DrawCustomZones(r, sec, key);
            DrawPins();
            UpdateZoneDisplay();
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
        foreach (var a in new[] { "default", "full", "half-left", "half-right", "custom" }) PadArea.Items.Add(a);
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

    // ---------------- recorded gestures tab --------------------------

    private void InitRecTab()
    {
        RecBtn.Click += (_, _) =>
        {
            RecStateLbl.Text = "녹화창에 두손가락으로 그리고 떼세요";
            try { _showRec?.Invoke(OnRecorded); } catch { }
        };
        RebuildFloatGestures();
    }

    /// <summary>Renders a recorded template's motion into a mini
    /// canvas (normalized path scaled to fit, green start, red end
    /// plus a direction arrow; degenerate templates draw a dot so an
    /// empty-looking row never happens silently).</summary>
    private static void RenderMotion(Canvas cv, RecordedGesture g)
    {
        try
        {
            cv.Children.Clear();
            var pts = Core.GestureMatch.Expand(g.Points ?? new List<double>());
            double w = cv.Width, h = cv.Height;
            if (w < 10 || h < 10) return;
            double sc = Math.Min(w, h) * 0.88;
            double cx = w / 2, cy = h / 2;
            if (pts.Count < 2)
            {
                var dot0 = new Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Fill = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x7F, 0x7F)),
                };
                cv.Children.Add(dot0);
                Canvas.SetLeft(dot0, cx - 5);
                Canvas.SetTop(dot0, cy - 5);
                return;
            }
            var line = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x7F, 0xE0, 0xA8)),
                StrokeThickness = 2.5,
                StrokeLineJoin = PenLineJoin.Round,
            };
            foreach (var p in pts)
                line.Points.Add(new System.Windows.Point(cx + p.x * sc, cy + p.y * sc));
            cv.Children.Add(line);
            var p0 = pts[0];
            var p1 = pts[pts.Count - 1];
            foreach (var (pp, col, sz) in new[] { (p0, 0xFF7FE0A8u, 8.0), (p1, 0xFFFF7F7Fu, 8.0) })
            {
                var dot = new Ellipse
                {
                    Width = sz,
                    Height = sz,
                    Fill = new SolidColorBrush(Color.FromArgb(
                        (byte)(col >> 24), (byte)(col >> 16), (byte)(col >> 8), (byte)col)),
                };
                cv.Children.Add(dot);
                Canvas.SetLeft(dot, cx + pp.x * sc - sz / 2);
                Canvas.SetTop(dot, cy + pp.y * sc - sz / 2);
            }
            // Direction arrow on the last segment.
            if (pts.Count >= 2)
            {
                var pa = pts[pts.Count - 2];
                double dx = p1.x - pa.x, dy = p1.y - pa.y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len > 1e-9)
                {
                    double ux = dx / len, uy = dy / len;
                    double ex = cx + p1.x * sc, ey = cy + p1.y * sc;
                    var arr = new Polygon
                    {
                        Fill = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x7F, 0x7F)),
                        Points = new PointCollection
                        {
                            new System.Windows.Point(ex + ux * 9, ey + uy * 9),
                            new System.Windows.Point(ex - uy * 5 - ux * 3, ey + ux * 5 - uy * 3),
                            new System.Windows.Point(ex + uy * 5 - ux * 3, ey - ux * 5 - uy * 3),
                        },
                    };
                    cv.Children.Add(arr);
                }
            }
        }
        catch { }
    }

    /// <summary>Unified gesture list: every row is the same 4-column
    /// grid (label/name | view | function | delete) so function dropdowns
    /// and buttons line up in one column. Built-ins save via 적용·저장,
    /// recorded rows save live.</summary>
    private static Grid GestureRow()
    {
        var head = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        return head;
    }

    private void RebuildFloatGestures()
    {
        try
        {
            FloatGestureList.Children.Clear();
            foreach (var (key, label, get, _) in Slots)
            {
                var head = GestureRow();
                head.Children.Add(new TextBlock
                {
                    Text = label, VerticalAlignment = VerticalAlignment.Center,
                });
                var cb = new WComboBox
                {
                    ItemsSource = GestureMap.Actions,
                    SelectedItem = get(_s.Gestures),
                    Margin = new Thickness(4, 0, 0, 0),
                };
                Grid.SetColumn(cb, 2);
                _gestures["float:" + key] = cb;
                head.Children.Add(cb);
                FloatGestureList.Children.Add(head);
            }
            // Default two-finger strokes (up = maximize, down = minimize):
            // same grid, delete clears the mapping to none.
            foreach (var (key, label, get, set) in new[] {
                ("swipe_up", "두손가락 위로",
                    (Func<GestureMap, string>)(m => m.SwipeUp),
                    (Action<GestureMap, string>)((m, v) => m.SwipeUp = v)),
                ("swipe_down", "두손가락 아래로",
                    (Func<GestureMap, string>)(m => m.SwipeDown),
                    (Action<GestureMap, string>)((m, v) => m.SwipeDown = v)),
            })
            {
                var head = GestureRow();
                head.Children.Add(new TextBlock
                {
                    Text = label, VerticalAlignment = VerticalAlignment.Center,
                });
                var cb = new WComboBox
                {
                    ItemsSource = GestureMap.Actions,
                    SelectedItem = get(_s.Gestures),
                    Margin = new Thickness(4, 0, 0, 0),
                };
                Grid.SetColumn(cb, 2);
                _gestures["float:" + key] = cb;
                var clr = new Button { Content = "삭제", Margin = new Thickness(4, 0, 0, 0), ToolTip = "매핑 해제 (none)" };
                Grid.SetColumn(clr, 3);
                clr.Click += (_, _) =>
                {
                    cb.SelectedItem = "none";
                    set(_s.Gestures, "none");
                    _s.Save();
                    FireApply();
                };
                head.Children.Add(cb);
                head.Children.Add(clr);
                FloatGestureList.Children.Add(head);
            }
            foreach (var g in _s.RecordedGestures.ToList())
            {
                var box = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 8) };
                // Same 4-column grid: name | view(fingers) | function | delete.
                var head = GestureRow();
                var name = new TextBox
                {
                    Text = g.Name,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                name.LostFocus += (_, _) =>
                {
                    g.Name = name.Text;
                    _s.Save();
                };
                var peek = new Button
                {
                    Content = $"보기({g.Fingers}핑거)",
                    Margin = new Thickness(4, 0, 0, 0),
                };
                Grid.SetColumn(peek, 1);
                var act = new WComboBox
                {
                    ItemsSource = GestureMap.Actions,
                    SelectedItem = g.Action,
                    Margin = new Thickness(4, 0, 0, 0),
                };
                Grid.SetColumn(act, 2);
                act.SelectionChanged += (_, _) =>
                {
                    g.Action = act.SelectedItem as string ?? "none";
                    _s.Save();
                    FireApply();
                };
                var del = new Button { Content = "삭제", Margin = new Thickness(4, 0, 0, 0) };
                Grid.SetColumn(del, 3);
                del.Click += (_, _) =>
                {
                    _s.RecordedGestures.Remove(g);
                    _s.Save();
                    RebuildFloatGestures();
                    FireApply();
                };
                head.Children.Add(name);
                head.Children.Add(peek);
                head.Children.Add(act);
                head.Children.Add(del);
                box.Children.Add(head);
                // Motion preview, toggled by the view button.
                var prev = new Canvas
                {
                    Width = 300,
                    Height = 64,
                    Margin = new Thickness(0, 0, 0, 0),
                    Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0A, 0x0F, 0x14)),
                    Visibility = Visibility.Collapsed,
                };
                RenderMotion(prev, g);
                peek.Click += (_, _) =>
                {
                    if (prev.Visibility == Visibility.Visible)
                        prev.Visibility = Visibility.Collapsed;
                    else
                        prev.Visibility = Visibility.Visible;
                };
                box.Children.Add(prev);
                FloatGestureList.Children.Add(box);
            }
        }
        catch { }
    }

    private void OnRecorded(RecordedGesture? g)
    {
        try
        {
            if (g == null || g.Points.Count == 0)
            {
                RecStateLbl.Text = "인식 실패: 두 손가락 이상으로 길게 그리세요";
                return;
            }
            int n = 1;
            foreach (var r in _s.RecordedGestures)
                if (r.Name.StartsWith("제스처 ")) n++;
            g.Name = $"제스처 {n}";
            _s.RecordedGestures.Add(g);
            _s.Save();
            RebuildFloatGestures();
            FireApply();
            RecStateLbl.Text = $"저장됨: {g.Name} ({g.Points.Count / 2}pts)";
        }
        catch { }
    }

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
