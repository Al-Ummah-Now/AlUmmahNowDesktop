using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Media;
using AlUmmahNow.App.Navigation;
using AlUmmahNow.App.ViewModels;
using AlUmmahNow.CoreBridge;
using Xunit;

namespace AlUmmahNow.App.Tests;

public class HomeViewModelTests
{
    [Fact]
    public void Card_stats_isolate_views_and_duration()
    {
        var v = FakeCatalog.Ready("a");
        v.ChannelName = "Noor Channel";
        v.Views = 12_820;
        v.DurationSeconds = 240;
        var card = new VideoCardViewModel(v, new Localizer("ar"));
        Assert.Equal("Noor Channel", card.Channel);
        Assert.Equal("\u2068١٢٫٨ ألف مشاهدة\u2069\u060C \u2068\u2068٤:٠٠\u2069\u2069", card.Stats);
        Assert.Equal("\u20682K views\u2069 \u00B7 \u2068\u20684:00\u2069\u2069", new VideoCardViewModel(new Video { Views = 2_000, DurationSeconds = 240 }, new Localizer("en")).Stats);
    }

    [Fact]
    public async Task Hides_empty_rails_and_unplayable_videos()
    {
        var c = new FakeCatalog();
        c.Videos.Add(FakeCatalog.Ready("a", "fiqh"));
        c.Videos.Add(new Video { Id = "up", IsPublic = true, UploadStatus = "uploading", PlayUrl = "u" });
        var vm = new HomeViewModel(c, new Localizer("en"), new Navigator());
        await vm.LoadAsync();
        Assert.Equal(LoadState.Content, vm.State);
        Assert.Equal(new[] { "Latest", "Most watched", "Fiqh" }, vm.Rails.Select(r => r.Title));
        Assert.DoesNotContain(vm.Rails.SelectMany(r => r.Items), i => i.Id == "up");
        Assert.Equal("a", vm.Featured!.Id);
    }

    [Fact]
    public async Task One_failed_rail_is_hidden_but_all_failed_is_error()
    {
        var c = new FakeCatalog();
        c.Videos.Add(FakeCatalog.Ready("a"));
        c.FailWhen = q => q.Sort == VideoSort.Views;
        var vm = new HomeViewModel(c, new Localizer("en"), new Navigator());
        await vm.LoadAsync();
        Assert.Equal(LoadState.Content, vm.State);
        Assert.DoesNotContain(vm.Rails, r => r.TitleKey == "home.rail.popular");

        c.FailWhen = _ => true;
        await vm.LoadAsync();
        Assert.Equal(LoadState.Error, vm.State);
    }

    private sealed class PagedCatalog : ICatalogClient
    {
        public int Calls;
        public Task<VideoPage> ListVideosAsync(VideoQuery q, CancellationToken ct = default)
        {
            if (q.Category != null || q.Sort != VideoSort.Date) return Task.FromResult(new VideoPage(Array.Empty<Video>(), null));
            Calls++;
            return Task.FromResult(q.Cursor == null
                ? new VideoPage(new[] { new Video { Id = "pending", IsPublic = true, UploadStatus = "uploading" } }, "next")
                : new VideoPage(new[] { FakeCatalog.Ready("second-page") }, null));
        }

        public Task<Video> GetVideoAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SubtitleTrack>> GetSubtitleTracksAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetTextAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Short_page_follows_cursor()
    {
        var c = new PagedCatalog();
        var vm = new HomeViewModel(c, new Localizer("en"), new Navigator());
        await vm.LoadAsync();
        Assert.Equal(2, c.Calls);
        Assert.Equal("second-page", vm.Featured!.Id);
    }

    [Fact]
    public async Task No_content_is_empty_state()
    {
        var vm = new HomeViewModel(new FakeCatalog(), new Localizer("en"), new Navigator());
        await vm.LoadAsync();
        Assert.Equal(LoadState.Empty, vm.State);
    }

    [Fact]
    public async Task Open_navigates_to_watch()
    {
        var c = new FakeCatalog();
        c.Videos.Add(FakeCatalog.Ready("a"));
        var nav = new Navigator();
        var vm = new HomeViewModel(c, new Localizer("en"), nav);
        await vm.LoadAsync();
        vm.Open(vm.Featured!);
        Assert.Equal("a", Assert.IsType<Route.Watch>(nav.Current).VideoId);
        Assert.True(nav.Back());
        Assert.False(nav.Back());
    }
}

public class WatchViewModelTests
{
    private const string Master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\n360p/v.m3u8\n";
    private const string PlayUrl = "https://cdn/v/playlist.m3u8";

