using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using WApplication = System.Windows.Application;

namespace TouchPadCloneV2;

/// <summary>
/// Wires everything: settings + presets, pad, mode strip, assist pad,
/// settings window and tray. No XAML startup window (see App.xaml).
/// </summary>
public partial class App : WApplication
{
    private Core.AppSettings _settings = new();
    private Dictionary<string, Core.Layout> _presets = new();
    private TouchPadWindow? _pad;
    private ModeStripWindow? _strip;
    private AuxPadWindow? _artistPad, _virtualPad;
    private string _placedMode = "";

    private Core.PadConfig Cfg(string name)
    {
        foreach (var kv in _settings.Pads)
            if (kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return new Core.PadConfig();
    }
    private AssistWindow? _assist;
    private SettingsWindow? _settingsWin;
    private TrayManager? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (Environment.GetEnvironmentVariable("TOUCHPAD_SELFTEST") == "1")
        {
            // Headless-ish selftest: real window + real handlers, no tray.
            // Must stay on the UI (STA) thread: async void keeps context.
            RunSelfTestAsync();
            return;
        }

        _settings = Core.AppSettings.Load();
        // Auto-start intent survives exe moves/updates: re-register the
        // running path when the flag is on but the key is missing.
        try
        {
            if (_settings.AutoStart && !SettingsWindow.IsAutoStartOn())
                SettingsWindow.SetAutoStart(true);
        }
        catch { }
        // Crash recovery: a previous run may have left the cursor hidden.
        // Relaunching the app always repairs it (touch works cursor-free).
        Core.InputSim.RestoreCursor();
        Core.DebugLog.Clear();
        Core.DebugLog.Write("settings: speed=" + _settings.Speed +
            " layout=" + _settings.Layout +
            " preset=" + _settings.PresetFile);
        _presets = LoadPresets();
        if (!_presets.ContainsKey(_settings.Layout))
            _settings.Layout = _presets.ContainsKey("floatpad")
                ? "floatpad" : _presets.Keys.First();

        _pad = new TouchPadWindow(_settings);
        _pad.RequestSettings += OpenSettings;
        _pad.RequestHide += () => HidePad();
        _pad.RequestAssist += ToggleAssist;
        _pad.RequestFullscreenToggle += ToggleFullscreen;
        // The pad never activates (NOACTIVATE), so outside-tap-collapse of
        // the picker cannot rely on Deactivated alone: any pad press closes.
        _pad.PressedAnywhere += () =>
        {
            try { _picker?.Close(); }
            catch (InvalidOperationException) { _picker = null; }
        };
        _pad.SetLayout(_presets[_settings.Layout]);
        // Launch = strip only, pad off (user model). Pad appears on strip tap.
        _pad.Hide();

        _strip = new ModeStripWindow(_settings);
        _strip.AuxToggled += name => ToggleAux(name);
        _strip.Gesture += HandleStripGesture;
        _strip.Tap += HandleStripTap;
        _strip.ClosePad += () => HidePad();
        _strip.ModePressed += () => FireSelected();
        _strip.Pressed += () => _stripTouchAt = DateTime.Now;
        _strip.LayoutSelected += name => SelectLayout(name);
        _strip.KnownLayouts = Core.PresetParser.OrderedNames(_presets);
        _strip.SetLabel(_settings.Layout);
        AdoptMenuCell(_settings.Layout);
        _strip.SetPadActive(false);
        _strip.Show();
        // Z-order: strip + strip menus always topmost, touch pad after.
        // No periodic pad raise (it fought the strip); the strip's own
        // keeper defends against outside apps, and the overlay hands
        // back to strip/picker after every Show.
        EffectOverlay.AfterShow = () =>
        {
            if (_picker != null) Core.TopmostKeeper.Raise(_picker);
            if (_assist != null) Core.TopmostKeeper.Raise(_assist);
            if (_strip != null) Core.TopmostKeeper.Raise(_strip);
            _strip?.RaiseChrome();
        };

        _tray = new TrayManager(
            () => _settings.StripVisible,
            () => ToggleStrip(),
            OpenSettings,
            () => { _pad?.EmergencyRestore(); _tray?.Dispose(); Shutdown(); });
        DispatcherUnhandledException += (_, e) =>
        {
            try
            {
                Core.DebugLog.Write("UNHANDLED: " + e.Exception.GetType().Name +
                    ": " + e.Exception.Message.Split('\n')[0]);
                _pad?.EmergencyRestore();
            }
            catch { }
            e.Handled = true; // utility stays alive; fault is logged
        };
        Exit += (_, _) => _pad?.EmergencyRestore();
        // Monitors changed (plug/unplug): re-resolve the strip monitor
        // (vanished -> next) and re-clamp everything.
        _onDisplayChanged = (_, _) =>
            Dispatcher.InvokeAsync(() => { try { Apply(); } catch { } });
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += _onDisplayChanged;
        Exit += (_, _) =>
        {
            try
            {
                if (_onDisplayChanged != null)
                    Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= _onDisplayChanged;
            }
            catch { }
        };
    }

