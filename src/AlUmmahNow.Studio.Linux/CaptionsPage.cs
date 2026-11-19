using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Captions;

namespace AlUmmahNow.Studio.Linux;

/// <summary>Captions of a video: drafts with their origin, server or on-device transcription, and the cue editor.</summary>
public sealed class CaptionsPage
{
    private readonly Localizer _l;
    private readonly CaptionsViewModel _vm;
    private readonly Gtk.Window _window;
    private readonly Gtk.Stack _stack = Gtk.Stack.New();
    private readonly Gtk.DropDown _video = Gtk.DropDown.NewFromStrings(Array.Empty<string>());
    private readonly Gtk.Label _status = Ui.Label("", dim: true);
    private readonly Gtk.Label _error = Ui.Label("");
    private readonly Gtk.Label _notice = Ui.Label("");
    private readonly Gtk.Box _drafts = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
    private readonly Gtk.Box _jobs = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
    private readonly Gtk.Label _minutes = Ui.Label("", dim: true);
    private readonly Gtk.DropDown _language;
    private readonly Gtk.Button _server;
    private readonly Gtk.Box _device = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
    private readonly Gtk.DropDown _model = Gtk.DropDown.NewFromStrings(Array.Empty<string>());
    private readonly Gtk.Button _transcribe;
    private readonly Gtk.Button _cancelDevice;
    private readonly Gtk.ProgressBar _progress = Gtk.ProgressBar.New();
    private readonly Gtk.Box _editorHost = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private IReadOnlyList<string> _videoIds = Array.Empty<string>();
    private IReadOnlyList<string> _modelIds = Array.Empty<string>();
    private bool _syncing;

