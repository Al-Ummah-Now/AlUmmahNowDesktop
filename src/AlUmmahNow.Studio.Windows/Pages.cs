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

/// <summary>Encode queue (Lantern Glass card): jobs with a tile, progress and which encoder ran.</summary>
internal sealed class EncodePage
{
    private readonly Localizer _l;
    private readonly EncodeQueue _encode;
    private readonly StackPanel _rows = new() { Spacing = 10 };
    private readonly TextBlock _empty;
    private int _dirty = 1;

    public EncodePage(Localizer l, EncodeQueue encode)
    {
        _l = l;
        _encode = encode;
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = l["studio.tab.encode"], Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        // Never an empty page: say so while nothing is encoding.
        _empty = new TextBlock { Text = l["studio.encode.empty"], Foreground = LanternGlass.Res("StudioMutedBrush") };
        body.Children.Add(_empty);
        body.Children.Add(_rows);
        Root = new StackPanel { Padding = new Thickness(16, 4, 16, 8) };
        Root.Children.Add(LanternGlass.Card(body));
        encode.Changed += (_, _) => Interlocked.Exchange(ref _dirty, 1);
    }

    public StackPanel Root { get; }

    public void Tick()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
        _rows.Children.Clear();
        var jobs = _encode.Jobs.ToList();
        _empty.Visibility = jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var job in jobs)
        {
            var status = job.Status switch
            {
                EncodeStatus.Running => _l["studio.encode.running"],
                EncodeStatus.Completed => _l["studio.status.completed"],
                EncodeStatus.Failed => _l["studio.status.failed"],
                EncodeStatus.Canceled => _l["studio.encode.canceled"],
                _ => _l["studio.status.queued"],
            };
            var name = _l.Join(Localizer.Isolate(Path.GetFileName(job.Request.Source)), _l["studio.preset." + job.Request.Preset.Key]);
            var encoder = job.UsedEncoder is { } e ? _l[e.Kind == EncoderKind.Hardware ? "studio.encode.hardware" : "studio.encode.software"] : null;
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(Tile.Film(80, 45));
            var text = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = job.Progress };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(bar, name);
            text.Children.Add(bar);
            var pace = job.Status != EncodeStatus.Running ? null
                : _l.Join(_l.Percent(job.Progress), job.Remaining is { } left ? _l.Format("studio.encode.remaining", _l.Duration(Math.Ceiling(left.TotalSeconds))) : null);
            text.Children.Add(new TextBlock { Text = _l.Join(status, pace, encoder), FontSize = 12, Foreground = LanternGlass.Res("StudioMutedBrush") });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            _rows.Children.Add(row);
        }
    }
}

/// <summary>A video file's tile: night blue with a gold film glyph.</summary>
internal static class Tile
{
    public static Border Film(double width, double height) => new()
    {
        Width = width,
        Height = height,
        CornerRadius = new CornerRadius(8),
        VerticalAlignment = VerticalAlignment.Center,
        Background = new LinearGradientBrush
        {
            StartPoint = new global::Windows.Foundation.Point(0, 0),
            EndPoint = new global::Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = global::Windows.UI.Color.FromArgb(0xFF, 0x18, 0x44, 0x56), Offset = 0 },
                new GradientStop { Color = LanternGlass.Night, Offset = 1 },
            },
        },
        Child = new FontIcon { Glyph = "\uE8B2", FontSize = height * 0.4, Foreground = new SolidColorBrush(LanternGlass.Gold) },
    };
}

