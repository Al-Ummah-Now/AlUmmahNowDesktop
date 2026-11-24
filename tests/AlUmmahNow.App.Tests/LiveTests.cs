using System.Net;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Live;
using AlUmmahNow.App.Media;
using AlUmmahNow.App.Navigation;
using AlUmmahNow.App.ViewModels;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Temporary;
using Xunit;

namespace AlUmmahNow.App.Tests;

public sealed class FakeLive : ILiveClient
{
    public LiveState Current { get; set; } = new() { Status = LiveStatus.Scheduled };
    public LiveFeatures Features { get; set; } = new() { Premieres = true, Reminders = true, Events = true };
    public Dictionary<LiveStatus, IReadOnlyList<LiveStream>> Lists { get; } = new();
    public Dictionary<long, LiveChatPage> Windows { get; } = new();
    public List<bool> Beats { get; } = new();
    public List<bool> Reminders { get; } = new();
    public int StateReads { get; private set; }
    public int ChatReads { get; private set; }
    public LiveEventType? Asked { get; private set; }
    public bool Fails { get; set; }
    public bool ChatFails { get; set; }
    public TimeSpan? ChatPollAfter { get; set; }
    public LiveChatMessage? Pinned { get; set; }
    public (LiveEventType Type, TaskCompletionSource Gate)? Held { get; set; }

    /// <summary>Every read is refused with this Retry-After.</summary>
    public TimeSpan? RetryAfter { get; set; }

    /// <summary>What passes while an answer is on its way.</summary>
    public Action Answering { get; set; } = () => { };

    private void Refuse()
    {
        Answering();
        if (RetryAfter is { } wait) throw new CatalogException("429", retryAfter: wait);
    }

    public Task<LiveFeatures> GetFeaturesAsync(CancellationToken ct = default) => Task.FromResult(Features);

    public async Task<IReadOnlyList<LiveStream>> ListAsync(LiveStatus status, LiveEventType? type = null, CancellationToken ct = default)
    {
        if (Held is { } held && held.Type == type) await held.Gate.Task;
        if (Fails) throw new CatalogException("503");
        Asked = type;
        return Lists.TryGetValue(status, out var list) ? list : Array.Empty<LiveStream>();
    }

    public Task<LiveStream> GetStreamAsync(string id, CancellationToken ct = default) =>
        Fails ? throw new CatalogException("404") : Task.FromResult(new LiveStream { Id = id, Title = "T", Status = Current.Status });

    public Task<LiveState> GetStateAsync(string id, CancellationToken ct = default)
    {
        StateReads++;
        Refuse();
        return Task.FromResult(Current);
    }

    public Task<LiveChatPage> GetChatAsync(string id, CancellationToken ct = default)
    {
        ChatReads++;
        Refuse();
        if (ChatFails) throw new CatalogException("503");
        return Task.FromResult(new LiveChatPage(new[] { new LiveChatMessage { Id = "m" } }, Pinned, pollAfter: ChatPollAfter));
    }

    public Task<LiveChatPage> GetChatReplayAsync(string id, long from, long to, CancellationToken ct = default) =>
        Task.FromResult(Windows.TryGetValue(from, out var page) ? page : new LiveChatPage(Array.Empty<LiveChatMessage>()));

    public Task<long?> BeatAsync(string id, string viewerId, bool leaving, CancellationToken ct = default)
    {
        Beats.Add(leaving);
        if (!leaving) Refuse();
        return Task.FromResult<long?>(5);
    }

    public Task SetReminderAsync(string id, bool on, CancellationToken ct = default)
    {
        if (Fails) throw new CatalogException("503");
        Reminders.Add(on);
        return Task.CompletedTask;
    }
}

public sealed class FakeAuth : IAuthSession
{
    public event EventHandler? AccountChanged;
    public AuthAccount? Account { get; private set; }
    public void Restore() { }
    public Task SignInAsync(CancellationToken ct = default)
    {
        Account = new AuthAccount("u", "reader@example.com");
        AccountChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("token");
    public void SignOut() => Account = null;
}

public class LiveTests
{
    private DateTimeOffset _now = new(2026, 11, 24, 18, 0, 0, TimeSpan.Zero);
    private readonly FakeLive _live = new();
    private readonly FakePlayer _player = new();
    private readonly FakeCatalog _catalog = new();
    private readonly Localizer _localizer = new("en");

