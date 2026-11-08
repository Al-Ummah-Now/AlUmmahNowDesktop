using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Temporary;
using AlUmmahNow.Studio.Encode;
using AlUmmahNow.Studio.Linux;
using AlUmmahNow.Studio.Timeline;
using AlUmmahNow.Studio.Upload;

Adw.Module.Initialize();
Gst.Module.Initialize();
var gstArgs = Array.Empty<string>();
Gst.Functions.Init(ref gstArgs);

var localizer = new Localizer(Environment.GetEnvironmentVariable("AUN_LANG") is { Length: > 0 } l ? l : "ar");
var automation = Environment.GetEnvironmentVariable("AUN_AUTOMATION") ?? "";
var tabName = Environment.GetEnvironmentVariable("AUN_TAB") ?? "uploads";
var exitAfter = int.TryParse(Environment.GetEnvironmentVariable("AUN_EXIT_AFTER"), out var s) ? s : 0;

var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
if (string.IsNullOrEmpty(configHome)) configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
var queue = new UploadQueue(new SimulatedUploadEngine(TimeSpan.FromMilliseconds(250)), Path.Combine(configHome, "alummahnow-studio", "uploads.json"));
var encode = new EncodeQueue(new GStreamerEncodeBackend());

var project = new TimelineProject();
var videoTrack = new Track { Kind = TrackKind.Video };
videoTrack.Clips.Add(new Clip { Source = "intro.mp4", Start = 0, Duration = 12 });
videoTrack.Clips.Add(new Clip { Source = "lecture.mp4", Start = 12, Duration = 40 });
var audioTrack = new Track { Kind = TrackKind.Audio };
audioTrack.Clips.Add(new Clip { Source = "lecture.m4a", Start = 0, Duration = 52 });
project.Tracks.Add(videoTrack);
project.Tracks.Add(audioTrack);

var app = Adw.Application.New("net.alummahnow.Studio", Gio.ApplicationFlags.NonUnique);
var exitCode = 0;

string StatusText(UploadStatus status) => localizer["studio.status." + status.ToString().ToLowerInvariant()];