    private static (WatchViewModel Vm, FakePlayer Player, FakeSession Session, FakeCatalog Catalog) Make(string language = "ar")
    {
        var c = new FakeCatalog();
        var v = FakeCatalog.Ready("v", title: "Fallback");
        v.Title = new LocalizedText("Fallback", new Dictionary<string, string> { ["ar"] = "عنوان" });
        v.Chapters = new[] { new Chapter(0, 15, new LocalizedText("c1")), new Chapter(15, 30, new LocalizedText("c2")) };
        c.Videos.Add(v);
        c.Texts[PlayUrl] = Master;
        c.Tracks.Add(new SubtitleTrack("en", "English", "https://s/en", true));
        c.Tracks.Add(new SubtitleTrack("ar", "العربية", "https://s/ar", false));
        c.Texts["https://s/en"] = "WEBVTT\n\n00:00.000 --> 00:05.000\nHello\n";
        c.Texts["https://s/ar"] = "WEBVTT\n\n00:00.000 --> 00:05.000\nمرحبا\n";
        var p = new FakePlayer();
        var s = new FakeSession();
        return (new WatchViewModel(c, p, s, new Localizer(language)), p, s, c);
    }

    [Fact]
    public async Task Load_opens_auto_and_lists_qualities()
    {
        var (vm, p, s, _) = Make();
        await vm.LoadAsync("v");
        Assert.Equal(LoadState.Content, vm.State);
        Assert.Equal(new[] { "تلقائي", "٣٦٠p" }, vm.Qualities.Select(q => q.Label));
        Assert.Equal((PlayUrl, 0d, true), Assert.Single(p.Opened));
        Assert.Equal("عنوان", s.Title);
    }

    [Fact]
    public async Task Quality_change_keeps_position_and_paused_state()
    {
        var (vm, p, _, _) = Make();
        await vm.LoadAsync("v");
        p.Tick(42);
        p.Pause();
        vm.SelectQuality(vm.Qualities[1]);
        Assert.Equal(("https://cdn/v/360p/v.m3u8", 42d, false), p.Opened[^1]);
    }

    [Theory]
    [InlineData("ar", "مرحبا")]
    [InlineData("en", "Hello")]
    public async Task Subtitles_prefer_ui_language(string language, string expected)
    {
        var (vm, p, _, _) = Make(language);
        await vm.LoadAsync("v");
        p.Tick(1);
        Assert.Equal(expected, vm.Caption);
        p.Tick(6);
        Assert.Equal("", vm.Caption);
    }

    [Fact]
    public async Task Subtitles_off_clears_caption()
    {
        var (vm, p, _, _) = Make();
        await vm.LoadAsync("v");
        p.Tick(1);
        await vm.SelectSubtitleAsync(null);
        p.Tick(2);
        Assert.Equal("", vm.Caption);
    }

    [Fact]
    public async Task Chapter_tracks_position()
    {
        var (vm, p, _, _) = Make();
        await vm.LoadAsync("v");
        p.Tick(20);
        Assert.Equal("c2", vm.CurrentChapter!.Title.Fallback);
    }

    [Fact]
    public async Task Seek_clamps_and_skip_moves_ten_seconds()
    {
        var (vm, p, _, _) = Make();
        await vm.LoadAsync("v");
        p.Tick(5);
        vm.SkipBack();
        Assert.Equal(0, p.Position);
        vm.Seek(500);
        Assert.Equal(100, p.Position);
    }

    [Fact]
    public async Task Speed_is_applied()
    {
        var (vm, p, _, _) = Make();
        await vm.LoadAsync("v");
        vm.SelectSpeed(1.5);
        Assert.Equal(1.5, p.Rate);
    }

    [Theory]
    [InlineData(MediaCommand.Pause, PlayerState.Paused)]
    [InlineData(MediaCommand.Toggle, PlayerState.Paused)]
    public async Task Media_keys_control_player(MediaCommand command, PlayerState expected)
    {
        var (vm, p, s, _) = Make();
        await vm.LoadAsync("v");
        s.Raise(command);
        Assert.Equal(expected, p.State);
        Assert.Equal(expected, s.LastState);
        vm.Dispose();
        Assert.True(p.Stopped);
        Assert.True(s.Cleared);
    }