    public LiveTests() => _catalog.Videos.Add(FakeCatalog.Ready("v1"));

    private LiveWatchViewModel Model() => new("s", _live, _catalog, _player, new NullMediaSession(), _localizer, () => _now);

    private async Task<LiveWatchViewModel> Loaded()
    {
        var vm = Model();
        await vm.LoadAsync();
        return vm;
    }

    private void Advance(double seconds) => _now = _now.AddSeconds(seconds);

    private LiveState Premiere(LiveStatus status, string? video = "v1", DateTimeOffset? startedAt = null) => new()
    {
        Status = status, Kind = LiveKind.Premiere, PremiereVideoId = video, StartedAt = startedAt ?? _now, DurationSeconds = 600,
    };

    private static LiveState OnAir(bool chat = false) => new() { Status = LiveStatus.Live, PlaybackUrl = "https://x.test/p.m3u8", ChatEnabled = chat };

    // ---- The clock and following

    [Fact]
    public void The_moment_is_the_servers_between_the_start_and_the_end()
    {
        var clock = new PremiereClock(_now, TimeSpan.FromSeconds(2), 60);
        Assert.Equal(0, clock.Position(_now.AddSeconds(-10)));
        Assert.Equal(12, clock.Position(_now.AddSeconds(10)));
        Assert.Equal(60, clock.Position(_now.AddSeconds(100)));
        Assert.True(clock.IsOver(_now.AddSeconds(58)));
        Assert.False(new PremiereClock(_now, TimeSpan.Zero, null).IsOver(_now.AddDays(1)));
    }

    [Fact]
    public void A_drift_is_brought_back_but_not_while_it_loads()
    {
        var f = new PremiereFollower();
        Assert.Null(f.Tick(100, new PlayerReading(98, true, true, 1)).SeekTo);
        Assert.Null(f.Tick(100, new PlayerReading(90, true, false, 1)).SeekTo);
        Assert.Equal(100, f.Tick(100, new PlayerReading(90, true, true, 1)).SeekTo);
        Assert.False(f.IsBehind);
    }

    [Fact]
    public void Nobody_gets_ahead_or_plays_faster_at_the_moment()
    {
        var f = new PremiereFollower();
        Assert.Equal(100, f.Tick(100, new PlayerReading(130, true, true, 1, seeked: true)).SeekTo);
        Assert.True(f.Tick(100, new PlayerReading(100, true, true, 2)).NormalSpeed);
    }

    [Fact]
    public void Going_back_or_pausing_is_behind_until_caught_up_or_returned()
    {
        var f = new PremiereFollower();
        f.Tick(100, new PlayerReading(100, true, true, 1));
        Assert.Null(f.Tick(101, new PlayerReading(40, true, true, 1, seeked: true)).SeekTo);
        Assert.True(f.IsBehind);
        // Faster until they catch up, also past it between two ticks
        Assert.False(f.Tick(102, new PlayerReading(60, true, true, 2)).NormalSpeed);
        var caught = f.Tick(103, new PlayerReading(104.5, true, true, 2));
        Assert.Equal(103, caught.SeekTo);
        Assert.True(caught.NormalSpeed);
        Assert.False(f.IsBehind);

        f.Tick(110, new PlayerReading(105, false, false, 1));
        Assert.True(f.IsBehind);
        f.Returned();
        Assert.False(f.IsBehind);
    }

    [Fact]
    public async Task A_position_read_while_a_seek_is_on_its_way_is_not_where_playback_is()
    {
        _live.Current = Premiere(LiveStatus.Live, startedAt: _now.AddSeconds(-100));
        var vm = await Loaded();
        var moment = Assert.Single(_player.Opened).Start;
        Assert.Equal(100, moment);
        // Opening: where it was before
        _player.Position = 3;
        vm.Follow();
        Assert.Equal(3, _player.Position);
        // It never arrived: taken as it is after 5 seconds, and brought back once it moves
        Advance(6);
        vm.Follow();
        _player.Position = 4;
        vm.Follow();
        Assert.Equal(106, _player.Position);
    }