app.OnActivate += (_, _) =>
{
    var direction = localizer.IsRtl ? Gtk.TextDirection.Rtl : Gtk.TextDirection.Ltr;
    Gtk.Widget.SetDefaultDirection(direction);
    Adw.StyleManager.GetDefault().SetColorScheme(localizer.IsRtl ? Adw.ColorScheme.ForceDark : Adw.ColorScheme.ForceLight);

    var window = Adw.ApplicationWindow.New(app);
    window.SetDefaultSize(1280, 800);
    window.SetTitle(localizer["studio.name"]);

    var stack = Gtk.Stack.New();
    stack.SetVexpand(true);

    // Uploads
    var uploads = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
    uploads.SetMarginStart(20); uploads.SetMarginEnd(20); uploads.SetMarginTop(16);
    var notice = Gtk.Label.New(localizer["studio.upload.simulated"]);
    notice.SetHalign(Gtk.Align.Start);
    notice.AddCssClass("dim-label");
    uploads.Append(notice);
    var uploadRows = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
    uploads.Append(uploadRows);

    void RenderUploads()
    {
        while (uploadRows.GetFirstChild() is { } child) uploadRows.Remove(child);
        foreach (var item in queue.Snapshot())
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
            var name = Gtk.Label.New(Path.GetFileName(item.FilePath));
            name.SetHalign(Gtk.Align.Start);
            name.SetSizeRequest(320, -1);
            var bar = Gtk.ProgressBar.New();
            bar.SetFraction(item.Fraction);
            bar.SetHexpand(true);
            bar.SetValign(Gtk.Align.Center);
            row.Append(name);
            row.Append(bar);
            row.Append(Gtk.Label.New(StatusText(item.Status)));
            uploadRows.Append(row);
        }
    }

    var uploadsDirty = 1;
    queue.Changed += (_, _) => Interlocked.Exchange(ref uploadsDirty, 1);
    stack.AddTitled(uploads, "uploads", localizer["studio.tab.uploads"]);

    // Encode
    var encodePage = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
    encodePage.SetMarginStart(20); encodePage.SetMarginEnd(20); encodePage.SetMarginTop(16);
    var encodeRows = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
    encodePage.Append(encodeRows);

    void RenderEncode()
    {
        while (encodeRows.GetFirstChild() is { } child) encodeRows.Remove(child);
        foreach (var job in encode.Jobs.ToList())
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
            var name = Gtk.Label.New(Path.GetFileName(job.Request.Source) + " · " + localizer["studio.preset." + job.Request.Preset.Key]);
            name.SetHalign(Gtk.Align.Start);
            name.SetSizeRequest(320, -1);
            var bar = Gtk.ProgressBar.New();
            bar.SetFraction(job.Progress);
            bar.SetHexpand(true);
            bar.SetValign(Gtk.Align.Center);
            var status = job.Status switch
            {
                EncodeStatus.Running => localizer["studio.encode.running"],
                EncodeStatus.Completed => localizer["studio.status.completed"],
                EncodeStatus.Failed => localizer["studio.status.failed"],
                EncodeStatus.Canceled => localizer["studio.encode.canceled"],
                _ => localizer["studio.status.queued"],
            };
            var encoder = job.UsedEncoder is { } e ? localizer[e.Kind == EncoderKind.Hardware ? "studio.encode.hardware" : "studio.encode.software"] + " (" + e.Name + ")" : "";
            row.Append(name);
            row.Append(bar);
            row.Append(Gtk.Label.New(status + " " + encoder));
            encodeRows.Append(row);
        }
    }

    var encodeDirty = 1;
    encode.Changed += (_, _) => Interlocked.Exchange(ref encodeDirty, 1);
    stack.AddTitled(encodePage, "encode", localizer["studio.tab.encode"]);

    // Timeline
    var timeline = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
    timeline.SetMarginStart(20); timeline.SetMarginEnd(20); timeline.SetMarginTop(16);
    double playhead = 20;
    const double TrackHeight = 56;
    var area = Gtk.DrawingArea.New();
    area.SetContentHeight(180);
    area.SetVexpand(true);

    TimelineViewport Viewport(int width) => new(Math.Max(1, width / Math.Max(1, project.Duration)), 0, width, localizer.IsRtl);

    area.SetDrawFunc((_, cr, w, h) =>
    {
        var v = Viewport(w);
        for (var t = 0; t < project.Tracks.Count; t++)
        {
            foreach (var clip in project.Tracks[t].Clips)
            {
                var a = v.ToX(clip.Start);
                var b = v.ToX(clip.End);
                if (project.Tracks[t].Kind == TrackKind.Video) cr.SetSourceRgb(0.03, 0.26, 0.35);
                else cr.SetSourceRgb(0.56, 0.42, 0.33);
                cr.Rectangle(Math.Min(a, b) + 1, 10 + (t * (TrackHeight + 8)), Math.Abs(b - a) - 2, TrackHeight);
                cr.Fill();
            }
        }

        cr.SetSourceRgb(1, 0.63, 0.22);
        cr.Rectangle(v.ToX(playhead) - 1, 0, 2, h);
        cr.Fill();
    });

    var drag = Gtk.GestureDrag.New();
    double dragStart = 0;
    drag.OnDragBegin += (_, a) =>
    {
        dragStart = a.StartX;
        playhead = Math.Clamp(Viewport(area.GetWidth()).ToTime(dragStart), 0, project.Duration);
        area.QueueDraw();
    };
    drag.OnDragUpdate += (_, a) =>
    {
        playhead = Math.Clamp(Viewport(area.GetWidth()).ToTime(dragStart + a.OffsetX), 0, project.Duration);
        area.QueueDraw();
    };
    area.AddController(drag);

    var tools = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    var split = Gtk.Button.NewWithLabel(localizer["studio.timeline.split"]);
    split.OnClicked += (_, _) => { foreach (var t in project.Tracks) project.Split(t, playhead); area.QueueDraw(); };
    var ripple = Gtk.Button.NewWithLabel(localizer["studio.timeline.delete"]);
    ripple.OnClicked += (_, _) =>
    {
        foreach (var t in project.Tracks)
        {
            if (t.Clips.FirstOrDefault(c => playhead >= c.Start && playhead < c.End) is { } clip) project.RippleDelete(t, clip);
        }

        playhead = Math.Min(playhead, project.Duration);
        area.QueueDraw();
    };
    tools.Append(split);
    tools.Append(ripple);
    tools.Append(Gtk.Label.New(localizer["studio.timeline.hint"]));
    timeline.Append(tools);
    timeline.Append(area);
    stack.AddTitled(timeline, "timeline", localizer["studio.tab.timeline"]);

    var switcher = Gtk.StackSwitcher.New();
    switcher.SetStack(stack);
    var header = Adw.HeaderBar.New();
    header.SetTitleWidget(switcher);
    var root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    root.Append(header);
    root.Append(stack);
    window.SetContent(root);
    window.Present();
    stack.SetVisibleChildName(tabName);

    GLib.Functions.TimeoutAdd(0, 300, () =>
    {
        if (Interlocked.Exchange(ref uploadsDirty, 0) == 1) RenderUploads();
        if (Interlocked.Exchange(ref encodeDirty, 0) == 1) RenderEncode();
        return true;
    });

    // Folder watch: AUN_WATCH_DIR names the folder until the folder picker lands.
    if (Environment.GetEnvironmentVariable("AUN_WATCH_DIR") is { Length: > 0 } watchDir)
    {
        var watch = new FolderWatch(watchDir);
        GLib.Functions.TimeoutAdd(0, 1000, () =>
        {
            foreach (var (path, size) in watch.Scan()) queue.Enqueue(path, size);
            return true;
        });
    }

    EncodeJob? automationJob = null;
    if (automation.StartsWith("encode:", StringComparison.Ordinal))
    {
        var source = automation.Substring(7);
        automationJob = encode.Add(new EncodeRequest(source, source + ".out.mp4", EncodePreset.Lecture));
    }

    if (exitAfter > 0)
    {
        GLib.Functions.TimeoutAdd(0, (uint)exitAfter * 1000, () =>
        {
            var uploadsDone = queue.Snapshot().Count > 0 && queue.Snapshot().All(i => i.Status == UploadStatus.Completed);
            var encoded = automationJob == null || automationJob.Status == EncodeStatus.Completed;
            var pass = (automation.StartsWith("encode:", StringComparison.Ordinal) ? encoded : uploadsDone || tabName == "timeline") && encoded;
            Console.WriteLine($"AUTOMATION {tabName} uploads={queue.Snapshot().Count} uploadsDone={uploadsDone} encode={automationJob?.Status} encoder={automationJob?.UsedEncoder?.Name} error={automationJob?.Error} RESULT {(pass ? "PASS" : "FAIL")}");
            exitCode = pass ? 0 : 3;
            app.Quit();
            return false;
        });
    }
};

var rc = app.RunWithSynchronizationContext(null);
return rc != 0 ? rc : exitCode;