    [Fact]
    public async Task Missing_video_is_error_and_failed_player_flags()
    {
        var (vm, p, _, _) = Make();
        await vm.LoadAsync("nope");
        Assert.Equal(LoadState.Error, vm.State);
        await vm.LoadAsync("v");
        p.Set(PlayerState.Failed);
        Assert.True(vm.PlaybackFailed);
    }

    [Fact]
    public async Task Master_fetch_failure_still_plays_auto()
    {
        var (vm, p, _, c) = Make();
        c.Texts.Remove(PlayUrl);
        await vm.LoadAsync("v");
        Assert.Single(vm.Qualities);
        Assert.Single(p.Opened);
    }
}

public class LiveWatchViewModelTests
{
    private sealed class FakeLiveCatalog : ILiveCatalogClient
    {
        public LiveStream Stream { get; set; } = new()
        {
            Id = "live-1",
            ChannelId = "ch-1",
            ChannelName = "الأمة الآن",
            Title = new LocalizedText("Friday Khutbah", new Dictionary<string, string> { ["ar"] = "خطبة الجمعة" }),
            Status = LiveStreamStatus.Live,
            StreamKind = LiveStreamKind.Live,
            EventType = LiveEventType.Jummah,
            ViewerCount = 80,
            PeakViewerCount = 100,
            PlaybackUrl = "https://cdn/live-1/index.m3u8",
            VodUrl = "https://cdn/live-1/vod.m3u8",
            Category = "khutbah",
        };

        public List<LiveChatMessage> Messages { get; } = new()
        {
            new LiveChatMessage
            {
                Id = "m1",
                StreamId = "live-1",
                UserId = "u1",
                ChannelId = "ch-1",
                AuthorName = "أحمد",
                Text = "السلام عليكم",
                OffsetMs = 5_000,
                Status = LiveChatStatus.Published,
            },
        };

        public LiveReminderStatus Reminder { get; set; } = new(false, 9);

        public Task<LiveStream> GetLiveStreamAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(Stream);

        public Task<LiveStreamState> GetLiveStreamStateAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(new LiveStreamState
            {
                Id = Stream.Id,
                Status = Stream.Status,
                StreamKind = Stream.StreamKind,
                EventType = Stream.EventType,
                ViewerCount = Stream.ViewerCount,
                PeakViewerCount = Stream.PeakViewerCount,
                PlaybackUrl = Stream.PlaybackUrl,
                VodUrl = Stream.VodUrl,
                ChatEnabled = Stream.ChatEnabled,
            });

        public Task<LiveStreamState> SendLiveHeartbeatAsync(string id, string viewerId, CancellationToken ct = default) =>
            Task.FromResult(new LiveStreamState
            {
                Id = Stream.Id,
                Status = Stream.Status,
                ViewerCount = 128,
                PeakViewerCount = 140,
                PlaybackUrl = Stream.PlaybackUrl,
                ChatEnabled = Stream.ChatEnabled,
            });

        public Task<LiveChatPage> GetLiveChatMessagesAsync(string streamId, string? afterId = null, long? maxOffsetMs = null, int limit = 50, CancellationToken ct = default) =>
            Task.FromResult(new LiveChatPage(Messages.ToList(), settings: new LiveChatSettings { ChatEnabled = true, SlowModeSeconds = 5, LinksAllowed = false }));

        public Task<LiveChatMessage> PostLiveChatMessageAsync(string streamId, string text, long? offsetMs = null, CancellationToken ct = default) =>
            Task.FromResult(new LiveChatMessage
            {
                Id = "m-posted",
                StreamId = streamId,
                UserId = "u-me",
                ChannelId = "ch-1",
                AuthorName = "عمر",
                Text = text,
                OffsetMs = offsetMs ?? 0,
                Status = LiveChatStatus.Published,
            });

        public Task<LiveChatMessage> ModerateLiveChatMessageAsync(string streamId, string messageId, LiveModerationAction action, CancellationToken ct = default)
        {
            var msg = Messages.FirstOrDefault(m => m.Id == messageId) ?? new LiveChatMessage { Id = messageId, Text = "x" };
            return Task.FromResult(new LiveChatMessage
            {
                Id = msg.Id,
                StreamId = streamId,
                AuthorName = msg.AuthorName,
                Text = msg.Text,
                OffsetMs = msg.OffsetMs,
                Status = action == LiveModerationAction.Remove ? LiveChatStatus.Removed : LiveChatStatus.Published,
                IsPinned = action == LiveModerationAction.Pin,
            });
        }