    [Fact]
    public async Task The_viewers_own_seek_back_is_behind()
    {
        _live.Current = Premiere(LiveStatus.Live, startedAt: _now.AddSeconds(-100));
        var vm = await Loaded();
        vm.Follow();
        Advance(1);
        vm.Watch!.Seek(20);
        vm.Follow();
        Assert.True(vm.IsBehind);
        Assert.Equal(20, _player.Position);
        vm.ReturnToLive();
        Assert.False(vm.IsBehind);
        Assert.Equal(101, _player.Position);
    }

    // ---- The page

    [Fact]
    public void Modes()
    {
        Assert.Equal(LiveMode.Soon, LiveWatchViewModel.ModeOf(new LiveState { Status = LiveStatus.Scheduled }));
        Assert.Equal(LiveMode.Live, LiveWatchViewModel.ModeOf(OnAir()));
        Assert.Equal(LiveMode.Soon, LiveWatchViewModel.ModeOf(new LiveState { Status = LiveStatus.Live }));
        Assert.Equal(LiveMode.Ended, LiveWatchViewModel.ModeOf(new LiveState { Status = LiveStatus.Ended }));
        Assert.Equal(LiveMode.Recording, LiveWatchViewModel.ModeOf(new LiveState { Status = LiveStatus.Ended, VodUrl = "x" }));
        Assert.Equal(LiveMode.Cancelled, LiveWatchViewModel.ModeOf(new LiveState { Status = LiveStatus.Cancelled }));
        Assert.Equal(LiveMode.Live, LiveWatchViewModel.ModeOf(Premiere(LiveStatus.Live)));
        Assert.Equal(LiveMode.Soon, LiveWatchViewModel.ModeOf(Premiere(LiveStatus.Scheduled)));
        Assert.Equal(LiveMode.Recording, LiveWatchViewModel.ModeOf(Premiere(LiveStatus.Ended)));
        Assert.Equal(LiveMode.Ended, LiveWatchViewModel.ModeOf(Premiere(LiveStatus.Ended, video: null)));
    }

    [Fact]
    public async Task Counts_down_by_the_servers_clock_and_asks_often_when_it_is_due()
    {
        _live.Current = new LiveState { Status = LiveStatus.Scheduled, Kind = LiveKind.Premiere, ScheduledStart = _now.AddSeconds(100), ClockOffset = TimeSpan.FromSeconds(10) };
        var vm = await Loaded();
        Assert.Equal(TimeSpan.FromSeconds(90), vm.UntilStart);
        Assert.Contains("1:30", vm.Notice.Headline);
        Advance(14);
        await vm.TickAsync();
        Assert.Equal(1, _live.StateReads);
        Advance(77);
        await vm.TickAsync();
        Assert.Equal(2, _live.StateReads);
        Assert.Equal("Starting now…", vm.Notice.Headline);
        Advance(5);
        await vm.TickAsync();
        Assert.Equal(3, _live.StateReads);
    }

    [Fact]
    public async Task A_premiere_goes_on_after_its_end_and_is_not_opened_again()
    {
        _live.Current = Premiere(LiveStatus.Live, startedAt: _now.AddSeconds(-30));
        var vm = await Loaded();
        _live.Current = Premiere(LiveStatus.Ended, startedAt: _now.AddSeconds(-30));
        Advance(61);
        await vm.TickAsync();
        Assert.Equal(LiveMode.Recording, vm.Mode);
        Assert.Equal(30, Assert.Single(_player.Opened).Start);
    }

    [Fact]
    public async Task A_video_that_is_gone_is_said_to_be_and_one_not_yet_released_is_asked_for_again()
    {
        _live.Current = Premiere(LiveStatus.Ended, video: "nope");
        Assert.Equal(LiveMode.Ended, (await Loaded()).Mode);

        _live.Current = Premiere(LiveStatus.Live, video: "nope");
        var vm = await Loaded();
        Assert.Null(vm.Watch);
        Assert.Equal("Starting now…", vm.Notice.Headline);
        _live.Current = Premiere(LiveStatus.Live);
        Advance(5);
        await vm.TickAsync();
        Assert.NotNull(vm.Watch);
    }

