using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TouchPadCloneV2.Core;

namespace TouchPadCloneV2;

/// <summary>Dedicated gesture-recording window. Touches here NEVER act
/// (no clicks, no wheel, no actions) - they only draw the template.
/// Two fingers or more required; single-finger input is ignored.</summary>
public sealed class RecWindow : Window
{
    private readonly Action<RecordedGesture?> _onDone;
    private readonly Canvas _canvas = new();
    private readonly Polyline _trailLine = new();
    private readonly TextBlock _status = new();
    private readonly Dictionary<int, Ellipse> _dots = new();
    private readonly Dictionary<int, Point> _live = new();
    private readonly List<(double x, double y)> _trail = new();
    private int _maxN;
    private bool _done;

    public RecWindow(Action<RecordedGesture?> onDone)
    {
        _onDone = onDone;
        Title = "제스처 녹화";
        Width = 520;
        Height = 480;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0x13, 0x1A));

        var root = new StackPanel { Margin = new Thickness(12) };
        _status.Text = "두손가락으로 그리고 떼세요 (한손가락은 무시됨)";
        _status.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x9A, 0xA6, 0xBD));
        _status.Margin = new Thickness(0, 0, 0, 6);
        root.Children.Add(_status);

        _canvas.Height = 340;
        _canvas.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0A, 0x0F, 0x14));
        _canvas.ClipToBounds = true;
        _trailLine.Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x7F, 0xE0, 0xA8));
        _trailLine.StrokeThickness = 2.5;
        _canvas.Children.Add(_trailLine);
        _canvas.TouchDown += OnDown;
        _canvas.TouchMove += OnMove;
        _canvas.TouchUp += OnUp;
        root.Children.Add(_canvas);

        var cancel = new Button
        {
            Content = "취소",
            Width = 100,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        cancel.Click += (_, _) => Finish(null);
        root.Children.Add(cancel);

        Content = root;
    }

    private void OnDown(object sender, TouchEventArgs e)
    {
        try
        {
            var p = e.GetTouchPoint(_canvas).Position;
            _live[e.TouchDevice.Id] = p;
            if (_live.Count > _maxN) _maxN = _live.Count;
            var dot = new Ellipse
            {
                Width = 26,
                Height = 26,
                Fill = new SolidColorBrush(Color.FromArgb(0x88, 0x35, 0xC4, 0xFF)),
                Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x35, 0xC4, 0xFF)),
                StrokeThickness = 1.5,
            };
            _dots[e.TouchDevice.Id] = dot;
            _canvas.Children.Add(dot);
            Canvas.SetLeft(dot, p.X - 13);
            Canvas.SetTop(dot, p.Y - 13);
            FeedTrail();
            e.Handled = true;
        }
        catch { }
    }

    private void OnMove(object sender, TouchEventArgs e)
    {
        try
        {
            if (!_live.ContainsKey(e.TouchDevice.Id)) return;
            var p = e.GetTouchPoint(_canvas).Position;
            _live[e.TouchDevice.Id] = p;
            if (_dots.TryGetValue(e.TouchDevice.Id, out var dot))
            {
                Canvas.SetLeft(dot, p.X - 13);
                Canvas.SetTop(dot, p.Y - 13);
            }
            FeedTrail();
            e.Handled = true;
        }
        catch { }
    }

    private void OnUp(object sender, TouchEventArgs e)
    {
        try
        {
            _live.Remove(e.TouchDevice.Id);
            if (_dots.TryGetValue(e.TouchDevice.Id, out var dot))
            {
                _canvas.Children.Remove(dot);
                _dots.Remove(e.TouchDevice.Id);
            }
            e.Handled = true;
            if (_live.Count == 0) Finish(FinishTemplate());
        }
        catch { }
    }

    private void FeedTrail()
    {
        try
        {
            if (_live.Count < 2 || _trail.Count >= 512) return;
            double sx = 0, sy = 0;
            foreach (var kv in _live) { sx += kv.Value.X; sy += kv.Value.Y; }
            _trail.Add((sx / _live.Count, sy / _live.Count));
        }
        catch { }
    }

    private RecordedGesture? FinishTemplate()
    {
        try
        {
            if (_maxN < 2 || _trail.Count < 4) return null;
            if (GestureMatch.PathLength(_trail) < GestureMatch.MinTravelDip) return null;
            var norm = GestureMatch.Normalize(GestureMatch.Resample(_trail, GestureMatch.Size));
            return new RecordedGesture
            {
                Name = "",
                Action = "none",
                Fingers = Math.Min(_maxN, 5),
                Points = GestureMatch.Flatten(norm),
            };
        }
        catch { return null; }
    }

    private void Finish(RecordedGesture? g)
    {
        if (_done) return;
        _done = true;
        try { _onDone?.Invoke(g); } catch { }
        try { Close(); } catch { }
    }
}
