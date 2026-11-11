using AlUmmahNow.App.I18n;
using AlUmmahNow.Studio.Timeline;

namespace AlUmmahNow.Studio.Linux;

/// <summary>Timeline editor spike: split and ripple delete with a draggable playhead.</summary>
public sealed class TimelinePage
{
    private const double TrackHeight = 56;
    private readonly TimelineProject _project = new();
    private readonly Gtk.DrawingArea _area = Gtk.DrawingArea.New();
    private readonly bool _rtl;
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

        Root = Ui.Page();
        _area.SetContentHeight(180);
        _area.SetVexpand(true);
        _area.SetDrawFunc((_, cr, w, h) =>
        {
            var v = Viewport(w);
            for (var t = 0; t < _project.Tracks.Count; t++)
            {
                foreach (var clip in _project.Tracks[t].Clips)
                {
                    var a = v.ToX(clip.Start);
                    var b = v.ToX(clip.End);
                    if (_project.Tracks[t].Kind == TrackKind.Video) cr.SetSourceRgb(0.03, 0.26, 0.35);
                    else cr.SetSourceRgb(0.56, 0.42, 0.33);
                    cr.Rectangle(Math.Min(a, b) + 1, 10 + (t * (TrackHeight + 8)), Math.Abs(b - a) - 2, TrackHeight);
                    cr.Fill();
                }
            }

            cr.SetSourceRgb(1, 0.63, 0.22);
            cr.Rectangle(v.ToX(_playhead) - 1, 0, 2, h);
            cr.Fill();
        });

        var drag = Gtk.GestureDrag.New();
        double start = 0;
        drag.OnDragBegin += (_, a) =>
        {
            start = a.StartX;
            Move(start);
        };
        drag.OnDragUpdate += (_, a) => Move(start + a.OffsetX);
        _area.AddController(drag);

        var tools = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        tools.Append(Ui.Button(l["studio.timeline.split"], () =>
        {
            foreach (var t in _project.Tracks) _project.Split(t, _playhead);
            _area.QueueDraw();
        }));
        tools.Append(Ui.Button(l["studio.timeline.delete"], () =>
        {
            foreach (var t in _project.Tracks)
            {
                if (t.Clips.FirstOrDefault(c => _playhead >= c.Start && _playhead < c.End) is { } clip) _project.RippleDelete(t, clip);
            }

            _playhead = Math.Min(_playhead, _project.Duration);
            _area.QueueDraw();
        }));
        tools.Append(Gtk.Label.New(l["studio.timeline.hint"]));
        Root.Append(tools);
        Root.Append(_area);
    }

    public Gtk.Box Root { get; }

    private TimelineViewport Viewport(int width) => new(Math.Max(1, width / Math.Max(1, _project.Duration)), 0, width, _rtl);

    private void Move(double x)
    {
        _playhead = Math.Clamp(Viewport(_area.GetWidth()).ToTime(x), 0, _project.Duration);
        _area.QueueDraw();
    }
}