    [Fact]
    public async Task A_recording_takes_the_streams_place_once()
    {
        _live.Current = OnAir();
        var vm = await Loaded();
        Assert.True(vm.Watch!.IsLive);
        _live.Current = new LiveState { Status = LiveStatus.Ended, VodUrl = "https://x.test/v.m3u8" };
        Advance(60);
        await vm.TickAsync();
        Assert.False(vm.Watch!.IsLive);
        Assert.Equal(new[] { "https://x.test/p.m3u8", "https://x.test/v.m3u8" }, _player.Opened.Select(o => o.Url));
    }

    [Fact]
    public async Task A_paused_stream_is_behind_and_returning_opens_its_newest_moment()
    {
        _live.Current = OnAir();
        var vm = await Loaded();
        vm.Watch!.TogglePlay();
        vm.Follow();
        Advance(11);
        vm.Follow();
        Assert.True(vm.IsBehind);
        vm.Watch.TogglePlay();
        vm.Follow();
        Assert.True(vm.IsBehind);
        vm.ReturnToLive();
        vm.Follow();
        Assert.False(vm.IsBehind);
        Assert.Equal(2, _player.Opened.Count);
    }

    [Fact]
    public async Task A_stream_that_fails_is_tried_again()
    {
        _live.Current = OnAir();
        var vm = await Loaded();
        _player.Set(PlayerState.Failed);
        vm.Follow();
        Advance(11);
        vm.Follow();
        Assert.Equal(2, _player.Opened.Count);
    }

    [Fact]
    public async Task Leaving_is_counted_once()
    {
        _live.Current = OnAir();
        var vm = await Loaded();
        await vm.TickAsync();
        vm.Dispose();
        vm.Dispose();
        Assert.Equal(new[] { false, true }, _live.Beats);
    }

    [Fact]
    public async Task Chat_is_read_only_while_the_server_says_so_and_the_window_is_seen()
    {
        _live.Current = OnAir();
        var vm = await Loaded();
        await vm.TickAsync();
        Assert.Equal(0, _live.ChatReads);
        _live.Current = OnAir(chat: true);
        Advance(60);
        await vm.TickAsync();
        await vm.TickAsync();
        Assert.Equal(1, _live.ChatReads);
        Assert.Single(vm.Messages);
        vm.IsHidden = true;
        Advance(10);
        await vm.TickAsync();
        Assert.Equal(1, _live.ChatReads);
        vm.IsHidden = false;
        await vm.TickAsync();
        Assert.Equal(2, _live.ChatReads);
        // Turned off: gone from the page at the next read of the state
        _live.Current = OnAir();
        Advance(60);
        await vm.TickAsync();
        Assert.Empty(vm.Messages);
        Assert.Null(vm.WriteUrl(ShortLinks.ProdHost));
    }

    [Fact]
    public async Task A_chat_that_cannot_be_read_is_asked_for_once_a_minute()
    {
        _live.Current = OnAir(chat: true);
        _live.ChatFails = true;
        var vm = await Loaded();
        await vm.TickAsync();
        Advance(59);
        await vm.TickAsync();
        Assert.Equal(1, _live.ChatReads);
        Advance(1);
        await vm.TickAsync();
        Assert.Equal(2, _live.ChatReads);
    }

    [Fact]
    public async Task The_server_says_when_to_ask_again()
    {
        LiveState Said(TimeSpan? wait)
        {
            var state = OnAir(chat: true);
            state.PollAfter = wait;
            return state;
        }

        _live.Current = Said(TimeSpan.FromSeconds(120));
        _live.ChatPollAfter = TimeSpan.FromSeconds(30);
        var vm = await Loaded();
        await vm.TickAsync();
        Advance(29);
        await vm.TickAsync();
        Assert.Equal(1, _live.ChatReads);
        Advance(1);
        await vm.TickAsync();
        Assert.Equal(2, _live.ChatReads);
        Advance(89);
        await vm.TickAsync();
        Assert.Equal(1, _live.StateReads);
        Advance(1);
        _live.Current = Said(null);
        await vm.TickAsync();
        Assert.Equal(2, _live.StateReads);
        // It says nothing any more: this app's interval again
        Advance(60);
        await vm.TickAsync();
        Assert.Equal(3, _live.StateReads);
    }

