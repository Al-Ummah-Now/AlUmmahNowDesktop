using System.ComponentModel;
using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Timeline;
using AlUmmahNow.Studio.Upload;

namespace AlUmmahNow.Studio.Linux;

/// <summary>
/// The editor (E19 19.5): add videos, cut them on one video track, export an MP4 with GStreamer Editing Services and
/// queue it for upload. The timeline is drawn (mirrored in Arabic); the clip list under it gives the same clips to the
/// keyboard and to screen readers.
/// </summary>
public sealed class TimelinePage
{
    private const double RulerHeight = 22;
    private const double TrackTop = 30;
    private const double TrackHeight = 64;

    private readonly Localizer _l;
    private readonly EditorViewModel _vm;
    private readonly UploadsViewModel _uploads;
    private readonly Gtk.Window _window;
    private readonly Gtk.DrawingArea _area = Gtk.DrawingArea.New();
    private readonly Gtk.ListBox _clips = Gtk.ListBox.New();
    private readonly Gtk.Label _empty;
    private readonly Gtk.Label _position;
    private readonly Gtk.Label _error;
    private readonly Gtk.Label _done;
    private readonly Gtk.Box _exportRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    private readonly Gtk.ProgressBar _progress = Gtk.ProgressBar.New();
    private readonly Gtk.Box _doneRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    private readonly Dictionary<string, Gtk.Button> _buttons = new();
    private bool _rendering;

    public TimelinePage(Localizer l, EditorViewModel vm, UploadsViewModel uploads, Gtk.Window window)
    {
        _l = l;
        _vm = vm;
        _uploads = uploads;
        _window = window;
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

        var frame = Gtk.Frame.New(null);
        _area.SetContentHeight((int)(TrackTop + TrackHeight + 16));
        _area.SetHexpand(true);
        _area.SetFocusable(true);
        _area.SetDrawFunc((_, cr, w, h) => Draw(cr, w, h));
        frame.SetChild(_area);
        Root.Append(frame);

        var click = Gtk.GestureClick.New();
        click.OnPressed += (_, a) =>
        {
            _area.GrabFocus();
            _vm.ClickAt(Viewport().ToMs(a.X));
        };
        _area.AddController(click);
        var drag = Gtk.GestureDrag.New();
        double startX = 0;
        string? dragged = null;
        drag.OnDragBegin += (_, a) =>
        {
            startX = a.StartX;
            dragged = _vm.ClipAt(Viewport().ToMs(a.StartX))?.ClipId;
        };
        drag.OnDragEnd += (_, a) =>
        {
            // A drag of a few pixels is a click; a longer one moves the clip by that much time.
            if (dragged == null || Math.Abs(a.OffsetX) < 6 || _vm.Selected is not { } c || c.ClipId != dragged) return;
            var v = Viewport();
            _vm.MoveTo(c.TimelineStartMs + (v.ToMs(startX + a.OffsetX) - v.ToMs(startX)));
        };
        _area.AddController(drag);
        var keys = Gtk.EventControllerKey.New();
        keys.OnKeyPressed += (_, a) =>
        {
            var step = (a.State & Gdk.ModifierType.ShiftMask) != 0 ? 1000 : 100;
            var forward = _l.IsRtl ? Gdk.Constants.KEY_Left : Gdk.Constants.KEY_Right;
            var back = _l.IsRtl ? Gdk.Constants.KEY_Right : Gdk.Constants.KEY_Left;
            if (a.Keyval == forward) _vm.Seek(_vm.PlayheadMs + step);
            else if (a.Keyval == back) _vm.Seek(_vm.PlayheadMs - step);
            else if (a.Keyval == Gdk.Constants.KEY_Delete) _vm.Delete();
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

        var clipsHeading = Ui.Heading(l["studio.editor.clips"]);
        Root.Append(clipsHeading);
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
        Root.Append(scroll);

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
                var label = Ui.Label(_l.Format("studio.editor.clip", Localizer.Isolate(Name(c)), Localizer.IsolateLtr(Time(c.TimelineStartMs)), Localizer.IsolateLtr(Time(EditorViewModel.End(c)))));
                label.AddCssClass("aun-pad");
                _clips.Append(label);
            }

            var selected = _vm.Selected is { } s ? clips.ToList().FindIndex(c => c.ClipId == s.ClipId) : -1;
            if (selected >= 0) _clips.SelectRow(_clips.GetRowAtIndex(selected));
            else _clips.UnselectAll();
        }
        finally
        {
            _rendering = false;
        }

        _area.QueueDraw();
    }

    private TimelineViewport Viewport() => TimelineViewport.Fit(_vm.Timeline.DurationMs, Math.Max(1, _area.GetWidth()), _l.IsRtl);

    private void Draw(Cairo.Context cr, int width, int height)
    {
        var dark = Adw.StyleManager.GetDefault().GetDark();
        var v = TimelineViewport.Fit(_vm.Timeline.DurationMs, width, _l.IsRtl);
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
            if (major) Text(cr, Time(ms), x + (_l.IsRtl ? -4 : 4), 2, fg, 0.7, alignEnd: _l.IsRtl, small: true);
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
            var start = _l.IsRtl ? left + w - 8 : left + 8;
            Text(cr, Name(c), start, TrackTop + 8, (1, 1, 1), 1, alignEnd: _l.IsRtl);
            Text(cr, Time(c.TimelineDurationMs), start, TrackTop + 34, (1, 1, 1), 0.8, alignEnd: _l.IsRtl, small: true);
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
