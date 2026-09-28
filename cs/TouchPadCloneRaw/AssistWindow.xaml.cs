using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TouchPadCloneV2.Core;
using WButton = System.Windows.Controls.Button;

namespace TouchPadCloneV2;

/// <summary>Ctrl/Shift/Alt/Space hold pad (original virtualctrls).</summary>
public partial class AssistWindow : Window
{
    private readonly Dictionary<WButton, int> _keys = new();

    public AssistWindow()
    {
        InitializeComponent();
        Core.NoActivate.Apply(this);
        Core.TabletTweaks.DisableSystemGestures(this);
        Left = 40;
        Top = SystemParameters.PrimaryScreenHeight - 160;
        _keys[BCtrl] = 0x11; _keys[BShift] = 0x10;
        _keys[BAlt] = 0x12; _keys[BSpace] = 0x20;
        foreach (var (btn, vk) in _keys)
        {
            btn.PreviewTouchDown += (_, e) => { InputSim.HoldKey(vk, true); e.Handled = true; };
            btn.PreviewTouchUp += (_, e) => { InputSim.HoldKey(vk, false); e.Handled = true; };
            btn.PreviewMouseDown += (_, e) =>
            {
                if (e.StylusDevice != null) return;
                InputSim.HoldKey(vk, true); e.Handled = true;
            };
            btn.PreviewMouseUp += (_, e) =>
            {
                if (e.StylusDevice != null) return;
                InputSim.HoldKey(vk, false); e.Handled = true;
            };
        }
        BClose.Click += (_, _) => Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (int vk in _keys.Values)
            try { InputSim.HoldKey(vk, false); } catch { }
        base.OnClosed(e);
    }
}