    [Fact]
    public async Task A_refusal_says_when_to_ask_again()
    {
        _live.Current = OnAir(chat: true);
        var vm = await Loaded();
        _live.RetryAfter = TimeSpan.FromSeconds(200);
        await vm.TickAsync();
        Advance(60);
        await vm.TickAsync();
        int[] Asked() => new[] { _live.StateReads, _live.ChatReads, _live.Beats.Count };
        var asked = Asked();
        Advance(139);
        await vm.TickAsync();
        Assert.Equal(asked, Asked());
        Advance(61);
        await vm.TickAsync();
        Assert.Equal(asked.Select(n => n + 1), Asked());
        // Leaving never fails
        vm.Dispose();
    }

    [Fact]
    public async Task A_wait_begins_when_the_answer_is_there()
    {
        _live.Current = OnAir(chat: true);
        var vm = await Loaded();
        _live.Answering = () => Advance(8);
        await vm.TickAsync();
        Advance(60);
        // The state, the chat and the beat, 8 seconds each
        await vm.TickAsync();
        _live.Answering = () => { };
        var (states, chats, beats) = (_live.StateReads, _live.ChatReads, _live.Beats.Count);
        async Task After(double seconds)
        {
            Advance(seconds);
            await vm.TickAsync();
        }

        // The chat's 10 seconds began 8 before the beat's 20, and the state's 60 8 before that
        await After(1);
        Assert.Equal(chats, _live.ChatReads);
        await After(1);
        Assert.Equal(chats + 1, _live.ChatReads);
        await After(17);
        Assert.Equal(beats, _live.Beats.Count);
        await After(1);
        Assert.Equal(beats + 1, _live.Beats.Count);
        await After(23);
        Assert.Equal(states, _live.StateReads);
        await After(1);
        Assert.Equal(states + 1, _live.StateReads);
    }

    [Fact]
    public async Task A_premieres_video_that_could_not_be_read_is_asked_for_again()
    {
        _catalog.Broken = true;
        _live.Current = Premiere(LiveStatus.Ended, video: "nope");
        var vm = await Loaded();
        Assert.Equal(LiveMode.Recording, vm.Mode);
        Assert.Equal(_localizer["state.error"], vm.Notice.Headline);
        Advance(15);
        await vm.TickAsync();
        Assert.Equal(2, _live.StateReads);
        _catalog.Broken = false;
        Advance(15);
        await vm.TickAsync();
        Assert.Equal(LiveMode.Ended, vm.Mode);
    }

    [Fact]
    public void What_the_server_says_is_kept_between_5_seconds_and_10_minutes()
    {
        Assert.Null(LiveWatchViewModel.PollAfter(null));
        Assert.Equal(
            new double[] { 5, 5, 7, 600 },
            new[] { TimeSpan.FromSeconds(-1), TimeSpan.Zero, TimeSpan.FromSeconds(7), TimeSpan.MaxValue }.Select(t => LiveWatchViewModel.PollAfter(t)!.Value.TotalSeconds));
    }

    [Fact]
    public async Task The_replay_is_what_had_been_written_by_then()
    {
        LiveChatMessage M(string id, long at) => new() { Id = id, OffsetMs = at };
        _live.Current = new LiveState { Status = LiveStatus.Ended, VodUrl = "https://x.test/v.m3u8", ChatReplay = true };
        _live.Windows[0] = new LiveChatPage(new[] { M("a", 0), M("b", 5_000) });
        _live.Windows[60_000] = new LiveChatPage(new[] { M("c", 61_000) });
        var vm = await Loaded();
        await vm.TickAsync();
        Assert.Equal(new[] { "a" }, vm.Messages.Select(m => m.Id));
        _player.Tick(62);
        await vm.TickAsync();
        Assert.Equal(new[] { "a", "b", "c" }, vm.Messages.Select(m => m.Id));
    }

    [Fact]
    public async Task A_reminder_asks_to_sign_in_first()
    {
        _live.Current = new LiveState { Status = LiveStatus.Scheduled, ReminderEnabled = true };
        var vm = await Loaded();
        Assert.True(vm.CanRemind);
        var auth = new AuthViewModel(new FakeAuth());
        await vm.ToggleReminderAsync(auth);
        Assert.True(auth.IsSignedIn);
        Assert.True(vm.ReminderSet);
        await vm.ToggleReminderAsync(auth);
        Assert.Equal(new[] { true, false }, _live.Reminders);
        _live.Fails = true;
        await vm.ToggleReminderAsync(auth);
        Assert.True(vm.ReminderFailed);
        Assert.False(vm.ReminderSet);
        // Not while reminders are off
        _live.Current = new LiveState { Status = LiveStatus.Scheduled };
        Advance(15);
        await vm.TickAsync();
        Assert.False(vm.CanRemind);
    }

