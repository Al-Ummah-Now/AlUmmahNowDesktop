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
    // Lantern Glass: a grid of thumbnail cards (Edit and Delete in each card's footer).
    private const int Gap = 16, CardWidth = 280;

    /// <summary>
    /// The title's box in pixels, the same for one line or two and for either script: two Latin heading lines take 43 px
    /// and two Arabic ones 36, so a box of 40 grew for a wrapped English title and pushed that card's meta line down.
    /// 44 holds both, and the title never takes more than two lines.
    /// </summary>
    private const int TitleBox = 44;
    private readonly Gtk.FlowBox _list = Gtk.FlowBox.New();
    private readonly Dictionary<string, Gdk.Texture> _thumbs = new();
    private readonly List<Gtk.Widget> _thumbBoxes = new();
    private int _thumbHeight = 146;
    private readonly HashSet<string> _thumbsLoading = new();
    private readonly Gtk.Label _empty = Ui.Label("", dim: true);
    private readonly Gtk.Label _error = Ui.Label("");
    private readonly Gtk.Button _more;
    private readonly Gtk.Spinner _spinner = Gtk.Spinner.New();
    private readonly Disclosure _deletedRow;
    private readonly Gtk.ScrolledWindow _scroll;
    private readonly Gtk.Box _deleted = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
    private bool _syncingSearch;
    private Gtk.Box? _deletedCard;

    /// <summary>The header's search: show this text in the search field and search for it.</summary>
    public void SearchFor(string text)
    {
        _search.SetText(text);
        _vm.Search.Submit(text);
    }

    public VideosPage(Localizer l, VideosViewModel vm, UploadsViewModel uploads, Gtk.Window window)
    {
        _l = l;
        _vm = vm;
        _uploads = uploads;
        _window = window;
        var scroll = _scroll = Gtk.ScrolledWindow.New();
        var page = Ui.Page(14);
        page.SetMarginStart(24);
        page.SetMarginEnd(24);
        scroll.SetChild(page);
        _toasts.SetChild(scroll);

        // Search and filter
        var top = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        // The visible label names the field (AT-SPI labelled-by), so no placeholder repeats it.
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
        // Empty status lines are hidden, not blank: a blank label still takes a line and the page spacing.
        _suggestionCount.SetVisible(false);
        page.Append(_suggestionCount);
        page.Append(_suggestions);
        _error.AddCssClass("error");
        _error.SetVisible(false);
        page.Append(_error);

        // Selection actions: a gold bar while videos are selected.
        _selectionBar.AddCssClass("selection-bar");
        _selectionLabel.AddCssClass("heading");
        // One line ("8 selected" / «٨ محدد»): a wrapping label broke the count onto its own line.
        _selectionLabel.SetWrap(false);
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
                var b = Gtk.Button.NewWithLabel(p.TitleFor(_l.Language));
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

        _list.SetSelectionMode(Gtk.SelectionMode.None);
        _list.SetHomogeneous(true);
        _list.SetColumnSpacing(Gap);
        _list.SetRowSpacing(Gap);
        _list.SetMinChildrenPerLine(2);
        _list.SetMaxChildrenPerLine(6);
        _list.SetValign(Gtk.Align.Start);
        // FlowBox picks its columns from the cards' natural width, which follows the text, so English and Arabic got a
        // different count at the same window width. The count comes from the grid's own width instead.
        var columns = 0;
        _list.AddTickCallback((_, _) =>
        {
            var width = _list.GetWidth();
            var fit = Math.Clamp((width + Gap) / (CardWidth + Gap), 2, 6);
            if (fit != columns)
            {
                columns = fit;
                _list.SetMinChildrenPerLine((uint)fit);
                _list.SetMaxChildrenPerLine((uint)fit);
            }

            // Thumbnails are 16:9 of the narrowest card's width, one height for every card: the grid hands its spare
            // pixels to some columns, so measuring each card made one column's thumbnails a pixel taller than the rest.
            var narrowest = _thumbBoxes.Select(t => t.GetWidth()).Where(w => w > 0).DefaultIfEmpty(0).Min();
            var height = narrowest * 9 / 16;
            if (height > 0 && height != _thumbHeight)
            {
                _thumbHeight = height;
                foreach (var t in _thumbBoxes) t.SetSizeRequest(-1, height);
            }

            return true;
        });
        page.Append(_list);
        page.Append(_empty);
        _more = Ui.Button(l["studio.videos.loadMore"], () => _ = _vm.LoadMoreAsync());
        _more.SetHalign(Gtk.Align.Center);
        page.Append(_more);

        _deletedRow = new Disclosure(l, "", _deleted);
        _deletedCard = Ui.Card(_deletedRow.Root);
        page.Append(_deletedCard);

        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(VideosViewModel.Items): RenderList(); break;
                case nameof(VideosViewModel.Selected): RenderSelection(); break;
                case nameof(VideosViewModel.IsLoading): RenderLoading(); break;
                case nameof(VideosViewModel.ShowEmpty): RenderEmpty(); break;
                case nameof(VideosViewModel.Deleted): RenderDeleted(); break;
                case nameof(VideosViewModel.ErrorKey):
                    _error.SetText(_vm.ErrorKey is { } k ? _l[k] : "");
                    _error.SetVisible(_vm.ErrorKey != null);
                    break;
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
                var remove = Gtk.Button.NewWithLabel(_l["studio.upload.remove"]);
                remove.AddCssClass("flat");
                Ui.Named(remove, _l["studio.upload.remove"] + ": " + s.Text);
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
        _suggestionCount.SetVisible(!empty && items.Count > 0);
    }

    private void RenderLoading()
    {
        if (_vm.IsLoading) _spinner.Start();
        else _spinner.Stop();
        _spinner.SetVisible(_vm.IsLoading);
    }

    /// <summary>CI screenshots: "search", "edit", "selected", "bulk", "delete", "deleted" (the closed row) or "deleted-open".</summary>
    public void RunScene(string scene)
    {
        switch (scene)
        {
            case "search":
            {
                // A query that visibly narrows the list (the Seerah video only), in the page's language.
                var q = _l.Language == "ar" ? "السيرة" : "Seerah";
                _search.SetText(q);
                _vm.Search.Submit(q);
                break;
            }
            case "edit" when _vm.Items.Count > 0:
                _ = EditAsync(_vm.Items[0]);
                break;
            case "selected":
                _vm.SelectAll(true);
                RenderList();
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
            case "deleted":
            case "deleted-open":
                if (scene == "deleted-open") _deletedRow.Open();
                // To the end of the page, once the row has opened
                GLib.Functions.TimeoutAdd(0, 1500, () =>
                {
                    if (_scroll.GetVadjustment() is { } v)
                    {
                        v.SetValue(v.GetUpper());
                        // Kept: the scenario may press the row afterwards, and the page grows
                        DeletedIsShown = _deletedCard?.GetVisible() == true && v.GetValue() + v.GetPageSize() >= v.GetUpper() - 1 && (v.GetUpper() <= v.GetPageSize() || v.GetValue() > 0);
                    }

                    return false;
                });
                break;
        }
    }

    /// <summary>CI: the page was scrolled to its end, where the deleted videos are.</summary>
    public bool DeletedIsShown { get; private set; }

    private void RenderSelection()
    {
        var n = _vm.Selected.Count;
        _selectionBar.SetVisible(n > 0);
        _selectionLabel.SetText(_l.Format("studio.videos.selected", n));
    }

    /// <summary>Where a check button's label starts, from its own start edge.</summary>
    private void RenderList()
    {
        while (_list.GetChildAtIndex(0) is { } c) _list.Remove(c);
        _thumbBoxes.Clear();
        foreach (var v in _vm.Items) _list.Append(Card(v));

        _list.SetVisible(_vm.Items.Count > 0);
        RenderEmpty();
        _more.SetVisible(_vm.HasMore);
        RenderSelection();
    }

    /// <summary>
    /// A video card: the thumbnail (night tile until it loads) with visibility/status and duration on it;
    /// the title as the selection checkbox's label (Orca reads "title, check box"); views and date;
    /// Edit and Delete in the footer, end-aligned.
    /// </summary>
    private Gtk.Widget Card(StudioVideo v)
    {
        var title = v.TitleFor(_l.Language);
        var thumb = Gtk.Overlay.New();
        thumb.AddCssClass("video-thumb");
        thumb.SetOverflow(Gtk.Overflow.Hidden);
        // The night tile sizes the card; a loaded thumbnail is an overlay on it, because an overlay never adds its own
        // size (a picture's natural width is the image's, which would widen every card and drop grid columns).
        var tile = Ui.FileTile(260, 146);
        tile.SetHexpand(true);
        thumb.SetChild(tile);
        // The tile's icon expands, and that expand reached the thumbnail, which then took a row's spare height whenever a
        // neighbour's card was taller. The thumbnail never takes spare height; the card's body does.
        thumb.SetVexpand(false);
        thumb.SetValign(Gtk.Align.Start);
        if (v.ThumbnailUrl is { Length: > 0 } url && _thumbs.TryGetValue(url, out var texture))
        {
            var picture = Gtk.Picture.NewForPaintable(texture);
            picture.SetContentFit(Gtk.ContentFit.Cover);
            picture.SetCanShrink(true);
            thumb.AddOverlay(picture);
        }
        else if (v.ThumbnailUrl is { Length: > 0 } u)
        {
            _ = LoadThumbAsync(u);
        }

        // 16:9 at the width the grid gives the cards (set from the grid's tick): a fixed height cropped the title card's top
        // and bottom (the lantern and the channel mark) at widths where the card is wide and short.
        thumb.SetSizeRequest(-1, _thumbHeight);
        _thumbBoxes.Add(thumb);
        var pill = Gtk.Label.New(v.Status is StudioVideoStatus.Processing or StudioVideoStatus.Failed or StudioVideoStatus.Uploading
            ? _l["studio.videos.status." + v.Status.ToString().ToLowerInvariant()]
            : _l["studio.upload.visibility." + (v.IsPublic ? "public" : "private")]);
        pill.AddCssClass("pill-tag");
        pill.AddCssClass(v.Status == StudioVideoStatus.Failed ? "bad" : v.Status is StudioVideoStatus.Processing or StudioVideoStatus.Uploading ? "busy" : v.IsPublic ? "good" : "quiet");
        pill.SetHalign(Gtk.Align.End);
        pill.SetValign(Gtk.Align.Start);
        thumb.AddOverlay(pill);
        if (v.DurationSeconds is { } d && d > 0)
        {
            var duration = Gtk.Label.New(_l.Duration(d));
            duration.AddCssClass("duration");
            duration.SetHalign(Gtk.Align.End);
            duration.SetValign(Gtk.Align.End);
            thumb.AddOverlay(duration);
        }

        // Our own label as the check button's child: the label a CheckButton makes from NewWithLabel is internal (GetChild
        // returns null), so it could never wrap and a long title ran one line to the card's edge. This one wraps to two
        // lines inside the width the card gets, then ellipsizes; a small natural width keeps it from widening the card.
        var label = Gtk.Label.New(title);
        label.SetWrap(true);
        label.SetWrapMode(Pango.WrapMode.WordChar);
        label.SetLines(2);
        label.SetEllipsize(Pango.EllipsizeMode.End);
        label.SetMaxWidthChars(18);
        label.SetXalign(0);
        label.SetHexpand(true);
        label.AddCssClass("heading");
        var check = Gtk.CheckButton.New();
        check.SetChild(label);
        check.SetActive(_vm.IsSelected(v.Id));
        Ui.Named(check, _l.Format("studio.videos.select", title));
        check.OnToggled += (_, _) => _vm.SetSelected(v.Id, check.GetActive());

        var meta = Ui.Label(_l.Join(_l.Views(v.Views), v.CreatedAt is { } at ? _l.Date(at) : null, v.Category is { } c ? _l["studio.category." + c] : null), dim: true);
        meta.AddCssClass("caption");
        // Ellipsized, so the line's natural width never sets the card's minimum width: English and Arabic get the same
        // number of columns at the same window width.
        meta.SetEllipsize(Pango.EllipsizeMode.End);
        meta.SetMaxWidthChars(24);
        var titleRow = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        titleRow.SetSizeRequest(-1, TitleBox);
        check.SetValign(Gtk.Align.Start);
        titleRow.Append(check);
        // The body's spacing and top margin give back the 4 px the title box grew by, so two rows still fit whole in the
        // 1440x900 window.
        var body = Gtk.Box.New(Gtk.Orientation.Vertical, 3);
        body.Append(titleRow);
        body.Append(meta);

        var footer = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        var edit = Ui.Button(_l["studio.videos.edit"], () => _ = EditAsync(v), "flat");
        Ui.Named(edit, _l.Format("studio.videos.editOne", title));
        footer.Append(edit);
        var delete = Ui.Button(_l["studio.videos.delete"], () => _ = DeleteAsync(new[] { v.Id }), "flat");
        delete.AddCssClass("danger");
        Ui.Named(delete, _l.Format("studio.videos.deleteOne", title));
        footer.Append(delete);

        var card = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        card.AddCssClass("card");
        card.AddCssClass("video-card");
        if (_vm.IsSelected(v.Id)) card.AddCssClass("selected");
        card.Append(thumb);
        body.SetMarginStart(14);
        body.SetMarginEnd(14);
        body.SetMarginTop(7);
        // The title and meta line must not set the card's minimum width: in English it came out wider than in Arabic, so
        // the grid's columns no longer fit the window. An external horizontal policy ignores the text's width request;
        // the body takes the width the grid gives the card and the labels wrap or ellipsize inside it.
        var bodyFrame = Gtk.ScrolledWindow.New();
        bodyFrame.SetPolicy(Gtk.PolicyType.External, Gtk.PolicyType.Never);
        bodyFrame.SetPropagateNaturalHeight(true);
        bodyFrame.SetChild(body);
        bodyFrame.SetVexpand(true);
        card.Append(bodyFrame);
        var bar = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        bar.AddCssClass("card-footer");
        bar.AddCssClass("quiet");
        footer.SetHalign(Gtk.Align.End);
        footer.SetHexpand(true);
        bar.Append(footer);
        card.Append(bar);
        card.SetSizeRequest(240, -1);
        check.OnToggled += (_, _) =>
        {
            if (check.GetActive()) card.AddCssClass("selected");
            else card.RemoveCssClass("selected");
        };
        return card;
    }

    private async Task LoadThumbAsync(string url)
    {
        if (!_thumbsLoading.Add(url)) return;
        try
        {
            var data = await Ui.ThumbnailBytesAsync(url);
            _thumbs[url] = Gdk.Texture.NewFromBytes(GLib.Bytes.New(data));
            RenderList();
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException || ex is GLib.GException)
        {
            // The night tile stays.
        }
    }

    private void RenderEmpty()
    {
        _empty.SetVisible(_vm.ShowEmpty);
        _empty.SetText(_vm.Query.Length > 0 ? _l.Format("studio.videos.noResults", _vm.Query) : _l["studio.videos.empty"]);
    }

    private string Meta(StudioVideo v)
    {
        var parts = new List<string> { _l["studio.upload.visibility." + (v.IsPublic ? "public" : "private")] };
        if (v.Status is StudioVideoStatus.Processing or StudioVideoStatus.Failed or StudioVideoStatus.Uploading) parts.Add(_l["studio.videos.status." + v.Status.ToString().ToLowerInvariant()]);
        parts.Add(_l.Views(v.Views));
        if (v.CreatedAt is { } at) parts.Add(_l.Date(at));
        if (v.Category is { } c) parts.Add(_l["studio.category." + c]);
        return _l.Join(parts);
    }

    private void RenderDeleted()
    {
        Ui.Clear(_deleted);
        _deletedRow.SetTitle(_l.Format("studio.videos.deletedTitle", _vm.Deleted.Count));
        _deletedCard?.SetVisible(_vm.Deleted.Count > 0);
        foreach (var d in _vm.Deleted)
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            var text = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
            text.SetHexpand(true);
            text.Append(Ui.Label(d.Title));
            text.Append(Ui.Label(DeletedMeta(d), dim: true));
            row.Append(text);
            if (d.CanRestore)
            {
                var restore = Ui.Button(_l["studio.videos.restore"], () => _ = _vm.RestoreAsync(d.Id));
                Ui.Named(restore, _l.Format("studio.videos.restoreOne", d.Title));
                row.Append(restore);
                row.Append(Ui.Button(_l["studio.videos.deleteNow"], async () =>
                {
                    if (await Confirm.AskAsync(_window, _l.Format("studio.videos.deleteNowTitle", Localizer.Isolate(d.Title)), _l["studio.videos.deleteNowBody"], _l["studio.videos.deleteNow"], _l["common.cancel"]))
                    {
                        await _vm.DeleteNowAsync(d.Id);
                    }
                }, "flat"));
            }

            _deleted.Append(row);
        }
    }

    private string DeletedMeta(DeletedVideo d) =>
        d.CanRestore
            ? _l.Format("studio.videos.deletedOn", _l.Date(d.DeletedAt), _l.Date(d.PurgeAfter ?? d.DeletedAt.AddDays(VideosViewModel.RestoreDays)))
            : _l["studio.videos.queuedForDeletion"];

    private async Task DeleteAsync(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return;
        var title = ids.Count == 1
            ? _l.Format("studio.videos.deleteTitleOne", Localizer.Isolate(_vm.Items.FirstOrDefault(v => v.Id == ids[0])?.Title ?? ""))
            : _l.Format("studio.videos.deleteTitle", ids.Count);
        if (await Confirm.AskAsync(_window, title, _l.Format("studio.videos.deleteBody", VideosViewModel.RestoreDays), _l["studio.videos.delete"], _l["common.cancel"])) await _vm.DeleteAsync(ids);
    }

    private async Task EditAsync(StudioVideo v)
    {
        var edit = new VideoEdit(v);
        var form = Ui.Form();
        var title = Gtk.Entry.New();
        title.SetText(edit.Title);
        Ui.FormRow(form, _l["studio.upload.titlePattern"], title);
        var description = Gtk.TextView.New();
        description.SetWrapMode(Gtk.WrapMode.WordChar);
        description.SetSizeRequest(-1, 90);
        description.GetBuffer().SetText(edit.Description, -1);
        var frame = Gtk.Frame.New(null);
        frame.SetChild(description);
        Ui.FormRow(form, _l["studio.upload.description"], frame, top: true);
        var categories = new[] { (string?)null }.Concat(StudioCatalog.Categories).ToList();
        var category = Gtk.DropDown.NewFromStrings(categories.Select(c => c == null ? _l["studio.upload.none"] : _l["studio.category." + c]).ToArray());
        category.SetSelected((uint)Math.Max(0, categories.IndexOf(edit.Category)));
        Ui.FormRow(form, _l["studio.upload.category"], category);
        var languages = new[] { (string?)null }.Concat(StudioCatalog.Languages).ToList();
        var language = Gtk.DropDown.NewFromStrings(languages.Select(c => c == null ? _l["studio.upload.none"] : _l["studio.language." + c]).ToArray());
        language.SetSelected((uint)Math.Max(0, languages.IndexOf(edit.Language)));
        Ui.FormRow(form, _l["studio.upload.language"], language);
        var visibility = Gtk.DropDown.NewFromStrings(new[] { _l["studio.upload.visibility.private"], _l["studio.upload.visibility.public"] });
        visibility.SetSelected(edit.IsPublic ? 1u : 0u);
        Ui.FormRow(form, _l["studio.upload.visibility"], visibility);
        var tags = Gtk.Entry.New();
        tags.SetText(edit.Tags);
        Ui.FormRow(form, _l["studio.upload.tags"], tags);

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
        var form = Ui.Form();
        var visibility = Gtk.DropDown.NewFromStrings(new[] { _l["studio.videos.noChange"], _l["studio.upload.visibility.public"], _l["studio.upload.visibility.private"] });
        Ui.FormRow(form, _l["studio.upload.visibility"], visibility);
        var categories = new[] { (string?)null, "" }.Concat(StudioCatalog.Categories).ToList();
        var category = Gtk.DropDown.NewFromStrings(categories.Select(c => c == null ? _l["studio.videos.noChange"] : c.Length == 0 ? _l["studio.upload.none"] : _l["studio.category." + c]).ToArray());
        Ui.FormRow(form, _l["studio.upload.category"], category);
        var languages = new[] { (string?)null }.Concat(StudioCatalog.Languages).ToList();
        var language = Gtk.DropDown.NewFromStrings(languages.Select(c => c == null ? _l["studio.videos.noChange"] : _l["studio.language." + c]).ToArray());
        Ui.FormRow(form, _l["studio.upload.language"], language);
        var add = Gtk.Entry.New();
        Ui.FormRow(form, _l["studio.videos.addTags"], add);
        var remove = Gtk.Entry.New();
        Ui.FormRow(form, _l["studio.videos.removeTags"], remove);
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