    private EventHandler? _onDisplayChanged;

    private async void RunSelfTestAsync()
    {
        await Task.Delay(500);
        int rc;
        try { rc = await SelfTest.Run(); }
        catch (Exception ex)
        {
            Console.WriteLine("SELFTEST CRASH: " + ex);
            rc = 2;
        }
        Environment.Exit(rc);
    }

    private Dictionary<string, Core.Layout> LoadPresets()
    {
        string bundled = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "presets", "Default.ini");
        string path = string.IsNullOrEmpty(_settings.PresetFile) ||
                      !File.Exists(_settings.PresetFile)
            ? bundled : _settings.PresetFile;
        try { return Core.PresetParser.ParseFile(path); }
        catch { return Core.PresetParser.ParseFile(bundled); }
    }

    private void Apply()
    {
        if (_pad == null || _strip == null) return;
        _presets = LoadPresets();
        if (!_presets.ContainsKey(_settings.Layout))
            _settings.Layout = _presets.Keys.First();
        _pad.RefreshOpacity();
        _pad.SetLayout(_presets[_settings.Layout]);
        _pad.RefreshChrome();
        _pad.RefreshDebugLabels();
        _pad.ApplyCursorStyle();
        // Cursor/overlay only while the pad is actually shown (launch = off).
        if (_pad.IsVisible)
        {
            if (_settings.FakeCursor) _pad.EnterPersistentFake();
            else _pad.EmergencyRestore();
        }
        // Per-pad area mode + visibility + opacity (fullscreen forced
        // full + visible). Geometry only on layout change so sliders
        // never yank the window; ShowPad places fresh opens.
        ApplyPad(false);
        _strip.ApplyStripLayout();
        _strip.SetLabel(_settings.Layout);
        _strip.KnownLayouts = Core.PresetParser.OrderedNames(_presets);
        UpdateStripText();
        _artistPad?.Refresh();
        _virtualPad?.Refresh();
        Core.TopmostKeeper.Raise(_strip);
    }

    /// <summary>Pad's home monitor in DIPs: the CONFIGURED strip monitor,
    /// so the pad opens where the strip is (dual monitors). Vanished
    /// monitor falls back to primary/first, then the virtual screen.</summary>
    private (double l, double t, double w, double h) HomeRect()
    {
        try
        {
            double d = 1.0;
            try
            {
                var s = System.Windows.Media.VisualTreeHelper.GetDpi(_pad);
                if (s.DpiScaleX >= 0.5 && s.DpiScaleX <= 4) d = s.DpiScaleX;
            }
            catch { }
            var sc = Core.MonitorList.Resolve(_settings.StripMonitor);
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

    private void ApplyPad(bool fresh)
    {
        if (_pad == null) return;
        var cfg = Cfg(_settings.Layout);
        string mode = cfg.AreaMode;
        if (_settings.Layout.StartsWith("fullscreen",
            StringComparison.OrdinalIgnoreCase)) mode = "full";
        if (mode != "default" && (fresh || mode != _placedMode || !_pad.IsVisible))
        {
            var home = HomeRect();
            var (l, t, w, h) = Core.PadPlacer.RectFor(mode,
                home.l, home.t, home.w, home.h);
            _pad.Left = l; _pad.Top = t; _pad.Width = w; _pad.Height = h;
            _placedMode = mode;
        }
        else if (mode == "default" && fresh)
        {
            var home = HomeRect();
            _pad.SyncWindowSize();
            _pad.Left = home.l + home.w - _pad.Width - 40;
            _pad.Top = home.t + home.h - _pad.Height - 120;
            _placedMode = mode;
        }
        bool show = cfg.Visible;
        if (mode == "full") show = true;   // fullscreen always on
        _pad.Visibility = show ? Visibility.Visible : Visibility.Hidden;
        _pad.RefreshOpacity();
    }

    private readonly System.Collections.Generic.HashSet<string> _auxOn = new();

    private void ToggleAux(string name)
    {
        if (!_auxOn.Remove(name)) _auxOn.Add(name);
        ShowAux(name, _auxOn.Contains(name));
    }

    private void ShowAux(string name, bool on)
    {
        try
        {
            AuxPadWindow? w = name == "artist" ? _artistPad
                : name == "virtual" ? _virtualPad : null;
            if (w == null && on)
            {
                w = new AuxPadWindow(_settings, name);
                if (name == "artist") _artistPad = w; else _virtualPad = w;
                string n = name;
                w.RequestClose += () =>
                {
                    _auxOn.Remove(n);
                    ShowAux(n, false);
                };
                w.Show();
            }
            if (w != null)
            {
                w.Refresh();
                w.Visibility = on ? Visibility.Visible : Visibility.Hidden;
            }
            _strip?.SetToggle(name, on);
        }
        catch { }
    }

    private void OpenSettings()
    {
        if (_settingsWin != null)
        {
            _settingsWin.Activate();
            return;
        }
        _settingsWin = new SettingsWindow(_settings,
            Core.PresetParser.OrderedNames(_presets), Apply,
            (name, on) => ShowAux(name, on));
        _settingsWin.Closed += (_, _) => _settingsWin = null;
        _settingsWin.Show();
    }

    private void ToggleAssist()
    {
        if (_assist != null) { _assist.Close(); _assist = null; return; }
        _assist = new AssistWindow();
        _assist.Closed += (_, _) => _assist = null;
        _assist.Show();
        Core.TopmostKeeper.Raise(_assist);
        if (_strip != null) Core.TopmostKeeper.Raise(_strip);
    }

    /// <summary>Strip tap: pad off -> activate last mode; pad on -> mapped tap.</summary>
    /// <summary>Strip tap: fires the displayed (selected) cell.
    /// Mode changes go through the Mode (☰) button popup; swipes only
    /// move the selection. Pad open/close: the in-strip X button.</summary>
    private void HandleStripTap()
    {
        if (_pad == null || _strip == null) return;
        // Toggle: open -> tap again closes. Tap never fires;
        // execution is the mode triangle / picker items.
        if (_picker != null)
        {
            try { _picker.Close(); } catch (InvalidOperationException) { }
            _picker = null;
            return;
        }
        ShowModePicker();
    }

    /// <summary>Fires the displayed (selected) cell: layout, aux,
    /// custom, gesture, cmd... (mode triangle).</summary>
    private void FireSelected()
    {
        if (_pad == null || _strip == null) return;
        try
        {
            if (!Core.ActionRunner.StripMenu.GetCell(
                _settings, _menuRow, _menuCol, out var cell)
                || cell.IsEmpty)
                return;
            Core.ActionRunner.StripMenu.Fire(cell, _settings,
                name => SelectLayout(name),
                name => ToggleAux(name),
                action => RunPickerAction(action));
        }
        catch { }
    }

    /// <summary>Strip show/hide (tray + picker close button).</summary>
    private void ToggleStrip()
    {
        try
        {
            _settings.StripVisible = !_settings.StripVisible;
            _settings.Save();
            Apply();
        }
        catch { }
    }

    public void ShowPad()
    {
        if (_pad == null || _strip == null) return;
        ApplyPad(true);
        _pad.SetLayout(_presets[_settings.Layout]);
        _pad.Show();
        _strip.SetPadActive(true);
        Core.TopmostKeeper.Raise(_strip);
    }

    public void HidePad()
    {
        Core.Log.Write("HidePad");
        _pad?.Hide();
        _pad?.EmergencyRestore();
        _strip?.SetPadActive(false);
        try
        {
            if (_picker != null) { _picker.Close(); _picker = null; }
        }
        catch (InvalidOperationException) { _picker = null; }
    }
    private void HandleStripGesture(string action)
    {
        switch (action)
        {
            case "prev_layout": CycleLayout(-1); break;
            case "next_layout": CycleLayout(1); break;
            case "menu_prev": StepMenu(0, -1); break;
            case "menu_next": StepMenu(0, 1); break;
            case "menu_up": StepMenu(-1, 0); break;
            case "menu_down": StepMenu(1, 0); break;
            case "show_modes": ShowModePicker(); break;
            case "toggle_modes": ToggleModePicker(); break;
            case "open_settings": OpenSettings(); break;
            case "toggle_fullscreen": ToggleFullscreen(); break;
            case "show_assist": ToggleAssist(); break;
            case "center_fake": _pad?.CenterFake(); break;
            case "toggle_pad":
                if (_pad == null) break;
                if (_pad.IsVisible) HidePad();
                else ShowPad();
                break;
        }
    }

    private ModePickerWindow? _picker;
    private DateTime _stripTouchAt = DateTime.MinValue;

    private void ToggleModePicker()
    {
        try
        {
            if (_picker != null)
            {
                _picker.Close();
                return;
            }
        }
        catch (InvalidOperationException) { _picker = null; }
        ShowModePicker();
    }

    private void ShowModePicker()
    {
        // Always rebuild: a reused instance would show stale buttons
        // after settings edits (never shrinks/grows with them).
        try
        {
            if (_picker != null) { _picker.Close(); _picker = null; }
        }
        catch (InvalidOperationException) { _picker = null; }
        _picker = new ModePickerWindow(
            _settings,
            _settings.Layout,
            name => SelectLayout(name),
            name => ToggleAux(name),
            action => RunPickerAction(action),
            name => _auxOn.Contains(name),
            () => OpenSettings(),
            Core.ActionRunner.StripMenu.GetCell(
                _settings, _menuRow, _menuCol, out var selCell)
                ? selCell.Value : null,
            () =>
            {
                if (_picker != null)
                {
                    try { _picker.Close(); } catch (InvalidOperationException) { }
                    _picker = null;
                }
                if (_settings.StripVisible)
                    ToggleStrip();
            });
        _picker.Closed += (_, _) => _picker = null;
        // The mode panel always opens docked to the strip, wherever the
        // strip is: below/above it for top/bottom edges, beside it for
        // left/right edges. Measured before showing (no jump).
        try
        {
            if (_strip != null)
            {
                _picker.UpdateLayout();
                string edge = (_settings.StripEdge ?? "top").ToLowerInvariant();
                var home = _strip.MonitorRect();
                double ox = home.l, oy = home.t, pw = home.w, ph = home.h;
                // Auto width: measured, fallback 380.
                double pwid = _picker.ActualWidth > 0 ? _picker.ActualWidth : 380;
                double phei = _picker.ActualHeight > 0 ? _picker.ActualHeight : 300;
                double cx = _strip.Left + (_strip.Width - pwid) / 2;
                cx = Math.Max(ox, Math.Min(ox + pw - pwid, cx));
                double cy = _strip.Top + (_strip.Height - phei) / 2;
                cy = Math.Max(oy, Math.Min(oy + ph - phei, cy));
                if (edge == "left")
                {
                    _picker.Left = Math.Min(_strip.Left + _strip.Width + 4, ox + pw - pwid);
                    _picker.Top = cy;
                }
                else if (edge == "right")
                {
                    _picker.Left = Math.Max(ox, _strip.Left - pwid - 4);
                    _picker.Top = cy;
                }
                else if (edge == "bottom")
                {
                    _picker.Left = cx;
                    _picker.Top = Math.Max(oy, _strip.Top - phei - 4);
                }
                else
                {
                    _picker.Left = cx;
                    _picker.Top = _strip.Top + _strip.Height + 4;
                }
            }
        }
        catch { }
        // Tap outside (any other window activates) collapses the panel.
        // A tap on the strip itself is exempt: the toggle gesture that
        // follows will close it (avoids close+reopen flicker).
        // Guarded: Deactivated can fire re-entrantly DURING Close (activation
        // shifts mid-teardown) - closing a closing window throws
        // InvalidOperationException and would kill the process (measured).
        _picker.Deactivated += (_, _) =>
        {
            try
            {
                if ((DateTime.Now - _stripTouchAt).TotalMilliseconds < 800) return;
                _picker?.Close();
            }
            catch (InvalidOperationException) { }
        };
        _picker.Show();
        Core.TopmostKeeper.Raise(_picker);
        if (_strip != null) Core.TopmostKeeper.Raise(_strip);
    }

    /// <summary>Picker cell action: direct cmd/program/shortcut,
    /// named custom action, or strip gesture. UI wiring only.</summary>
    private void RunPickerAction(string action)
    {
        if (action.StartsWith("cmd:", StringComparison.OrdinalIgnoreCase))
            Core.ActionRunner.Run(new Core.ActionDef
                { Kind = "cmd", Path = action.Substring(4) });
        else if (action.StartsWith("program:", StringComparison.OrdinalIgnoreCase))
            Core.ActionRunner.Run(new Core.ActionDef
                { Kind = "program", Path = action.Substring(8) });
        else if (action.StartsWith("shortcut:", StringComparison.OrdinalIgnoreCase))
            Core.ActionRunner.RunShortcut(action.Substring(9));
        else if (_settings.Actions != null
            && _settings.Actions.TryGetValue(action, out var def))
            Core.ActionRunner.Run(def);
        else HandleStripGesture(action);
    }

    /// <summary>Strip swipe menu navigation: left/right step inside
    /// the row (wrapping), up/down change rows. Swipes only move the
    /// selection shown on the strip; nothing fires. Tap opens the
    /// popup (or a cell tap there executes).</summary>
    private int _menuRow = -1, _menuCol = -1;

    private void StepMenu(int dRow, int dCol)
    {
        try
        {
            if (!Core.ActionRunner.StripMenu.MoveSelection(
                _settings, ref _menuRow, ref _menuCol, dRow, dCol, out _))
                return;
            UpdateStripText();
            // An open popup follows the selection live.
            try
            {
                if (_picker != null
                    && Core.ActionRunner.StripMenu.GetCell(
                        _settings, _menuRow, _menuCol, out var sel))
                    _picker.Refresh(sel.Value);
            }
            catch { }
        }
        catch { }
    }

    /// <summary>Strip bar text: the selected cell (swipe navigation
    /// display), else the layout.</summary>
    private void UpdateStripText()
    {
        try
        {
            if (_strip == null) return;
            if (Core.ActionRunner.StripMenu.GetCell(
                _settings, _menuRow, _menuCol, out var cell)
                && !cell.IsEmpty)
                _strip.SetSelection(cell.Label);
            else
                _strip.SetLabel(_settings.Layout);
        }
        catch { }
    }

    /// <summary>Selects a layout AND shows it: executing means
    /// intending to see it, so a stored Visible=false never swallows
    /// the pad on fire.</summary>
    private void SelectLayout(string name)
    {
        try
        {
            string key = Core.AppSettings.PadFamilyKey(name);
            if (!_settings.Pads.TryGetValue(key, out var p))
            { p = new Core.PadConfig(); _settings.Pads[key] = p; }
            p.Visible = true;
            _settings.Layout = name;
            _settings.Save();
            Apply();
            AdoptMenuCell(name);
        }
        catch { }
    }

    /// <summary>Keeps swipe navigation continuous after a picker tap:
    /// selection follows the chosen layout's cell.</summary>
    private void AdoptMenuCell(string layout)
    {
        try
        {
            if (Core.ActionRunner.StripMenu.LocateLayout(
                _settings, layout, out int r, out int c))
            { _menuRow = r; _menuCol = c; }
            UpdateStripText();
        }
        catch { }
    }

    private void CycleLayout(int step)
    {
        var names = Core.PresetParser.OrderedNames(_presets);
        int i = names.IndexOf(_settings.Layout);
        i = i < 0 ? 0 : (i + step + names.Count) % names.Count;
        _settings.Layout = names[i];
        _settings.Save();
        Apply();
    }

    private void ToggleFullscreen()
    {
        if (_pad == null) return;
        if (!_pad.IsVisible) { ShowPad(); return; }
        if (_pad.Width >= SystemParameters.PrimaryScreenWidth - 10)
        {
            _pad.SyncWindowSize();
            _pad.Left = SystemParameters.PrimaryScreenWidth - _pad.Width - 40;
            _pad.Top = SystemParameters.PrimaryScreenHeight - _pad.Height - 120;
        }
        else
        {
            _pad.Left = 0; _pad.Top = 0;
            _pad.Width = SystemParameters.PrimaryScreenWidth;
            _pad.Height = SystemParameters.PrimaryScreenHeight;
        }
        _pad.SetLayout(_presets[_settings.Layout]);
    }
}
