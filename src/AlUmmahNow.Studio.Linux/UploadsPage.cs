using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Services;
using AlUmmahNow.Studio.Upload;

namespace AlUmmahNow.Studio.Linux;

/// <summary>The upload queue with a watched folder.</summary>
public sealed class UploadsPage
{
    private readonly Localizer _l;
    private readonly UploadQueue _queue;
    private readonly Gtk.Box _rows = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
    private int _dirty = 1;

    public UploadsPage(Localizer l, UploadQueue queue, Func<Task<string?>> pickFolder, Action<string> watchFolder)
    {
        _l = l;
        _queue = queue;
        Root = Ui.Page();
        var bar = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        bar.Append(Ui.Button(l["studio.upload.watch"], async () =>
        {
            var path = await pickFolder();
            if (!string.IsNullOrEmpty(path)) watchFolder(path);
        }));
        Root.Append(bar);
        Root.Append(_rows);
        queue.Changed += (_, _) => Interlocked.Exchange(ref _dirty, 1);
    }

    public Gtk.Box Root { get; }

    public void Tick()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
        Ui.Clear(_rows);
        foreach (var item in _queue.Snapshot())
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
            var name = Gtk.Label.New(Path.GetFileName(item.FilePath));
            name.SetHalign(Gtk.Align.Start);
            name.SetSizeRequest(320, -1);
            var progress = Gtk.ProgressBar.New();
            progress.SetFraction(item.Fraction);
            progress.SetHexpand(true);
            progress.SetValign(Gtk.Align.Center);
            row.Append(name);
            row.Append(progress);
            row.Append(Gtk.Label.New(_l["studio.status." + item.Status.ToString().ToLowerInvariant()]));
            _rows.Append(row);
        }
    }
}
