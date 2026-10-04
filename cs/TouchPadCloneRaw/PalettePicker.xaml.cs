using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TouchPadCloneV2;

public static class ColorPalettes
{
    public const string Follow = "General 따름";
    public const string None = "없음";

    public static readonly string[] Effects =
    [
        "#FF7FE0A8", "#FF35C4FF", "#FFFFFFFF",
        "#FFFFD54F", "#FFFF8A80", "#FFEA80FC",
        "#FF00BCD4", "#FF8BC34A", "#FFFF9800",
        "#FF9C27B0", "#FF03A9F4", "#FF4CAF50",
    ];

    public static readonly string[] Zones =
    [
        "#40206040", "#40402060", "#40602020",
        "#30404040", "#60206060", "#60404020",
        "#40306080", "#40408060", "#40806040",
        "#40608040", "#40406040", "#40204060",
        "#60302050", "#60502030", "#30505050",
        "#40205060", "#50305060",
    ];

    public static bool IsNone(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        string t = s.Trim();
        return t == None || t.Equals("none", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHex(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        try { var _ = (Color)ColorConverter.ConvertFromString(s.Trim()); return true; }
        catch { return false; }
    }

    public static Brush NoneBrush()
    {
        var g = new GeometryGroup();
        g.Children.Add(new LineGeometry(new Point(2, 2), new Point(14, 14)));
        g.Children.Add(new LineGeometry(new Point(14, 2), new Point(2, 14)));
        return new DrawingBrush(new GeometryDrawing(Brushes.Transparent, new Pen(Brushes.Red, 2), g));
    }

    public static Brush BrushFor(string? v, bool allowFollow)
    {
        string s = (v ?? "").Trim();
        if (allowFollow && (s == "" || s == Follow)) return Brushes.Gray;
        if (IsNone(s)) return NoneBrush();
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(s)); }
        catch { return Brushes.Transparent; }
    }

    public static string DisplayFor(string? v, bool allowFollow)
    {
        string s = (v ?? "").Trim();
        if (allowFollow && s == "") return Follow;
        return s;
    }

    /// <summary>Cell text brush: cell color wins, empty/none falls back
    /// to the strip setting, then gray.</summary>
    public static Brush CellTextBrush(string? cell, string? strip)
    {
        foreach (var v in new[] { cell, strip })
        {
            string s = (v ?? "").Trim();
            if (s == "" || IsNone(s)) continue;
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(s)); }
            catch { }
        }
        return new SolidColorBrush(Color.FromArgb(0xFF, 0x80, 0x80, 0x80));
    }
}

public partial class PalettePicker : UserControl
{
    public IEnumerable<string> BasePalette { get; set; } = [];
    public IEnumerable<string> CustomColors { get; set; } = [];
    public bool AllowFollow { get; set; }

    private string _selected = "";

    public string Selected
    {
        get => _selected;
        set { _selected = value ?? ""; RefreshFace(); }
    }

    public event Action<string?>? Picked;

    public PalettePicker()
    {
        InitializeComponent();
        Loaded += (_, _) => { RebuildCells(); RefreshFace(); };
        Face.MouseLeftButtonUp += (_, _) => OpenPopup();
        CustomApply.Click += (_, _) => Choose(CustomBox.Text);
    }

    private void OpenPopup()
    {
        try
        {
            RebuildCells();
            CustomBox.Text = _selected;
            DropPopup.IsOpen = true;
        }
        catch { }
    }

    private void RefreshFace()
    {
        try
        {
            Swatch.Background = ColorPalettes.BrushFor(_selected, AllowFollow);
            string d = ColorPalettes.DisplayFor(_selected, AllowFollow);
            Face.ToolTip = d;
        }
        catch { }
    }

    private void RebuildCells()
    {
        try
        {
            Cells.Children.Clear();
            var items = new List<string>();
            if (AllowFollow) items.Add(ColorPalettes.Follow);
            items.Add(ColorPalettes.None);
            foreach (var c in BasePalette ?? [])
            {
                string t = (c ?? "").Trim();
                if (t == "" || t == ColorPalettes.Follow
                    || ColorPalettes.IsNone(t) || items.Contains(t)) continue;
                items.Add(t);
            }
            foreach (var c in CustomColors ?? [])
            {
                string t = (c ?? "").Trim();
                if (!ColorPalettes.IsHex(t) || items.Contains(t)) continue;
                items.Add(t);
            }
            foreach (var c in items) Cells.Children.Add(MakeCell(c));
            Cells.Children.Add(MakeCurrentCell());
        }
        catch { }
    }

    private Button MakeCell(string c)
    {
        bool cur = c == _selected;
        var r = new Border
        {
            Width = 30,
            Height = 26,
            Background = ColorPalettes.BrushFor(c, AllowFollow),
            BorderBrush = cur ? Brushes.Black : Brushes.Gray,
            BorderThickness = cur ? new Thickness(2) : new Thickness(1),
        };
        var b = new Button
        {
            Content = r,
            Padding = new Thickness(0),
            Margin = new Thickness(1),
            ToolTip = c,
        };
        b.Click += (_, _) => Choose(c);
        return b;
    }

    private UIElement MakeCurrentCell()
    {
        var s = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(2),
            IsHitTestVisible = false,
        };
        s.Children.Add(new Border
        {
            Width = 30,
            Height = 26,
            Background = ColorPalettes.BrushFor(_selected, AllowFollow),
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(2),
            ToolTip = "현재값",
        });
        s.Children.Add(new TextBlock
        {
            Text = "현재",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            FontSize = 11,
            Foreground = Brushes.Gray,
        });
        return s;
    }

    private void Choose(string? v)
    {
        _selected = (v ?? "").Trim();
        RefreshFace();
        DropPopup.IsOpen = false;
        try { Picked?.Invoke(_selected); } catch { }
    }
}