using System.Globalization;
using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Upload;
using AlUmmahNow.Studio.Videos;

namespace AlUmmahNow.Studio.Linux;

/// <summary>Manage videos: search, filter, edit one or many, delete to Recently deleted, restore.</summary>
public sealed class VideosPage
{
    private readonly Localizer _l;
    private readonly VideosViewModel _vm;
    private readonly UploadsViewModel _uploads;
    private readonly Gtk.Window _window;
    private readonly Adw.ToastOverlay _toasts = Adw.ToastOverlay.New();
    private readonly Gtk.SearchEntry _search = Gtk.SearchEntry.New();
    private readonly Gtk.ListBox _suggestions = Gtk.ListBox.New();
    private readonly Gtk.Label _suggestionCount = Ui.Label("", dim: true);
    private readonly Gtk.Box _selectionBar = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    private readonly Gtk.Label _selectionLabel = Ui.Label("");
    private readonly Gtk.ListBox _list = Gtk.ListBox.New();
    private readonly Gtk.Label _empty = Ui.Label("", dim: true);
    private readonly Gtk.Label _error = Ui.Label("");
    private readonly Gtk.Button _more;
    private readonly Gtk.Spinner _spinner = Gtk.Spinner.New();
    private readonly Gtk.Expander _deletedExpander = Gtk.Expander.New("");
    private readonly Gtk.Box _deleted = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
    private bool _syncingSearch;