    public CaptionsPage(Localizer l, CaptionsViewModel vm, Gtk.Window window)
    {
        _l = l;
        _vm = vm;
        _window = window;
        _vm.Confirm = ConfirmKeys.For(window, l);
        Root = _stack;

        var scroll = Gtk.ScrolledWindow.New();
        var page = Ui.Page(14);
        scroll.SetChild(page);

        _video.SetHexpand(true);
        _video.OnNotify += (_, e) =>
        {
            if (_syncing || e.Pspec.GetName() != "selected") return;
            var i = (int)_video.GetSelected();
            if (i >= 0 && i < _videoIds.Count) _vm.Video = _vm.Videos.First(v => v.Id == _videoIds[i]);
        };
        page.Append(Ui.Labelled(l["studio.captions.video"], _video));
        page.Append(_status);
        _error.AddCssClass("error");
        page.Append(_error);
        _notice.AddCssClass("success");
        page.Append(_notice);

        page.Append(Ui.Heading(l["studio.captions.drafts"]));
        page.Append(_drafts);

        page.Append(Ui.Heading(l["studio.captions.new"]));
        _language = Gtk.DropDown.NewFromStrings(CaptionsViewModel.Languages.Select(x => l[CaptionsViewModel.LanguageKey(x)]).ToArray());
        _language.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() == "selected") _vm.Language = CaptionsViewModel.Languages[(int)_language.GetSelected()];
        };
        page.Append(Ui.Labelled(l["studio.captions.language"], _language));

        var serverCard = Ui.Card();
        serverCard.Append(Ui.Label(l["studio.captions.server.title"]));
        serverCard.Append(Ui.Label(l["studio.captions.server.body"], dim: true));
        serverCard.Append(_minutes);
        _server = Ui.Button(l["studio.captions.server.start"], () => _ = _vm.StartServerJobAsync(), "suggested-action");
        _server.SetHalign(Gtk.Align.Start);
        serverCard.Append(_server);
        page.Append(serverCard);

        _device.AddCssClass("card");
        _device.AddCssClass("aun-pad");
        _device.Append(Ui.Label(l["studio.captions.device.title"]));
        _device.Append(Ui.Label(l["studio.captions.device.body"], dim: true));
        _model.OnNotify += (_, e) =>
        {
            if (_syncing || e.Pspec.GetName() != "selected") return;
            var i = (int)_model.GetSelected();
            if (i >= 0 && i < _modelIds.Count) _vm.Model = _modelIds[i];
        };
        _device.Append(Ui.Labelled(l["studio.captions.device.model"], _model));
        var deviceActions = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        _transcribe = Ui.Button(l["studio.captions.device.start"], () => _ = PickAndTranscribeAsync());
        _cancelDevice = Ui.Button(l["common.cancel"], () => _vm.CancelOnDevice(), "flat");
        deviceActions.Append(_transcribe);
        deviceActions.Append(_cancelDevice);
        _device.Append(deviceActions);
        _progress.SetShowText(true);
        _device.Append(_progress);
        page.Append(_device);

        page.Append(Ui.Heading(l["studio.captions.jobs"]));
        page.Append(_jobs);

        _stack.AddNamed(scroll, "list");
        _stack.AddNamed(_editorHost, "editor");
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CaptionsViewModel.Editor)) ShowEditor();
            else Render();
        };
        _vm.RefreshModels();
        Render();
    }

    public Gtk.Stack Root { get; }

    /// <summary>CI screenshots: open the first draft in the editor.</summary>
    public void RunScene(string scene)
    {
        if (scene == "captions-editor" && _vm.Drafts?.Drafts.FirstOrDefault() is { } d) _ = _vm.OpenDraftAsync(d.Language);
    }


    private void Render()
    {
        _syncing = true;
        try
        {
            _videoIds = _vm.Videos.Select(v => v.Id).ToList();
            _video.SetModel(Gtk.StringList.New(_vm.Videos.Select(v => v.Title).ToArray()));
            var at = _vm.Video == null ? -1 : _videoIds.ToList().IndexOf(_vm.Video.Id);
            if (at >= 0) _video.SetSelected((uint)at);

            _modelIds = _vm.Models.Select(m => m.Id).ToList();
            _model.SetModel(Gtk.StringList.New(_vm.Models.Select(m => _l.Format(m.Installed ? "studio.captions.device.installed" : "studio.captions.device.download", Localizer.Isolate(m.Id), Localizer.Isolate(Size(m.SizeBytes)))).ToArray()));
            var mi = _vm.Model == null ? -1 : _modelIds.ToList().IndexOf(_vm.Model);
            if (mi >= 0) _model.SetSelected((uint)mi);
        }
        finally
        {
            _syncing = false;
        }

        Ui.Status(_status, _vm.IsLoading ? _l["studio.shell.loading"] : _vm.Videos.Count == 0 ? _l["studio.captions.noVideos"] : null);
        Ui.Status(_error, _vm.ErrorKey is { } e ? _l[e] : null);
        Ui.Status(_notice, _vm.NoticeKey is { } n ? _l[n] : null);

        Ui.Clear(_drafts);
        var drafts = _vm.Drafts?.Drafts ?? Array.Empty<CaptionDraftSummary>();
        if (drafts.Count == 0) _drafts.Append(Ui.Label(_l["studio.captions.noDrafts"], dim: true));
        foreach (var d in drafts) _drafts.Append(DraftRow(d));
        foreach (var c in _vm.Drafts?.Claims ?? Array.Empty<CaptionClaim>())
        {
            _drafts.Append(Ui.Label(_l.Format(c.Status == CaptionClaimStatus.Failed ? "studio.captions.claimFailed" : "studio.captions.claimQueued", _l[CaptionsViewModel.LanguageKey(c.Language)]), dim: true));
        }

        if (_vm.Minutes is { } m)
        {
            Ui.Status(_minutes, m.Unlimited ? _l["studio.captions.minutes.unlimited"]
                : m.HasNoPlan ? _l["studio.captions.minutes.none"]
                : m.MinutesNeeded is { } need ? _l.Format("studio.captions.minutes.needed", _l.Number((long)Math.Ceiling(need)), _l.Number((long)Math.Floor(m.Remaining ?? 0)))
                : _l.Format("studio.captions.minutes.left", _l.Number((long)Math.Floor(m.Remaining ?? 0))));
        }

        _server.SetSensitive(_vm.Video != null && _vm.Minutes?.CanStart == true && !_vm.Jobs.Any(j => j.IsActive));

        _device.SetVisible(_vm.OnDeviceAvailable);
        var busy = _vm.IsBusyOnDevice;
        _transcribe.SetSensitive(!busy && _vm.Video != null && _vm.Model != null);
        _model.SetSensitive(!busy);
        _cancelDevice.SetVisible(busy);
        _progress.SetVisible(busy);
        _progress.SetFraction(_vm.Progress);
        _progress.SetText(_vm.Stage switch
        {
            OnDeviceStage.Downloading => _l.Format("studio.captions.device.downloading", _l.Number((long)Math.Round(_vm.Progress * 100))),
            OnDeviceStage.Transcribing => _l.Format("studio.captions.device.transcribing", _l.Number((long)Math.Round(_vm.Progress * 100))),
            OnDeviceStage.Sending => _l["studio.captions.device.sending"],
            OnDeviceStage.Waiting => _l["studio.captions.device.waiting"],
            _ => "",
        });

        Ui.Clear(_jobs);
        if (_vm.Jobs.Count == 0) _jobs.Append(Ui.Label(_l["studio.captions.noJobs"], dim: true));
        foreach (var j in _vm.Jobs)
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            var text = Ui.Label(_l.Format("studio.captions.jobRow", _l[CaptionsViewModel.LanguageKey(j.Language)], _l["studio.captions.job." + JobKey(j.Status)]));
            text.SetHexpand(true);
            row.Append(text);
            if (j.Fraction is { } f && j.IsActive)
            {
                var bar = Gtk.ProgressBar.New();
                bar.SetFraction(f);
                bar.SetValign(Gtk.Align.Center);
                row.Append(bar);
            }

            if (j.IsActive) row.Append(Ui.Named(Ui.Button(_l["common.cancel"], () => _ = _vm.CancelJobAsync(j.Id), "flat"), _l.Format("studio.captions.cancelJob", _l[CaptionsViewModel.LanguageKey(j.Language)])));
            _jobs.Append(row);
        }
    }

    private Gtk.Widget DraftRow(CaptionDraftSummary d)
    {
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        row.AddCssClass("card");
        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        text.SetMarginStart(12);
        text.SetMarginTop(8);
        text.SetMarginBottom(8);
        text.SetHexpand(true);
        var lang = _l[CaptionsViewModel.LanguageKey(d.Language)];
        var name = Ui.Label(lang);
        name.AddCssClass("heading");
        text.Append(name);
        var state = d.IsOutdated ? _l["studio.captions.state.outdated"] : d.IsPublished ? _l["studio.captions.state.published"] : _l["studio.captions.state.draft"];
        text.Append(Ui.Label(_l.Format("studio.captions.draftLine", _l[CaptionJson.OriginKey(d.Origin)], _l.Number(d.CueCount), state), dim: true));
        if (d.PendingQuran > 0) text.Append(Ui.Label(_l.Format("studio.captions.pendingQuran", _l.Number(d.PendingQuran)), dim: true));
        row.Append(text);
        var edit = Ui.Named(Ui.Button(_l["studio.captions.edit"], () => _ = _vm.OpenDraftAsync(d.Language)), _l.Format("studio.captions.editLang", lang));
        edit.SetValign(Gtk.Align.Center);
        edit.SetMarginEnd(12);
        row.Append(edit);
        return row;
    }

    private static string JobKey(CaptionJobStatus s) => s switch
    {
        CaptionJobStatus.Queued => "queued",
        CaptionJobStatus.WaitingBudget => "waiting",
        CaptionJobStatus.Extracting => "extracting",
        CaptionJobStatus.Transcribing => "transcribing",
        CaptionJobStatus.Postprocessing => "postprocessing",
        CaptionJobStatus.Ready => "ready",
        CaptionJobStatus.Cancelled => "cancelled",
        _ => "failed",
    };

    private string Size(long bytes) => bytes >= 1_000_000_000 ? _l.Format("studio.size.gb", _l.Decimal(bytes / 1e9)) : _l.Format("studio.size.mb", _l.Number((long)Math.Round(bytes / 1e6)));

    private async Task PickAndTranscribeAsync()
    {
        try
        {
            var dialog = Gtk.FileDialog.New();
            dialog.SetTitle(_l["studio.captions.device.pick"]);
            var file = await dialog.OpenAsync(_window);
            if (file?.GetPath() is { } path) await _vm.TranscribeOnDeviceAsync(path);
        }
        catch (GLib.GException)
        {
            // dismissed
        }
    }

    private void ShowEditor()
    {
        Ui.Clear(_editorHost);
        if (_vm.Editor is { } editor)
        {
            var view = new CueEditorView(_l, editor);
            view.Root.SetVexpand(true);
            _editorHost.Append(view.Root);
            _stack.SetVisibleChildName("editor");
        }
        else
        {
            _stack.SetVisibleChildName("list");
        }
    }
}

