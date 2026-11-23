using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Media;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Studio;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AlUmmahNow.App.ViewModels
{
    public sealed partial class LiveWatchViewModel : ObservableObject, IDisposable
    {
        public const double LiveEdgeToleranceSeconds = 12;

        private readonly ILiveCatalogClient _catalog;
        private readonly IMediaPlayer _player;
        private readonly IMediaSession _session;
        private readonly Localizer _localizer;
        private readonly string _viewerId;
        private readonly List<LiveChatMessage> _allMessages = new();

        [ObservableProperty] private LoadState _state = LoadState.Loading;
        [ObservableProperty] private LiveStream? _stream;
        [ObservableProperty] private UserFeatures _features;
        [ObservableProperty] private LiveChatMessage? _pinnedMessage;
        [ObservableProperty] private LiveChatSettings _chatSettings = new();
        [ObservableProperty] private LiveReminderStatus _reminder = new(false, 0);
        [ObservableProperty] private string _draftMessage = "";
        [ObservableProperty] private string? _chatErrorKey;
        [ObservableProperty] private bool _isSendingChat;
        [ObservableProperty] private bool _isTogglingReminder;
        [ObservableProperty] private double _position;
        [ObservableProperty] private double _duration;
        [ObservableProperty] private bool _isPlaying;
        [ObservableProperty] private bool _playbackFailed;
        [ObservableProperty] private bool _isAtLiveEdge = true;
        [ObservableProperty] private bool _canModerate;
        [ObservableProperty] private LiveChatMessage? _pendingRemovalMessage;
        [ObservableProperty] private LiveChatMessage? _pendingReportMessage;
        [ObservableProperty] private bool _reportSubmitted;

        public LiveWatchViewModel(
            ILiveCatalogClient catalog,
            IMediaPlayer player,
            IMediaSession session,
            Localizer localizer,
            UserFeatures? features = null,
            bool canModerate = false,
            string? viewerId = null)
        {
            _catalog = catalog;
            _player = player;
            _session = session;
            _localizer = localizer;
            _features = features ?? new UserFeatures(new[]
            {
                UserFeatures.Live,
                UserFeatures.LiveChat,
                UserFeatures.LivePremieres,
                UserFeatures.LiveReminders,
                UserFeatures.LiveEvents,
            });
            _canModerate = canModerate;
            _viewerId = viewerId ?? Guid.NewGuid().ToString("N");
            _player.StateChanged += OnPlayerState;
            _player.PositionChanged += OnPlayerPosition;
            _session.Command += OnSessionCommand;
        }

        public ObservableCollection<LiveChatMessage> VisibleChatMessages { get; } = new();

        public string Title => Stream?.Title.For(_localizer.Language) ?? "";
        public string Description => Stream?.Description.For(_localizer.Language) ?? "";
        public string Channel => Stream?.ChannelName ?? "";

        public bool ShowChatPanel =>
            Stream != null
            && Features.IsOn(UserFeatures.LiveChat)
            && !Stream.IsKidsContent
            && Stream.ChatEnabled
            && ChatSettings.FeatureEnabled;

        public bool ShowReminderButton =>
            Stream != null
            && Features.IsOn(UserFeatures.LiveReminders)
            && Stream.Status == LiveStreamStatus.Scheduled;

        public string StatusBadgeText => Stream switch
        {
            { StreamKind: LiveStreamKind.Premiere, Status: LiveStreamStatus.Live } => _localizer["live.badge.premiere"],
            { Status: LiveStreamStatus.Live } => _localizer["live.badge.live"],
            { Status: LiveStreamStatus.Scheduled } => _localizer["live.badge.upcoming"],
            _ => _localizer["live.badge.ended"],
        };

        public string? EventBadgeText
        {
            get
            {
                if (Stream == null || !Features.IsOn(UserFeatures.LiveEvents)) return null;
                return Stream.EventType switch
                {
                    LiveEventType.Jummah => _localizer["live.event.jummah"],
                    LiveEventType.Taraweh => _localizer["live.event.taraweh"],
                    LiveEventType.Eid => _localizer["live.event.eid"],
                    LiveEventType.Halqa => _localizer["live.event.halqa"],
                    _ => null,
                };
            }
        }

        public string MetaLine
        {
            get
            {
                if (Stream == null) return "";
                string? detail = Stream.Status switch
                {
                    LiveStreamStatus.Live => _localizer.Format("live.viewersWatching", _localizer.Number(Stream.ViewerCount)),
                    LiveStreamStatus.Scheduled when Stream.ScheduledStartAt.HasValue =>
                        _localizer.Format("live.scheduledFor", _localizer.DateTime(Stream.ScheduledStartAt.Value)),
                    _ => null,
                };
                return _localizer.Join(Stream.ChannelName, detail);
            }
        }

        public string ReminderButtonText => Reminder.Reminded
            ? _localizer.Format("live.reminderSet", _localizer.Number(Reminder.ReminderCount))
            : _localizer["live.remindMe"];

        public async Task LoadAsync(string streamId, CancellationToken cancellationToken = default)
        {
            State = LoadState.Loading;
            PlaybackFailed = false;
            ChatErrorKey = null;
            try
            {
                var stream = await _catalog.GetLiveStreamAsync(streamId, cancellationToken);
                try
                {
                    var state = await _catalog.GetLiveStreamStateAsync(streamId, cancellationToken);
                    ApplyState(stream, state);
                }
                catch (CatalogException)
                {
                    // Stream metadata alone is enough if state endpoint is temporarily unreachable.
                }

                Stream = stream;
                if (Features.IsOn(UserFeatures.LiveReminders) && stream.Status == LiveStreamStatus.Scheduled)
                {
                    try
                    {
                        Reminder = await _catalog.GetLiveReminderStatusAsync(streamId, cancellationToken);
                    }
                    catch (CatalogException)
                    {
                        Reminder = new LiveReminderStatus(false, 0);
                    }
                }

                _allMessages.Clear();
                if (Features.IsOn(UserFeatures.LiveChat) && !stream.IsKidsContent && stream.ChatEnabled)
                {
                    try
                    {
                        var chat = await _catalog.GetLiveChatMessagesAsync(streamId, cancellationToken: cancellationToken);
                        _allMessages.AddRange(chat.Items);
                        PinnedMessage = chat.PinnedMessage;
                        ChatSettings = chat.Settings;
                    }
                    catch (CatalogException)
                    {
                        ChatSettings = new LiveChatSettings { ChatEnabled = stream.ChatEnabled, SlowModeSeconds = stream.SlowModeSeconds };
                    }
                }

                SyncVisibleMessages();
                RaiseSummaryChanged();
                State = LoadState.Content;

                var playUrl = stream.Status == LiveStreamStatus.Ended
                    ? stream.VodUrl ?? stream.PlaybackUrl
                    : stream.PlaybackUrl;
                if (stream.Status != LiveStreamStatus.Scheduled && !string.IsNullOrEmpty(playUrl))
                {
                    _session.SetMetadata(Title, Channel, stream.ThumbnailUrl, 0);
                    _player.Open(playUrl!, 0, autoPlay: true);
                }
            }
            catch (CatalogException)
            {
                State = LoadState.Error;
            }
            catch (System.Text.Json.JsonException)
            {
                State = LoadState.Error;
            }
        }

        public async Task BeatViewerAsync(CancellationToken cancellationToken = default)
        {
            if (Stream == null || Stream.Status != LiveStreamStatus.Live) return;
            try
            {
                var liveState = await _catalog.SendLiveHeartbeatAsync(Stream.Id, _viewerId, cancellationToken);
                ApplyState(Stream, liveState);
                RaiseSummaryChanged();
            }
            catch (CatalogException)
            {
                // Heartbeat is best-effort.
            }
        }

        public async Task SendChatMessageAsync(CancellationToken cancellationToken = default)
        {
            if (Stream == null || !ShowChatPanel || IsSendingChat) return;
            var err = LiveChatFastFilter.Validate(DraftMessage, ChatSettings.LinksAllowed);
            if (err.HasValue)
            {
                ChatErrorKey = err.Value switch
                {
                    LiveChatValidationError.Empty => "live.chat.error.empty",
                    LiveChatValidationError.TooLong => "live.chat.error.tooLong",
                    LiveChatValidationError.LinksNotAllowed => "live.chat.error.links",
                    _ => "live.chat.error.blocked",
                };
                return;
            }

            IsSendingChat = true;
            ChatErrorKey = null;
            try
            {
                var offsetMs = (long)Math.Max(0, Math.Round(Position * 1000));
                var msg = await _catalog.PostLiveChatMessageAsync(Stream.Id, DraftMessage.Trim(), offsetMs, cancellationToken);
                if (_allMessages.All(m => m.Id != msg.Id)) _allMessages.Add(msg);
                DraftMessage = "";
                SyncVisibleMessages();
            }
            catch (CatalogException)
            {
                ChatErrorKey = "live.chat.error.send";
            }
            finally
            {
                IsSendingChat = false;
            }
        }

        public async Task ToggleReminderAsync(CancellationToken cancellationToken = default)
        {
            if (Stream == null || !ShowReminderButton || IsTogglingReminder) return;
            IsTogglingReminder = true;
            try
            {
                Reminder = await _catalog.SetLiveReminderAsync(Stream.Id, !Reminder.Reminded, cancellationToken);
                OnPropertyChanged(nameof(ReminderButtonText));
            }
            catch (CatalogException)
            {
                // Keep existing reminder state.
            }
            finally
            {
                IsTogglingReminder = false;
            }
        }

        public void RequestRemoveMessage(LiveChatMessage message) => PendingRemovalMessage = message;

        public void DismissRemoveMessage() => PendingRemovalMessage = null;

        public async Task ConfirmRemoveMessageAsync(CancellationToken cancellationToken = default)
        {
            if (Stream == null || PendingRemovalMessage == null) return;
            var target = PendingRemovalMessage;
            PendingRemovalMessage = null;
            try
            {
                var updated = await _catalog.ModerateLiveChatMessageAsync(Stream.Id, target.Id, LiveModerationAction.Remove, cancellationToken);
                ReplaceMessage(updated);
                if (PinnedMessage?.Id == updated.Id) PinnedMessage = null;
                SyncVisibleMessages();
            }
            catch (CatalogException)
            {
                // Keep current list on transient moderation failure.
            }
        }

        public async Task TogglePinMessageAsync(LiveChatMessage message, CancellationToken cancellationToken = default)
        {
            if (Stream == null) return;
            var action = message.IsPinned ? LiveModerationAction.Unpin : LiveModerationAction.Pin;
            try
            {
                var updated = await _catalog.ModerateLiveChatMessageAsync(Stream.Id, message.Id, action, cancellationToken);
                for (var i = 0; i < _allMessages.Count; i++)
                {
                    if (_allMessages[i].Id == updated.Id) _allMessages[i] = updated;
                    else _allMessages[i].IsPinned = false;
                }

                PinnedMessage = updated.IsPinned ? updated : null;
                SyncVisibleMessages();
            }
            catch (CatalogException)
            {
                // Keep current pin state.
            }
        }

        public void RequestReportMessage(LiveChatMessage message)
        {
            ReportSubmitted = false;
            PendingReportMessage = message;
        }

        public void DismissReportMessage() => PendingReportMessage = null;

        public async Task ConfirmReportMessageAsync(LiveReportReason reason = LiveReportReason.Abuse, CancellationToken cancellationToken = default)
        {
            if (Stream == null || PendingReportMessage == null) return;
            var target = PendingReportMessage;
            PendingReportMessage = null;
            try
            {
                await _catalog.ReportLiveChatMessageAsync(Stream.Id, target.Id, reason, cancellationToken: cancellationToken);
                ReportSubmitted = true;
            }
            catch (CatalogException)
            {
                // Ignore transient report error.
            }
        }

        [RelayCommand]
        public void SeekToLiveEdge()
        {
            if (_player.Duration > 0)
            {
                _player.Seek(_player.Duration);
            }

            IsAtLiveEdge = true;
        }

        public string FormatMessageOffset(LiveChatMessage message) =>
            _localizer.Duration(TimeSpan.FromMilliseconds(Math.Max(0, message.OffsetMs)));

        public void Dispose()
        {
            _player.StateChanged -= OnPlayerState;
            _player.PositionChanged -= OnPlayerPosition;
            _session.Command -= OnSessionCommand;
            _player.Stop();
            _session.Clear();
        }

        private static void ApplyState(LiveStream stream, LiveStreamState state)
        {
            stream.Status = state.Status;
            stream.ViewerCount = state.ViewerCount;
            stream.PeakViewerCount = Math.Max(stream.PeakViewerCount, state.PeakViewerCount);
            if (!string.IsNullOrEmpty(state.PlaybackUrl)) stream.PlaybackUrl = state.PlaybackUrl;
            if (!string.IsNullOrEmpty(state.VodUrl)) stream.VodUrl = state.VodUrl;
            stream.ChatEnabled = state.ChatEnabled;
            stream.SlowModeSeconds = state.SlowModeSeconds;
        }

        private void ReplaceMessage(LiveChatMessage updated)
        {
            for (var i = 0; i < _allMessages.Count; i++)
            {
                if (_allMessages[i].Id == updated.Id)
                {
                    _allMessages[i] = updated;
                    return;
                }
            }
        }

        private void SyncVisibleMessages()
        {
            var positionMs = (long)Math.Max(0, Math.Round(Position * 1000));
            var filtered = _allMessages
                .Where(m => m.Status == LiveChatStatus.Published)
                .Where(m => Stream?.Status != LiveStreamStatus.Ended || m.OffsetMs <= positionMs)
                .ToList();

            VisibleChatMessages.Clear();
            foreach (var m in filtered) VisibleChatMessages.Add(m);
        }

        private void RaiseSummaryChanged()
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(Channel));
            OnPropertyChanged(nameof(ShowChatPanel));
            OnPropertyChanged(nameof(ShowReminderButton));
            OnPropertyChanged(nameof(StatusBadgeText));
            OnPropertyChanged(nameof(EventBadgeText));
            OnPropertyChanged(nameof(MetaLine));
            OnPropertyChanged(nameof(ReminderButtonText));
        }

        private void OnPlayerState(object? sender, EventArgs e)
        {
            IsPlaying = _player.State == PlayerState.Playing;
            PlaybackFailed = _player.State == PlayerState.Failed;
            _session.SetPlayback(_player.State, _player.Position, _player.Rate);
        }

        private void OnPlayerPosition(object? sender, EventArgs e)
        {
            Position = _player.Position;
            Duration = _player.Duration;
            if (Stream?.Status == LiveStreamStatus.Live && Duration > 0)
            {
                IsAtLiveEdge = (Duration - Position) <= LiveEdgeToleranceSeconds;
            }
            else if (Stream?.Status == LiveStreamStatus.Ended)
            {
                SyncVisibleMessages();
            }
        }

        private void OnSessionCommand(object? sender, MediaCommand command)
        {
            switch (command)
            {
                case MediaCommand.Play: _player.Play(); break;
                case MediaCommand.Pause: _player.Pause(); break;
                case MediaCommand.Toggle:
                    if (_player.State == PlayerState.Playing) _player.Pause();
                    else _player.Play();
                    break;
            }
        }
    }
}