    public VideosPage(Localizer l, VideosViewModel vm, UploadsViewModel uploads, Gtk.Window window)
    {
        _l = l;
        _vm = vm;
        _uploads = uploads;
        _window = window;
        var scroll = Gtk.ScrolledWindow.New();
        var page = Ui.Page(10);
        scroll.SetChild(page);
        _toasts.SetChild(scroll);

        // Search and filter
        var top = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        _search.SetPlaceholderText(l["studio.videos.search"]);
        _search.SetHexpand(true);
        _search.SetSearchDelay(0);
        top.Append(Ui.Labelled(l["studio.videos.search"], _search));
        _search.GetParent()!.SetHexpand(true);
        _search.OnSearchChanged += (_, _) =>
        {
            if (_syncingSearch) return;
            _ = _vm.Search.TypedAsync(_search.GetText());
            RenderSuggestions();
        };
        _search.OnActivate += (_, _) => _vm.Search.Submit(_search.GetText());
        _search.OnStopSearch += (_, _) => _suggestions.SetVisible(false);
        // Recent searches appear when the empty field gets focus.
        var focus = Gtk.EventControllerFocus.New();
        focus.OnEnter += (_, _) => RenderSuggestions();
        _search.AddController(focus);
        var filters = new[] { StudioVisibilityFilter.All, StudioVisibilityFilter.Public, StudioVisibilityFilter.Private };
        var filter = Gtk.DropDown.NewFromStrings(filters.Select(f => l["studio.videos.filter." + f.ToString().ToLowerInvariant()]).ToArray());
        filter.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() == "selected") _vm.Visibility = filters[(int)filter.GetSelected()];
        };
        top.Append(Ui.Labelled(l["studio.videos.filter"], filter));
        top.Append(_spinner);
        page.Append(top);

        _suggestions.AddCssClass("boxed-list");
        _suggestions.SetVisible(false);
        _suggestions.OnRowActivated += (_, a) =>
        {
            if (a.Row.GetName() is { Length: > 0 } text) _vm.Search.Submit(text);
        };
        page.Append(_suggestionCount);
        page.Append(_suggestions);
        _error.AddCssClass("error");
        page.Append(_error);

        // Selection actions
        _selectionBar.Append(_selectionLabel);
        _selectionBar.Append(Ui.Button(l["studio.videos.bulkEdit"], () => _ = BulkEditAsync()));
        var playlistButton = Gtk.MenuButton.New();
        playlistButton.SetLabel(l["studio.videos.addToPlaylist"]);
        var playlistMenu = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        var playlistPopover = Gtk.Popover.New();
        playlistPopover.SetChild(playlistMenu);
        playlistButton.SetPopover(playlistPopover);
        playlistPopover.OnShow += (_, _) =>
        {
            Ui.Clear(playlistMenu);
            foreach (var p in _uploads.Playlists)
            {
                var b = Gtk.Button.NewWithLabel(p.Title);
                b.AddCssClass("flat");
                b.OnClicked += (_, _) =>
                {
                    playlistPopover.Popdown();
                    _ = _vm.AddSelectedToPlaylistAsync(p.Id);
                };
                playlistMenu.Append(b);
            }
        };
        _selectionBar.Append(playlistButton);
        _selectionBar.Append(Ui.Button(l["studio.videos.delete"], () => _ = DeleteAsync(_vm.Selected.ToList()), "destructive-action"));
        var all = Gtk.CheckButton.NewWithLabel(l["studio.videos.selectAll"]);
        all.OnToggled += (_, _) =>
        {
            _vm.SelectAll(all.GetActive());
            RenderList();
        };
        page.Append(all);
        page.Append(_selectionBar);

        _list.AddCssClass("boxed-list");
        _list.SetSelectionMode(Gtk.SelectionMode.None);
        page.Append(_list);
        page.Append(_empty);
        _more = Ui.Button(l["studio.videos.loadMore"], () => _ = _vm.LoadMoreAsync());
        _more.SetHalign(Gtk.Align.Center);
        page.Append(_more);

        _deletedExpander.SetChild(_deleted);
        page.Append(_deletedExpander);

        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(VideosViewModel.Items): RenderList(); break;
                case nameof(VideosViewModel.Selected): RenderSelection(); break;
                case nameof(VideosViewModel.IsLoading): RenderLoading(); break;
                case nameof(VideosViewModel.Deleted): RenderDeleted(); break;
                case nameof(VideosViewModel.ErrorKey): _error.SetText(_vm.ErrorKey is { } k ? _l[k] : ""); break;
                case nameof(VideosViewModel.NoticeKey):
                    if (_vm.NoticeKey is { } n) _toasts.AddToast(Adw.Toast.New(_l[n]));
                    break;
            }
        };
        _vm.Search.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.Search.Suggestions)) RenderSuggestions();
            if (e.PropertyName == nameof(_vm.Search.Text) && _search.GetText() != _vm.Search.Text)
            {
                _syncingSearch = true;
                _search.SetText(_vm.Search.Text);
                _syncingSearch = false;
            }
        };
        _vm.Search.Submitted += (_, _) => _suggestions.SetVisible(false);
        RenderList();
        RenderSelection();
        RenderDeleted();
    }

    public Gtk.Widget Root => _toasts;

    private void RenderSuggestions()
    {
        while (_suggestions.GetRowAtIndex(0) is { } row) _suggestions.Remove(row);
        var empty = _search.GetText().Trim().Length == 0;
        var items = empty ? _vm.Search.Recents.Select(r => new Suggestion(r, SuggestionKind.Query, null, null)).ToList() : _vm.Search.Suggestions.ToList();
        foreach (var s in items)
        {
            var row = Gtk.ListBoxRow.New();
            row.SetName(s.Text);
            var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            box.SetMarginStart(8);
            box.SetMarginEnd(8);
            box.SetMarginTop(4);
            box.SetMarginBottom(4);
            box.Append(Gtk.Image.NewFromIconName(s.Kind == SuggestionKind.Query ? "document-open-recent-symbolic" : "video-x-generic-symbolic"));
            var label = Ui.Label(s.Text);
            label.SetHexpand(true);
            box.Append(label);
            if (empty)
            {
                var remove = Gtk.Button.NewFromIconName("window-close-symbolic");
                remove.AddCssClass("flat");
                remove.SetTooltipText(_l["studio.upload.remove"] + ": " + s.Text);
                remove.OnClicked += (_, _) =>
                {
                    _vm.Search.RemoveRecent(s.Text);
                    RenderSuggestions();
                };
                box.Append(remove);
            }

            row.SetChild(box);
            _suggestions.Append(row);
        }

        if (empty && items.Count > 0)
        {
            var clearRow = Gtk.ListBoxRow.New();
            clearRow.SetActivatable(false);
            clearRow.SetChild(Ui.Button(_l["studio.videos.clearRecent"], async () =>
            {
                if (await Confirm.AskAsync(_window, _l["studio.videos.clearRecentTitle"], _l["studio.videos.clearRecentBody"], _l["studio.videos.clearRecent"], _l["common.cancel"]))
                {
                    _vm.Search.ClearRecents();
                    RenderSuggestions();
                }
            }, "flat"));
            _suggestions.Append(clearRow);
        }

        _suggestions.SetVisible(items.Count > 0);
        _suggestionCount.SetText(!empty && items.Count > 0 ? _l.Format("studio.videos.suggestions", items.Count) : "");
    }

    private void RenderLoading()
    {
        if (_vm.IsLoading) _spinner.Start();
        else _spinner.Stop();
        _spinner.SetVisible(_vm.IsLoading);
    }

    /// <summary>CI screenshots: "search", "edit", "bulk" or "delete".</summary>
    public void RunScene(string scene)
    {
        switch (scene)
        {
            case "search":
                _search.SetText("تفسير");
                _ = _vm.Search.TypedAsync("تفسير");
                break;
            case "edit" when _vm.Items.Count > 0:
                _ = EditAsync(_vm.Items[0]);
                break;
            case "bulk":
                _vm.SelectAll(true);
                RenderList();
                _ = BulkEditAsync();
                break;
            case "delete":
                _vm.SelectAll(true);
                RenderList();
                _ = DeleteAsync(_vm.Selected.ToList());
                break;
        }
    }

    private void RenderSelection()
    {
        var n = _vm.Selected.Count;
        _selectionBar.SetVisible(n > 0);
        _selectionLabel.SetText(_l.Format("studio.videos.selected", n));
    }

    private void RenderList()
    {
        while (_list.GetRowAtIndex(0) is { } r) _list.Remove(r);
        foreach (var v in _vm.Items)
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
            row.SetMarginStart(8);
            row.SetMarginEnd(8);
            row.SetMarginTop(6);
            row.SetMarginBottom(6);
            var check = Gtk.CheckButton.New();
            check.SetActive(_vm.IsSelected(v.Id));
            check.SetTooltipText(_l.Format("studio.videos.select", v.Title));
            check.OnToggled += (_, _) => _vm.SetSelected(v.Id, check.GetActive());
            var text = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
            text.SetHexpand(true);
            var title = Ui.Label(v.Title);
            title.SetEllipsize(Pango.EllipsizeMode.End);
            check.SetChild(null);
            text.Append(title);
            text.Append(Ui.Label(Meta(v), dim: true));
            row.Append(check);
            row.Append(text);
            var edit = Ui.Button(_l["studio.videos.edit"], () => _ = EditAsync(v), "flat");
            edit.SetTooltipText(_l.Format("studio.videos.editOne", v.Title));
            row.Append(edit);
            var delete = Ui.Button(_l["studio.videos.delete"], () => _ = DeleteAsync(new[] { v.Id }), "flat");
            delete.SetTooltipText(_l.Format("studio.videos.deleteOne", v.Title));
            row.Append(delete);
            _list.Append(row);
        }

        _list.SetVisible(_vm.Items.Count > 0);
        _empty.SetVisible(_vm.Items.Count == 0 && !_vm.IsLoading);
        _empty.SetText(_vm.Query.Length > 0 ? _l.Format("studio.videos.noResults", _vm.Query) : _l["studio.videos.empty"]);
        _more.SetVisible(_vm.HasMore);
        RenderSelection();
    }

    private string Meta(StudioVideo v)
    {
        var parts = new List<string> { _l["studio.upload.visibility." + (v.IsPublic ? "public" : "private")] };
        if (v.Status is StudioVideoStatus.Processing or StudioVideoStatus.Failed or StudioVideoStatus.Uploading) parts.Add(_l["studio.videos.status." + v.Status.ToString().ToLowerInvariant()]);
        parts.Add(_l.Format("studio.videos.views", _l.Number(v.Views)));
        if (v.CreatedAt is { } at) parts.Add(at.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (v.Category is { } c) parts.Add(_l["studio.category." + c]);
        return string.Join(" · ", parts);
    }

    private void RenderDeleted()
    {
        Ui.Clear(_deleted);
        _deletedExpander.SetLabel(_l.Format("studio.videos.deletedTitle", _vm.Deleted.Count));
        _deletedExpander.SetVisible(_vm.Deleted.Count > 0);
        foreach (var d in _vm.Deleted)
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            var text = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
            text.SetHexpand(true);
            text.Append(Ui.Label(d.Title));
            text.Append(Ui.Label(_l.Format("studio.videos.deletedOn", d.DeletedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), (d.DeletedAt + RecentlyDeleted.Retention).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), dim: true));
            row.Append(text);
            var restore = Ui.Button(_l["studio.videos.restore"], () => _ = _vm.RestoreAsync(d.VideoId));
            restore.SetTooltipText(_l.Format("studio.videos.restoreOne", d.Title));
            row.Append(restore);
            row.Append(Ui.Button(_l["studio.videos.deleteNow"], async () =>
            {
                if (await Confirm.AskAsync(_window, _l.Format("studio.videos.deleteNowTitle", Localizer.Isolate(d.Title)), _l["studio.videos.deleteNowBody"], _l["studio.videos.deleteNow"], _l["common.cancel"]))
                {
                    await _vm.DeleteNowAsync(d.VideoId);
                }
            }, "flat"));
            _deleted.Append(row);
        }
    }

    private async Task DeleteAsync(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return;
        var title = ids.Count == 1
            ? _l.Format("studio.videos.deleteTitleOne", Localizer.Isolate(_vm.Items.FirstOrDefault(v => v.Id == ids[0])?.Title ?? ""))
            : _l.Format("studio.videos.deleteTitle", ids.Count);
        if (await Confirm.AskAsync(_window, title, _l["studio.videos.deleteBody"], _l["studio.videos.delete"], _l["common.cancel"])) await _vm.DeleteAsync(ids);
    }

    private async Task EditAsync(StudioVideo v)
    {
        var edit = new VideoEdit(v);
        var form = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        var title = Gtk.Entry.New();
        title.SetText(edit.Title);
        form.Append(Ui.Labelled(_l["studio.upload.titlePattern"], title));
        var description = Gtk.TextView.New();
        description.SetWrapMode(Gtk.WrapMode.WordChar);
        description.SetSizeRequest(420, 90);
        description.GetBuffer().SetText(edit.Description, -1);
        form.Append(Ui.Label(_l["studio.upload.description"]));
        var frame = Gtk.Frame.New(null);
        frame.SetChild(description);
        form.Append(frame);
        var categories = new[] { (string?)null }.Concat(StudioCatalog.Categories).ToList();
        var category = Gtk.DropDown.NewFromStrings(categories.Select(c => c == null ? _l["studio.upload.none"] : _l["studio.category." + c]).ToArray());
        category.SetSelected((uint)Math.Max(0, categories.IndexOf(edit.Category)));
        form.Append(Ui.Labelled(_l["studio.upload.category"], category));
        var languages = new[] { (string?)null }.Concat(StudioCatalog.Languages).ToList();
        var language = Gtk.DropDown.NewFromStrings(languages.Select(c => c == null ? _l["studio.upload.none"] : _l["studio.language." + c]).ToArray());
        language.SetSelected((uint)Math.Max(0, languages.IndexOf(edit.Language)));
        form.Append(Ui.Labelled(_l["studio.upload.language"], language));
        var visibility = Gtk.DropDown.NewFromStrings(new[] { _l["studio.upload.visibility.private"], _l["studio.upload.visibility.public"] });
        visibility.SetSelected(edit.IsPublic ? 1u : 0u);
        form.Append(Ui.Labelled(_l["studio.upload.visibility"], visibility));
        var tags = Gtk.Entry.New();
        tags.SetText(edit.Tags);
        form.Append(Ui.Labelled(_l["studio.upload.tags"], tags));

        if (!await Ask(_l["studio.videos.editTitle"], form, _l["studio.videos.save"])) return;
        edit.Title = title.GetText();
        var buffer = description.GetBuffer();
        buffer.GetBounds(out var s, out var e);
        edit.Description = buffer.GetText(s, e, false);
        edit.Category = categories[(int)category.GetSelected()];
        edit.Language = languages[(int)language.GetSelected()];
        edit.IsPublic = visibility.GetSelected() == 1;
        edit.Tags = tags.GetText();
        if (edit.Problem is { } p)
        {
            _toasts.AddToast(Adw.Toast.New(_l[p]));
            return;
        }

        await _vm.SaveAsync(v.Id, edit.ChangesFrom());
    }

    private async Task BulkEditAsync()
    {
        var n = _vm.Selected.Count;
        var form = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        var visibility = Gtk.DropDown.NewFromStrings(new[] { _l["studio.videos.noChange"], _l["studio.upload.visibility.public"], _l["studio.upload.visibility.private"] });
        form.Append(Ui.Labelled(_l["studio.upload.visibility"], visibility));
        var categories = new[] { (string?)null, "" }.Concat(StudioCatalog.Categories).ToList();
        var category = Gtk.DropDown.NewFromStrings(categories.Select(c => c == null ? _l["studio.videos.noChange"] : c.Length == 0 ? _l["studio.upload.none"] : _l["studio.category." + c]).ToArray());
        form.Append(Ui.Labelled(_l["studio.upload.category"], category));
        var languages = new[] { (string?)null }.Concat(StudioCatalog.Languages).ToList();
        var language = Gtk.DropDown.NewFromStrings(languages.Select(c => c == null ? _l["studio.videos.noChange"] : _l["studio.language." + c]).ToArray());
        form.Append(Ui.Labelled(_l["studio.upload.language"], language));
        var add = Gtk.Entry.New();
        form.Append(Ui.Labelled(_l["studio.videos.addTags"], add));
        var remove = Gtk.Entry.New();
        form.Append(Ui.Labelled(_l["studio.videos.removeTags"], remove));
        if (!await Ask(_l.Format("studio.videos.bulkEditTitle", n), form, _l["studio.videos.apply"])) return;
        await _vm.BulkEditAsync(new BulkVideoChanges
        {
            IsPublic = visibility.GetSelected() switch { 1 => true, 2 => false, _ => null },
            Category = categories[(int)category.GetSelected()],
            Language = languages[(int)language.GetSelected()],
            AddTags = UploadPlanner.ParseTags(add.GetText()),
            RemoveTags = UploadPlanner.ParseTags(remove.GetText()),
        });
    }

    /// <summary>An in-app form dialog; Cancel closes it (Escape), the action must be chosen.</summary>
    private Task<bool> Ask(string heading, Gtk.Widget form, string action)
    {
        var done = new TaskCompletionSource<bool>();
        var dialog = Adw.AlertDialog.New(heading, null);
        dialog.SetExtraChild(form);
        dialog.AddResponse("cancel", _l["common.cancel"]);
        dialog.AddResponse("ok", action);
        dialog.SetResponseAppearance("ok", Adw.ResponseAppearance.Suggested);
        dialog.SetCloseResponse("cancel");
        dialog.OnResponse += (_, a) => done.TrySetResult(a.Response == "ok");
        dialog.Present(_window);
        return done.Task;
    }
}