/// <summary>The cue editor: one row per cue with times, text, and split, merge, nudge and delete.</summary>
public sealed class CueEditorView
{
    private readonly Localizer _l;
    private readonly CueEditorViewModel _vm;
    private readonly Gtk.Box _cues = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
    private readonly Gtk.Label _title = Ui.Label("");
    private readonly Gtk.Label _error = Ui.Label("");
    private readonly Gtk.Label _notice = Ui.Label("");
    private readonly Gtk.Button _undo;
    private readonly Gtk.Button _redo;
    private readonly Gtk.Button _save;
    private readonly Gtk.Button _publish;
    private int _renderedCount = -1;
    private object? _renderedList;

    public CueEditorView(Localizer l, CueEditorViewModel vm)
    {
        _l = l;
        _vm = vm;
        var outer = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        Root = outer;
        var bar = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        bar.SetMarginStart(20);
        bar.SetMarginEnd(20);
        bar.SetMarginTop(16);
        bar.Append(Ui.Named(Ui.Button(l["studio.captions.editor.back"], () => _ = _vm.CloseAsync(), "flat"), l["studio.captions.editor.back"]));
        _title.AddCssClass("title-3");
        _title.SetHexpand(true);
        _title.SetWrap(false);
        _title.SetEllipsize(Pango.EllipsizeMode.End);
        bar.Append(_title);
        outer.Append(bar);
        var actions = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        actions.SetMarginStart(20);
        actions.SetMarginEnd(20);
        _undo = Ui.Button(l["studio.captions.editor.undo"], () => _vm.Undo(), "flat");
        _redo = Ui.Button(l["studio.captions.editor.redo"], () => _vm.Redo(), "flat");
        _save = Ui.Button(l["studio.captions.editor.save"], () => _ = _vm.SaveAsync());
        _publish = Ui.Button(l["studio.captions.editor.publish"], () => _ = _vm.PublishAsync(), "suggested-action");
        var delete = Ui.Button(l["studio.captions.editor.delete"], () => _ = _vm.DeleteDraftAsync(), "destructive-action");
        foreach (var b in new[] { _undo, _redo, _save, _publish }) actions.Append(b);
        var spacer = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        spacer.SetHexpand(true);
        actions.Append(spacer);
        actions.Append(delete);
        outer.Append(actions);

        var info = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        info.SetMarginStart(20);
        info.SetMarginEnd(20);
        info.Append(Ui.Label(l["studio.captions.editor.hint"], dim: true));
        _error.AddCssClass("error");
        info.Append(_error);
        _notice.AddCssClass("success");
        info.Append(_notice);
        outer.Append(info);

        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetVexpand(true);
        var page = Ui.Page(6);
        page.Append(_cues);
        scroll.SetChild(page);
        outer.Append(scroll);

        var keys = Gtk.ShortcutController.New();
        keys.SetScope(Gtk.ShortcutScope.Managed);
        keys.AddShortcut(Gtk.Shortcut.New(Gtk.ShortcutTrigger.ParseString("<Control>z"), Gtk.CallbackAction.New((_, _) => { _vm.Undo(); return true; })));
        keys.AddShortcut(Gtk.Shortcut.New(Gtk.ShortcutTrigger.ParseString("<Control><Shift>z"), Gtk.CallbackAction.New((_, _) => { _vm.Redo(); return true; })));
        keys.AddShortcut(Gtk.Shortcut.New(Gtk.ShortcutTrigger.ParseString("<Control>s"), Gtk.CallbackAction.New((_, _) => { _ = _vm.SaveAsync(); return true; })));
        outer.AddController(keys);

        _vm.PropertyChanged += (_, _) => Render();
        Render();
    }

