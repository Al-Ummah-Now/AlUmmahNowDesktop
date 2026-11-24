using AlUmmahNow.App.I18n;
using AlUmmahNow.Studio.Timeline;

namespace AlUmmahNow.Studio.Linux;

/// <summary>
/// Timeline editor (Lantern Glass): a tracks card with Split, Ripple delete and the playhead time in
/// its header, a ruler, video and audio lanes, and a glowing gold playhead you drag; a Clips card
/// lists the video segments the splits made.
/// </summary>
public sealed class TimelinePage
{
    private const double TrackHeight = 54;
    private const double RulerHeight = TimelineRuler.Height;
    private const double Gap = 10;
    private readonly Localizer _l;
    private readonly TimelineProject _project = new();
    private readonly Gtk.DrawingArea _area = Gtk.DrawingArea.New();
    private readonly Gtk.Label _time = Ui.Label("");
    private readonly Gtk.Box _clips = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
    private double _playhead = 20;

    public TimelinePage(Localizer l)
    {
        _l = l;
        var video = new Track { Kind = TrackKind.Video };
        video.Clips.Add(new Clip { Source = "intro.mp4", Start = 0, Duration = 12 });
        video.Clips.Add(new Clip { Source = "main.mp4", Start = 12, Duration = 40 });
        var audio = new Track { Kind = TrackKind.Audio };
        audio.Clips.Add(new Clip { Source = "main.m4a", Start = 0, Duration = 52 });
        _project.Tracks.Add(video);
        _project.Tracks.Add(audio);

        Root = Gtk.Box.New(Gtk.Orientation.Vertical, 18);
        Root.SetMarginStart(24);
        Root.SetMarginEnd(24);
        Root.SetMarginTop(18);
        Root.SetMarginBottom(24);

        // Tracks card: tools and the playhead time on top, then the lanes.
        var tools = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var split = Ui.Button(l["studio.timeline.split"], () =>
        {
            foreach (var t in _project.Tracks) _project.Split(t, _playhead);
            Changed();
        });
        split.AddCssClass("suggested-action");
        tools.Append(split);
        var ripple = Ui.Button(l["studio.timeline.delete"], () =>
        {
            foreach (var t in _project.Tracks)
            {
                if (t.Clips.FirstOrDefault(c => _playhead >= c.Start && _playhead < c.End) is { } clip) _project.RippleDelete(t, clip);
            }

            _playhead = Math.Min(_playhead, _project.Duration);
            Changed();
        });
        ripple.AddCssClass("destructive-action");
        tools.Append(ripple);
        var hint = Ui.Label(l["studio.timeline.hint"], dim: true);
        hint.SetHexpand(true);
        hint.SetMarginStart(8);
        tools.Append(hint);
        _time.AddCssClass("timecode");
        tools.Append(_time);

        var lanes = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var names = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        names.SetSizeRequest(96, -1);
        names.Append(Spacer(RulerHeight + Gap));
        foreach (var t in _project.Tracks)
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
            row.SetSizeRequest(-1, (int)(TrackHeight + Gap));
            row.Append(Gtk.Image.NewFromIconName(t.Kind == TrackKind.Video ? "video-x-generic-symbolic" : "audio-x-generic-symbolic"));
            var name = Gtk.Label.New(l[t.Kind == TrackKind.Video ? "studio.timeline.video" : "studio.timeline.audio"]);
            name.AddCssClass("heading");
            row.Append(name);
            row.SetValign(Gtk.Align.Start);
            names.Append(row);
        }

        lanes.Append(names);
        _area.SetContentHeight((int)(RulerHeight + Gap + (_project.Tracks.Count * (TrackHeight + Gap))));
        _area.SetHexpand(true);
        _area.SetDrawFunc((_, cr, w, h) => Draw(cr, w, h));
        lanes.Append(_area);

        var body = Gtk.Box.New(Gtk.Orientation.Vertical, 14);
        body.Append(tools);
        body.Append(lanes);
        Root.Append(Ui.Card(body));

        var clipsBody = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        clipsBody.Append(Ui.CardTitle(l["studio.timeline.clips"]));
        clipsBody.Append(_clips);
        // The list keeps its rows' own height: the card never spreads them over the page.
        _clips.SetValign(Gtk.Align.Start);
        var clipsCard = Ui.Card(clipsBody);
        clipsCard.SetValign(Gtk.Align.Start);
        clipsCard.SetVexpand(false);
        Root.Append(clipsCard);

