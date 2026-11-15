using System.ComponentModel;
using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Timeline;
using AlUmmahNow.Studio.Upload;

namespace AlUmmahNow.Studio.Linux;

/// <summary>
/// The editor (E19 19.5): add videos, cut them on one video track, export an MP4 with GStreamer Editing Services and
/// queue it for upload. The timeline is drawn with time running left to right in both languages (only the toolbars,
/// labels and clip list follow Arabic's direction); the clip list under it gives the same clips to the
/// keyboard and to screen readers. Titles and lower thirds sit on their own lane under the clips, are previewed over
/// the picture at the playhead, and are drawn into the export with the same painter.
/// </summary>
public sealed class TimelinePage
{
    private const double RulerHeight = 22;
    private const double TrackTop = 30;
    private const double TrackHeight = 64;
    private const double TitleTop = TrackTop + TrackHeight + 8;
    private const double TitleHeight = 34;
    private const int PreviewWidth = 384;
    private const int PreviewHeight = 216;

    private readonly Localizer _l;
    private readonly EditorViewModel _vm;
    private readonly UploadsViewModel _uploads;
    private readonly Gtk.Window _window;
    private readonly Gtk.DrawingArea _area = Gtk.DrawingArea.New();
    private readonly Gtk.ListBox _clips = Gtk.ListBox.New();
    private readonly Gtk.ListBox _titles = Gtk.ListBox.New();
    private readonly Gtk.Label _titlesEmpty;
    private readonly Gtk.DrawingArea _preview = Gtk.DrawingArea.New();
    private readonly Gtk.Entry _arabic = Gtk.Entry.New();
    private readonly Gtk.Entry _english = Gtk.Entry.New();
    private readonly Gtk.DropDown _kind;
    private readonly CairoTitlePainter _painter;
    private readonly string _frameFile = Path.Combine(Path.GetTempPath(), "aun-editor-frame-" + Environment.ProcessId + ".png");
    private GdkPixbuf.Pixbuf? _frame;
    private (string Project, long Ms)? _frameKey;
    private (string Project, long Ms)? _frameWanted;
    private bool _frameBusy;
    private uint _frameTimer;
    private string? _shownTitleId;
    private readonly Gtk.Label _empty;
    private readonly Gtk.Label _position;
    private readonly Gtk.Label _error;
    private readonly Gtk.Label _done;
    private readonly Gtk.Box _exportRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    private readonly Gtk.ProgressBar _progress = Gtk.ProgressBar.New();
    private readonly Gtk.Box _doneRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    private readonly Dictionary<string, Gtk.Button> _buttons = new();
    private bool _rendering;