    [Fact]
    public async Task A_page_that_cannot_be_read_says_so()
    {
        _live.Fails = true;
        Assert.Equal(LoadState.Error, (await Loaded()).State);
    }

    [Fact]
    public void Days_take_the_form_of_their_number()
    {
        Assert.Equal("Premieres in 1 day", _localizer.Plural("live.premiere.inDays", 1));
        var ar = new Localizer("ar");
        Assert.Equal("يبدأ العرض الأول بعد يومين", ar.Plural("live.premiere.inDays", 2));
        Assert.Equal("يبدأ العرض الأول بعد ٣ أيام", ar.Plural("live.premiere.inDays", 3));
        Assert.Equal("يبدأ العرض الأول بعد ١٢ يومًا", ar.Plural("live.premiere.inDays", 12));
    }

    // ---- The list

    [Fact]
    public async Task The_list_shows_what_comes_next_and_only_with_its_switches()
    {
        LiveStream Coming(string id, DateTimeOffset? at) => new() { Id = id, Status = LiveStatus.Scheduled, ScheduledStart = at };
        _live.Lists[LiveStatus.Live] = new[] { new LiveStream { Id = "on", Status = LiveStatus.Live } };
        _live.Lists[LiveStatus.Scheduled] = new[] { Coming("later", _now.AddMinutes(15)), Coming("past", _now.AddSeconds(-1)), Coming("untimed", null), Coming("next", _now.AddMinutes(1)) };
        var navigator = new Navigator();
        var vm = new LiveListViewModel(_live, _localizer, navigator, () => _now);
        await vm.LoadAsync();
        Assert.Equal(new[] { "on", "next", "later" }, vm.All.Select(c => c.Id));
        await vm.ChooseAsync(LiveEventType.Jumuah);
        Assert.Equal(LiveEventType.Jumuah, _live.Asked);
        _live.Features = new LiveFeatures();
        await vm.LoadAsync();
        Assert.Equal(new[] { "on" }, vm.All.Select(c => c.Id));
        Assert.False(vm.HasEvents);
        Assert.Null(vm.Event);
        vm.Open(vm.All[0]);
        Assert.Equal("on", Assert.IsType<Route.Live>(navigator.Current).StreamId);
    }

    [Fact]
    public async Task An_earlier_choice_that_answers_late_is_not_shown()
    {
        var vm = new LiveListViewModel(_live, _localizer, new Navigator(), () => _now);
        var gate = new TaskCompletionSource();
        _live.Held = (LiveEventType.Jumuah, gate);
        var late = vm.ChooseAsync(LiveEventType.Jumuah);
        await vm.ChooseAsync(LiveEventType.Halqa);
        gate.SetResult();
        await late;
        Assert.Equal(LiveEventType.Halqa, vm.Event);
    }

