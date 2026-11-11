using System.Globalization;
using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Upload;
using AlUmmahNow.Studio.Videos;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>Manage videos: search, filter, edit one or many, delete to Recently deleted, restore.</summary>
internal sealed class VideosPage
{
    private readonly Localizer _l;
    private readonly VideosViewModel _vm;
    private readonly UploadsViewModel _uploads;
    private readonly Func<XamlRoot?> _root;
    private readonly FlowDirection _direction;
    private readonly AutoSuggestBox _search;
    private readonly StackPanel _selectionBar = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock _selectionLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _list = new() { Spacing = 4 };
    private readonly TextBlock _empty = new() { Opacity = 0.8 };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    private readonly InfoBar _notice = new() { Severity = InfoBarSeverity.Success, IsClosable = true };
    private readonly Button _more;
    private readonly ProgressRing _loading = new() { Width = 20, Height = 20 };
    private readonly Expander _deletedExpander = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _deleted = new() { Spacing = 6 };
    private readonly CheckBox _all;
    private readonly MenuFlyout _playlistMenu = new();

    public VideosPage(Localizer l, VideosViewModel vm, UploadsViewModel uploads, Func<XamlRoot?> root)
    {
        _l = l;
        _vm = vm;
        _uploads = uploads;
        _root = root;
        _direction = l.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Root = new StackPanel { Spacing = 10, Padding = new Thickness(20) };

        _search = new AutoSuggestBox { PlaceholderText = l["studio.videos.search"], QueryIcon = new SymbolIcon(Symbol.Find), MinWidth = 360 };
        AutomationProperties.SetName(_search, l["studio.videos.search"]);
        AutomationProperties.SetAutomationId(_search, "VideoSearch");
        _search.TextChanged += (s, e) =>
        {
            if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput) _ = _vm.Search.TypedAsync(s.Text);
        };
        _search.GotFocus += (_, _) =>
        {
            if (_search.Text.Length == 0) ShowRecents();
        };
        _search.SuggestionChosen += (_, e) =>
        {
            if (e.SelectedItem is SuggestionItem i && i.Action != null) i.Action();
        };
        _search.QuerySubmitted += (_, e) =>
        {
            if (e.ChosenSuggestion is SuggestionItem i)
            {
                if (i.Action == null) _vm.Search.Submit(i.Text);
            }
            else
            {
                _vm.Search.Submit(e.QueryText);
            }
        };
        var filters = new[] { StudioVisibilityFilter.All, StudioVisibilityFilter.Public, StudioVisibilityFilter.Private };
        var filter = new ComboBox { Header = l["studio.videos.filter"], ItemsSource = filters.Select(f => l["studio.videos.filter." + f.ToString().ToLowerInvariant()]).ToList(), SelectedIndex = 0, MinWidth = 160 };
        filter.SelectionChanged += (_, _) => _vm.Visibility = filters[Math.Max(0, filter.SelectedIndex)];
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        _search.VerticalAlignment = VerticalAlignment.Bottom;
        top.Children.Add(_search);
        top.Children.Add(filter);
        top.Children.Add(_loading);
        Root.Children.Add(top);
        Root.Children.Add(_error);
        Root.Children.Add(_notice);