    public TimelinePage(Localizer l, EditorViewModel vm, UploadsViewModel uploads, Gtk.Window window, CairoTitlePainter painter)
    {
        _l = l;
        _vm = vm;
        _uploads = uploads;
        _window = window;
        _painter = painter;
        _kind = Gtk.DropDown.NewFromStrings(new[] { l["studio.editor.titleKind.title"], l["studio.editor.titleKind.lowerThird"] });
        Root = Ui.Page(12);

        var project = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var title = Ui.Heading(l["studio.editor.timeline"]);
        title.SetHexpand(true);
        project.Append(title);
        project.Append(Add("new", Ui.Button(l["studio.editor.new"], _vm.New)));
        project.Append(Add("open", Ui.Button(l["studio.editor.open"], () => _ = OpenAsync())));
        project.Append(Add("save", Ui.Button(l["studio.editor.save"], () => _ = SaveAsync())));
        project.Append(Add("export", Ui.Button(l["studio.editor.export"], () => _ = ExportAsync())));
        project.Append(Add("add", Ui.Button(l["studio.editor.addVideos"], () => _ = PickVideosAsync(), "suggested-action")));
        Root.Append(project);

        // Edit tools wrap onto a second line rather than run off a narrow window.
        var tools = Gtk.FlowBox.New();
        tools.SetSelectionMode(Gtk.SelectionMode.None);
        tools.SetColumnSpacing(8);
        tools.SetRowSpacing(8);
        tools.SetMaxChildrenPerLine(12);
        tools.SetHomogeneous(false);
        tools.Append(Add("split", Ui.Button(l["studio.editor.split"], () => _vm.Split())));
        tools.Append(Add("trimStart", Ui.Button(l["studio.editor.trimStart"], () => _vm.TrimStartToPlayhead())));
        tools.Append(Add("trimEnd", Ui.Button(l["studio.editor.trimEnd"], () => _vm.TrimEndToPlayhead())));
        tools.Append(Add("join", Ui.Button(l["studio.editor.join"], () => _vm.JoinWithNext())));
        tools.Append(Add("earlier", Ui.Button(l["studio.editor.moveEarlier"], () => _vm.MoveBy(-1))));
        tools.Append(Add("later", Ui.Button(l["studio.editor.moveLater"], () => _vm.MoveBy(1))));
        tools.Append(Add("delete", Ui.Button(l["studio.editor.delete"], () => _vm.Delete(), "destructive-action")));
        tools.Append(Add("undo", Ui.Button(l["studio.editor.undo"], _vm.Undo)));
        tools.Append(Add("redo", Ui.Button(l["studio.editor.redo"], _vm.Redo)));
        Root.Append(tools);

        _position = Ui.Label("", dim: true);
        Root.Append(_position);
        _error = Ui.Label("");
        _error.AddCssClass("error");
        Ui.Status(_error, null);
        Root.Append(_error);

        _progress.SetHexpand(true);
        _progress.SetValign(Gtk.Align.Center);
        _exportRow.Append(_progress);
        _exportRow.Append(Ui.Button(l["studio.editor.cancelExport"], _vm.CancelExport));
        _exportRow.SetVisible(false);
        Root.Append(_exportRow);

        _done = Ui.Label("");
        _done.SetHexpand(true);
        _doneRow.Append(_done);
        _doneRow.Append(Ui.Button(l["studio.editor.upload"], QueueUpload, "suggested-action"));
        _doneRow.SetVisible(false);
        Root.Append(_doneRow);

        // The picture at the playhead with its titles, and the title editor beside it.
        var stage = Gtk.Box.New(Gtk.Orientation.Horizontal, 16);
        var previewBox = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        previewBox.Append(Ui.Label(l["studio.editor.preview"], dim: true));
        _preview.SetContentWidth(PreviewWidth);
        _preview.SetContentHeight(PreviewHeight);
        _preview.SetDrawFunc((_, cr, w, h) => DrawPreview(cr, w, h));
        _preview.SetTooltipText(l["studio.editor.previewName"]);
        previewBox.Append(_preview);
        stage.Append(previewBox);

        var panel = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        panel.SetHexpand(true);
        panel.Append(Ui.Heading(l["studio.editor.titles"]));
        _kind.SetSelected(1);
        _kind.OnNotify += (_, e) =>
        {
            if (_rendering || e.Pspec.GetName() != "selected" || _vm.SelectedTitle == null) return;
            _vm.SetTitleKind(Kind());
        };
        panel.Append(Ui.Labelled(l["studio.editor.titleKind"], _kind));
        // Each box keeps its language's direction whatever the interface language is.
        // The entry's text child follows the interface direction, so align each box's text to its language's start.
        _arabic.SetDirection(Gtk.TextDirection.Rtl);
        _arabic.SetAlignment(l.IsRtl ? 0f : 1f);
        _arabic.SetHexpand(true);
        _english.SetDirection(Gtk.TextDirection.Ltr);
        _english.SetAlignment(l.IsRtl ? 1f : 0f);
        _english.SetHexpand(true);
        _arabic.OnActivate += (_, _) => SubmitTitle();
        _english.OnActivate += (_, _) => SubmitTitle();
        panel.Append(Ui.Labelled(l["studio.editor.titleArabic"], _arabic));
        panel.Append(Ui.Labelled(l["studio.editor.titleEnglish"], _english));
        var titleTools = Gtk.FlowBox.New();
        titleTools.SetSelectionMode(Gtk.SelectionMode.None);
        titleTools.SetColumnSpacing(8);
        titleTools.SetRowSpacing(8);
        titleTools.SetMaxChildrenPerLine(8);
        titleTools.Append(Add("addTitle", Ui.Button(l["studio.editor.addTitle"], SubmitTitle, "suggested-action")));
        titleTools.Append(Add("saveTitle", Ui.Button(l["studio.editor.saveTitle"], SubmitTitle, "suggested-action")));
        titleTools.Append(Add("newTitle", Ui.Button(l["studio.editor.newTitle"], () => _vm.SelectTitle(null))));
        titleTools.Append(Add("titleStart", Ui.Button(l["studio.editor.titleStart"], () => _vm.TitleStartToPlayhead())));
        titleTools.Append(Add("titleEnd", Ui.Button(l["studio.editor.titleEnd"], () => _vm.TitleEndToPlayhead())));
        titleTools.Append(Add("titleMove", Ui.Button(l["studio.editor.titleMove"], () => _vm.MoveTitleToPlayhead())));
        titleTools.Append(Add("removeTitle", Ui.Button(l["studio.editor.removeTitle"], () => _vm.RemoveTitle(), "destructive-action")));
        panel.Append(titleTools);
        panel.Append(Ui.Label(l["studio.editor.titleBrand"], dim: true));
        stage.Append(panel);
        Root.Append(stage);

        var frame = Gtk.Frame.New(null);
        _area.SetContentHeight((int)(TitleTop + TitleHeight + 12));
        _area.SetHexpand(true);
        _area.SetFocusable(true);
        _area.SetDrawFunc((_, cr, w, h) => Draw(cr, w, h));
        frame.SetChild(_area);
        Root.Append(frame);

        var click = Gtk.GestureClick.New();
        click.OnPressed += (_, a) =>
        {
            _area.GrabFocus();
            var ms = Viewport().ToMs(a.X);
            if (a.Y >= TitleTop - 4) _vm.ClickTitleAt(ms);
            else _vm.ClickAt(ms);
        };
        _area.AddController(click);
        var drag = Gtk.GestureDrag.New();
        double startX = 0;
        string? dragged = null;
        string? draggedTitle = null;
        var grip = TitleGrip.None;
        drag.OnDragBegin += (_, a) =>
        {
            startX = a.StartX;
            var v = Viewport();
            var ms = v.ToMs(a.StartX);
            dragged = null;
            draggedTitle = null;
            grip = TitleGrip.None;
            if (a.StartY >= TitleTop - 4)
            {
                // The selected title's edges win, so a short title can still be retimed.
                var title = _vm.SelectedTitle is { } s && TitleHit.At(v, s, a.StartX) != TitleGrip.None ? s : _vm.Titles.FirstOrDefault(t => TitleHit.At(v, t, a.StartX) != TitleGrip.None);
                if (title != null)
                {
                    draggedTitle = title.Id;
                    grip = TitleHit.At(v, title, a.StartX);
                }
            }
            else
            {
                dragged = _vm.ClipAt(ms)?.ClipId;
            }
        };
        drag.OnDragEnd += (_, a) =>
        {
            // A drag of a few pixels is a click; a longer one moves the clip or title by that much time.
            if (Math.Abs(a.OffsetX) < 6) return;
            var v = Viewport();
            var delta = v.ToMs(startX + a.OffsetX) - v.ToMs(startX);
            if (draggedTitle != null)
            {
                if (_vm.Titles.FirstOrDefault(t => t.Id == draggedTitle) is not { } t) return;
                _vm.SelectTitle(t.Id);
                if (grip == TitleGrip.Start) _vm.RetimeTitle(t.StartMs + delta, t.EndMs);
                else if (grip == TitleGrip.End) _vm.RetimeTitle(t.StartMs, t.EndMs + delta);
                else _vm.MoveTitleTo(t.StartMs + delta);
                return;
            }

            if (dragged == null || _vm.Selected is not { } c || c.ClipId != dragged) return;
            _vm.MoveTo(c.TimelineStartMs + delta);
        };
        _area.AddController(drag);
        var keys = Gtk.EventControllerKey.New();
        keys.OnKeyPressed += (_, a) =>
        {
            var step = (a.State & Gdk.ModifierType.ShiftMask) != 0 ? 1000 : 100;
            // Time runs left to right in both languages, so Right always moves forward.
            var forward = Gdk.Constants.KEY_Right;
            var back = Gdk.Constants.KEY_Left;
            if (a.Keyval == forward) _vm.Seek(_vm.PlayheadMs + step);
            else if (a.Keyval == back) _vm.Seek(_vm.PlayheadMs - step);
            else if (a.Keyval == Gdk.Constants.KEY_Delete)
            {
                if (_vm.SelectedTitle != null) _vm.RemoveTitle();
                else _vm.Delete();
            }
            else if (a.Keyval == Gdk.Constants.KEY_s && (a.State & Gdk.ModifierType.ControlMask) == 0) _vm.Split();
            else return false;
            return true;
        };
        _area.AddController(keys);

        var shortcuts = Gtk.ShortcutController.New();
        shortcuts.SetScope(Gtk.ShortcutScope.Managed);
        shortcuts.AddShortcut(Gtk.Shortcut.New(Gtk.ShortcutTrigger.ParseString("<Control>z"), Gtk.CallbackAction.New((_, _) => { _vm.Undo(); return true; })));
        shortcuts.AddShortcut(Gtk.Shortcut.New(Gtk.ShortcutTrigger.ParseString("<Control><Shift>z"), Gtk.CallbackAction.New((_, _) => { _vm.Redo(); return true; })));
        Root.AddController(shortcuts);

        _empty = Ui.Label(l["studio.editor.empty"], dim: true);
        Root.Append(_empty);
        Root.Append(Ui.Label(l["studio.editor.hint"], dim: true));

        // Clips and titles side by side, each a list the keyboard and screen readers can use.
        var lists = Gtk.Box.New(Gtk.Orientation.Horizontal, 16);
        lists.SetHomogeneous(true);
        lists.SetVexpand(true);
        var clipsColumn = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        clipsColumn.Append(Ui.Heading(l["studio.editor.clips"]));
        _clips.AddCssClass("boxed-list");
        _clips.SetSelectionMode(Gtk.SelectionMode.Single);
        _clips.OnRowSelected += (_, a) =>
        {
            if (_rendering || a.Row is not { } row) return;
            var clip = _vm.Clips.ElementAtOrDefault(row.GetIndex());
            if (clip == null) return;
            _vm.Seek(clip.TimelineStartMs);
            _vm.Select(clip.ClipId);
        };
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetChild(_clips);
        scroll.SetVexpand(true);
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        clipsColumn.Append(scroll);
        lists.Append(clipsColumn);

        var titlesColumn = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        titlesColumn.Append(Ui.Heading(l["studio.editor.titles"]));
        _titlesEmpty = Ui.Label(l["studio.editor.titlesEmpty"], dim: true);
        titlesColumn.Append(_titlesEmpty);
        _titles.AddCssClass("boxed-list");
        _titles.SetSelectionMode(Gtk.SelectionMode.Single);
        _titles.OnRowSelected += (_, a) =>
        {
            if (_rendering || a.Row is not { } row) return;
            var title = _vm.Titles.ElementAtOrDefault(row.GetIndex());
            if (title == null) return;
            _vm.Seek(title.StartMs);
            _vm.SelectTitle(title.Id);
        };
        var titlesScroll = Gtk.ScrolledWindow.New();
        titlesScroll.SetChild(_titles);
        titlesScroll.SetVexpand(true);
        titlesScroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        titlesColumn.Append(titlesScroll);
        lists.Append(titlesColumn);
        Root.Append(lists);

        _vm.PropertyChanged += OnChanged;
        Render();
    }

