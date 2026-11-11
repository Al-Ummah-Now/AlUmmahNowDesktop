using AlUmmahNow.App.I18n;
using AlUmmahNow.Studio.Encode;

namespace AlUmmahNow.Studio.Linux;

/// <summary>Encode queue (spike): jobs, progress and which encoder ran.</summary>
public sealed class EncodePage
{
    private readonly Localizer _l;
    private readonly EncodeQueue _encode;
    private readonly Gtk.Box _rows = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
    private int _dirty = 1;

    public EncodePage(Localizer localizer, EncodeQueue encode)
    {
        _l = localizer;
        _encode = encode;
        Root = Ui.Page();
        Root.Append(_rows);
        encode.Changed += (_, _) => Interlocked.Exchange(ref _dirty, 1);
    }

    public Gtk.Box Root { get; }

    public void Tick()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
        Ui.Clear(_rows);
        foreach (var job in _encode.Jobs.ToList())
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
            var name = Gtk.Label.New(Path.GetFileName(job.Request.Source) + " · " + _l["studio.preset." + job.Request.Preset.Key]);
            name.SetHalign(Gtk.Align.Start);
            name.SetSizeRequest(320, -1);
            var bar = Gtk.ProgressBar.New();
            bar.SetFraction(job.Progress);
            bar.SetHexpand(true);
            bar.SetValign(Gtk.Align.Center);
            var status = job.Status switch
            {
                EncodeStatus.Running => _l["studio.encode.running"],
                EncodeStatus.Completed => _l["studio.status.completed"],
                EncodeStatus.Failed => _l["studio.status.failed"],
                EncodeStatus.Canceled => _l["studio.encode.canceled"],
                _ => _l["studio.status.queued"],
            };
            var encoder = job.UsedEncoder is { } e ? _l[e.Kind == EncoderKind.Hardware ? "studio.encode.hardware" : "studio.encode.software"] + " (" + e.Name + ")" : "";
            row.Append(name);
            row.Append(bar);
            row.Append(Gtk.Label.New(status + " " + encoder));
            _rows.Append(row);
        }
    }
}
