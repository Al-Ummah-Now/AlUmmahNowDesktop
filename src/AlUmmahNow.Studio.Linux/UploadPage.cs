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
    private readonly Gtk.Box _queue = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
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
        Root = Gtk.ScrolledWindow.New();
        var page = Ui.Page(14);
        Root.SetChild(page);

        // Add
        var add = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        add.Append(Ui.Button(l["studio.upload.addFiles"], () => _ = PickFilesAsync(), "suggested-action"));
        add.Append(Ui.Button(l["studio.upload.addFolder"], () => _ = PickFolderAsync()));
        _recursive = Gtk.CheckButton.NewWithLabel(l["studio.upload.includeSubfolders"]);
        add.Append(_recursive);
        add.Append(Ui.Button(l["studio.upload.watch"], () => _ = PickWatchFolderAsync(rememberWatchFolder)));
        page.Append(add);
        var watchRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        watchRow.Append(_watching);
        _stopWatching = Ui.Button(l["studio.upload.stopWatching"], () =>
        {
            _vm.Watch(null);
            rememberWatchFolder(null);
        }, "flat");
        watchRow.Append(_stopWatching);
        page.Append(watchRow);
        _error.AddCssClass("error");
        page.Append(_error);

        // Set for all
        var expander = Gtk.Expander.New(l["studio.upload.forAll"]);
        expander.SetExpanded(true);
        expander.SetChild(BuildDefaults());
        page.Append(expander);

        // Staged
        _stagedTitle.AddCssClass("heading");
        page.Append(_stagedTitle);
        page.Append(_staged);
        var actions = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        _start.AddCssClass("suggested-action");
        _start.OnClicked += (_, _) => _vm.Start();
        actions.Append(_start);
        _clear = Ui.Button(l["studio.upload.clear"], () => _vm.ClearStaged(), "flat");
        actions.Append(_clear);
        page.Append(actions);

        // Queue
        var queueHeader = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var queueTitle = Ui.Label(l["studio.upload.queue"]);
        queueTitle.AddCssClass("heading");
        queueTitle.SetHexpand(true);
        queueHeader.Append(queueTitle);
        queueHeader.Append(Ui.Button(l["studio.upload.clearFinished"], () => _vm.ClearFinished(), "flat"));
        page.Append(queueHeader);
        _queueEmpty = Ui.Label(l["studio.upload.queueEmpty"], dim: true);
        page.Append(_queueEmpty);
        page.Append(_queue);

        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(UploadsViewModel.Staged): RenderStaged(); break;
                case nameof(UploadsViewModel.Items): RenderQueue(); break;
                case nameof(UploadsViewModel.Playlists): RenderPlaylists(); break;
                case nameof(UploadsViewModel.ErrorKey): _error.SetText(_vm.ErrorKey is { } k ? _l[k] : ""); break;
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
            grid.Attach(name, 0, row, 1, 1);
            control.SetHexpand(true);
            if (hint == null)
            {
                grid.Attach(control, 1, row, 1, 1);
            }
            else
            {
                var box = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
                box.Append(control);
                var h = Ui.Label(hint, dim: true);
                h.AddCssClass("caption");
                box.Append(h);
                grid.Attach(box, 1, row, 1, 1);
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
        Add(_l["studio.upload.titlePattern"], title, _l["studio.upload.titlePatternHint"]);

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
        grid.Attach(frame, 1, row - 1, 1, 1);

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
        var visibility = Gtk.DropDown.NewFromStrings(visibilities.Select(v => _l["studio.upload.visibility." + v.ToString().ToLowerInvariant()]).ToArray());
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
        _scheduleStart.SetText(Formats.ToLocalInput(D.ScheduleStart.Value));
        var scheduleError = Ui.Label("");
        _scheduleStart.OnChanged += (_, _) =>
        {
            var t = Formats.ParseLocal(_scheduleStart.GetText());
            scheduleError.SetText(t == null ? _l["studio.upload.scheduleInvalid"] : "");
            if (t == null) return;
            D.ScheduleStart = t;
            _vm.ApplyDefaults();
        };
        _scheduleEvery.SetValue(D.ScheduleInterval.TotalHours);
        _scheduleEvery.OnValueChanged += (_, _) =>
        {
            D.ScheduleInterval = TimeSpan.FromHours(_scheduleEvery.GetValue());
            _vm.ApplyDefaults();
        };
        _scheduleRow.Append(Ui.Labelled(_l["studio.upload.scheduleStart"], _scheduleStart));
        _scheduleRow.Append(scheduleError);
        _scheduleRow.Append(Ui.Labelled(_l["studio.upload.scheduleEvery"], _scheduleEvery));
        _scheduleRow.SetVisible(false);
        grid.Attach(_scheduleRow, 1, row++, 1, 1);

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
    }

    private void RenderStaged()
    {
        var staged = _vm.Staged;
        _start.SetLabel(staged.Count == 1 ? _l["studio.upload.startOne"] : _l.Format("studio.upload.start", staged.Count));
        _start.SetSensitive(_vm.CanStart);
        _start.SetVisible(staged.Count > 0);
        _clear.SetVisible(staged.Count > 0);
        _stagedTitle.SetText(staged.Count > 0
            ? _l.Format("studio.upload.stagedTitle", staged.Count)
            : _l.Format("studio.upload.stagedEmpty", Localizer.Isolate(_shell.ActiveChannel?.Name ?? "")));
        if (ReferenceEquals(staged, _renderedStaged)) return;
        _renderedStaged = staged;
        Ui.Clear(_staged);
        foreach (var plan in staged)
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            var file = Path.GetFileName(plan.FilePath);
            var title = Gtk.Entry.New();
            title.SetText(plan.Title);
            title.SetHexpand(true);
            Ui.Named(title, _l.Format("studio.upload.titleFor", file));
            title.OnChanged += (_, _) => _vm.RenameStaged(plan.FilePath, title.GetText());
            var meta = Ui.Label(Localizer.Isolate(file) + " · " + Formats.Bytes(plan.Size, CultureInfo.InvariantCulture)
                + (plan.PublishAt is { } at ? " · " + _l.Format("studio.upload.publishesAt", Formats.ToLocalInput(at)) : ""), dim: true);
            meta.SetSizeRequest(320, -1);
            var remove = Gtk.Button.NewWithLabel(_l["studio.upload.remove"]);
            remove.AddCssClass("flat");
            Ui.Named(remove, _l.Format("studio.upload.removeStaged", file));
            remove.OnClicked += (_, _) => _vm.RemoveStaged(plan.FilePath);
            row.Append(title);
            row.Append(meta);
            row.Append(remove);
            _staged.Append(row);
        }
    }

    private void RenderQueue()
    {
        var items = _vm.Items;
        _queueEmpty.SetVisible(items.Count == 0);
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
            text.SetSizeRequest(360, -1);
            _title.SetEllipsize(Pango.EllipsizeMode.End);
            text.Append(_title);
            text.Append(_status);
            _bar.SetHexpand(true);
            _bar.SetValign(Gtk.Align.Center);
            Root.Append(text);
            Root.Append(_bar);
            Root.Append(_buttons);
        }

        public Gtk.Box Root { get; }

        public void Update(UploadSnapshot item)
        {
            var l = _page._l;
            _title.SetText(item.Title);
            _bar.SetFraction(item.Fraction);
            var phase = l["studio.phase." + item.Phase.ToString().ToLowerInvariant()];
            var amount = l.Format("studio.upload.progress", Formats.Bytes(item.SentBytes, CultureInfo.InvariantCulture), Formats.Bytes(item.TotalBytes, CultureInfo.InvariantCulture));
            _status.SetText(item.Phase == UploadPhase.Uploading ? phase + " · " + amount : phase);
            Ui.Named(_bar, item.Title + " · " + phase);
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
