using AlUmmahNow.Contrast;
using AlUmmahNow.App.Theme;
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
    // A gold bar while videos are selected.
    private readonly StackPanel _selectionBar = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Padding = new Thickness(14, 8, 10, 8),
        CornerRadius = new CornerRadius(12),
        BorderThickness = new Thickness(1),
        BorderBrush = LanternGlass.Res("StudioEdgeStrongBrush"),
        Background = LanternGlass.Res("StudioSelectedBrush"),
    };
    private readonly TextBlock _selectionLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    // Lantern Glass: a grid of thumbnail cards (Edit and Delete in each card's footer).
    private readonly ItemsRepeater _list = new()
    {
        Layout = new UniformGridLayout { MinItemWidth = 250, MinColumnSpacing = 16, MinRowSpacing = 16, ItemsStretch = UniformGridLayoutItemsStretch.Fill },
    };
    private readonly TextBlock _empty = new TextBlock().Dim(0.8);
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    private readonly InfoBar _notice = new() { Severity = InfoBarSeverity.Success, IsClosable = true };
    private readonly Button _more;
    private readonly ProgressRing _loading = new() { Width = 20, Height = 20 };
    private readonly Expander _deletedExpander = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _deleted = new() { Spacing = 6 };
    private readonly CheckBox _all;
    private readonly ComboBox _filter;
    private readonly List<(FrameworkElement Card, FrameworkElement Footer)> _cards = new();
    private FrameworkElement? _deletedCard;

    // Held in a field: a DispatcherQueueTimer referenced only locally can be garbage collected
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _sceneTimer;
    private readonly MenuFlyout _playlistMenu = new();

    /// <summary>The title-bar search: show this text in the search box and search for it.</summary>
    public void SearchFor(string text)
    {
        _search.Text = text;
        _vm.Search.Submit(text);
    }

    public VideosPage(Localizer l, VideosViewModel vm, UploadsViewModel uploads, Func<XamlRoot?> root)
    {
        _l = l;
        _vm = vm;
        _uploads = uploads;
        _root = root;
        _direction = l.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Root = new StackPanel { Spacing = 14, Padding = new Thickness(16, 4, 16, 8) };

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
        var filter = _filter = new ComboBox { Header = l["studio.videos.filter"], ItemsSource = filters.Select(f => l["studio.videos.filter." + f.ToString().ToLowerInvariant()]).ToList(), SelectedIndex = 0, MinWidth = 160 };
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
                var item = new MenuFlyoutItem { Text = p.TitleFor(_l.Language) };
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
        _deletedExpander.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _deletedCard = LanternGlass.Card(_deletedExpander);
        Root.Children.Add(_deletedCard);

        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(VideosViewModel.Items): RenderList(); break;
                case nameof(VideosViewModel.Selected): RenderSelection(); break;
                case nameof(VideosViewModel.IsLoading): _loading.IsActive = _vm.IsLoading; break;
                case nameof(VideosViewModel.ShowEmpty): RenderEmpty(); break;
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
            {
                // A query that visibly narrows the list (the Seerah video only), in the page's language.
                var q = _l.Language == "ar" ? "السيرة" : "Seerah";
                _search.Text = q;
                _vm.Search.Submit(q);
                break;
            }
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
            case "private":
                // Its first card has one line of details and its second two, at some widths
                _filter.SelectedIndex = 2;
                break;
            case "deleted":
            case "deleted-open":
                _deletedExpander.IsExpanded = scene == "deleted-open";
                // To the end of the page, once it has opened
                _sceneTimer = _list.DispatcherQueue.CreateTimer();
                _sceneTimer.Interval = TimeSpan.FromMilliseconds(1500);
                _sceneTimer.IsRepeating = false;
                _sceneTimer.Tick += (_, _) => _deletedCard?.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 1, AnimationDesired = false });
                _sceneTimer.Start();
                break;
        }
    }

    /// <summary>CI: the page is scrolled to the whole of the deleted videos' card.</summary>
    public bool DeletedIsShown
    {
        get
        {
            if (_deletedCard is not { Visibility: Visibility.Visible, ActualHeight: > 0 } card) return false;
            DependencyObject? parent = card;
            while (parent is not (null or ScrollViewer)) parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent);
            if (parent is not ScrollViewer scroll || (scroll.ScrollableHeight > 0 && scroll.VerticalOffset <= 0)) return false;
            var top = card.TransformToVisual(scroll).TransformPoint(new global::Windows.Foundation.Point(0, 0)).Y;
            return top >= 0 && top + card.ActualHeight <= scroll.ViewportHeight + 1;
        }
    }

    private void RenderSelection()
    {
        var n = _vm.Selected.Count;
        _selectionBar.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        _selectionLabel.Text = _l.Format("studio.videos.selected", n);
    }

    /// <summary>No card cuts its Edit and Delete (CI's scenarios ask): the grid gives every card one height.</summary>
    public bool CardsAreWhole => _cards.Where(c => c.Card.IsLoaded && c.Card.ActualHeight > 0).All(c =>
        c.Footer.TransformToVisual(c.Card).TransformPoint(new global::Windows.Foundation.Point(0, c.Footer.ActualHeight)).Y <= c.Card.ActualHeight + 0.5);

    /// <summary>
    /// Text in the room of two lines, whether it takes one or two, so every card has one height, which is what the grid
    /// gives them, and a row's buttons are on one line. The room is two lines of both scripts, unseen: it grows with the
    /// system's text size.
    /// </summary>
    private static Grid InTwoLines(TextBlock text)
    {
        text.TextWrapping = TextWrapping.Wrap;
        text.MaxLines = 2;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        var room = new TextBlock { Text = "Aغ\nAغ", FontSize = text.FontSize, FontWeight = text.FontWeight, Opacity = 0, IsHitTestVisible = false };
        AutomationProperties.SetAccessibilityView(room, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var grid = new Grid();
        grid.Children.Add(room);
        grid.Children.Add(text);
        return grid;
    }

    private void RenderList()
    {
        _cards.Clear();
        _list.ItemsSource = _vm.Items.Select(Card).ToList();

        RenderEmpty();
        _more.Visibility = _vm.HasMore ? Visibility.Visible : Visibility.Collapsed;
        RenderSelection();
    }

    /// <summary>
    /// A video card: the thumbnail (night tile until it loads) with visibility/status and duration on it;
    /// the title as the selection checkbox's content; views and date; Edit and Delete in the footer, end-aligned.
    /// </summary>
    private FrameworkElement Card(StudioVideo v)
    {
        var title = v.TitleFor(_l.Language);
        var gold = LanternGlass.Brush(LanternGlass.Gold, HighContrastRole.Highlight);
        var thumb = new Grid { Height = 146 };
        thumb.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(13, 13, 0, 0),
            Background = LanternGlass.Film(),
            Child = new FontIcon { Glyph = "\uE8B2", FontSize = 40, Foreground = gold },
        });
        if (Uri.TryCreate(v.ThumbnailUrl, UriKind.Absolute, out var url))
        {
            thumb.Children.Add(LanternGlass.Picture(new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(url) { DecodePixelWidth = 520 }, new CornerRadius(13, 13, 0, 0)));
        }

        var busy = v.Status is StudioVideoStatus.Processing or StudioVideoStatus.Failed or StudioVideoStatus.Uploading;
        var pillText = busy ? _l["studio.videos.status." + v.Status.ToString().ToLowerInvariant()] : _l["studio.upload.visibility." + (v.IsPublic ? "public" : "private")];
        var pillColor = v.Status == StudioVideoStatus.Failed ? global::Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x9C, 0x94)
            : busy ? global::Windows.UI.Color.FromArgb(0xFF, 0xFB, 0xC6, 0x61)
            : v.IsPublic ? global::Windows.UI.Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80) : global::Windows.UI.Color.FromArgb(0xFF, 0xD6, 0xDF, 0xE2);
        thumb.Children.Add(Chip(pillText, pillColor, HorizontalAlignment.Right, VerticalAlignment.Top));
        if (v.DurationSeconds is { } d && d > 0) thumb.Children.Add(Chip(_l.Duration(d), LanternGlass.OnNight, HorizontalAlignment.Right, VerticalAlignment.Bottom, 4));

        var check = new CheckBox
        {
            IsChecked = _vm.IsSelected(v.Id),
            MinWidth = 0,
            Content = InTwoLines(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }),
        };
        AutomationProperties.SetName(check, _l.Format("studio.videos.select", title));
        var meta = new TextBlock
        {
            // Wraps to a second line rather than cutting the category ("Islamic hist…").
            Text = _l.JoinWrapped(_l.Views(v.Views), v.CreatedAt is { } at ? _l.Date(at) : null, v.Category is { } c ? _l["studio.category." + c] : null),
            Foreground = LanternGlass.Res("StudioMutedBrush"),
            FontSize = 12.5,
        };
        var metaRoom = InTwoLines(meta);
        metaRoom.Margin = new Thickness(28, 0, 0, 0);
        var body = new StackPanel { Spacing = 2, Padding = new Thickness(12, 10, 12, 0) };
        body.Children.Add(check);
        body.Children.Add(metaRoom);

        var edit = new Button { Content = _l["studio.videos.edit"], Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Foreground = LanternGlass.Res("StudioLinkBrush") };
        AutomationProperties.SetName(edit, _l.Format("studio.videos.editOne", title));
        edit.Click += async (_, _) => await EditAsync(v);
        var delete = new Button { Content = _l["studio.videos.delete"], Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Foreground = LanternGlass.Res("StudioDangerBrush") };
        AutomationProperties.SetName(delete, _l.Format("studio.videos.deleteOne", title));
        delete.Click += async (_, _) => await DeleteAsync(new[] { v.Id });
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(8, 4, 8, 8) };
        footer.Children.Add(edit);
        footer.Children.Add(delete);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(thumb);
        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);
        var card = new Border
        {
            Background = LanternGlass.Res("StudioCardBrush"),
            BorderBrush = _vm.IsSelected(v.Id) ? gold : LanternGlass.Res("StudioEdgeBrush"),
            BorderThickness = new Thickness(_vm.IsSelected(v.Id) ? 2 : 1),
            CornerRadius = new CornerRadius(14),
            Child = grid,
        };
        _cards.Add((card, footer));
        check.Click += (_, _) =>
        {
            _vm.SetSelected(v.Id, check.IsChecked == true);
            card.BorderBrush = check.IsChecked == true ? gold : LanternGlass.Res("StudioEdgeBrush");
            card.BorderThickness = new Thickness(check.IsChecked == true ? 2 : 1);
        };
        return LanternGlass.WithGlow(card, 14, 0.22f, 22f);
    }

    private static Border Chip(string text, global::Windows.UI.Color color, HorizontalAlignment h, VerticalAlignment v, double radius = 10) => new()
    {
        HorizontalAlignment = h,
        VerticalAlignment = v,
        Margin = new Thickness(8),
        // An edge in high contrast, where the tile under it may be the same color
        Padding = new Thickness(7, 1, 7, 1),
        BorderThickness = new Thickness(1),
        BorderBrush = LanternGlass.Brush(Microsoft.UI.Colors.Transparent, HighContrastRole.Text),
        BackgroundSizing = Microsoft.UI.Xaml.Controls.BackgroundSizing.OuterBorderEdge,
        CornerRadius = new CornerRadius(radius),
        Background = LanternGlass.Brush(global::Windows.UI.Color.FromArgb(0xC0, 0x09, 0x15, 0x1E), HighContrastRole.Window),
        Child = new TextBlock { Text = text, FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = LanternGlass.Brush(color, HighContrastRole.Text) },
    };

    private void RenderEmpty()
    {
        _empty.Visibility = _vm.ShowEmpty ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = _vm.Query.Length > 0 ? _l.Format("studio.videos.noResults", _vm.Query) : _l["studio.videos.empty"];
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
        _deleted.Children.Clear();
        _deletedExpander.Header = _l.Format("studio.videos.deletedTitle", _vm.Deleted.Count);
        _deletedExpander.Visibility = _vm.Deleted.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_deletedCard != null) _deletedCard.Visibility = _deletedExpander.Visibility;
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
                Text = d.CanRestore
                    ? _l.Format("studio.videos.deletedOn", _l.Date(d.DeletedAt), _l.Date(d.PurgeAfter ?? d.DeletedAt.AddDays(VideosViewModel.RestoreDays)))
                    : _l["studio.videos.queuedForDeletion"],
                FontSize = 12,
            }.Dim(0.75));
            row.Children.Add(text);
            if (d.CanRestore)
            {
                var restore = new Button { Content = _l["studio.videos.restore"] };
                AutomationProperties.SetName(restore, _l.Format("studio.videos.restoreOne", d.Title));
                restore.Click += (_, _) => _ = _vm.RestoreAsync(d.Id);
                Grid.SetColumn(restore, 1);
                var now = new Button { Content = _l["studio.videos.deleteNow"] };
                now.Click += async (_, _) =>
                {
                    if (_root() is { } root && await Confirm.AskAsync(root, _direction, _l.Format("studio.videos.deleteNowTitle", Localizer.Isolate(d.Title)), _l["studio.videos.deleteNowBody"], _l["studio.videos.deleteNow"], _l["common.cancel"]))
                    {
                        await _vm.DeleteNowAsync(d.Id);
                    }
                };
                Grid.SetColumn(now, 2);
                row.Children.Add(restore);
                row.Children.Add(now);
            }

            _deleted.Children.Add(row);
        }
    }

    private async Task DeleteAsync(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0 || _root() is not { } root) return;
        var title = ids.Count == 1
            ? _l.Format("studio.videos.deleteTitleOne", Localizer.Isolate(_vm.Items.FirstOrDefault(v => v.Id == ids[0])?.Title ?? ""))
            : _l.Format("studio.videos.deleteTitle", ids.Count);
        if (await Confirm.AskAsync(root, _direction, title, _l.Format("studio.videos.deleteBody", VideosViewModel.RestoreDays), _l["studio.videos.delete"], _l["common.cancel"])) await _vm.DeleteAsync(ids);
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
        // Edit and bulk edit: Apply/Save is the gold default.
        return await LanternGlass.Dialog(dialog, destructive: false).ShowAsync() == ContentDialogResult.Primary;
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