    public Gtk.Box Root { get; }

    /// <summary>CI scene: two videos, the first split in the middle and its second half selected.</summary>
    public async Task RunSceneAsync(IReadOnlyList<string> files)
    {
        await _vm.AddVideosAsync(files);
        if (_vm.Clips.FirstOrDefault() is { } first)
        {
            _vm.ClickAt(first.TimelineStartMs + (first.TimelineDurationMs / 2));
            _vm.Split();
            _vm.ClickAt(_vm.PlayheadMs + 400);
        }
    }

    /// <summary>CI scene: the cut, then a bilingual lower third and a centred title; the playhead on the lower third.</summary>
    public async Task RunTitlesSceneAsync(IReadOnlyList<string> files)
    {
        await RunSceneAsync(files);
        foreach (var (ms, kind, ar, en) in EditorScenes.Titles)
        {
            _vm.Seek(ms);
            _vm.AddTitle(kind, ar, en);
        }

        _vm.Seek(EditorScenes.PreviewMs);
        _vm.SelectTitle(_vm.TitlesOnScreen.FirstOrDefault()?.Id);
    }

    /// <summary>The picture at <paramref name="ms"/> of a file (CI checks the export), as a PNG.</summary>
    public static Task<string> FrameOfAsync(string video, long durationMs, long ms, int width, int height)
    {
        var timeline = new FlattenedTimeline(durationMs, new[] { new FlatTrack("v", "video", false, new[] { new FlatClip("c", "s", video, 0, durationMs, 0, 1, 1) }) });
        var png = Path.Combine(Path.GetTempPath(), "aun-export-frame-" + Environment.ProcessId + ".png");
        return Task.Run(() =>
        {
            GesTimelineRenderer.SaveFrame(timeline, Array.Empty<TitleOverlay>(), ms, width, height, png, CancellationToken.None);
            return png;
        });
    }

