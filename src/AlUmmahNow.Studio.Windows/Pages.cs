using AlUmmahNow.App.I18n;
using AlUmmahNow.Studio.Encode;
using AlUmmahNow.Studio.Timeline;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = System.IO.Path;

namespace AlUmmahNow.Studio.WindowsApp;

internal static class Rows
{
    public static FrameworkElement Progress(string name, double fraction, string status)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = fraction, VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(bar, name);
        Grid.SetColumn(bar, 1);
        grid.Children.Add(bar);
        var text = new TextBlock { Text = status };
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        return grid;
    }
}

/// <summary>Encode queue (spike).</summary>
internal sealed class EncodePage
{
    private readonly Localizer _l;
    private readonly EncodeQueue _encode;
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private int _dirty = 1;

    public EncodePage(Localizer l, EncodeQueue encode)
    {
        _l = l;
        _encode = encode;
        Root = new StackPanel { Padding = new Thickness(16) };
        Root.Children.Add(_rows);
        encode.Changed += (_, _) => Interlocked.Exchange(ref _dirty, 1);
    }

    public StackPanel Root { get; }

    public void Tick()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
        _rows.Children.Clear();
        foreach (var job in _encode.Jobs.ToList())
        {
            var status = job.Status switch
            {
                EncodeStatus.Running => _l["studio.encode.running"],
                EncodeStatus.Completed => _l["studio.status.completed"],
                EncodeStatus.Failed => _l["studio.status.failed"],
                EncodeStatus.Canceled => _l["studio.encode.canceled"],
                _ => _l["studio.status.queued"],
            };
            var name = Path.GetFileName(job.Request.Source) + " · " + _l["studio.preset." + job.Request.Preset.Key];
            var encoder = job.UsedEncoder is { } e ? " " + _l[e.Kind == EncoderKind.Hardware ? "studio.encode.hardware" : "studio.encode.software"] : "";
            _rows.Children.Add(Rows.Progress(name, job.Progress, status + encoder));
        }
    }
}

/// <summary>Timeline editor spike.</summary>
internal sealed class TimelinePage
{
    private readonly TimelineProject _project = new();
    private readonly bool _rtl;
    private readonly Canvas _canvas;
    private double _playhead = 20;

    public TimelinePage(Localizer l)
    {
        _rtl = l.IsRtl;
        var video = new Track { Kind = TrackKind.Video };
        video.Clips.Add(new Clip { Source = "intro.mp4", Start = 0, Duration = 12 });
        video.Clips.Add(new Clip { Source = "lecture.mp4", Start = 12, Duration = 40 });
        var audio = new Track { Kind = TrackKind.Audio };
        audio.Clips.Add(new Clip { Source = "lecture.m4a", Start = 0, Duration = 52 });
        _project.Tracks.Add(video);
        _project.Tracks.Add(audio);

        // The canvas computes mirrored coordinates itself, so it must not be mirrored again.
        _canvas = new Canvas { Height = 220, FlowDirection = FlowDirection.LeftToRight, Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 9, 48, 65)) };
        _canvas.SizeChanged += (_, _) => Draw();
        _canvas.PointerPressed += (_, e) => { _canvas.CapturePointer(e.Pointer); Scrub(e); };
        _canvas.PointerMoved += (_, e) => { if (e.Pointer.IsInContact) Scrub(e); };
        _canvas.PointerReleased += (_, e) => _canvas.ReleasePointerCapture(e.Pointer);

        var split = new Button { Content = l["studio.timeline.split"] };
        split.Click += (_, _) => { foreach (var t in _project.Tracks) _project.Split(t, _playhead); Draw(); };
        var ripple = new Button { Content = l["studio.timeline.delete"] };
        ripple.Click += (_, _) =>
        {
            foreach (var t in _project.Tracks)
            {
                if (t.Clips.FirstOrDefault(c => _playhead >= c.Start && _playhead < c.End) is { } clip) _project.RippleDelete(t, clip);
            }

            _playhead = Math.Min(_playhead, _project.Duration);
            Draw();
        };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tools.Children.Add(split);
        tools.Children.Add(ripple);
        tools.Children.Add(new TextBlock { Text = l["studio.timeline.hint"], VerticalAlignment = VerticalAlignment.Center });
        Root = new StackPanel { Spacing = 10, Padding = new Thickness(16) };
        Root.Children.Add(tools);
        Root.Children.Add(_canvas);
    }

    public StackPanel Root { get; }

    private void Draw()
    {
        _canvas.Children.Clear();
        var width = _canvas.ActualWidth > 0 ? _canvas.ActualWidth : 1200;
        var viewport = new TimelineViewport(width / Math.Max(1, _project.Duration), 0, width, _rtl);
        for (var t = 0; t < _project.Tracks.Count; t++)
        {
            foreach (var clip in _project.Tracks[t].Clips)
            {
                var a = viewport.ToX(clip.Start);
                var b = viewport.ToX(clip.End);
                var color = _project.Tracks[t].Kind == TrackKind.Video ? global::Windows.UI.Color.FromArgb(255, 7, 66, 90) : global::Windows.UI.Color.FromArgb(255, 142, 108, 83);
                var rect = new Rectangle { Width = Math.Max(1, Math.Abs(b - a) - 2), Height = 56, Fill = new SolidColorBrush(color) };
                Canvas.SetLeft(rect, Math.Min(a, b) + 1);
                Canvas.SetTop(rect, 10 + (t * 64));
                _canvas.Children.Add(rect);
            }
        }

        var head = new Rectangle { Width = 2, Height = 220, Fill = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 160, 55)) };
        Canvas.SetLeft(head, viewport.ToX(_playhead) - 1);
        _canvas.Children.Add(head);
    }

    private void Scrub(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var viewport = new TimelineViewport(_canvas.ActualWidth / Math.Max(1, _project.Duration), 0, _canvas.ActualWidth, _rtl);
        _playhead = Math.Clamp(viewport.ToTime(e.GetCurrentPoint(_canvas).Position.X), 0, _project.Duration);
        Draw();
    }
}