        public Task ReportLiveChatMessageAsync(string streamId, string messageId, LiveReportReason reason, string? details = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<LiveReminderStatus> GetLiveReminderStatusAsync(string streamId, CancellationToken ct = default) =>
            Task.FromResult(Reminder);

        public Task<LiveReminderStatus> SetLiveReminderAsync(string streamId, bool remind, CancellationToken ct = default)
        {
            Reminder = new LiveReminderStatus(remind, remind ? Reminder.ReminderCount + 1 : Reminder.ReminderCount - 1);
            return Task.FromResult(Reminder);
        }
    }

    [Fact]
    public async Task Live_stream_loads_beats_viewer_validates_chat_and_moderates()
    {
        var cat = new FakeLiveCatalog();
        var player = new FakePlayer();
        var session = new FakeSession();
        var vm = new LiveWatchViewModel(cat, player, session, new Localizer("ar"), canModerate: true);

        await vm.LoadAsync("live-1");
        Assert.Equal(LoadState.Content, vm.State);
        Assert.Equal("خطبة الجمعة", vm.Title);
        Assert.Equal("صلاة الجمعة", vm.EventBadgeText);
        Assert.Single(vm.VisibleChatMessages);
        Assert.Single(player.Opened);

        await vm.BeatViewerAsync();
        Assert.Equal(128, vm.Stream!.ViewerCount);
        Assert.Contains("١٢٨ يشاهدون الآن", vm.MetaLine);

        vm.DraftMessage = "Visit https://spam.example";
        await vm.SendChatMessageAsync();
        Assert.Equal("live.chat.error.links", vm.ChatErrorKey);

        vm.DraftMessage = "بارك الله فيكم";
        await vm.SendChatMessageAsync();
        Assert.Null(vm.ChatErrorKey);
        Assert.Equal(2, vm.VisibleChatMessages.Count);
        Assert.Equal("", vm.DraftMessage);

        var first = vm.VisibleChatMessages[0];
        vm.RequestRemoveMessage(first);
        Assert.Equal(first.Id, vm.PendingRemovalMessage?.Id);
        await vm.ConfirmRemoveMessageAsync();
        Assert.Single(vm.VisibleChatMessages);
    }

    [Fact]
    public async Task Scheduled_premiere_toggles_reminder_and_vod_replays_chat_by_position()
    {
        var cat = new FakeLiveCatalog();
        cat.Stream.Status = LiveStreamStatus.Scheduled;
        cat.Stream.StreamKind = LiveStreamKind.Premiere;
        var player = new FakePlayer();
        var vm = new LiveWatchViewModel(cat, player, new FakeSession(), new Localizer("ar"));

        await vm.LoadAsync("live-1");
        Assert.Empty(player.Opened);
        Assert.True(vm.ShowReminderButton);
        Assert.Equal("ذكّرني", vm.ReminderButtonText);

        await vm.ToggleReminderAsync();
        Assert.True(vm.Reminder.Reminded);
        Assert.Equal("تم ضبط التذكير (١٠)", vm.ReminderButtonText);

        // VOD chat replay filters by playback position
        cat.Stream.Status = LiveStreamStatus.Ended;
        cat.Messages.Add(new LiveChatMessage
        {
            Id = "m2",
            StreamId = "live-1",
            AuthorName = "زيد",
            Text = "جزاكم الله خيرا",
            OffsetMs = 30_000,
            Status = LiveChatStatus.Published,
        });
        await vm.LoadAsync("live-1");
        Assert.Empty(vm.VisibleChatMessages);

        player.Tick(10);
        Assert.Equal(new[] { "m1" }, vm.VisibleChatMessages.Select(m => m.Id));

        player.Tick(35);
        Assert.Equal(new[] { "m1", "m2" }, vm.VisibleChatMessages.Select(m => m.Id));

        // Ummah Kids category hides live chat
        cat.Stream.Category = "islamic_kids";
        await vm.LoadAsync("live-1");
        Assert.False(vm.ShowChatPanel);
    }
}
