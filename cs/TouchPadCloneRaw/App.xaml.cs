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
        _strip.Pressed += () => _stripTouchAt = DateTime.Now;
        _strip.LayoutSelected += name =>
        {
            _settings.Layout = name;
            _settings.Save();
            Apply();
        };
        _strip.KnownLayouts = Core.PresetParser.OrderedNames(_presets);
        _strip.SetLabel(_settings.Layout);
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
        };

        _tray = new TrayManager(
            () => ShowPad(),
            () => HidePad(),
            OpenSettings,
            () => { _pad?.EmergencyRestore(); _tray?.Dispose(); Shutdown(); },
            () => _pad?.EmergencyRestore());
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
    }

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
        _strip.SetLabel(_settings.Layout);
        _strip.KnownLayouts = Core.PresetParser.OrderedNames(_presets);
        _strip.RefreshRows();
        _artistPad?.Refresh();
        _virtualPad?.Refresh();
        Core.TopmostKeeper.Raise(_strip);
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
            var (l, t, w, h) = Core.PadPlacer.RectFor(mode,
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            _pad.Left = l; _pad.Top = t; _pad.Width = w; _pad.Height = h;
            _placedMode = mode;
        }
        else if (mode == "default" && fresh)
        {
            _pad.SyncWindowSize();
            _pad.Left = SystemParameters.PrimaryScreenWidth - _pad.Width - 40;
            _pad.Top = SystemParameters.PrimaryScreenHeight - _pad.Height - 120;
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
    private void HandleStripTap()
    {
        if (_pad == null || _strip == null) return;
        if (!_pad.IsVisible)
        {
            ShowPad();
            return;
        }
        string action = _settings.StripGestures?.Tap ?? "toggle_modes";
        if (action == "toggle_modes") ToggleModePicker();
        else if (action != "none") HandleStripGesture(action);
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
        if (_picker != null) { _picker.Activate(); return; }
        _picker = new ModePickerWindow(
            _settings,
            _settings.Layout,
            name =>
            {
                _settings.Layout = name;
                _settings.Save();
                Apply();
            },
            name => ToggleAux(name),
            action =>
            {
                // Custom program/cmd actions run; the rest are strip gestures.
                if (_settings.Actions != null
                    && _settings.Actions.TryGetValue(action, out var def))
                    Core.ActionRunner.Run(def);
                else HandleStripGesture(action);
            },
            name => _auxOn.Contains(name),
            () => OpenSettings());
        _picker.Closed += (_, _) => _picker = null;
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
