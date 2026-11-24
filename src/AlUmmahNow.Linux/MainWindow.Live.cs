using AlUmmahNow.App.Live;
using AlUmmahNow.App.ViewModels;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Native;

namespace AlUmmahNow.Linux;

/// <summary>Live streams and premieres (wiki E17): home's row, the list, and the page with the chat beside the video.</summary>
public sealed partial class MainWindow
{
    private const int ChatWidth = 340;

    private readonly ILiveClient _live;
    private readonly string _shortLinkHost;
    private readonly LiveListViewModel _liveList;
    private readonly LivePage _livePage;

    public LiveListViewModel LiveList => _liveList;
    public LiveWatchViewModel? LiveWatch => _livePage.Current;

    private async Task RefreshLiveAsync()
    {
        string Shown() => string.Join(",", _liveList.All.Select(c => c.Id + c.Stats));
        var before = Shown();
        var state = _liveList.State;
        await _liveList.LoadAsync();
        // Built again only for what is new, so a page being read stays where it is
        if (LiveWatch == null && _watch == null && (before != Shown() || state != _liveList.State)) Render();
    }

    /// <summary>The app is closing.</summary>
    public void Close() => LiveWatch?.Close(TimeSpan.FromSeconds(2));

    // ---- Home's row and the list

    private void AppendLiveRail(Gtk.Box column)
    {
        var heading = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        var title = SectionTitle("live.title");
        title.SetHexpand(true);
        heading.Append(title);
        var all = Gtk.Button.NewWithLabel(_localizer["live.seeAll"]);
        all.AddCssClass("flat");
        all.OnClicked += (_, _) => _liveList.OpenList();
        heading.Append(all);
        column.Append(heading);
        column.Append(LiveGrid(_liveList.All.Take(RailColumns).ToList()));
    }

    private Gtk.Widget BuildLiveList()
    {
        if (_liveList.State == LoadState.Loading) return StateMessage("state.loading", null);
        if (_liveList.State == LoadState.Error) return StateMessage("state.error", () => _liveList.LoadAsync());

        var column = Gtk.Box.New(Gtk.Orientation.Vertical, 20);
        column.SetMarginTop(16);
        column.SetMarginBottom(24);
        column.SetMarginStart(20);
        column.SetMarginEnd(20);

        if (_liveList.HasEvents)
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            row.Append(Gtk.Label.New(_localizer["live.event"]));
            var events = new LiveEventType?[] { null, LiveEventType.Jumuah, LiveEventType.Taraweeh, LiveEventType.Conference, LiveEventType.Halqa };
            row.Append(BuildSelector("live.event", events.Select(e => LiveText.Event(_localizer, e)).ToArray(), Array.IndexOf(events, _liveList.Event), async i =>
            {
                await _liveList.ChooseAsync(events[i]);
                Render();
            }));
            column.Append(row);
        }

        if (_liveList.All.Count == 0)
        {
            var empty = Gtk.Label.New(_localizer["live.empty"]);
            empty.AddCssClass("dim-label");
            empty.SetMarginTop(48);
            column.Append(empty);
        }

        foreach (var (key, cards) in new[] { ("live.onAir", _liveList.OnAir), ("live.coming", _liveList.Coming) })
        {
            if (cards.Count == 0) continue;
            column.Append(SectionTitle(key));
            for (var i = 0; i < cards.Count; i += RailColumns) column.Append(LiveGrid(cards.Skip(i).Take(RailColumns).ToList()));
        }