        _selectionBar.Children.Add(_selectionLabel);
        var bulk = new Button { Content = l["studio.videos.bulkEdit"] };
        bulk.Click += async (_, _) => await BulkEditAsync();
        _selectionBar.Children.Add(bulk);
        var playlists = new DropDownButton { Content = l["studio.videos.addToPlaylist"], Flyout = _playlistMenu };
        _playlistMenu.Opening += (_, _) =>
        {
            _playlistMenu.Items.Clear();
            foreach (var p in _uploads.Playlists)
            {
                var item = new MenuFlyoutItem { Text = p.Title };
                item.Click += (_, _) => _ = _vm.AddSelectedToPlaylistAsync(p.Id);
                _playlistMenu.Items.Add(item);
            }
        };
        _selectionBar.Children.Add(playlists);
        var delete = new Button { Content = l["studio.videos.delete"] };
        delete.Click += async (_, _) => await DeleteAsync(_vm.Selected.ToList());
        _selectionBar.Children.Add(delete);
        _all = new CheckBox { Content = l["studio.videos.selectAll"] };
        _all.Click += (_, _) =>
        {
            _vm.SelectAll(_all.IsChecked == true);
            RenderList();
        };
        Root.Children.Add(_all);
        Root.Children.Add(_selectionBar);
        Root.Children.Add(_list);
        Root.Children.Add(_empty);
        _more = new Button { Content = l["studio.videos.loadMore"], HorizontalAlignment = HorizontalAlignment.Center };
        _more.Click += (_, _) => _ = _vm.LoadMoreAsync();
        Root.Children.Add(_more);
        _deletedExpander.Content = _deleted;
        Root.Children.Add(_deletedExpander);

        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(VideosViewModel.Items): RenderList(); break;
                case nameof(VideosViewModel.Selected): RenderSelection(); break;
                case nameof(VideosViewModel.IsLoading): _loading.IsActive = _vm.IsLoading; break;
                case nameof(VideosViewModel.Deleted): RenderDeleted(); break;
                case nameof(VideosViewModel.ErrorKey):
                    _error.Message = _vm.ErrorKey is { } k ? _l[k] : "";
                    _error.IsOpen = _vm.ErrorKey != null;
                    break;
                case nameof(VideosViewModel.NoticeKey):
                    _notice.Message = _vm.NoticeKey is { } n ? _l[n] : "";
                    _notice.IsOpen = _vm.NoticeKey != null;
                    break;
            }
        };
        _vm.Search.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.Search.Suggestions)) ShowSuggestions();
            if (e.PropertyName == nameof(_vm.Search.Text) && _search.Text != _vm.Search.Text) _search.Text = _vm.Search.Text;
        };
        RenderList();
        RenderSelection();
        RenderDeleted();
    }

    public StackPanel Root { get; }

    private sealed class SuggestionItem
    {
        public SuggestionItem(string text, string kind, Action? action = null)
        {
            Text = text;
            Kind = kind;
            Action = action;
        }

        public string Text { get; }
        public string Kind { get; }
        public Action? Action { get; }

        // Narrator reads "<title>, <kind>".
        public override string ToString() => Kind.Length == 0 ? Text : Text + ", " + Kind;
    }

    private void ShowSuggestions()
    {
        var items = _vm.Search.Suggestions.Select(s => new SuggestionItem(s.Text, s.Kind == SuggestionKind.Query ? _l["studio.videos.recent"] : _l["studio.tab.videos"])).ToList();
        _search.ItemsSource = items;
        Announce(items.Count > 0 ? _l.Format("studio.videos.suggestions", items.Count) : "");
    }

    private void ShowRecents()
    {
        var items = _vm.Search.Recents.Select(r => new SuggestionItem(r, _l["studio.videos.recent"])).ToList();
        if (items.Count > 0) items.Add(new SuggestionItem(_l["studio.videos.clearRecent"], "", () => _ = ClearRecentsAsync()));
        _search.ItemsSource = items;
        _search.IsSuggestionListOpen = items.Count > 0;
    }

    private async Task ClearRecentsAsync()
    {
        _search.Text = "";
        if (_root() is { } root && await Confirm.AskAsync(root, _direction, _l["studio.videos.clearRecentTitle"], _l["studio.videos.clearRecentBody"], _l["studio.videos.clearRecent"], _l["common.cancel"]))
        {
            _vm.Search.ClearRecents();
        }
    }

    private void Announce(string text)
    {
        if (text.Length == 0) return;
        var peer = FrameworkElementAutomationPeer.FromElement(_search) ?? FrameworkElementAutomationPeer.CreatePeerForElement(_search);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.MostRecent, text, "suggestions");
    }

    /// <summary>CI screenshots: "search", "edit", "bulk" or "delete".</summary>
    public void RunScene(string scene)
    {
        switch (scene)
        {
            case "search":
                _search.Text = "تفسير";
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
        _selectionBar.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        _selectionLabel.Text = _l.Format("studio.videos.selected", n);
    }

    private void RenderList()
    {
        _list.Children.Clear();
        foreach (var v in _vm.Items)
        {
            var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(8, 6, 8, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var check = new CheckBox { IsChecked = _vm.IsSelected(v.Id), MinWidth = 0 };
            AutomationProperties.SetName(check, _l.Format("studio.videos.select", v.Title));
            check.Click += (_, _) => _vm.SetSelected(v.Id, check.IsChecked == true);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = v.Title, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = Meta(v), Opacity = 0.75, FontSize = 12 });
            Grid.SetColumn(text, 1);
            var edit = new Button { Content = _l["studio.videos.edit"] };
            AutomationProperties.SetName(edit, _l.Format("studio.videos.editOne", v.Title));
            edit.Click += async (_, _) => await EditAsync(v);
            Grid.SetColumn(edit, 2);
            var delete = new Button { Content = _l["studio.videos.delete"] };
            AutomationProperties.SetName(delete, _l.Format("studio.videos.deleteOne", v.Title));
            delete.Click += async (_, _) => await DeleteAsync(new[] { v.Id });
            Grid.SetColumn(delete, 3);
            grid.Children.Add(check);
            grid.Children.Add(text);
            grid.Children.Add(edit);
            grid.Children.Add(delete);
            _list.Children.Add(grid);
        }

        _empty.Visibility = _vm.Items.Count == 0 && !_vm.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = _vm.Query.Length > 0 ? _l.Format("studio.videos.noResults", _vm.Query) : _l["studio.videos.empty"];
        _more.Visibility = _vm.HasMore ? Visibility.Visible : Visibility.Collapsed;
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
        _deleted.Children.Clear();
        _deletedExpander.Header = _l.Format("studio.videos.deletedTitle", _vm.Deleted.Count);
        _deletedExpander.Visibility = _vm.Deleted.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var d in _vm.Deleted)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = d.Title });
            text.Children.Add(new TextBlock
            {
                Text = _l.Format("studio.videos.deletedOn", d.DeletedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), (d.DeletedAt + RecentlyDeleted.Retention).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                Opacity = 0.75,
                FontSize = 12,
            });
            var restore = new Button { Content = _l["studio.videos.restore"] };
            AutomationProperties.SetName(restore, _l.Format("studio.videos.restoreOne", d.Title));
            restore.Click += (_, _) => _ = _vm.RestoreAsync(d.VideoId);
            Grid.SetColumn(restore, 1);
            var now = new Button { Content = _l["studio.videos.deleteNow"] };
            now.Click += async (_, _) =>
            {
                if (_root() is { } root && await Confirm.AskAsync(root, _direction, _l.Format("studio.videos.deleteNowTitle", Localizer.Isolate(d.Title)), _l["studio.videos.deleteNowBody"], _l["studio.videos.deleteNow"], _l["common.cancel"]))
                {
                    await _vm.DeleteNowAsync(d.VideoId);
                }
            };
            Grid.SetColumn(now, 2);
            row.Children.Add(text);
            row.Children.Add(restore);
            row.Children.Add(now);
            _deleted.Children.Add(row);
        }
    }

    private async Task DeleteAsync(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0 || _root() is not { } root) return;
        var title = ids.Count == 1
            ? _l.Format("studio.videos.deleteTitleOne", Localizer.Isolate(_vm.Items.FirstOrDefault(v => v.Id == ids[0])?.Title ?? ""))
            : _l.Format("studio.videos.deleteTitle", ids.Count);
        if (await Confirm.AskAsync(root, _direction, title, _l["studio.videos.deleteBody"], _l["studio.videos.delete"], _l["common.cancel"])) await _vm.DeleteAsync(ids);
    }

    private async Task<bool> FormAsync(string title, FrameworkElement form, string action)
    {
        if (_root() is not { } root) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            FlowDirection = _direction,
            Title = title,
            Content = new ScrollViewer { Content = form },
            PrimaryButtonText = action,
            CloseButtonText = _l["common.cancel"],
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task EditAsync(StudioVideo v)
    {
        var edit = new VideoEdit(v);
        var form = new StackPanel { Spacing = 10, MinWidth = 440 };
        var title = new TextBox { Header = _l["studio.upload.titlePattern"], Text = edit.Title, MaxLength = TitlePatterns.MaxTitleLength };
        var description = new TextBox { Header = _l["studio.upload.description"], Text = edit.Description, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90 };
        var categories = new[] { (string?)null }.Concat(StudioCatalog.Categories).ToList();
        var category = new ComboBox { Header = _l["studio.upload.category"], ItemsSource = categories.Select(c => c == null ? _l["studio.upload.none"] : _l["studio.category." + c]).ToList(), SelectedIndex = Math.Max(0, categories.IndexOf(edit.Category)) };
        var languages = new[] { (string?)null }.Concat(StudioCatalog.Languages).ToList();
        var language = new ComboBox { Header = _l["studio.upload.language"], ItemsSource = languages.Select(c => c == null ? _l["studio.upload.none"] : _l["studio.language." + c]).ToList(), SelectedIndex = Math.Max(0, languages.IndexOf(edit.Language)) };
        var visibility = new ComboBox { Header = _l["studio.upload.visibility"], ItemsSource = new[] { _l["studio.upload.visibility.private"], _l["studio.upload.visibility.public"] }, SelectedIndex = edit.IsPublic ? 1 : 0 };
        var tags = new TextBox { Header = _l["studio.upload.tags"], Text = edit.Tags };
        foreach (var c in new FrameworkElement[] { title, description, category, language, visibility, tags }) form.Children.Add(c);
        if (!await FormAsync(_l["studio.videos.editTitle"], form, _l["studio.videos.save"])) return;
        edit.Title = title.Text;
        edit.Description = description.Text;
        edit.Category = categories[Math.Max(0, category.SelectedIndex)];
        edit.Language = languages[Math.Max(0, language.SelectedIndex)];
        edit.IsPublic = visibility.SelectedIndex == 1;
        edit.Tags = tags.Text;
        if (edit.Problem is { } p)
        {
            _error.Message = _l[p];
            _error.IsOpen = true;
            return;
        }

        await _vm.SaveAsync(v.Id, edit.ChangesFrom());
    }

    private async Task BulkEditAsync()
    {
        var form = new StackPanel { Spacing = 10, MinWidth = 400 };
        var visibility = new ComboBox { Header = _l["studio.upload.visibility"], ItemsSource = new[] { _l["studio.videos.noChange"], _l["studio.upload.visibility.public"], _l["studio.upload.visibility.private"] }, SelectedIndex = 0 };
        var categories = new[] { (string?)null, "" }.Concat(StudioCatalog.Categories).ToList();
        var category = new ComboBox { Header = _l["studio.upload.category"], ItemsSource = categories.Select(c => c == null ? _l["studio.videos.noChange"] : c.Length == 0 ? _l["studio.upload.none"] : _l["studio.category." + c]).ToList(), SelectedIndex = 0 };
        var languages = new[] { (string?)null }.Concat(StudioCatalog.Languages).ToList();
        var language = new ComboBox { Header = _l["studio.upload.language"], ItemsSource = languages.Select(c => c == null ? _l["studio.videos.noChange"] : _l["studio.language." + c]).ToList(), SelectedIndex = 0 };
        var add = new TextBox { Header = _l["studio.videos.addTags"] };
        var remove = new TextBox { Header = _l["studio.videos.removeTags"] };
        foreach (var c in new FrameworkElement[] { visibility, category, language, add, remove }) form.Children.Add(c);
        if (!await FormAsync(_l.Format("studio.videos.bulkEditTitle", _vm.Selected.Count), form, _l["studio.videos.apply"])) return;
        await _vm.BulkEditAsync(new BulkVideoChanges
        {
            IsPublic = visibility.SelectedIndex switch { 1 => true, 2 => false, _ => null },
            Category = categories[Math.Max(0, category.SelectedIndex)],
            Language = languages[Math.Max(0, language.SelectedIndex)],
            AddTags = UploadPlanner.ParseTags(add.Text),
            RemoveTags = UploadPlanner.ParseTags(remove.Text),
        });
    }
}
