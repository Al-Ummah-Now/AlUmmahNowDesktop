using AlUmmahNow.App.Theme;
using AlUmmahNow.Contrast;
using System.ComponentModel;
using AlUmmahNow.App.Live;
using AlUmmahNow.App.ViewModels;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Native;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AlUmmahNow.Windows;

/// <summary>Live streams and premieres (wiki E17): home's row, the list, and the page with the chat beside the video.</summary>
public sealed partial class Shell
{
    private const int ChatWidth = 340;

    private readonly ILiveClient _live;
    private readonly string _shortLinkHost;
    private readonly LivePage _livePage;

    public LiveListViewModel LiveList { get; }
    public LiveWatchViewModel? LiveWatch => _livePage.Current;

    /// <summary>Minimized or hidden: nothing is asked for.</summary>
    public void SetHidden(bool hidden)
    {
        _isHidden = hidden;
        if (LiveWatch != null) LiveWatch.IsHidden = hidden;
    }

    private bool _isHidden;

    private async Task RefreshLiveAsync()
    {
        string Shown() => string.Join(",", LiveList.All.Select(c => c.Id + c.Stats));
        var before = Shown();
        var state = LiveList.State;
        await LiveList.LoadAsync();
        // Built again only for what is new, so a page being read stays where it is
        if (LiveWatch == null && _watch == null && (before != Shown() || state != LiveList.State)) Render();
    }

    /// <summary>The app ends: completes when the stream was told that this viewer left.</summary>
    public Task LeaveLive()
    {
        if (LiveWatch is not { } watch) return Task.CompletedTask;
        watch.Dispose();
        return watch.Leaving;
    }

    private SolidColorBrush Gold => ViewerTheme.Brush("Primary", IsDark, HighContrastRole.Highlight);
    private SolidColorBrush OnGold => ViewerTheme.Brush("OnPrimary", IsDark, HighContrastRole.HighlightText);

    private SolidColorBrush CardFill => SystemBrushes.Brush(IsDark ? global::Windows.UI.Color.FromArgb(255, 14, 43, 61) : Colors.White, HighContrastRole.Window);

    /// <summary>The edge of a card: gold, and the text's color in high contrast.</summary>
    private SolidColorBrush CardEdge => ViewerTheme.Brush("Primary", IsDark, HighContrastRole.Text);

    // ---- Home's row and the list

    private void AppendLiveRail(StackPanel column)
    {
        var heading = new Grid();
        heading.Children.Add(SectionTitle("live.title"));
        var all = new Button { Content = _localizer["live.seeAll"], HorizontalAlignment = HorizontalAlignment.Right };
        all.Click += (_, _) => LiveList.OpenList();
        heading.Children.Add(all);
        column.Children.Add(heading);
        column.Children.Add(LiveGrid(LiveList.All.Take(RailColumns).ToList()));
    }

