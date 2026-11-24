using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Media;
using AlUmmahNow.App.ViewModels;
using AlUmmahNow.CoreBridge;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlUmmahNow.App.Live
{
    public enum LiveMode { Live, Recording, Soon, Ended, Cancelled }

    /// <summary>One live stream or premiere: the countdown, the stream, its recording, and the chat beside it.</summary>
    public sealed partial class LiveWatchViewModel : ObservableObject, IDisposable
    {
        private const long ReplayWindowMs = 60_000;
        private const int ReplayPages = 5;
        private const int Shown = 100;

        /// <summary>A stream paused for longer than this is behind.</summary>
        private static readonly TimeSpan PausedBehind = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan MinPoll = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MaxPoll = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan Retry = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan LivePoll = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan SoonPoll = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DuePoll = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ChatPoll = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ChatSoonPoll = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ChatBackoff = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan BeatEvery = TimeSpan.FromSeconds(20);

        private readonly ILiveClient _live;
        private readonly ICatalogClient _catalog;
        private readonly Localizer _localizer;
        private readonly Func<DateTimeOffset> _now;
        private readonly FollowedPlayer _player;
        private readonly IMediaSession _session;
        private readonly CancellationTokenSource _stop = new();
        private readonly string _viewerId = Guid.NewGuid().ToString();
        private readonly PremiereFollower _follower = new();
        private readonly Dictionary<long, IReadOnlyList<LiveChatMessage>> _replay = new();
        private PremiereClock? _clock;
        private string? _opened;
        private bool _isGone;
        private bool _hasBeaten;
        private DateTimeOffset _stateAt = DateTimeOffset.MinValue;
        private DateTimeOffset _chatDue = DateTimeOffset.MinValue;
        private TimeSpan? _stateWait;
        private DateTimeOffset _beatDue = DateTimeOffset.MinValue;
        private DateTimeOffset? _pausedSince;
        private DateTimeOffset? _failedSince;

        [ObservableProperty] private LoadState _state = LoadState.Loading;
        [ObservableProperty] private LiveMode _mode = LiveMode.Soon;
        [ObservableProperty] private IReadOnlyList<LiveChatMessage> _messages = Array.Empty<LiveChatMessage>();
        [ObservableProperty] private LiveChatMessage? _pinned;
        [ObservableProperty] private long? _viewers;

        /// <summary>The viewer went back or paused; the stream or premiere goes on without them.</summary>
        [ObservableProperty] private bool _isBehind;

        /// <summary>Time to a premiere's start; null when it has no countdown.</summary>
        [ObservableProperty] private TimeSpan? _untilStart;
        [ObservableProperty] private bool _reminderSet;
        [ObservableProperty] private bool _reminderFailed;

        public LiveWatchViewModel(string streamId, ILiveClient live, ICatalogClient catalog, IMediaPlayer player, IMediaSession session, Localizer localizer, Func<DateTimeOffset>? now = null)
        {
            StreamId = streamId;
            _live = live;
            _catalog = catalog;
            _localizer = localizer;
            _session = session;
            _now = now ?? (() => DateTimeOffset.UtcNow);
            _player = new FollowedPlayer(player, _now);
        }

        /// <summary>The page is built anew: another mode, the video, or the chat came or went.</summary>
        public event EventHandler? LayoutChanged;

        public string StreamId { get; }
        public LiveStream? Stream { get; private set; }
        public LiveState? Live { get; private set; }

        /// <summary>The player's page, once there is something to play.</summary>
        public WatchViewModel? Watch { get; private set; }

        /// <summary>The window is hidden: nothing is asked for the chat.</summary>
        public bool IsHidden { get; set; }

        public bool IsPremiere => Live?.Kind == LiveKind.Premiere;
        public string Title => Stream?.Title ?? "";
        public string Channel => Stream?.ChannelName ?? "";
        public string Description => Stream?.Description ?? "";
        public IReadOnlyList<LiveBadge> Badges => LiveText.Badges(_localizer, Mode == LiveMode.Live, Mode == LiveMode.Recording ? LiveKind.Live : Live?.Kind ?? LiveKind.Live, Live?.EventType);
        public string Watching => Mode == LiveMode.Live && Viewers is { } v ? LiveText.Watching(_localizer, v) : "";
        public string ChatTitle => _localizer[Mode == LiveMode.Recording ? "live.chat.replay" : "live.chat"];
        public string ChatEmpty => _localizer[Mode == LiveMode.Recording ? "live.chat.replay.empty" : "live.chat.empty"];

        public bool HasChat => State == LoadState.Content && Mode switch
        {
            LiveMode.Live or LiveMode.Soon => Live?.ChatEnabled == true,
            LiveMode.Recording => Live?.ChatReplay == true,
            _ => false,
        };

        /// <summary>The stream's page on the website, for writing; only while people write.</summary>
        public string? WriteUrl(string shortLinkHost) =>
            HasChat && Mode != LiveMode.Recording ? ShortLinks.Live(StreamId, shortLinkHost, lang: _localizer.Language == "ar" ? "ar" : null) : null;

        public bool CanRemind => Mode == LiveMode.Soon && Live?.ReminderEnabled == true;

        /// <summary>The line above, in large, and the lines below of what stands where the video will be.</summary>
        public (string Headline, IReadOnlyList<string> Lines) Notice
        {
            get
            {
                string T(string key) => _localizer[key];
                switch (Mode)
                {
                    case LiveMode.Cancelled: return (T(IsPremiere ? "live.premiere.cancelled" : "live.cancelled"), Array.Empty<string>());
                    case LiveMode.Ended when IsPremiere: return (T("live.premiere.ended"), Array.Empty<string>());
                    case LiveMode.Ended: return (T("live.ended"), new[] { T("live.recording.preparing") });
                }

                var when = (Live?.ScheduledStart ?? Stream?.ScheduledStart) is { } at ? new[] { _localizer.Format("live.scheduledFor", _localizer.DateTime(at)) } : Array.Empty<string>();
                if (UntilStart is not { } left) return (T("live.soon"), new[] { T("live.soon.body") }.Concat(when).ToList());
                var together = new[] { T("live.premiere.together") }.Concat(when).ToList();
                if (left <= TimeSpan.Zero) return (T("live.premiere.starting"), together);
                var days = (int)left.TotalDays;
                return days >= 1
                    ? (_localizer.Plural("live.premiere.inDays", days), together)
                    : (T("live.premiere.in") + "\n" + _localizer.Duration(Math.Ceiling(left.TotalSeconds)), together);
            }
        }

        public async Task LoadAsync()
        {
            State = LoadState.Loading;
            LayoutChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                var stream = _live.GetStreamAsync(StreamId, _stop.Token);
                var state = _live.GetStateAsync(StreamId, _stop.Token);
                await Task.WhenAll(stream, state);
                Stream = await stream;
                var live = await state;
                _stateWait = PollAfter(live.PollAfter);
                State = LoadState.Content;
                await ShowAsync(live);
            }
            catch (Exception ex) when (ex is CatalogException || ex is AuthRequiredException)
            {
                State = LoadState.Error;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Runs while the page is there. Following does not wait for the network: a late answer must not leave it with
        /// where the player was before.
        /// </summary>
        public async Task RunAsync()
        {
            await LoadAsync();
            await Task.WhenAll(EverySecond(TickAsync), EverySecond(() =>
            {
                Follow();
                return Task.CompletedTask;
            }));
        }

        private async Task EverySecond(Func<Task> work)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await work();
                    await Task.Delay(1_000, _stop.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // The page is gone
            }
        }

        /// <summary>Back to the moment: a premiere's, or the newest of a stream.</summary>
        public void ReturnToLive()
        {
            _pausedSince = null;
            IsBehind = false;
            if (_clock is { } clock)
            {
                _follower.Returned();
                _player.Follow(clock.Position(_now()));
                _player.Play();
            }
            else
            {
                Watch?.Reopen();
            }
        }

        /// <summary>Sets or takes back the reminder, asking to sign in first.</summary>
        public async Task ToggleReminderAsync(AuthViewModel auth)
        {
            ReminderFailed = false;
            if (!auth.IsSignedIn) await auth.SignInAsync(_stop.Token);
            if (!auth.IsSignedIn) return;
            var on = !ReminderSet;
            try
            {
                await _live.SetReminderAsync(StreamId, on, _stop.Token);
                ReminderSet = on;
            }
            catch (Exception ex) when (ex is CatalogException || ex is AuthRequiredException)
            {
                ReminderFailed = true;
            }
            catch (OperationCanceledException)
            {
                // The page is gone
            }
        }

        public void Dispose()
        {
            if (_stop.IsCancellationRequested) return;
            _stop.Cancel();
            // In a request of its own: the page's are canceled
            if (_hasBeaten && Mode == LiveMode.Live) _ = LeaveAsync();
            Watch?.Dispose();
            _player.Dispose();
        }

        private async Task LeaveAsync()
        {
            try
            {
                await _live.BeatAsync(StreamId, _viewerId, leaving: true);
            }
            catch (CatalogException)
            {
                // Counted out when its time runs out
            }
        }

        /// <summary>One second: the clock and whatever is due.</summary>
        public async Task TickAsync()
        {
            if (State != LoadState.Content) return;
            UntilStart = Countdown();
            if (_now() - _stateAt >= StateEvery()) await RefreshStateAsync();
            await RefreshChatAsync();
            if (Mode == LiveMode.Live && _now() >= _beatDue)
            {
                _beatDue = _now() + BeatEvery;
                _hasBeaten = true;
                try
                {
                    if (await _live.BeatAsync(StreamId, _viewerId, leaving: false, _stop.Token) is { } count) Viewers = count;
                }
                catch (CatalogException ex)
                {
                    if (PollAfter(ex.RetryAfter) is { } wait) _beatDue = _now() + wait;
                }
            }
        }

        /// <summary>
        /// When the server wants to be asked again, so it can slow installed apps down. Between 5 seconds and 10 minutes
        /// whatever it says: a bad value neither stops the app nor makes it hammer.
        /// </summary>
        public static TimeSpan? PollAfter(TimeSpan? said) =>
            said is { } t ? (t < MinPoll ? MinPoll : t > MaxPoll ? MaxPoll : t) : (TimeSpan?)null;

        /// <summary>One second of the player: kept at a premiere's moment, and known to be behind.</summary>
        public void Follow()
        {
            if (Mode != LiveMode.Live || Watch == null)
            {
                IsBehind = false;
                return;
            }

            if (_clock is not { } clock)
            {
                // The encoder went away and may come back
                if (_player.State != PlayerState.Failed) _failedSince = null;
                else if (_now() - (_failedSince ??= _now()) > Retry)
                {
                    _failedSince = null;
                    Watch.Reopen();
                }

                if (_player.State == PlayerState.Playing) _pausedSince = null;
                if (_player.State == PlayerState.Paused) _pausedSince ??= _now();
                // Playing on from a pause is still behind: only returning (or the stream's end) is not
                if (_pausedSince is { } since && _now() - since > PausedBehind) IsBehind = true;
                return;
            }

            if (clock.IsOver(_now()))
            {
                IsBehind = false;
                return;
            }

            if (_player.Read() is not { } reading) return;
            var action = _follower.Tick(clock.Position(_now()), reading);
            if (action.NormalSpeed) Watch.SelectSpeed(1);
            if (action.SeekTo is { } to) _player.Follow(to);
            IsBehind = _follower.IsBehind;
        }

        /// <summary>The server's wait when it gave one, else this app's; nothing is asked of what will not change.</summary>
        private TimeSpan StateEvery()
        {
            var own = OwnStateEvery();
            return own == TimeSpan.MaxValue ? own : _stateWait ?? own;
        }

        private TimeSpan OwnStateEvery() => Mode switch
        {
            LiveMode.Live => Watch == null ? DuePoll : LivePoll,
            // A premiere's time has come: it starts within the minute
            LiveMode.Soon => UntilStart is { } left && left <= TimeSpan.Zero ? DuePoll : SoonPoll,
            LiveMode.Ended when !_isGone => SoonPoll,
            _ => TimeSpan.MaxValue,
        };

        private async Task RefreshStateAsync()
        {
            _stateAt = _now();
            _stateWait = null;
            if (await Quiet(() => _live.GetStateAsync(StreamId, _stop.Token), wait => _stateWait = wait) is not { } live) return;
            _stateWait = PollAfter(live.PollAfter);
            await ShowAsync(live);
        }

        private async Task ShowAsync(LiveState live)
        {
            _stateAt = _now();
            var before = (Mode, HasChat, Watch, CanRemind, Live?.EventType);
            Live = live;
            var mode = ModeOf(live);
            Mode = _isGone && mode == LiveMode.Recording ? LiveMode.Ended : mode;
            Viewers = live.Viewers ?? Viewers;
            ReminderSet = live.ReminderSet;
            if (!HasChat)
            {
                Messages = Array.Empty<LiveChatMessage>();
                Pinned = null;
            }

            _clock = IsPremiere && Mode == LiveMode.Live && live.StartedAt is { } startedAt ? new PremiereClock(startedAt, live.ClockOffset, live.DurationSeconds) : null;
            UntilStart = Countdown();
            await OpenAsync(live);
            if (!before.Equals((Mode, HasChat, Watch, CanRemind, Live?.EventType))) LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        public static LiveMode ModeOf(LiveState live)
        {
            if (live.Status == LiveStatus.Cancelled) return LiveMode.Cancelled;
            if (live.Kind == LiveKind.Premiere)
            {
                if (live.Status == LiveStatus.Live && live.PremiereVideoId != null) return LiveMode.Live;
                if (live.Status == LiveStatus.Ended) return live.PremiereVideoId == null ? LiveMode.Ended : LiveMode.Recording;
                return LiveMode.Soon;
            }

            if (live.Status == LiveStatus.Ended || live.SessionStatus == "ended") return live.VodUrl == null ? LiveMode.Ended : LiveMode.Recording;
            return (live.Status == LiveStatus.Live || live.SessionStatus == "live") && live.PlaybackUrl != null ? LiveMode.Live : LiveMode.Soon;
        }

        private async Task OpenAsync(LiveState live)
        {
            if (Mode != LiveMode.Live && Mode != LiveMode.Recording)
            {
                CloseWatch();
                return;
            }

            if (IsPremiere)
            {
                await OpenPremiereAsync(live.PremiereVideoId!);
                return;
            }

            var isLive = Mode == LiveMode.Live;
            var key = isLive ? "live" : "recording";
            // A recording's address is signed anew at every read: the one that plays goes on
            if (_opened == key) return;
            CloseWatch();
            _opened = key;
            Watch = NewWatch();
            Watch.LoadStream((isLive ? live.PlaybackUrl : live.VodUrl)!, Title, Channel, Stream?.ThumbnailUrl, isLive);
        }

        /// <summary>The video is released a moment after the premiere starts: asked for again in 5 seconds until it is there.</summary>
        private async Task OpenPremiereAsync(string videoId)
        {
            // After the end the same video goes on
            if (_opened == videoId) return;
            var video = await Quiet(() => _catalog.GetVideoAsync(videoId, _stop.Token));
            if (_stop.IsCancellationRequested) return;
            if (video == null || string.IsNullOrEmpty(video.PlayUrl))
            {
                if (Mode != LiveMode.Recording) return;
                _isGone = true;
                Mode = LiveMode.Ended;
                return;
            }

            CloseWatch();
            _opened = videoId;
            Watch = NewWatch();
            await Watch.LoadAsync(videoId, _stop.Token, () => _clock?.Position(_now()) ?? 0);
        }

        private WatchViewModel NewWatch() => new WatchViewModel(_catalog, _player, _session, _localizer);

        private void CloseWatch()
        {
            Watch?.Dispose();
            Watch = null;
            _opened = null;
            _pausedSince = null;
        }

        private TimeSpan? Countdown() =>
            Mode == LiveMode.Soon && IsPremiere && (Live?.ScheduledStart ?? Stream?.ScheduledStart) is { } start
                ? start - _now() - Live!.ClockOffset
                : (TimeSpan?)null;

        private async Task RefreshChatAsync()
        {
            if (!HasChat || IsHidden) return;
            if (Mode == LiveMode.Recording)
            {
                await RefreshReplayAsync((long)((Watch?.Position ?? 0) * 1_000));
                return;
            }

            if (_now() < _chatDue) return;
            TimeSpan? refused = null;
            var page = await Quiet(() => _live.GetChatAsync(StreamId, _stop.Token), wait => refused = wait);
            _chatDue = _now() + (page == null ? refused ?? ChatBackoff : PollAfter(page.PollAfter) ?? (Mode == LiveMode.Live ? ChatPoll : ChatSoonPoll));
            if (page == null || !HasChat) return;
            if (!Messages.Select(m => m.Id).SequenceEqual(page.Messages.Select(m => m.Id))) Messages = page.Messages;
            if (Pinned?.Id != page.Pinned?.Id) Pinned = page.Pinned;
        }

        /// <summary>What had been written by the moment being watched, read in windows as playback reaches them.</summary>
        private async Task RefreshReplayAsync(long positionMs)
        {
            var window = positionMs / ReplayWindowMs;
            // The one before, so a seek does not open an empty chat, and the next, so it is there in time
            foreach (var n in new[] { window, window + 1, window - 1 })
            {
                if (n < 0 || _replay.ContainsKey(n) || _now() < _chatDue) continue;
                TimeSpan? refused = null;
                var list = await ReadWindowAsync(n, wait => refused = wait);
                if (list == null) _chatDue = _now() + (refused ?? ChatBackoff);
                else _replay[n] = list;
            }

            var shown = new List<LiveChatMessage>();
            for (var n = window; shown.Count < Shown && _replay.TryGetValue(n, out var list); n--)
            {
                shown.InsertRange(0, list.Where(m => m.OffsetMs <= positionMs));
            }

            if (shown.Count > Shown) shown.RemoveRange(0, shown.Count - Shown);
            if (!Messages.Select(m => m.Id).SequenceEqual(shown.Select(m => m.Id))) Messages = shown;
        }

        /// <summary>null when it could not be read; asked for again after a while.</summary>
        private async Task<IReadOnlyList<LiveChatMessage>?> ReadWindowAsync(long n, Action<TimeSpan> refused)
        {
            var from = n * ReplayWindowMs;
            var to = from + ReplayWindowMs;
            var all = new Dictionary<string, LiveChatMessage>();
            for (var i = 0; i < ReplayPages; i++)
            {
                var start = from;
                var page = await Quiet(() => _live.GetChatReplayAsync(StreamId, start, to, _stop.Token), refused);
                if (page == null) return null;
                foreach (var m in page.Messages.Where(m => m.OffsetMs < to)) all[m.Id] = m;
                var last = page.Messages.LastOrDefault();
                if (!page.Truncated || last == null || last.OffsetMs <= from) break;
                from = last.OffsetMs;
            }

            return all.Values.OrderBy(m => m.OffsetMs).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
        }

        /// <param name="refused">The server said when to ask again.</param>
        private static async Task<T?> Quiet<T>(Func<Task<T>> work, Action<TimeSpan>? refused = null) where T : class
        {
            try
            {
                return await work();
            }
            catch (Exception ex) when (ex is CatalogException || ex is AuthRequiredException || ex is System.Text.Json.JsonException)
            {
                if (PollAfter((ex as CatalogException)?.RetryAfter) is { } wait) refused?.Invoke(wait);
                return null;
            }
        }
    }
}