    [Fact]
    public async Task The_replay_begins_without_what_was_live_and_at_its_own_time()
    {
        _live.Current = OnAir(chat: true);
        _live.Pinned = new LiveChatMessage { Id = "p" };
        var vm = await Loaded();
        await vm.TickAsync();
        Assert.Equal("p", vm.Pinned?.Id);
        Advance(59);
        // A live chat that could not be read waits a minute; the replay does not
        _live.ChatFails = true;
        await vm.TickAsync();
        Advance(1);
        _live.Current = new LiveState { Status = LiveStatus.Ended, VodUrl = "https://x.test/v.m3u8", ChatReplay = true };
        _live.Windows[0] = new LiveChatPage(new[] { new LiveChatMessage { Id = "a" } });
        await vm.TickAsync();
        Assert.Equal(LiveMode.Recording, vm.Mode);
        Assert.Null(vm.Pinned);
        Assert.Equal(new[] { "a" }, vm.Messages.Select(m => m.Id));
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_list_and_a_failed_first_read_says_so()
    {
        var vm = new LiveListViewModel(_live, _localizer, new Navigator(), () => _now);
        await vm.LoadAsync();
        _live.Fails = true;
        await vm.LoadAsync();
        Assert.Equal(LoadState.Content, vm.State);
        var first = new LiveListViewModel(_live, _localizer, new Navigator(), () => _now);
        await first.LoadAsync();
        Assert.Equal(LoadState.Error, first.State);
    }

    // ---- The server

    private sealed class Handler : HttpMessageHandler
    {
        public Dictionary<string, string> Answers { get; } = new();
        public List<HttpRequestMessage> Requests { get; } = new();
        public string? Body { get; private set; }
        public (HttpStatusCode Status, string? RetryAfter)? Refusal { get; set; }
        public string? RefusalDate { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            if (Refusal is { } refusal)
            {
                var refused = new HttpResponseMessage(refusal.Status);
                if (refusal.RetryAfter != null) refused.Headers.TryAddWithoutValidation("Retry-After", refusal.RetryAfter);
                if (RefusalDate != null) refused.Headers.TryAddWithoutValidation("Date", RefusalDate);
                return refused;
            }

            if (request.Content != null) Body = await request.Content.ReadAsStringAsync(ct);
            return Answers.TryGetValue(request.Method + " " + request.RequestUri!.PathAndQuery, out var answer)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private (HttpLiveClient Client, Handler Handler) Server(string? token = null, Func<DateTimeOffset>? now = null)
    {
        var handler = new Handler();
        return (new HttpLiveClient(new HttpClient(handler), new Uri("https://api.test"), _ => Task.FromResult(token), now ?? (() => _now)), handler);
    }

    [Fact]
    public async Task Reads_lists_and_leaves_out_what_it_does_not_know()
    {
        var (client, handler) = Server();
        handler.Answers["GET /api/live-streams?status=live&limit=50&eventType=halqa"] =
            """{"success":true,"result":{"data":[{"id":"a","status":"live","title":"A","kind":"premiere","eventType":"halqa","channelName":"C","thumbnailUrl":"","scheduledStartTime":"2026-11-24T18:00:00Z","currentViewers":3},{"id":"b","status":"paused"},{"id":"c","status":"live","kind":"new","eventType":"new"}]}}""";
        var list = await client.ListAsync(LiveStatus.Live, LiveEventType.Halqa);
        Assert.Equal(new[] { "a", "c" }, list.Select(s => s.Id));
        Assert.Equal(LiveKind.Premiere, list[0].Kind);
        Assert.Equal(LiveEventType.Halqa, list[0].EventType);
        Assert.Null(list[0].ThumbnailUrl);
        Assert.Equal(_now, list[0].ScheduledStart);
        Assert.Equal(3, list[0].Viewers);
        Assert.Equal(LiveKind.Live, list[1].Kind);
        Assert.Null(list[1].EventType);
    }

    [Fact]
    public async Task The_states_clock_is_the_servers_at_the_middle_of_the_request_and_missing_parts_are_off()
    {
        var times = new Queue<DateTimeOffset>(new[] { _now, _now.AddSeconds(2) });
        var (client, handler) = Server("token", () => times.Dequeue());
        handler.Answers["GET /api/live-streams/s/state"] = """{"result":{"status":"scheduled","serverNow":NOW,"chat":{"enabled":true},"reminder":{"enabled":true,"set":true}}}""".Replace("NOW", _now.AddSeconds(11).ToUnixTimeMilliseconds().ToString());
        var state = await client.GetStateAsync("s");
        Assert.Equal(TimeSpan.FromSeconds(10), state.ClockOffset);
        Assert.True(state.ChatEnabled && state.ReminderEnabled && state.ReminderSet);
        Assert.False(state.ChatReplay);
        Assert.Equal("Bearer token", handler.Requests[0].Headers.Authorization!.ToString());
        Assert.Null(state.PlaybackUrl);
    }

    [Fact]
    public async Task Reads_chat_counts_viewers_and_sets_reminders()
    {
        var (client, handler) = Server("token");
        handler.Answers["GET /api/live-streams/s/chat"] = """{"result":{"messages":[{"id":"m","authorName":"A","text":"x","authorRole":"owner","offsetMs":7}],"pinned":null}}""";
        handler.Answers["GET /api/live-streams/s/chat?fromMs=0&toMs=60000"] = """{"result":{"messages":[],"truncated":true}}""";
        handler.Answers["POST /api/live-streams/s/viewers"] = """{"result":{"viewers":4}}""";
        handler.Answers["DELETE /api/live-streams/s/reminders"] = """{"result":{"set":false,"count":0}}""";
        var page = await client.GetChatAsync("s");
        Assert.Equal(LiveChatRole.Owner, Assert.Single(page.Messages).Role);
        Assert.Null(page.Pinned);
        Assert.True((await client.GetChatReplayAsync("s", 0, 60_000)).Truncated);
        Assert.Equal(4, await client.BeatAsync("s", "v", leaving: true));
        Assert.Equal("""{"viewerId":"v","leaving":true}""", handler.Body);
        await Assert.ThrowsAsync<CatalogException>(() => client.BeatAsync("gone", "v", leaving: false));
        await client.SetReminderAsync("s", false);
        await Assert.ThrowsAsync<CatalogException>(() => client.SetReminderAsync("s", true));
        await Assert.ThrowsAsync<AuthRequiredException>(() => Server().Client.SetReminderAsync("s", true));
        await Assert.ThrowsAsync<CatalogException>(() => client.GetStreamAsync("s"));
    }

    [Fact]
    public async Task Reads_when_to_ask_again_and_a_bad_value_is_as_if_absent()
    {
        var (client, handler) = Server();
        handler.Answers["GET /api/live-streams/s/chat"] = """{"result":{"messages":[],"pollAfterMs":30000}}""";
        Assert.Equal(TimeSpan.FromSeconds(30), (await client.GetChatAsync("s")).PollAfter);
        foreach (var bad in new[] { "\"30000\"", "null", "{}", "0", "-5", "1e999" })
        {
            handler.Answers["GET /api/live-streams/s/state"] = """{"result":{"status":"live","pollAfterMs":BAD}}""".Replace("BAD", bad);
            Assert.Null((await client.GetStateAsync("s")).PollAfter);
        }
    }

    [Theory]
    [InlineData(429, "30", 30)]
    [InlineData(503, "60", 60)]
    [InlineData(429, " 30 ", 30)]
    [InlineData(429, "-1", null)]
    [InlineData(503, null, null)]
    [InlineData(429, "soon", null)]
    [InlineData(429, "99999999999", null)]
    [InlineData(500, "30", null)]
    public async Task A_refused_read_says_when_to_ask_again(int status, string? header, int? seconds)
    {
        var (client, handler) = Server();
        handler.Refusal = ((HttpStatusCode)status, header);
        var error = await Assert.ThrowsAsync<CatalogException>(() => client.GetChatAsync("s"));
        Assert.Equal(seconds, error.RetryAfter is { } wait ? (int)wait.TotalSeconds : null);
    }

    [Theory]
    [InlineData(429, "Tue, 24 Nov 2026 12:00:45 GMT", "Tue, 24 Nov 2026 12:00:00 GMT", 45)]
    [InlineData(503, "Tue, 24 Nov 2026 12:00:45 GMT", "Tue, 24 Nov 2026 12:00:00 GMT", 45)]
    [InlineData(429, "Tue, 24 Nov 2026 11:00:00 GMT", "Tue, 24 Nov 2026 12:00:00 GMT", 0)]
    // Without the answer's own date, from the computer's clock (18:00)
    [InlineData(429, "Tue, 24 Nov 2026 18:00:30 GMT", null, 30)]
    public async Task A_refusal_may_give_a_date(int status, string at, string? sent, int seconds)
    {
        var (client, handler) = Server();
        handler.Refusal = ((HttpStatusCode)status, at);
        handler.RefusalDate = sent;
        var error = await Assert.ThrowsAsync<CatalogException>(() => client.GetChatAsync("s"));
        Assert.Equal(TimeSpan.FromSeconds(seconds), error.RetryAfter);
    }

    [Fact]
    public async Task Features_are_off_when_they_cannot_be_read()
    {
        var (client, handler) = Server();
        Assert.False((await client.GetFeaturesAsync()).HasComing);
        handler.Answers["GET /api/me/features"] = """{"version":1,"features":{"liveEvents":true,"livePremieres":"yes"}}""";
        var features = await client.GetFeaturesAsync();
        Assert.True(features.Events);
        Assert.False(features.Premieres);
    }
}
