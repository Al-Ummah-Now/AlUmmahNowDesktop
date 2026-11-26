using AlUmmahNow.App.I18n;
using AlUmmahNow.Studio.Encode;

namespace AlUmmahNow.Studio.Linux;

/// <summary>Encode queue (Lantern Glass card): jobs with a tile, progress and which encoder ran.</summary>
public sealed class EncodePage
{
    private readonly Localizer _l;
    private readonly EncodeQueue _encode;
    private readonly Gtk.Box _rows = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
    private readonly Gtk.Label _empty;
    private int _dirty = 1;

    public EncodePage(Localizer localizer, EncodeQueue encode)
    {
        _l = localizer;
        _encode = encode;
        _empty = Ui.Label(localizer["studio.encode.empty"], dim: true);
        Root = Gtk.Box.New(Gtk.Orientation.Vertical, 18);
        Root.SetMarginStart(24);
        Root.SetMarginEnd(24);
        Root.SetMarginTop(18);
        Root.SetMarginBottom(24);
        var body = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        body.Append(Ui.CardTitle(localizer["studio.tab.encode"]));
        // Never an empty page: say so while nothing is encoding.
        _empty.SetVisible(true);
        body.Append(_empty);
        body.Append(_rows);
        // The card keeps its rows' own height: it never spreads the jobs over the page.
        _rows.SetValign(Gtk.Align.Start);
        var card = Ui.Card(body);
        card.SetValign(Gtk.Align.Start);
        card.SetVexpand(false);
        Root.Append(card);
        encode.Changed += (_, _) => Interlocked.Exchange(ref _dirty, 1);
    }

    public Gtk.Box Root { get; }

    /// <summary>While running: the percent done, then the time left once there is an estimate (joined by Localizer.Join).</summary>
    private string? Pace(EncodeJob job) => job.Status != EncodeStatus.Running ? null
        : _l.Join(_l.Percent(job.Progress), job.Remaining is { } left ? _l.Format("studio.encode.remaining", _l.Duration(Math.Ceiling(left.TotalSeconds))) : null);

    public void Tick()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
        Ui.Clear(_rows);
        var jobs = _encode.Jobs.ToList();
        _empty.SetVisible(jobs.Count == 0);
        foreach (var job in jobs)
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
            row.Append(Ui.FileTile(80, 45));
            var text = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
            text.SetHexpand(true);
            text.SetValign(Gtk.Align.Center);
            var name = Ui.Label(_l.JoinFileName(Path.GetFileName(job.Request.Source), _l["studio.preset." + job.Request.Preset.Key]));
            name.AddCssClass("heading");
            var bar = Gtk.ProgressBar.New();
            bar.SetFraction(job.Progress);
            bar.SetHexpand(true);
            var status = job.Status switch
            {
                EncodeStatus.Running => _l["studio.encode.running"],
                EncodeStatus.Completed => _l["studio.status.completed"],
                EncodeStatus.Failed => _l["studio.status.failed"],
                EncodeStatus.Canceled => _l["studio.encode.canceled"],
                _ => _l["studio.status.queued"],
            };
            var encoder = job.UsedEncoder is { } e ? _l[e.Kind == EncoderKind.Hardware ? "studio.encode.hardware" : "studio.encode.software"] + " (" + e.Name + ")" : "";
            text.Append(name);
            text.Append(bar);
            var line = Ui.Label(_l.Join(status, Pace(job), encoder), dim: true);
            line.AddCssClass("caption");
            text.Append(line);
            row.Append(text);
            _rows.Append(row);
        }
    }
}
