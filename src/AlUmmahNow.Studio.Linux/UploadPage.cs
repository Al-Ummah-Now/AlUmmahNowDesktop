using System.Globalization;
using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Shell;
using AlUmmahNow.Studio.Upload;

namespace AlUmmahNow.Studio.Linux;

/// <summary>
/// Upload: stage videos or a folder, set values for all of them, upload, and follow the queue.
/// Queue rows are updated in place so keyboard focus stays where it was.
/// </summary>
public sealed class UploadPage
{
    private readonly Localizer _l;
    private readonly UploadsViewModel _vm;
    private readonly StudioShellViewModel _shell;
    private readonly Gtk.Window _window;
    private readonly Gtk.Box _staged = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
    private readonly Gtk.Label _stagedTitle = Ui.Label("");
    private readonly Gtk.Button _start = Gtk.Button.NewWithLabel("");
    private readonly Gtk.Button _clear;
    private readonly Gtk.Label _dropText = Ui.Label("");
    private readonly Gtk.Box _stagedCard;
    private readonly Gtk.Box _queue = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
    // Rows with nothing to show are hidden: an empty box or blank label still takes the page spacing.
    private readonly Gtk.Box _watchRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    private readonly Gtk.Box _stagedActions = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    private readonly Gtk.Label _queueEmpty;
    private readonly Gtk.Label _error = Ui.Label("");
    private readonly Gtk.Label _watching = Ui.Label("", dim: true);
    private readonly Gtk.Button _stopWatching;
    private readonly Gtk.Box _playlists = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
    private readonly Gtk.Entry _scheduleStart = Gtk.Entry.New();
    private readonly Gtk.SpinButton _scheduleEvery = Gtk.SpinButton.NewWithRange(0, 24 * 30, 1);
    private readonly Gtk.Box _scheduleRow = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
    private readonly Gtk.CheckButton _recursive;
    private readonly Dictionary<string, QueueRow> _rows = new();
    private IReadOnlyList<PlannedUpload>? _renderedStaged;