    private FrameworkElement BuildLiveList()
    {
        if (LiveList.State == LoadState.Loading) return Message("state.loading", null);
        if (LiveList.State == LoadState.Error) return Message("state.error", () => LiveList.LoadAsync());

        var column = new StackPanel { Spacing = 20, Padding = new Thickness(20, 8, 20, 24) };
        if (LiveList.HasEvents)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = _localizer["live.event"], VerticalAlignment = VerticalAlignment.Center });
            var events = new LiveEventType?[] { null, LiveEventType.Jumuah, LiveEventType.Taraweeh, LiveEventType.Conference, LiveEventType.Halqa };
            row.Children.Add(Selector("live.event", events.Select(e => LiveText.Event(_localizer, e)).ToList(), Array.IndexOf(events, LiveList.Event), async i =>
            {
                await LiveList.ChooseAsync(events[i]);
                Render();
            }));
            column.Children.Add(row);
        }

        if (LiveList.All.Count == 0)
        {
            column.Children.Add(new TextBlock { Text = _localizer["live.empty"], HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 48, 0, 0) }.Dim(0.7));
        }

        foreach (var (key, cards) in new[] { ("live.onAir", LiveList.OnAir), ("live.coming", LiveList.Coming) })
        {
            if (cards.Count == 0) continue;
            column.Children.Add(SectionTitle(key));
            for (var i = 0; i < cards.Count; i += RailColumns) column.Children.Add(LiveGrid(cards.Skip(i).Take(RailColumns).ToList()));
        }

        return new ScrollViewer { Content = column };
    }

    private TextBlock SectionTitle(string key) =>
        new() { Text = _localizer[key], FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };

    private Grid LiveGrid(IReadOnlyList<LiveCardViewModel> cards)
    {
        var grid = new Grid { ColumnSpacing = 14 };
        for (var i = 0; i < RailColumns; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (i >= cards.Count) continue;
            var card = BuildLiveCard(cards[i]);
            Grid.SetColumn(card, i);
            grid.Children.Add(card);
        }

        return grid;
    }

    private FrameworkElement BuildLiveCard(LiveCardViewModel card)
    {
        var panel = new StackPanel { Spacing = 4 };
        var thumb = new Grid { Background = CardFill, CornerRadius = new CornerRadius(8), BorderBrush = SystemBrushes.Brush(Colors.Transparent, HighContrastRole.Text), BorderThickness = new Thickness(SystemBrushes.IsHighContrast ? 1 : 0) };
        panel.SizeChanged += (_, e) => thumb.Height = e.NewSize.Width * 9 / 16;
        // A stream without a picture of its own shows what it is
        thumb.Children.Add(new FontIcon { Glyph = "", FontSize = 32 }.Dim(0.7));
        if (card.ThumbnailUrl != null) thumb.Children.Add(new Image { Stretch = Stretch.UniformToFill, Source = new BitmapImage(new Uri(card.ThumbnailUrl)) });
        panel.Children.Add(thumb);
        panel.Children.Add(LiveTitle(card.Title));
        panel.Children.Add(Badges(card.Badges));
        foreach (var text in new[] { card.Channel, card.Stats })
        {
            if (!string.IsNullOrEmpty(text)) panel.Children.Add(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis }.Dim(0.7));
        }

        var button = new Button { Content = panel, Padding = new Thickness(6), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top };
        AutomationName(button, card.Title);
        button.Click += (_, _) => LiveList.Open(card);
        return button;
    }

    /// <summary>
    /// A title in the room of two lines, whether it takes one or two, so that what is under it is in line with the other
    /// cards. The room is two lines of both scripts, unseen: it grows with the system's text size.
    /// </summary>
    private FrameworkElement LiveTitle(string text)
    {
        var title = new TextBlock { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.WrapWholeWords, MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis };
        var room = new TextBlock { Text = "Aغ\nAغ", FontWeight = title.FontWeight, Opacity = 0, IsHitTestVisible = false };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(room, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var both = new Grid();
        both.Children.Add(room);
        both.Children.Add(title);
        _liveTitles.Add(title);
        return both;
    }

    private readonly List<TextBlock> _liveTitles = new();

    /// <summary>No title of a live card is cut (CI's scenarios ask; the demo's fit).</summary>
    public bool LiveTitlesAreWhole => _liveTitles.Where(t => t.IsLoaded).All(t => !t.IsTextTrimmed);

    private FrameworkElement Badges(IReadOnlyList<LiveBadge> badges)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Visibility = badges.Count > 0 ? Visibility.Visible : Visibility.Collapsed };
        foreach (var badge in badges) row.Children.Add(Badge(badge.Text, badge.IsOnAir));
        return row;
    }

    private Border Badge(string text, bool isOnAir)
    {
        var label = new TextBlock { Text = text, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        if (isOnAir) label.Foreground = OnGold;
        var border = new Border { Child = label, Padding = new Thickness(10, 1, 10, 2), CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center };
        if (isOnAir)
        {
            border.Background = Gold;
        }
        else
        {
            border.BorderThickness = new Thickness(1);
            border.BorderBrush = SystemBrushes.Brush(Colors.Gray, HighContrastRole.Text);
        }

        return border;
    }

    // ---- The page

    private FrameworkElement BuildLive(LiveWatchViewModel vm)
    {
        if (vm.State == LoadState.Loading) return Message("state.loading", null);
        if (vm.State != LoadState.Content) return Message("state.error", vm.LoadAsync);

        var heading = BuildLiveHeading(vm);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(vm.Watch is { } watch ? BuildWatch(watch, heading) : BuildNotice(vm, heading));
        if (vm.HasChat)
        {
            var chat = BuildChat(vm);
            Grid.SetColumn(chat, 1);
            row.Children.Add(chat);
        }

        return row;
    }

    private FrameworkElement BuildNotice(LiveWatchViewModel vm, FrameworkElement heading)
    {
        var grid = new Grid { Padding = new Thickness(20, 4, 20, 16), RowSpacing = 12 };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headline = new TextBlock { FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var lines = new TextBlock { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var text = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(24, 0, 24, 0) };
        text.Children.Add(headline);
        text.Children.Add(lines);
        grid.Children.Add(new Border { Child = text, Background = CardFill, BorderBrush = CardEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) });
        Grid.SetRow(heading, 1);
        grid.Children.Add(heading);

        Bind(vm, () =>
        {
            var notice = vm.Notice;
            headline.Text = notice.Headline;
            lines.Text = string.Join("\n", notice.Lines);
            lines.Visibility = Shown(notice.Lines.Count > 0);
        });
        return grid;
    }

    private FrameworkElement BuildLiveHeading(LiveWatchViewModel vm)
    {
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 6 };
        text.Children.Add(Badges(vm.Badges));
        text.Children.Add(new TextBlock { Text = vm.Title, FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var meta = new TextBlock { TextWrapping = TextWrapping.Wrap }.Dim(0.7);
        text.Children.Add(meta);
        if (vm.Description.Length > 0) text.Children.Add(new TextBlock { Text = vm.Description, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(text);

        var actions = new StackPanel { Spacing = 4, MaxWidth = 240 };
        var back = new Button { Content = _localizer["live.return"], HorizontalAlignment = HorizontalAlignment.Right, Style = ViewerTheme.AccentButton };
        back.Click += (_, _) => vm.ReturnToLive();
        actions.Children.Add(back);
        var remind = new Button { HorizontalAlignment = HorizontalAlignment.Right };
        remind.Click += async (_, _) =>
        {
            remind.IsEnabled = false;
            await vm.ToggleReminderAsync(_auth);
            remind.IsEnabled = true;
        };
        actions.Children.Add(remind);
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Right }.Dim(0.7);
        actions.Children.Add(note);
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);

        Bind(vm, () =>
        {
            meta.Text = _localizer.Join(vm.Channel, vm.Watching);
            back.Visibility = Shown(vm.IsBehind);
            remind.Visibility = Shown(vm.CanRemind);
            remind.Content = _localizer[vm.ReminderSet ? "live.remind.set" : "live.remind"];
            note.Visibility = Shown(vm.CanRemind);
            note.Text = _localizer[vm.ReminderFailed ? "live.remind.failed" : "live.remind.when"];
        });
        return row;
    }

    private FrameworkElement BuildChat(LiveWatchViewModel vm)
    {
        var panel = new Grid { Width = ChatWidth, Margin = new Thickness(0, 4, 20, 16), Background = CardFill, BorderBrush = CardEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        panel.Children.Add(new TextBlock { Text = vm.ChatTitle, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(12, 10, 12, 10) });
        var pinned = new Border { Padding = new Thickness(12, 10, 12, 10), Background = SystemBrushes.Brush(global::Windows.UI.Color.FromArgb(24, 128, 128, 128), HighContrastRole.None) };
        Grid.SetRow(pinned, 1);
        panel.Children.Add(pinned);

        var list = new StackPanel { Spacing = 10, Padding = new Thickness(12, 10, 12, 10), VerticalAlignment = vm.Mode == LiveMode.Recording ? VerticalAlignment.Top : VerticalAlignment.Bottom };
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2);
        panel.Children.Add(scroll);
        var empty = new TextBlock { Text = vm.ChatEmpty, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12) }.Dim(0.7);
        Grid.SetRow(empty, 2);
        panel.Children.Add(empty);

        if (vm.WriteUrl(_shortLinkHost) is { } url)
        {
            var write = new Button { Content = _localizer["live.chat.write"], HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(12, 10, 12, 10) };
            write.Click += (_, _) => new ShellBrowserLauncher().Open(new Uri(url));
            Grid.SetRow(write, 3);
            panel.Children.Add(write);
        }

        // The newest message stays in view, unless the reader went up to older ones
        var atEnd = true;
        scroll.ViewChanged += (_, e) =>
        {
            if (!e.IsIntermediate) atEnd = scroll.VerticalOffset >= scroll.ScrollableHeight - 40;
        };
        list.SizeChanged += (_, _) =>
        {
            if (atEnd) scroll.ChangeView(null, double.MaxValue, null, true);
        };

        IReadOnlyList<LiveChatMessage>? shown = null;
        LiveChatMessage? shownPinned = null;
        Bind(vm, () =>
        {
            if (!ReferenceEquals(shownPinned, vm.Pinned) || shown == null)
            {
                shownPinned = vm.Pinned;
                pinned.Visibility = Shown(vm.Pinned != null);
                pinned.Child = null;
                if (vm.Pinned is { } p)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                    var icon = new FontIcon { Glyph = "", FontSize = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
                    AutomationName(icon, _localizer["live.chat.pinned"]);
                    row.Children.Add(icon);
                    var text = ChatMessage(p, null);
                    text.MaxWidth = ChatWidth - 60;
                    row.Children.Add(text);
                    pinned.Child = row;
                }
            }

            if (ReferenceEquals(shown, vm.Messages)) return;
            shown = vm.Messages;
            list.Children.Clear();
            foreach (var m in shown) list.Children.Add(ChatMessage(m, vm.Mode == LiveMode.Recording ? _localizer.Duration(m.OffsetMs / 1000.0) : null));
            empty.Visibility = Shown(shown.Count == 0);
        });
        return panel;
    }

    private FrameworkElement ChatMessage(LiveChatMessage message, string? at)
    {
        var box = new StackPanel { Spacing = 2 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (at != null) head.Children.Add(new TextBlock { Text = at, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }.Dim(0.7));
        head.Children.Add(new TextBlock { Text = message.AuthorName, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, MaxWidth = 190, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        if (message.Role is { } role) head.Children.Add(Badge(_localizer[role == LiveChatRole.Owner ? "live.chat.host" : "live.chat.moderator"], false));
        box.Children.Add(head);
        // Each message runs in its own direction, and sits on the page's side
        box.Children.Add(new TextBlock { Text = message.Text, TextWrapping = TextWrapping.Wrap, TextReadingOrder = TextReadingOrder.DetectFromContent });
        return box;
    }

    private static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows <paramref name="sync"/> now and at every change, until the page is built again.</summary>
    private void Bind(LiveWatchViewModel vm, Action sync)
    {
        void Changed(object? s, PropertyChangedEventArgs e) => sync();
        vm.PropertyChanged += Changed;
        _unbind += () => vm.PropertyChanged -= Changed;
        sync();
    }
}