        var drag = Gtk.GestureDrag.New();
        double start = 0;
        drag.OnDragBegin += (_, a) =>
        {
            start = a.StartX;
            Move(start);
        };
        drag.OnDragUpdate += (_, a) => Move(start + a.OffsetX);
        _area.AddController(drag);
        Changed();
    }

    public Gtk.Box Root { get; }

    private static Gtk.Widget Spacer(double height)
    {
        var s = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        s.SetSizeRequest(-1, (int)height);
        return s;
    }

    // Time runs left to right in every language, as media players do (Apple HIG, Material; our web and
    // mobile players): only the chrome around the lanes follows the page direction.
    private TimelineViewport Viewport(int width) => new(Math.Max(1, width / Math.Max(1, _project.Duration)), 0, width, false);

    private void Move(double x)
    {
        _playhead = Math.Clamp(Viewport(_area.GetWidth()).ToTime(x), 0, _project.Duration);
        Changed();
    }

    /// <summary>Redraws the lanes; refreshes the time and the clip list.</summary>
    private void Changed()
    {
        _time.SetText(_l.Duration(_playhead));
        Ui.Clear(_clips);
        var video = _project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video);
        foreach (var clip in video?.Clips ?? new List<Clip>())
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
            row.AddCssClass("clip-row");
            if (_playhead >= clip.Start && _playhead < clip.End) row.AddCssClass("current");
            row.Append(Ui.FileTile(72, 40));
            var text = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
            text.SetValign(Gtk.Align.Center);
            var name = Ui.Label(Localizer.Isolate(clip.Source));
            name.AddCssClass("heading");
            text.Append(name);
            var span = Ui.Label(_l.Format("studio.timeline.span", _l.Duration(clip.Start), _l.Duration(clip.End)), dim: true);
            span.AddCssClass("caption");
            text.Append(span);
            row.Append(text);
            _clips.Append(row);
        }

        _area.QueueDraw();
    }

    private void Draw(Cairo.Context cr, int w, int h)
    {
        var v = Viewport(w);
        _area.GetColor(out var fg);

        // Ruler: a tick every 10 seconds, labelled every 20.
        cr.LineWidth = 1;
        for (var s = 0; s <= _project.Duration; s += 10)
        {
            var x = Math.Round(v.ToX(s)) + 0.5;
            cr.SetSourceRgba(fg.Red, fg.Green, fg.Blue, 0.35);
            cr.MoveTo(x, RulerHeight - TimelineRuler.Tick);
            cr.LineTo(x, RulerHeight);
            cr.Stroke();
            if (s % 20 != 0) continue;
            var label = _area.CreatePangoLayout(_l.Duration(s));
            label.GetPixelSize(out var tw, out _);
            cr.SetSourceRgba(fg.Red, fg.Green, fg.Blue, 0.7);
            cr.MoveTo(Math.Clamp(x - (tw / 2.0), 0, w - tw), 0);
            PangoCairo.Functions.ShowLayout(cr, label);
        }

        for (var t = 0; t < _project.Tracks.Count; t++)
        {
            var track = _project.Tracks[t];
            var top = RulerHeight + Gap + (t * (TrackHeight + Gap));
            foreach (var clip in track.Clips)
            {
                var a = v.ToX(clip.Start);
                var b = v.ToX(clip.End);
                var x = Math.Min(a, b) + 1;
                var width = Math.Max(2, Math.Abs(b - a) - 2);
                var current = _playhead >= clip.Start && _playhead < clip.End;
                RoundedRect(cr, x, top, width, TrackHeight, 8);
                using (var fill = new Cairo.LinearGradient(0, top, 0, top + TrackHeight))
                {
                    if (track.Kind == TrackKind.Video)
                    {
                        fill.AddColorStopRgb(0, 0.122, 0.298, 0.376);
                        fill.AddColorStopRgb(1, 0.067, 0.200, 0.267);
                    }
                    else
                    {
                        fill.AddColorStopRgb(0, 0.557, 0.424, 0.325);
                        fill.AddColorStopRgb(1, 0.420, 0.310, 0.235);
                    }

                    cr.SetSource(fill);
                    cr.FillPreserve();
                }

                // Gold edge; the clip under the playhead glows.
                cr.SetSourceRgba(1, 0.851, 0.541, current ? 1 : 0.45);
                cr.LineWidth = current ? 2 : 1;
                cr.Stroke();
                if (track.Kind == TrackKind.Audio)
                {
                    // A waveform impression.
                    cr.SetSourceRgba(0.961, 0.812, 0.573, 0.75);
                    for (var i = 4.0; i < width - 4; i += 4)
                    {
                        var amp = (Math.Abs(Math.Sin((x + i) * 0.13)) * 0.6 + Math.Abs(Math.Cos((x + i) * 0.031)) * 0.3) * (TrackHeight - 16);
                        cr.Rectangle(x + i, top + ((TrackHeight - amp) / 2), 2, amp);
                    }

                    cr.Fill();
                }
                else
                {
                    var name = _area.CreatePangoLayout(clip.Source);
                    cr.SetSourceRgb(0.969, 0.945, 0.910);
                    cr.Save();
                    cr.Rectangle(x + 4, top, Math.Max(0, width - 8), TrackHeight);
                    // Clip first: Clip() clears the path and its current point, so the text position comes after.
                    cr.Clip();
                    cr.MoveTo(x + 8, top + 6);
                    PangoCairo.Functions.ShowLayout(cr, name);
                    cr.Restore();
                }
            }
        }

        // The playhead, under the labels: a soft gold glow, the line, and a handle on the ruler.
        var px = v.ToX(_playhead);
        const double head = TimelineRuler.LabelRoom;
        cr.SetSourceRgba(0.957, 0.682, 0.227, 0.25);
        cr.Rectangle(px - 4, head, 8, h - head);
        cr.Fill();
        cr.SetSourceRgb(0.957, 0.682, 0.227);
        cr.Rectangle(px - 1, head, 2, h - head);
        cr.Fill();
        cr.MoveTo(px - 7, head);
        cr.LineTo(px + 7, head);
        cr.LineTo(px + 7, head + 8);
        cr.LineTo(px, head + TimelineRuler.HandleHeight);
        cr.LineTo(px - 7, head + 8);
        cr.ClosePath();
        cr.Fill();
    }

    private static void RoundedRect(Cairo.Context cr, double x, double y, double w, double h, double r)
    {
        r = Math.Min(r, Math.Min(w, h) / 2);
        cr.NewSubPath();
        cr.Arc(x + w - r, y + r, r, -Math.PI / 2, 0);
        cr.Arc(x + w - r, y + h - r, r, 0, Math.PI / 2);
        cr.Arc(x + r, y + h - r, r, Math.PI / 2, Math.PI);
        cr.Arc(x + r, y + r, r, Math.PI, 3 * Math.PI / 2);
        cr.ClosePath();
    }
}