    public UploadPage(Localizer l, UploadsViewModel vm, StudioShellViewModel shell, Gtk.Window window, Action<string?> rememberWatchFolder)
    {
        _l = l;
        _vm = vm;
        _shell = shell;
        _window = window;
        // Lantern Glass: files and the queue on the left, "Set for all" on the right; card actions in each footer.
        Root = Gtk.ScrolledWindow.New();
        Root.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        var page = Gtk.Box.New(Gtk.Orientation.Horizontal, 20);
        page.SetMarginStart(24);
        page.SetMarginEnd(24);
        page.SetMarginTop(18);
        page.SetMarginBottom(24);
        Root.SetChild(page);
        var left = Gtk.Box.New(Gtk.Orientation.Vertical, 18);
        left.SetHexpand(true);
        page.Append(left);

        // Add: the lantern, where the files go, and the ways to add them.
        // Two rows, so the actions fit the card down to the 1024 window.
        var add = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var addMore = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        add.Append(Ui.Button(l["studio.upload.addFiles"], () => _ = PickFilesAsync(), "suggested-action"));
        add.Append(Ui.Button(l["studio.upload.addFolder"], () => _ = PickFolderAsync()));
        _recursive = Gtk.CheckButton.NewWithLabel(l["studio.upload.includeSubfolders"]);
        addMore.Append(_recursive);
        addMore.Append(Ui.Button(l["studio.upload.watch"], () => _ = PickWatchFolderAsync(rememberWatchFolder)));
        var drop = Gtk.Box.New(Gtk.Orientation.Horizontal, 20);
        drop.AddCssClass("drop-zone");
        var lantern = Gtk.Image.NewFromIconName("go-up-symbolic");
        lantern.SetPixelSize(30);
        lantern.AddCssClass("lantern-ring");
        lantern.SetValign(Gtk.Align.Center);
        drop.Append(lantern);
        var dropText = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        dropText.SetValign(Gtk.Align.Center);
        dropText.SetHexpand(true);
        _dropText.AddCssClass("heading");
        dropText.Append(_dropText);
        dropText.Append(add);
        dropText.Append(addMore);
        drop.Append(dropText);
        var addBody = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        addBody.Append(drop);
        var watchRow = _watchRow;
        _watching.SetHexpand(true);
        _watching.SetWrapMode(Pango.WrapMode.WordChar);
        watchRow.Append(_watching);
        _stopWatching = Ui.Button(l["studio.upload.stopWatching"], () =>
        {
            _vm.Watch(null);
            rememberWatchFolder(null);
        }, "flat");
        watchRow.Append(_stopWatching);
        addBody.Append(watchRow);
        _error.AddCssClass("error");
        _error.SetVisible(false);
        addBody.Append(_error);
        left.Append(Ui.Card(addBody));

        // Ready to upload: the staged files with their titles; Clear list and Upload in the footer.
        var stagedBody = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        _stagedTitle.AddCssClass("title-4");
        stagedBody.Append(_stagedTitle);
        stagedBody.Append(_staged);
        var actions = _stagedActions;
        _clear = Ui.Button(l["studio.upload.clear"], () => _vm.ClearStaged(), "flat");
        actions.Append(_clear);
        _start.AddCssClass("suggested-action");
        _start.OnClicked += (_, _) => _vm.Start();
        actions.Append(_start);
        _stagedCard = Ui.Card(stagedBody, actions);
        left.Append(_stagedCard);

        // Uploads: the queue; Clear finished in the footer.
        var queueBody = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        queueBody.Append(Ui.CardTitle(l["studio.upload.queue"]));
        _queueEmpty = Ui.Label(l["studio.upload.queueEmpty"], dim: true);
        queueBody.Append(_queueEmpty);
        queueBody.Append(_queue);
        left.Append(Ui.Card(queueBody, Ui.Button(l["studio.upload.clearFinished"], () => _vm.ClearFinished(), "flat")));

        // Set for all.
        var forAll = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        forAll.Append(Ui.CardTitle(l["studio.upload.forAll"]));
        forAll.Append(BuildDefaults());
        var right = Ui.Card(forAll);
        right.SetValign(Gtk.Align.Start);
        // 360: the page fits the 1024 minimum window (with the rail) without growing the window.
        right.SetSizeRequest(360, -1);
        page.Append(right);

        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(UploadsViewModel.Staged): RenderStaged(); break;
                case nameof(UploadsViewModel.Items): RenderQueue(); break;
                case nameof(UploadsViewModel.Playlists): RenderPlaylists(); break;
                case nameof(UploadsViewModel.ErrorKey):
                    _error.SetText(_vm.ErrorKey is { } k ? _l[k] : "");
                    _error.SetVisible(_vm.ErrorKey != null);
                    break;
                case nameof(UploadsViewModel.WatchFolder): RenderWatching(); break;
            }
        };
        _shell.ChannelChanged += (_, _) => RenderStaged();
        RenderStaged();
        RenderQueue();
        RenderPlaylists();
        RenderWatching();
    }

    public Gtk.ScrolledWindow Root { get; }

    private Gtk.DropDown? _visibility;

    /// <summary>GTK_INPUT_ERROR: the "input" handler took care of the value itself.</summary>
    private const int GtkInputError = -1;

    /// <summary>CI screenshots: "scheduled" picks Scheduled, showing the schedule fields.</summary>
    public void RunScene(string scene)
    {
        if (scene == "scheduled") _visibility?.SetSelected(2);
    }

    /// <summary>CI screenshots: what the hours stepper shows, so an empty field fails the log, not just the eye.</summary>
    public string ScheduleEveryText => _scheduleEvery.GetText();

    private UploadDefaults D => _vm.Defaults;

    private Gtk.Widget BuildDefaults()
    {
        var grid = Gtk.Grid.New();
        grid.SetRowSpacing(8);
        grid.SetColumnSpacing(12);
        grid.SetMarginTop(8);
        var row = 0;

        void Add(string label, Gtk.Widget control, string? hint = null)
        {
            var name = Gtk.Label.NewWithMnemonic(label);
            name.SetMnemonicWidget(control);
            name.SetXalign(0);
            name.SetValign(Gtk.Align.Start);
            // Labels above the fields: the form sits in a narrow card.
            grid.Attach(name, 0, row++, 1, 1);
            control.SetHexpand(true);
            if (hint == null)
            {
                grid.Attach(control, 0, row, 1, 1);
            }
            else
            {
                var box = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
                box.Append(control);
                var h = Ui.Label(hint, dim: true);
                h.AddCssClass("caption");
                box.Append(h);
                grid.Attach(box, 0, row, 1, 1);
            }

            row++;
        }

        var title = Gtk.Entry.New();
        title.SetText(D.TitlePattern);
        title.OnChanged += (_, _) =>
        {
            D.TitlePattern = title.GetText();
            _vm.ApplyDefaults();
        };
        Add(_l["studio.upload.titlePattern"], title, TitlePatternHint(_l));

        var description = Gtk.TextView.New();
        description.SetWrapMode(Gtk.WrapMode.WordChar);
        description.SetSizeRequest(-1, 72);
        description.GetBuffer().OnChanged += (b, _) =>
        {
            b.GetBounds(out var start, out var end);
            D.Description = b.GetText(start, end, false);
            _vm.ApplyDefaults();
        };
        var frame = Gtk.Frame.New(null);
        frame.SetChild(description);
        Add(_l["studio.upload.description"], description);
        grid.Remove(description);
        grid.Attach(frame, 0, row - 1, 1, 1);

        var categories = new[] { (string?)null }.Concat(StudioCatalog.Categories).ToList();
        var category = Gtk.DropDown.NewFromStrings(categories.Select(c => c == null ? _l["studio.upload.none"] : _l["studio.category." + c]).ToArray());
        category.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() != "selected") return;
            D.Category = categories[(int)category.GetSelected()];
            _vm.ApplyDefaults();
        };
        Add(_l["studio.upload.category"], category);

        var languages = new[] { (string?)null }.Concat(StudioCatalog.Languages).ToList();
        var language = Gtk.DropDown.NewFromStrings(languages.Select(c => c == null ? _l["studio.upload.none"] : _l["studio.language." + c]).ToArray());
        language.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() != "selected") return;
            D.Language = languages[(int)language.GetSelected()];
            _vm.ApplyDefaults();
        };
        Add(_l["studio.upload.language"], language);

        var visibilities = new[] { UploadVisibility.Private, UploadVisibility.Public, UploadVisibility.Scheduled };
        var visibility = _visibility = Gtk.DropDown.NewFromStrings(visibilities.Select(v => _l["studio.upload.visibility." + v.ToString().ToLowerInvariant()]).ToArray());
        visibility.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() != "selected") return;
            D.Visibility = visibilities[(int)visibility.GetSelected()];
            _scheduleRow.SetVisible(D.Visibility == UploadVisibility.Scheduled);
            _vm.ApplyDefaults();
        };
        Add(_l["studio.upload.visibility"], visibility);

        D.TimeZone = Formats.LocalTimeZone();
        D.ScheduleStart ??= DateTimeOffset.Now.Date.AddDays(1).AddHours(9);
        // Prefilled in the UI's digits (٠–٩ in Arabic); typed digits of either kind read back.
        _scheduleStart.SetText(_l.InputDigits(Formats.ToLocalInput(D.ScheduleStart.Value)));
        var scheduleError = Ui.Label("");
        scheduleError.AddCssClass("error");
        scheduleError.SetVisible(false); // A blank label still takes the row spacing.
        _scheduleStart.OnChanged += (_, _) =>
        {
            var t = Formats.ParseLocal(Localizer.NormalizeDigits(_scheduleStart.GetText()));
            scheduleError.SetText(t == null ? _l["studio.upload.scheduleInvalid"] : "");
            scheduleError.SetVisible(t == null);
            if (t == null) return;
            D.ScheduleStart = t;
            _vm.ApplyDefaults();
        };
        // The hours show in the UI's digits; typed ٠–٩ are read back (GTK's own parser only knows 0–9).
        _scheduleEvery.SetUpdatePolicy(Gtk.SpinButtonUpdatePolicy.IfValid);
        _scheduleEvery.OnOutput += (spin, _) =>
        {
            spin.SetText(_l.Number((long)Math.Round(spin.GetValue())));
            return true;
        };
        _scheduleEvery.OnInput += (spin, _) =>
        {
            if (double.TryParse(Localizer.NormalizeDigits(spin.GetText()).Replace(",", ""), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var typed))
            {
                spin.SetValue(typed);
            }

            return GtkInputError; // The value is set above; GTK then re-shows it through OnOutput.
        };
        // NewWithRange turns on numeric mode, which drops every character but 0–9: the ٠–٩ text set by OnOutput
        // was filtered away, leaving the field blank in Arabic. OnInput parses both kinds of digits instead.
        _scheduleEvery.SetNumeric(false);

        _scheduleEvery.SetValue(D.ScheduleInterval.TotalHours);
        _scheduleEvery.OnValueChanged += (_, _) =>
        {
            D.ScheduleInterval = TimeSpan.FromHours(_scheduleEvery.GetValue());
            _vm.ApplyDefaults();
        };
        // Labels above the fields, as the rest of the form: the card keeps its width.
        _scheduleRow.Append(Ui.LabelledAbove(_l["studio.upload.scheduleStart"], _scheduleStart));
        _scheduleRow.Append(scheduleError);
        // "Every … hours" across a batch is bulk scheduling: hidden when bulkSchedule is off.
        var every = Ui.LabelledAbove(_l["studio.upload.scheduleEvery"], _scheduleEvery);
        every.SetVisible(_vm.BulkScheduleOn);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UploadsViewModel.BulkScheduleOn)) every.SetVisible(_vm.BulkScheduleOn);
        };
        _scheduleRow.Append(every);
        _scheduleRow.SetVisible(false);
        grid.Attach(_scheduleRow, 0, row++, 1, 1);

        var newPlaylist = Gtk.Entry.New();
        newPlaylist.SetPlaceholderText(_l["studio.upload.newPlaylist"]);
        var create = Ui.Button(_l["studio.upload.createPlaylist"], async () =>
        {
            var p = await _vm.CreatePlaylistAsync(newPlaylist.GetText(), isPublic: true);
            if (p == null) return;
            D.PlaylistIds.Add(p.Id);
            newPlaylist.SetText("");
            _vm.ApplyDefaults();
            RenderPlaylists();
        });
        var playlistBox = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        playlistBox.Append(_playlists);
        var createRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        createRow.Append(newPlaylist);
        createRow.Append(create);
        playlistBox.Append(createRow);
        Add(_l["studio.upload.playlists"], playlistBox);

        var tags = Gtk.Entry.New();
        tags.OnChanged += (_, _) =>
        {
            D.Tags = UploadPlanner.ParseTags(tags.GetText());
            _vm.ApplyDefaults();
        };
        Add(_l["studio.upload.tags"], tags);
        return grid;
    }

    private void RenderPlaylists()
    {
        Ui.Clear(_playlists);
        foreach (var p in _vm.Playlists)
        {
            var check = Gtk.CheckButton.NewWithLabel(p.Title);
            check.SetActive(D.PlaylistIds.Contains(p.Id));
            check.OnToggled += (_, _) =>
            {
                if (check.GetActive()) { if (!D.PlaylistIds.Contains(p.Id)) D.PlaylistIds.Add(p.Id); }
                else D.PlaylistIds.Remove(p.Id);
                _vm.ApplyDefaults();
            };
            _playlists.Append(check);
        }
    }

    private void RenderWatching()
    {
        _watching.SetText(_vm.WatchFolder is { } f ? _l.Format("studio.upload.watchingFolder", Localizer.Isolate(f)) : "");
        _stopWatching.SetVisible(_vm.WatchFolder != null);
        _watchRow.SetVisible(_vm.WatchFolder != null);
    }

    private void RenderStaged()
    {
        var staged = _vm.Staged;
        _start.SetLabel(staged.Count == 1 ? _l["studio.upload.startOne"] : _l.Format("studio.upload.start", staged.Count));
        _start.SetSensitive(_vm.CanStart);
        _stagedCard.SetVisible(staged.Count > 0);
        _stagedTitle.SetText(_l.Format("studio.upload.stagedTitle", staged.Count));
        _dropText.SetText(_l.Format("studio.upload.stagedEmpty", Localizer.Isolate(_shell.ActiveChannel?.Name ?? "")));
        if (ReferenceEquals(staged, _renderedStaged)) return;
        _renderedStaged = staged;
        Ui.Clear(_staged);
        foreach (var plan in staged)
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
            var file = Path.GetFileName(plan.FilePath);
            var title = Gtk.Entry.New();
            title.SetText(plan.Title);
            title.SetHexpand(true);
            Ui.Named(title, _l.Format("studio.upload.titleFor", file));
            title.OnChanged += (_, _) => _vm.RenameStaged(plan.FilePath, title.GetText());
            var meta = Ui.Label(_l.Join(Localizer.Isolate(file), _l.Bytes(plan.Size),
                plan.PublishAt is { } at ? _l.Format("studio.upload.publishesAt", _l.DateTime(at)) : null), dim: true);
            meta.AddCssClass("caption");
            var text = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
            text.SetHexpand(true);
            text.Append(title);
            text.Append(meta);
            var remove = Gtk.Button.NewFromIconName("window-close-symbolic");
            remove.SetValign(Gtk.Align.Center);
            remove.AddCssClass("flat");
            Ui.Named(remove, _l.Format("studio.upload.removeStaged", file));
            remove.OnClicked += (_, _) => _vm.RemoveStaged(plan.FilePath);
            row.Append(Ui.FileTile(96, 54));
            row.Append(text);
            row.Append(remove);
            _staged.Append(row);
        }
    }

    private void RenderQueue()
    {
        var items = _vm.Items;
        _queueEmpty.SetVisible(items.Count == 0);
        _queue.SetVisible(items.Count > 0);
        var seen = new HashSet<string>();
        foreach (var item in items)
        {
            seen.Add(item.Id);
            if (!_rows.TryGetValue(item.Id, out var row))
            {
                row = new QueueRow(this, item.Id);
                _rows[item.Id] = row;
                _queue.Append(row.Root);
            }

            row.Update(item);
        }

        foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _queue.Remove(_rows[gone].Root);
            _rows.Remove(gone);
        }
    }

    private async Task PickFilesAsync()
    {
        try
        {
            var dialog = Gtk.FileDialog.New();
            dialog.SetTitle(_l["studio.upload.pickVideos"]);
            var files = await dialog.OpenMultipleAsync(_window);
            if (files == null) return;
            var paths = new List<string>();
            for (uint i = 0; i < files.GetNItems(); i++)
            {
                if (files.GetObject(i) is Gio.File f && f.GetPath() is { } p) paths.Add(p);
            }

            _vm.AddFiles(paths);
        }
        catch (GLib.GException)
        {
            // dismissed
        }
    }

    private async Task<string?> ChooseFolderAsync()
    {
        try
        {
            var dialog = Gtk.FileDialog.New();
            dialog.SetTitle(_l["studio.upload.pickFolder"]);
            var folder = await dialog.SelectFolderAsync(_window);
            return folder?.GetPath();
        }
        catch (GLib.GException)
        {
            return null;
        }
    }

    private async Task PickFolderAsync()
    {
        if (await ChooseFolderAsync() is { } path) _vm.AddFolder(path, _recursive.GetActive());
    }

    private async Task PickWatchFolderAsync(Action<string?> remember)
    {
        if (await ChooseFolderAsync() is not { } path) return;
        _vm.Watch(path);
        remember(path);
    }

    /// <summary>The title-pattern hint; its {n} examples use the UI digits (the token itself writes 0–9).</summary>
    private static string TitlePatternHint(Localizer l) => l.Format("studio.upload.titlePatternHint", 1, 2, 3);

    private sealed class QueueRow
    {
        private readonly UploadPage _page;
        private readonly string _id;
        private readonly Gtk.Label _title = Ui.Label("");
        private readonly Gtk.ProgressBar _bar = Gtk.ProgressBar.New();
        private readonly Gtk.Label _status = Ui.Label("", dim: true);
        private readonly Gtk.Box _buttons = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        private UploadPhase? _phase;

        public QueueRow(UploadPage page, string id)
        {
            _page = page;
            _id = id;
            Root = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
            var text = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
            text.SetHexpand(true);
            text.SetValign(Gtk.Align.Center);
            _title.SetEllipsize(Pango.EllipsizeMode.End);
            _title.AddCssClass("heading");
            text.Append(_title);
            text.Append(_bar);
            text.Append(_status);
            _buttons.SetValign(Gtk.Align.Center);
            Root.Append(Ui.FileTile(80, 45));
            Root.Append(text);
            Root.Append(_buttons);
        }

        public Gtk.Box Root { get; }

        public void Update(UploadSnapshot item)
        {
            var l = _page._l;
            _title.SetText(item.Title);
            _bar.SetFraction(item.Fraction);
            var phase = l["studio.phase." + item.Phase.ToString().ToLowerInvariant()];
            var amount = l.Format("studio.upload.progress", l.Bytes(item.SentBytes), l.Bytes(item.TotalBytes));
            _status.SetText(item.Phase == UploadPhase.Uploading ? l.Join(phase, amount) : phase);
            Ui.Named(_bar, l.Join(Localizer.Isolate(item.Title), phase));
            if (_phase == item.Phase) return;
            _phase = item.Phase;
            Ui.Clear(_buttons);
            var vm = _page._vm;
            switch (item.Phase)
            {
                case UploadPhase.Paused:
                    _buttons.Append(Ui.Button(l["studio.upload.resume"], () => vm.Resume(_id)));
                    _buttons.Append(Cancel(item));
                    break;
                case UploadPhase.Failed:
                    _buttons.Append(Ui.Button(l["studio.upload.retry"], () => vm.Retry(_id)));
                    _buttons.Append(Ui.Button(l["studio.upload.remove"], () => vm.Remove(_id), "flat"));
                    break;
                case UploadPhase.Done:
                case UploadPhase.Cancelled:
                    _buttons.Append(Ui.Button(l["studio.upload.remove"], () => vm.Remove(_id), "flat"));
                    break;
                default:
                    _buttons.Append(Ui.Button(l["studio.upload.pause"], () => vm.Pause(_id)));
                    _buttons.Append(Cancel(item));
                    break;
            }
        }

        private Gtk.Button Cancel(UploadSnapshot item) => Ui.Button(_page._l["studio.upload.cancel"], async () =>
        {
            var l = _page._l;
            if (await Confirm.AskAsync(_page._window, l.Format("studio.upload.cancelTitle", Localizer.Isolate(item.Title)), l["studio.upload.cancelBody"], l["studio.upload.cancelConfirm"], l["studio.upload.keepUploading"]))
            {
                await _page._vm.CancelAsync(_id);
            }
        }, "flat");
    }
}