/// <summary>
/// Timeline editor (Lantern Glass): a tracks card with Split, Ripple delete and the playhead time in
/// its header, a ruler, video and audio lanes, and a glowing gold playhead you drag; a Clips card
/// lists the video segments the splits made.
/// </summary>
internal sealed class TimelinePage
{
    private const double TrackHeight = 54;
    private const double RulerHeight = 24;
    private const double Gap = 10;
    private readonly Localizer _l;
    private readonly TimelineProject _project = new();
    private readonly bool _rtl;
    private readonly Canvas _canvas;
    private readonly TextBlock _time = new() { FontWeight = Microsoft.UI.Text.FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _clips = new() { Spacing = 6 };
    private double _playhead = 20;

    public TimelinePage(Localizer l)
    {
        _l = l;
        _rtl = l.IsRtl;
        var video = new Track { Kind = TrackKind.Video };
        video.Clips.Add(new Clip { Source = "intro.mp4", Start = 0, Duration = 12 });
        video.Clips.Add(new Clip { Source = "main.mp4", Start = 12, Duration = 40 });
        var audio = new Track { Kind = TrackKind.Audio };
        audio.Clips.Add(new Clip { Source = "main.m4a", Start = 0, Duration = 52 });
        _project.Tracks.Add(video);
        _project.Tracks.Add(audio);

        // The canvas computes mirrored coordinates itself, so it must not be mirrored again.
        var height = RulerHeight + Gap + (_project.Tracks.Count * (TrackHeight + Gap));
        _canvas = new Canvas { Height = height, FlowDirection = FlowDirection.LeftToRight, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        _canvas.SizeChanged += (_, _) => Draw();
        _canvas.PointerPressed += (_, e) => { _canvas.CapturePointer(e.Pointer); Scrub(e); };
        _canvas.PointerMoved += (_, e) => { if (e.Pointer.IsInContact) Scrub(e); };
        _canvas.PointerReleased += (_, e) => _canvas.ReleasePointerCapture(e.Pointer);

        var split = new Button { Content = l["studio.timeline.split"], Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        split.Click += (_, _) => { foreach (var t in _project.Tracks) _project.Split(t, _playhead); Changed(); };
        var ripple = new Button { Content = l["studio.timeline.delete"], Foreground = LanternGlass.Res("StudioDangerBrush") };
        ripple.Click += (_, _) =>
        {
            foreach (var t in _project.Tracks)
            {
                if (t.Clips.FirstOrDefault(c => _playhead >= c.Start && _playhead < c.End) is { } clip) _project.RippleDelete(t, clip);
            }

            _playhead = Math.Min(_playhead, _project.Duration);
            Changed();
        };
        var tools = new Grid { ColumnSpacing = 8 };
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.Children.Add(split);
        Grid.SetColumn(ripple, 1);
        tools.Children.Add(ripple);
        var hint = new TextBlock { Text = l["studio.timeline.hint"], VerticalAlignment = VerticalAlignment.Center, Foreground = LanternGlass.Res("StudioMutedBrush"), Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(hint, 2);
        tools.Children.Add(hint);
        _time.Foreground = LanternGlass.Res("AccentTextFillColorPrimaryBrush");
        Grid.SetColumn(_time, 3);
        tools.Children.Add(_time);

        var lanes = new Grid { ColumnSpacing = 8 };
        lanes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        lanes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var names = new StackPanel { Padding = new Thickness(0, RulerHeight + Gap, 0, 0) };
        foreach (var t in _project.Tracks)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Height = TrackHeight + Gap };
            row.Children.Add(new FontIcon { Glyph = t.Kind == TrackKind.Video ? "\uE8B2" : "\uE8D6", FontSize = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 18, 0, 0) });
            row.Children.Add(new TextBlock { Text = l[t.Kind == TrackKind.Video ? "studio.timeline.video" : "studio.timeline.audio"], FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 0) });
            names.Children.Add(row);
        }

        lanes.Children.Add(names);
        Grid.SetColumn(_canvas, 1);
        lanes.Children.Add(_canvas);
        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(tools);
        body.Children.Add(lanes);

        var clipsBody = new StackPanel { Spacing = 8 };
        clipsBody.Children.Add(new TextBlock { Text = l["studio.timeline.clips"], Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        clipsBody.Children.Add(_clips);
        Root = new StackPanel { Spacing = 18, Padding = new Thickness(16, 4, 16, 8) };
        Root.Children.Add(LanternGlass.Card(body));
        Root.Children.Add(LanternGlass.Card(clipsBody));
        Changed();
    }

    public StackPanel Root { get; }

    private void Changed()
    {
        _time.Text = _l.Duration(_playhead);
        _clips.Children.Clear();
        var video = _project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video);
        foreach (var clip in video?.Clips ?? new List<Clip>())
        {
            var current = _playhead >= clip.Start && _playhead < clip.End;
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(10) };
            if (current)
            {
                row.Background = LanternGlass.Res("StudioSelectedBrush");
                row.BorderBrush = LanternGlass.Res("StudioEdgeBrush");
                row.BorderThickness = new Thickness(1);
            }

            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(Tile.Film(72, 40));
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = clip.Source, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = _l.Format("studio.timeline.span", _l.Duration(clip.Start), _l.Duration(clip.End)), FontSize = 12, Foreground = LanternGlass.Res("StudioMutedBrush") });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            _clips.Children.Add(row);
        }

        Draw();
    }

    private void Draw()
    {
        _canvas.Children.Clear();
        var width = _canvas.ActualWidth > 0 ? _canvas.ActualWidth : 1000;
        var viewport = new TimelineViewport(width / Math.Max(1, _project.Duration), 0, width, _rtl);
        var muted = LanternGlass.Res("StudioMutedBrush");
        var edge = global::Windows.UI.Color.FromArgb(0x73, 0xFF, 0xD9, 0x8A);

        // Ruler: a tick every 10 seconds, labelled every 20.
        for (var s = 0; s <= _project.Duration; s += 10)
        {
            var x = viewport.ToX(s);
            _canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = RulerHeight - 6, Y2 = RulerHeight, Stroke = muted, StrokeThickness = 1 });
            if (s % 20 != 0) continue;
            var label = new TextBlock { Text = _l.Duration(s), FontSize = 11, Foreground = muted };
            label.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, Math.Clamp(x - (label.DesiredSize.Width / 2), 0, Math.Max(0, width - label.DesiredSize.Width)));
            _canvas.Children.Add(label);
        }

        for (var t = 0; t < _project.Tracks.Count; t++)
        {
            var track = _project.Tracks[t];
            var top = RulerHeight + Gap + (t * (TrackHeight + Gap));
            foreach (var clip in track.Clips)
            {
                var a = viewport.ToX(clip.Start);
                var b = viewport.ToX(clip.End);
                var current = _playhead >= clip.Start && _playhead < clip.End;
                var video = track.Kind == TrackKind.Video;
                var rect = new Border
                {
                    Width = Math.Max(2, Math.Abs(b - a) - 2),
                    Height = TrackHeight,
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(current ? 2 : 1),
                    BorderBrush = new SolidColorBrush(current ? LanternGlass.GoldEdge : edge),
                    Background = new LinearGradientBrush
                    {
                        StartPoint = new global::Windows.Foundation.Point(0, 0),
                        EndPoint = new global::Windows.Foundation.Point(0, 1),
                        GradientStops =
                        {
                            new GradientStop { Color = video ? global::Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x4C, 0x60) : global::Windows.UI.Color.FromArgb(0xFF, 0x8E, 0x6C, 0x53), Offset = 0 },
                            new GradientStop { Color = video ? global::Windows.UI.Color.FromArgb(0xFF, 0x11, 0x33, 0x44) : global::Windows.UI.Color.FromArgb(0xFF, 0x6B, 0x4F, 0x3C), Offset = 1 },
                        },
                    },
                    Child = video
                        ? new TextBlock { Text = clip.Source, Margin = new Thickness(8, 6, 8, 0), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(LanternGlass.OnNight), TextTrimming = TextTrimming.CharacterEllipsis, FlowDirection = _rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight }
                        : Waveform(Math.Max(2, Math.Abs(b - a) - 2)),
                };
                Canvas.SetLeft(rect, Math.Min(a, b) + 1);
                Canvas.SetTop(rect, top);
                _canvas.Children.Add(rect);
            }
        }

        // The playhead: a soft gold glow, the line, and a handle on the ruler.
        var px = viewport.ToX(_playhead);
        var glow = new Rectangle { Width = 8, Height = _canvas.Height, Fill = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0x40, 0xF4, 0xAE, 0x3A)) };
        Canvas.SetLeft(glow, px - 4);
        _canvas.Children.Add(glow);
        var head = new Rectangle { Width = 2, Height = _canvas.Height, Fill = new SolidColorBrush(LanternGlass.Gold) };
        Canvas.SetLeft(head, px - 1);
        _canvas.Children.Add(head);
        var handle = new Polygon { Fill = new SolidColorBrush(LanternGlass.Gold), Points = { new global::Windows.Foundation.Point(px - 7, 0), new global::Windows.Foundation.Point(px + 7, 0), new global::Windows.Foundation.Point(px + 7, 8), new global::Windows.Foundation.Point(px, 14), new global::Windows.Foundation.Point(px - 7, 8) } };
        _canvas.Children.Add(handle);
    }

    /// <summary>A waveform impression for an audio clip.</summary>
    private static FrameworkElement Waveform(double width)
    {
        var canvas = new Canvas();
        var brush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0xC0, 0xF5, 0xCF, 0x92));
        for (var i = 4.0; i < width - 4; i += 4)
        {
            var amp = (Math.Abs(Math.Sin(i * 0.13)) * 0.6 + Math.Abs(Math.Cos(i * 0.031)) * 0.3) * (TrackHeight - 16);
            var bar = new Rectangle { Width = 2, Height = amp, Fill = brush };
            Canvas.SetLeft(bar, i);
            Canvas.SetTop(bar, (TrackHeight - amp) / 2);
            canvas.Children.Add(bar);
        }

        return canvas;
    }

    private void Scrub(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var viewport = new TimelineViewport(_canvas.ActualWidth / Math.Max(1, _project.Duration), 0, _canvas.ActualWidth, _rtl);
        _playhead = Math.Clamp(viewport.ToTime(e.GetCurrentPoint(_canvas).Position.X), 0, _project.Duration);
        Changed();
    }
}