        var page = Gtk.ScrolledWindow.New();
        page.SetVexpand(true);
        page.SetChild(column);
        return page;
    }

    private Gtk.Label SectionTitle(string key)
    {
        var title = Gtk.Label.New(_localizer[key]);
        title.SetHalign(Gtk.Align.Start);
        title.SetValign(Gtk.Align.Start);
        title.AddCssClass("title-3");
        title.SetSizeRequest(-1, RailHeadingLine);
        return title;
    }

    private Gtk.Widget LiveGrid(IReadOnlyList<LiveCardViewModel> cards)
    {
        var grid = Gtk.Grid.New();
        grid.SetColumnHomogeneous(true);
        grid.SetColumnSpacing(14);
        for (var i = 0; i < RailColumns; i++)
        {
            grid.Attach(i < cards.Count ? BuildLiveCard(cards[i]) : Gtk.Box.New(Gtk.Orientation.Vertical, 0), i, 0, 1, 1);
        }

        return grid;
    }

    private Gtk.Widget BuildLiveCard(LiveCardViewModel card)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        box.SetSizeRequest(200, -1);

        var thumb = Gtk.Overlay.New();
        thumb.AddCssClass("aun-thumb");
        thumb.AddCssClass("aun-thumb-empty");
        thumb.SetOverflow(Gtk.Overflow.Hidden);
        thumb.SetHexpand(true);
        thumb.SetValign(Gtk.Align.Start);
        // A stream without a picture of its own shows what it is
        var icon = Gtk.Image.NewFromIconName("camera-video-symbolic");
        icon.SetPixelSize(32);
        icon.AddCssClass("dim-label");
        thumb.SetChild(icon);
        if (card.ThumbnailUrl != null)
        {
            var picture = Gtk.Picture.New();
            picture.SetContentFit(Gtk.ContentFit.Cover);
            thumb.AddOverlay(picture);
            _ = LoadThumbnailAsync(picture, card.ThumbnailUrl);
        }

        KeepAspect(thumb, 16, 9, 200);
        box.Append(thumb);

        var title = Gtk.Label.New(card.Title);
        title.SetHalign(Gtk.Align.Start);
        title.SetSizeRequest(-1, TitleLine);
        title.SetEllipsize(Pango.EllipsizeMode.End);
        title.SetMaxWidthChars(30);
        title.AddCssClass("heading");
        box.Append(title);
        box.Append(Badges(card.Badges));
        foreach (var text in new[] { card.Channel, card.Stats })
        {
            if (string.IsNullOrEmpty(text)) continue;
            var meta = Gtk.Label.New(text);
            meta.SetHalign(Gtk.Align.Start);
            meta.SetEllipsize(Pango.EllipsizeMode.End);
            meta.SetMaxWidthChars(36);
            meta.AddCssClass("dim-label");
            meta.SetSizeRequest(-1, MetaLine);
            box.Append(meta);
        }

        var button = Gtk.Button.New();
        button.AddCssClass("flat");
        button.SetChild(box);
        button.OnClicked += (_, _) => _liveList.Open(card);
        return button;
    }

    private static Gtk.Widget Badges(IReadOnlyList<LiveBadge> badges)
    {
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        row.SetVisible(badges.Count > 0);
        foreach (var badge in badges)
        {
            var label = Gtk.Label.New(badge.Text);
            label.AddCssClass("aun-badge");
            if (badge.IsOnAir) label.AddCssClass("on-air");
            row.Append(label);
        }

        return row;
    }

    // ---- The page

    private Gtk.Widget BuildLive(LiveWatchViewModel vm)
    {
        if (vm.State == LoadState.Loading) return StateMessage("state.loading", null);
        if (vm.State != LoadState.Content) return StateMessage("state.error", vm.LoadAsync);

        var heading = BuildLiveHeading(vm);
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        var main = vm.Watch is { } watch ? BuildWatch(watch, heading) : BuildNotice(vm, heading);
        main.SetHexpand(true);
        row.Append(main);
        if (vm.HasChat) row.Append(BuildChat(vm));
        return row;
    }

    private Gtk.Widget BuildNotice(LiveWatchViewModel vm, Gtk.Widget heading)
    {
        var column = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        column.SetMarginStart(20);
        column.SetMarginEnd(20);
        column.SetMarginTop(12);
        column.SetMarginBottom(16);
        column.SetVexpand(true);

        var card = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        card.AddCssClass("aun-card");
        card.SetVexpand(true);
        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        text.SetVexpand(true);
        text.SetValign(Gtk.Align.Center);
        text.SetMarginStart(24);
        text.SetMarginEnd(24);
        var headline = Gtk.Label.New("");
        headline.AddCssClass("title-1");
        headline.SetWrap(true);
        headline.SetJustify(Gtk.Justification.Center);
        text.Append(headline);
        var lines = Gtk.Label.New("");
        lines.SetWrap(true);
        lines.SetJustify(Gtk.Justification.Center);
        text.Append(lines);
        card.Append(text);
        column.Append(card);
        column.Append(heading);

        Bind(vm, () =>
        {
            var notice = vm.Notice;
            headline.SetText(notice.Headline);
            lines.SetText(string.Join("\n", notice.Lines));
            lines.SetVisible(notice.Lines.Count > 0);
        });
        return column;
    }

    private Gtk.Widget BuildLiveHeading(LiveWatchViewModel vm)
    {
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 16);
        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        text.SetHexpand(true);
        text.Append(Badges(vm.Badges));
        var title = Gtk.Label.New(vm.Title);
        title.SetHalign(Gtk.Align.Start);
        title.SetXalign(0);
        title.SetWrap(true);
        title.AddCssClass("title-2");
        text.Append(title);
        var meta = Gtk.Label.New("");
        meta.SetHalign(Gtk.Align.Start);
        meta.AddCssClass("dim-label");
        text.Append(meta);
        if (vm.Description.Length > 0)
        {
            var description = Gtk.Label.New(vm.Description);
            description.SetHalign(Gtk.Align.Start);
            description.SetXalign(0);
            description.SetWrap(true);
            text.Append(description);
        }

        row.Append(text);

        var actions = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        actions.SetValign(Gtk.Align.Start);
        var back = Gtk.Button.NewWithLabel(_localizer["live.return"]);
        back.AddCssClass("suggested-action");
        back.SetHalign(Gtk.Align.End);
        back.OnClicked += (_, _) => vm.ReturnToLive();
        actions.Append(back);
        var remind = Gtk.Button.New();
        remind.SetHalign(Gtk.Align.End);
        remind.OnClicked += async (_, _) =>
        {
            remind.SetSensitive(false);
            await vm.ToggleReminderAsync(_auth);
            remind.SetSensitive(true);
        };
        actions.Append(remind);
        var note = Gtk.Label.New("");
        note.SetHalign(Gtk.Align.End);
        note.SetWrap(true);
        note.AddCssClass("dim-label");
        actions.Append(note);
        row.Append(actions);

        Bind(vm, () =>
        {
            meta.SetText(_localizer.Join(vm.Channel, vm.Watching));
            back.SetVisible(vm.IsBehind);
            remind.SetVisible(vm.CanRemind);
            remind.SetLabel(_localizer[vm.ReminderSet ? "live.remind.set" : "live.remind"]);
            note.SetVisible(vm.CanRemind);
            note.SetText(_localizer[vm.ReminderFailed ? "live.remind.failed" : "live.remind.when"]);
        });
        return row;
    }

    private Gtk.Widget BuildChat(LiveWatchViewModel vm)
    {
        var panel = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        panel.AddCssClass("aun-card");
        panel.SetSizeRequest(ChatWidth, -1);
        panel.SetMarginTop(12);
        panel.SetMarginBottom(16);
        panel.SetMarginEnd(20);

        var title = Gtk.Label.New(vm.ChatTitle);
        title.SetHalign(Gtk.Align.Start);
        title.AddCssClass("heading");
        Inset(title, 12, 10);
        panel.Append(title);
        panel.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));

        var pinned = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        pinned.AddCssClass("aun-pinned");
        panel.Append(pinned);

        var list = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        list.SetValign(vm.Mode == LiveMode.Recording ? Gtk.Align.Start : Gtk.Align.End);
        Inset(list, 12, 10);
        var empty = Gtk.Label.New(vm.ChatEmpty);
        empty.AddCssClass("dim-label");
        empty.SetWrap(true);
        empty.SetJustify(Gtk.Justification.Center);
        empty.SetVexpand(true);
        var body = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        body.Append(empty);
        body.Append(list);
        list.SetVexpand(true);
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.SetVexpand(true);
        scroll.SetChild(body);
        panel.Append(scroll);

        if (vm.WriteUrl(_shortLinkHost) is { } url)
        {
            var write = Gtk.Button.NewWithLabel(_localizer["live.chat.write"]);
            Inset(write, 12, 10);
            write.OnClicked += (_, _) => new ShellBrowserLauncher().Open(new Uri(url));
            panel.Append(write);
        }

        // The newest message stays in view, unless the reader went up to older ones
        var adjustment = scroll.GetVadjustment();
        var atEnd = true;
        adjustment.OnValueChanged += (_, _) => atEnd = adjustment.GetValue() >= adjustment.GetUpper() - adjustment.GetPageSize() - 40;
        adjustment.OnChanged += (_, _) =>
        {
            if (atEnd) adjustment.SetValue(adjustment.GetUpper() - adjustment.GetPageSize());
        };

        IReadOnlyList<LiveChatMessage>? shown = null;
        LiveChatMessage? shownPinned = null;
        Bind(vm, () =>
        {
            if (!ReferenceEquals(shownPinned, vm.Pinned) || shown == null)
            {
                shownPinned = vm.Pinned;
                Clear(pinned);
                pinned.SetVisible(vm.Pinned != null);
                if (vm.Pinned is { } p)
                {
                    var icon = Gtk.Image.NewFromIconName("view-pin-symbolic");
                    icon.SetTooltipText(_localizer["live.chat.pinned"]);
                    icon.SetValign(Gtk.Align.Start);
                    pinned.Append(icon);
                    pinned.Append(Message(p, null));
                }
            }

            if (ReferenceEquals(shown, vm.Messages)) return;
            shown = vm.Messages;
            Clear(list);
            foreach (var m in shown) list.Append(Message(m, vm.Mode == LiveMode.Recording ? _localizer.Duration(m.OffsetMs / 1000.0) : null));
            empty.SetVisible(shown.Count == 0);
            list.SetVisible(shown.Count > 0);
        });
        return panel;
    }

    private Gtk.Widget Message(LiveChatMessage message, string? at)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        box.SetHexpand(true);
        var head = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        if (at != null)
        {
            var time = Gtk.Label.New(at);
            time.AddCssClass("dim-label");
            time.AddCssClass("caption");
            head.Append(time);
        }

        var name = Gtk.Label.New(message.AuthorName);
        name.AddCssClass("caption-heading");
        name.SetEllipsize(Pango.EllipsizeMode.End);
        head.Append(name);
        if (message.Role is { } role)
        {
            var badge = Gtk.Label.New(_localizer[role == LiveChatRole.Owner ? "live.chat.host" : "live.chat.moderator"]);
            badge.AddCssClass("aun-badge");
            head.Append(badge);
        }

        box.Append(head);
        var text = Gtk.Label.New(message.Text);
        // Each message runs in its own direction, and sits on the page's side
        text.SetHalign(Gtk.Align.Start);
        text.SetXalign(0);
        text.SetWrap(true);
        text.SetWrapMode(Pango.WrapMode.WordChar);
        box.Append(text);
        return box;
    }

    /// <summary>Shows <paramref name="sync"/> now and at every change, until the page is built again.</summary>
    private void Bind(LiveWatchViewModel vm, Action sync)
    {
        void Changed(object? s, System.ComponentModel.PropertyChangedEventArgs e) => sync();
        vm.PropertyChanged += Changed;
        _unbindWatch += () => vm.PropertyChanged -= Changed;
        sync();
    }

    private static void Inset(Gtk.Widget widget, int x, int y)
    {
        widget.SetMarginStart(x);
        widget.SetMarginEnd(x);
        widget.SetMarginTop(y);
        widget.SetMarginBottom(y);
    }

    private static void Clear(Gtk.Box box)
    {
        while (box.GetFirstChild() is { } child) box.Remove(child);
    }
}