    public Gtk.Box Root { get; }

    private void Render()
    {
        var s = _vm.Summary;
        _title.SetText(_l.Format("studio.captions.editor.title", _l[CaptionsViewModel.LanguageKey(s.Language)], _l[CaptionJson.OriginKey(s.Origin)]));
        _undo.SetSensitive(_vm.CanUndo);
        _redo.SetSensitive(_vm.CanRedo);
        _save.SetSensitive(_vm.IsDirty && !_vm.IsSaving);
        _publish.SetSensitive(!_vm.IsSaving);
        Ui.Status(_error, _vm.ErrorKey is { } e ? _l[e] : null);
        Ui.Status(_notice, _vm.NoticeKey is { } n && !_vm.IsDirty ? _l[n] : null);

        // Rebuilding while typing would steal focus: only rebuild when the list itself changed shape.
        var list = _vm.Cues;
        if (ReferenceEquals(list, _renderedList)) return;
        var structural = list.Count != _renderedCount || !_typingOnly;
        _renderedList = list;
        _renderedCount = list.Count;
        if (!structural) return;
        Ui.Clear(_cues);
        for (var i = 0; i < list.Count; i++) _cues.Append(CueRow(i, list[i]));
    }

    private bool _typingOnly;

    private Gtk.Widget CueRow(int index, CaptionCue cue)
    {
        var card = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        card.AddCssClass("card");
        var times = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        times.SetMarginStart(12);
        times.SetMarginEnd(12);
        times.SetMarginTop(8);
        // The row follows the reading direction (start, then end); the time fields themselves stay left to right.
        var number = Ui.Label(_l.Number(index + 1), dim: true);
        number.SetWidthChars(3);
        times.Append(number);
        var start = TimeEntry(_l.Format("studio.captions.editor.startOf", _l.Number(index + 1)), cue.StartMs);
        var end = TimeEntry(_l.Format("studio.captions.editor.endOf", _l.Number(index + 1)), cue.EndMs);
        void Commit()
        {
            var a = CueEditorViewModel.ParseTime(start.GetText());
            var b = CueEditorViewModel.ParseTime(end.GetText());
            if (a is int s && b is int e && (s != cue.StartMs || e != cue.EndMs)) _vm.SetTimes(index, s, e);
        }

        start.OnActivate += (_, _) => Commit();
        end.OnActivate += (_, _) => Commit();
        times.Append(Nudge(_l["studio.captions.editor.minus"], _l.Format("studio.captions.editor.startEarlier", _l.Number(index + 1)), () => _vm.Nudge(index, CueEditing.Edge.Start, -CueEditing.NudgeMs)));
        times.Append(Ui.Labelled(_l["studio.captions.editor.start"], start));
        times.Append(Nudge(_l["studio.captions.editor.plus"], _l.Format("studio.captions.editor.startLater", _l.Number(index + 1)), () => _vm.Nudge(index, CueEditing.Edge.Start, CueEditing.NudgeMs)));
        // Start to end follows the reading direction: the arrow points left in Arabic.
        times.Append(Ui.Label(_l.IsRtl ? "←" : "→", dim: true, start: false));
        times.Append(Nudge(_l["studio.captions.editor.minus"], _l.Format("studio.captions.editor.endEarlier", _l.Number(index + 1)), () => _vm.Nudge(index, CueEditing.Edge.End, -CueEditing.NudgeMs)));
        times.Append(Ui.Labelled(_l["studio.captions.editor.end"], end));
        times.Append(Nudge(_l["studio.captions.editor.plus"], _l.Format("studio.captions.editor.endLater", _l.Number(index + 1)), () => _vm.Nudge(index, CueEditing.Edge.End, CueEditing.NudgeMs)));
        card.Append(times);

        var segments = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        segments.SetMarginStart(12);
        segments.SetMarginEnd(12);
        Gtk.Entry? firstText = null;
        var firstSegment = -1;
        for (var si = 0; si < cue.Segments.Count; si++)
        {
            var seg = cue.Segments[si];
            if (seg.IsQuran)
            {
                var q = Ui.Label(_l.Format("studio.captions.editor.quran", seg.QuranRange ?? ""), dim: true);
                segments.Append(q);
                continue;
            }

            var entry = Gtk.Entry.New();
            entry.SetText(seg.Speech ?? "");
            entry.SetHexpand(true);
            entry.SetDirection(_vm.IsRtl ? Gtk.TextDirection.Rtl : Gtk.TextDirection.Ltr);
            entry.SetTooltipText(_l.Format("studio.captions.editor.textOf", _l.Number(index + 1)));
            var segIndex = si;
            entry.OnChanged += (_, _) =>
            {
                _typingOnly = true;
                try
                {
                    _vm.SetText(index, segIndex, entry.GetText());
                }
                finally
                {
                    _typingOnly = false;
                }
            };
            var labelled = Ui.Labelled(_l["studio.captions.editor.text"], entry);
            entry.SetHexpand(true);
            segments.Append(labelled);
            if (firstText == null)
            {
                firstText = entry;
                firstSegment = si;
            }
        }

        card.Append(segments);

        var actions = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        actions.SetMarginStart(12);
        actions.SetMarginEnd(12);
        actions.SetMarginBottom(8);
        var n = _l.Number(index + 1);
        if (firstText != null)
        {
            var text = firstText;
            var seg = firstSegment;
            actions.Append(Ui.Named(Ui.Button(_l["studio.captions.editor.split"], () =>
            {
                if (!_vm.Split(index, seg, text.GetPosition())) _vm.ErrorKey = "studio.captions.editor.cantSplit";
            }, "flat"), _l.Format("studio.captions.editor.splitOf", n)));
        }

        if (index + 1 < _vm.Cues.Count)
        {
            actions.Append(Ui.Named(Ui.Button(_l["studio.captions.editor.merge"], () =>
            {
                if (!_vm.MergeWithNext(index)) _vm.ErrorKey = "studio.captions.editor.cantMerge";
            }, "flat"), _l.Format("studio.captions.editor.mergeOf", n)));
        }

        actions.Append(Ui.Named(Ui.Button(_l["studio.captions.editor.deleteCue"], () => _vm.Delete(index), "flat"), _l.Format("studio.captions.editor.deleteCueOf", n)));
        card.Append(actions);
        return card;
    }

    private static Gtk.Entry TimeEntry(string name, int ms)
    {
        var e = Gtk.Entry.New();
        e.SetText(CueEditorViewModel.Time(ms));
        e.SetWidthChars(10);
        e.SetDirection(Gtk.TextDirection.Ltr);
        e.SetTooltipText(name);
        return e;
    }

    private static Gtk.Button Nudge(string symbol, string name, Action act)
    {
        // The visible label ("−0.1 s") is what screen readers announce; the tooltip names the full action.
        var b = Ui.Button(symbol, act, "flat");
        b.SetTooltipText(name);
        return b;
    }
}