    private Gtk.Button Add(string name, Gtk.Button button)
    {
        _buttons[name] = button;
        return button;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void Render()
    {
        _rendering = true;
        try
        {
            var clips = _vm.Clips;
            var busy = _vm.IsBusy || _vm.IsExporting;
            _buttons["split"].SetSensitive(!busy && _vm.CanSplit);
            _buttons["trimStart"].SetSensitive(!busy && _vm.CanTrimStart);
            _buttons["trimEnd"].SetSensitive(!busy && _vm.CanTrimEnd);
            _buttons["join"].SetSensitive(!busy && _vm.CanJoin);
            var index = _vm.Target is { } t ? clips.ToList().FindIndex(c => c.ClipId == t.ClipId) : -1;
            _buttons["earlier"].SetSensitive(!busy && index > 0);
            _buttons["later"].SetSensitive(!busy && index >= 0 && index < clips.Count - 1);
            _buttons["delete"].SetSensitive(!busy && _vm.Target != null);
            _buttons["undo"].SetSensitive(!busy && _vm.CanUndo);
            _buttons["redo"].SetSensitive(!busy && _vm.CanRedo);
            _buttons["export"].SetSensitive(_vm.CanExport);
            _buttons["save"].SetSensitive(!busy && clips.Count > 0);
            _buttons["add"].SetSensitive(!busy);
            _buttons["open"].SetSensitive(!busy);
            _buttons["new"].SetSensitive(!busy && (clips.Count > 0 || _vm.CanUndo));
            var title = _vm.SelectedTitle;
            _buttons["addTitle"].SetVisible(title == null);
            _buttons["addTitle"].SetSensitive(!busy && _vm.CanAddTitle);
            _buttons["saveTitle"].SetVisible(title != null);
            _buttons["saveTitle"].SetSensitive(!busy);
            foreach (var name in new[] { "newTitle", "titleStart", "titleEnd", "titleMove", "removeTitle" }) _buttons[name].SetSensitive(!busy && title != null);
            if (title?.Id != _shownTitleId)
            {
                // Selecting a title shows its text; leaving it clears the boxes for the next one.
                _shownTitleId = title?.Id;
                _arabic.SetText(title?.In("ar") ?? "");
                _english.SetText(title?.In("en") ?? "");
            }

            if (title != null) _kind.SetSelected(TitleTemplates.KindOf(title) == TitleKind.Title ? 0u : 1u);

            _empty.SetVisible(clips.Count == 0);
            _position.SetText(_l.Format("studio.editor.playhead", Localizer.IsolateLtr(Time(_vm.PlayheadMs))) + " · " +
                _l.Format("studio.editor.length", Localizer.IsolateLtr(Time(_vm.Timeline.DurationMs))));
            Ui.Status(_error, _vm.ErrorKey is { } key ? (_vm.ErrorDetail is { } d ? _l.Format(key, Localizer.Isolate(d)) : _l[key]) : null);
            _exportRow.SetVisible(_vm.IsExporting);
            _progress.SetFraction(_vm.ExportProgress);
            _progress.SetText(_l.Format("studio.editor.exporting", _l.Number((long)Math.Round(_vm.ExportProgress * 100))));
            _progress.SetShowText(true);
            _doneRow.SetVisible(_vm.ExportedPath != null && !_vm.IsExporting);
            if (_vm.ExportedPath is { } exported) _done.SetText(_l.Format("studio.editor.exported", Localizer.Isolate(Path.GetFileName(exported))));

            _clips.RemoveAll();
            foreach (var c in clips)
            {
                var label = Ui.Label(_l.LineStart + _l.Format("studio.editor.clip", Localizer.Isolate(Name(c)), Localizer.IsolateLtr(Time(c.TimelineStartMs)), Localizer.IsolateLtr(Time(EditorViewModel.End(c)))));
                label.AddCssClass("aun-pad");
                _clips.Append(label);
            }

            var selected = _vm.Selected is { } s ? clips.ToList().FindIndex(c => c.ClipId == s.ClipId) : -1;
            if (selected >= 0) _clips.SelectRow(_clips.GetRowAtIndex(selected));
            else _clips.UnselectAll();

            var titles = _vm.Titles;
            _titlesEmpty.SetVisible(titles.Count == 0);
            _titles.RemoveAll();
            foreach (var item in titles)
            {
                var kind = _l[TitleTemplates.KindOf(item) == TitleKind.Title ? "studio.editor.titleKind.title" : "studio.editor.titleKind.lowerThird"];
                var label = Ui.Label(_l.LineStart + _l.Format("studio.editor.titleItem", Localizer.Isolate(TitleTemplates.Label(item, _l.Language)), kind, Localizer.IsolateLtr(Time(item.StartMs)), Localizer.IsolateLtr(Time(item.EndMs))));
                label.AddCssClass("aun-pad");
                _titles.Append(label);
            }

            var selectedTitle = title != null ? titles.ToList().FindIndex(t => t.Id == title.Id) : -1;
            if (selectedTitle >= 0) _titles.SelectRow(_titles.GetRowAtIndex(selectedTitle));
            else _titles.UnselectAll();
        }
        finally
        {
            _rendering = false;
        }

        _area.QueueDraw();
        _preview.QueueDraw();
        RequestFrame();
    }

    private TitleKind Kind() => _kind.GetSelected() == 0 ? TitleKind.Title : TitleKind.LowerThird;

    private void SubmitTitle()
    {
        if (_vm.SelectedTitle != null) _vm.SetTitleText(_arabic.GetText(), _english.GetText());
        else if (_vm.AddTitle(Kind(), _arabic.GetText(), _english.GetText())) _shownTitleId = _vm.SelectedTitleId;
    }

    /// <summary>Asks GES for the picture at the playhead, at most one at a time; the newest request wins.</summary>
    private void RequestFrame()
    {
        if (_vm.Clips.Count == 0)
        {
            _frame = null;
            _frameKey = null;
            return;
        }

        var key = (_vm.Project, _vm.PlayheadMs);
        if (_frameKey == key || _frameWanted == key) return;
        _frameWanted = key;
        if (_frameTimer != 0) GLib.Functions.SourceRemove(_frameTimer);
        _frameTimer = GLib.Functions.TimeoutAdd(0, 150, () =>
        {
            _frameTimer = 0;
            _ = GrabFrameAsync();
            return false;
        });
    }

    private async Task GrabFrameAsync()
    {
        if (_frameBusy || _frameWanted is not { } key) return;
        _frameBusy = true;
        try
        {
            var settings = _vm.Settings;
            var height = PreviewHeight * 2;
            var width = Math.Max(2, (int)Math.Round(height * (double)settings.Width / Math.Max(1, settings.Height)));
            var timeline = _vm.Timeline;
            await Task.Run(() => GesTimelineRenderer.SaveFrame(timeline, Array.Empty<TitleOverlay>(), key.Ms, width, height, _frameFile, CancellationToken.None));
            _frame = GdkPixbuf.Pixbuf.NewFromFile(_frameFile);
            _frameKey = key;
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is GLib.GException || ex is IOException || ex is DllNotFoundException || ex is NotSupportedException)
        {
            // The preview still shows the titles, over black.
            Log.For("editor").Warn("no preview frame", ex);
            _frame = null;
            _frameKey = key;
        }
        finally
        {
            _frameBusy = false;
        }

        _preview.QueueDraw();
        if (_frameWanted is { } wanted && wanted != _frameKey) _ = GrabFrameAsync();
    }

    /// <summary>The picture at the playhead, letterboxed to the project's shape, with the titles shown there.</summary>
    private void DrawPreview(Cairo.Context cr, int w, int h)
    {
        cr.SetSourceRgb(0, 0, 0);
        cr.Rectangle(0, 0, w, h);
        cr.Fill();
        var settings = _vm.Settings;
        var scale = Math.Min(w / (double)Math.Max(1, settings.Width), h / (double)Math.Max(1, settings.Height));
        var fw = settings.Width * scale;
        var fh = settings.Height * scale;
        var x = (w - fw) / 2;
        var y = (h - fh) / 2;
        if (_frame != null && _vm.Clips.Count > 0)
        {
            cr.Save();
            cr.Translate(x, y);
            cr.Scale(fw / _frame.Width, fh / _frame.Height);
            Gdk.Functions.CairoSetSourcePixbuf(cr, _frame, 0, 0);
            cr.Paint();
            cr.Restore();
        }

        cr.Save();
        cr.Rectangle(x, y, fw, fh);
        cr.Clip();
        cr.Translate(x, y);
        cr.Scale(scale, scale);
        var decor = _vm.Decor;
        foreach (var title in _vm.TitlesOnScreen) _painter.Paint(cr, title, decor, settings.Width, settings.Height);
        cr.Restore();
    }

    private TimelineViewport Viewport() => TimelineViewport.Fit(_vm.Timeline.DurationMs, Math.Max(1, _area.GetWidth()), EditorTime.TimeIsRtl);

    private void Draw(Cairo.Context cr, int width, int height)
    {
        var dark = Adw.StyleManager.GetDefault().GetDark();
        var v = TimelineViewport.Fit(_vm.Timeline.DurationMs, width, EditorTime.TimeIsRtl);
        var fg = dark ? (0.92, 0.92, 0.92) : (0.12, 0.12, 0.12);

        // Ruler: a tick every second or every five, labelled every 5 or 10.
        var duration = Math.Max(1000, _vm.Timeline.DurationMs);
        var step = duration > 120_000 ? 10_000 : duration > 30_000 ? 5_000 : 1_000;
        cr.SetSourceRgba(fg.Item1, fg.Item2, fg.Item3, 0.45);
        for (long ms = 0; ms <= duration; ms += step)
        {
            var x = v.ToX(ms);
            var major = ms % (step * 5) == 0;
            cr.Rectangle(x, major ? 4 : 12, 1, major ? RulerHeight - 4 : RulerHeight - 12);
            cr.Fill();
            if (major) Text(cr, Time(ms), x + 4, 2, fg, 0.7, alignEnd: false, small: true);
        }

        var names = _vm.SourceNames;
        foreach (var c in _vm.Clips)
        {
            var a = v.ToX(c.TimelineStartMs);
            var b = v.ToX(EditorViewModel.End(c));
            var left = Math.Min(a, b) + 1;
            var w = Math.Max(2, Math.Abs(b - a) - 2);
            var selected = c.ClipId == _vm.SelectedClipId;
            if (dark) cr.SetSourceRgb(0.05, 0.33, 0.43);
            else cr.SetSourceRgb(0.04, 0.35, 0.47);
            RoundRect(cr, left, TrackTop, w, TrackHeight, 6);
            cr.Fill();
            if (selected)
            {
                cr.SetSourceRgb(1, 0.63, 0.22);
                cr.LineWidth = 3;
                RoundRect(cr, left + 1.5, TrackTop + 1.5, w - 3, TrackHeight - 3, 5);
                cr.Stroke();
            }

            cr.Save();
            cr.Rectangle(left + 6, TrackTop, Math.Max(0, w - 12), TrackHeight);
            cr.Clip();
            // Labels start at the clip's start (its left edge); Arabic names still shape right to left inside.
            var start = left + 8;
            Text(cr, Name(c), start, TrackTop + 8, (1, 1, 1), 1, alignEnd: false);
            Text(cr, Time(c.TimelineDurationMs), start, TrackTop + 34, (1, 1, 1), 0.8, alignEnd: false, small: true);
            cr.Restore();
        }

        // The title lane: time still runs left to right; each label shapes in its own direction inside its block.
        foreach (var t in _vm.Titles)
        {
            var a = v.ToX(t.StartMs);
            var b = v.ToX(t.EndMs);
            var left = Math.Min(a, b) + 1;
            var w = Math.Max(2, Math.Abs(b - a) - 2);
            cr.SetSourceRgb(0.42, 0.33, 0.62);
            RoundRect(cr, left, TitleTop, w, TitleHeight, 5);
            cr.Fill();
            if (t.Id == _vm.SelectedTitleId)
            {
                cr.SetSourceRgb(1, 0.63, 0.22);
                cr.LineWidth = 3;
                RoundRect(cr, left + 1.5, TitleTop + 1.5, w - 3, TitleHeight - 3, 4);
                cr.Stroke();
            }

            cr.Save();
            cr.Rectangle(left + 6, TitleTop, Math.Max(0, w - 12), TitleHeight);
            cr.Clip();
            Text(cr, TitleTemplates.Label(t, _l.Language), left + 8, TitleTop + 8, (1, 1, 1), 1, alignEnd: false, small: true);
            cr.Restore();
        }

        cr.SetSourceRgb(1, 0.63, 0.22);
        var head = v.ToX(_vm.PlayheadMs);
        cr.Rectangle(head - 1, 0, 2, height);
        cr.Fill();
        cr.MoveTo(head - 6, 0);
        cr.LineTo(head + 6, 0);
        cr.LineTo(head, 8);
        cr.ClosePath();
        cr.Fill();
    }

    private void Text(Cairo.Context cr, string text, double x, double y, (double R, double G, double B) color, double alpha, bool alignEnd, bool small = false)
    {
        var layout = _area.CreatePangoLayout(text);
        if (small)
        {
            var font = Pango.FontDescription.FromString("Sans 9");
            layout.SetFontDescription(font);
        }

        layout.GetPixelSize(out var w, out _);
        cr.SetSourceRgba(color.R, color.G, color.B, alpha);
        cr.MoveTo(alignEnd ? x - w : x, y);
        PangoCairo.Functions.ShowLayout(cr, layout);
    }

    private static void RoundRect(Cairo.Context cr, double x, double y, double w, double h, double r)
    {
        r = Math.Min(r, Math.Min(w, h) / 2);
        cr.NewSubPath();
        cr.Arc(x + w - r, y + r, r, -Math.PI / 2, 0);
        cr.Arc(x + w - r, y + h - r, r, 0, Math.PI / 2);
        cr.Arc(x + r, y + h - r, r, Math.PI / 2, Math.PI);
        cr.Arc(x + r, y + r, r, Math.PI, 3 * Math.PI / 2);
        cr.ClosePath();
    }

    private string Name(FlatClip c) => _vm.SourceNames.TryGetValue(c.SourceId, out var n) ? n : c.SourceId;

    private static string Time(long ms) => EditorTime.Format(ms);

    private async Task PickVideosAsync()
    {
        try
        {
            var dialog = Gtk.FileDialog.New();
            dialog.SetTitle(_l["studio.editor.pickVideos"]);
            var files = await dialog.OpenMultipleAsync(_window);
            if (files == null) return;
            var paths = new List<string>();
            for (uint i = 0; i < files.GetNItems(); i++)
            {
                if (files.GetObject(i) is Gio.File f && f.GetPath() is { } p) paths.Add(p);
            }

            await _vm.AddVideosAsync(paths);
        }
        catch (GLib.GException)
        {
            // dismissed
        }
    }

    private async Task OpenAsync()
    {
        try
        {
            var dialog = Gtk.FileDialog.New();
            dialog.SetTitle(_l["studio.editor.open"]);
            var file = await dialog.OpenAsync(_window);
            if (file?.GetPath() is { } path) await _vm.OpenAsync(path);
        }
        catch (GLib.GException)
        {
            // dismissed
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            var dialog = Gtk.FileDialog.New();
            dialog.SetTitle(_l["studio.editor.save"]);
            dialog.SetInitialName(_l["studio.editor.projectFile"] + EditorTime.ProjectExtension);
            var file = await dialog.SaveAsync(_window);
            if (file?.GetPath() is { } path) await _vm.SaveAsync(path);
        }
        catch (GLib.GException)
        {
            // dismissed
        }
    }

    private async Task ExportAsync()
    {
        try
        {
            var dialog = Gtk.FileDialog.New();
            dialog.SetTitle(_l["studio.editor.export"]);
            dialog.SetInitialName(_l["studio.editor.exportName"] + ".mp4");
            var file = await dialog.SaveAsync(_window);
            if (file?.GetPath() is { } path) await _vm.ExportAsync(path);
        }
        catch (GLib.GException)
        {
            // dismissed
        }
    }

    private void QueueUpload()
    {
        if (_vm.ExportedPath is not { } path) return;
        _uploads.AddFiles(new[] { path });
        Ui.Status(_done, _l["studio.editor.uploadAdded"]);
    }
}
