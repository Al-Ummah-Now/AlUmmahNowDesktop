using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge
{
    public enum LiveStreamStatus
    {
        Scheduled,
        Live,
        Ended,
    }

    public enum LiveStreamKind
    {
        Live,
        Premiere,
    }

    public enum LiveEventType
    {
        None,
        Jummah,
        Taraweh,
        Eid,
        Halqa,
    }

    public enum LiveAuthorRole
    {
        Viewer,
        Moderator,
        Owner,
    }

    public enum LiveChatStatus
    {
        Published,
        Held,
        Removed,
    }

    public enum LiveModerationAction
    {
        Hold,
        Approve,
        Remove,
        Pin,
        Unpin,
    }

    public enum LiveReportReason
    {
        Spam,
        Abuse,
        Harassment,
        Misinformation,
        Copyright,
        Other,
    }

    public enum LiveChatValidationError
    {
        Empty,
        TooLong,
        LinksNotAllowed,
        BlockedContent,
    }

    public sealed class LiveStream
    {
        public string Id { get; set; } = "";
        public string ChannelId { get; set; } = "";
        public string ChannelName { get; set; } = "";
        public string? ChannelAvatar { get; set; }
        public LocalizedText Title { get; set; } = new LocalizedText("");
        public LocalizedText Description { get; set; } = new LocalizedText("");
        public string? ThumbnailUrl { get; set; }
        public LiveStreamStatus Status { get; set; } = LiveStreamStatus.Scheduled;
        public LiveStreamKind StreamKind { get; set; } = LiveStreamKind.Live;
        public LiveEventType EventType { get; set; } = LiveEventType.None;
        public string? PremiereVideoId { get; set; }
        public DateTimeOffset? ScheduledStartAt { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
        public long ViewerCount { get; set; }
        public long PeakViewerCount { get; set; }
        public bool ChatEnabled { get; set; } = true;
        public int SlowModeSeconds { get; set; }
        public bool DvrEnabled { get; set; } = true;
        public long DvrWindowSeconds { get; set; } = 7200;
        public string? PlaybackUrl { get; set; }
        public string? VodUrl { get; set; }
        public string? Category { get; set; }

        /// <summary>Ummah Kids (Epic 20) safety guard: kids content never exposes live chat.</summary>
        public bool IsKidsContent =>
            string.Equals(Category, "islamic_kids", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Category, "kids", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class LiveStreamState
    {
        public string Id { get; set; } = "";
        public LiveStreamStatus Status { get; set; } = LiveStreamStatus.Scheduled;
        public LiveStreamKind StreamKind { get; set; } = LiveStreamKind.Live;
        public LiveEventType EventType { get; set; } = LiveEventType.None;
        public long ViewerCount { get; set; }
        public long PeakViewerCount { get; set; }
        public DateTimeOffset? ScheduledStartAt { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
        public string? PlaybackUrl { get; set; }
        public string? VodUrl { get; set; }
        public bool DvrEnabled { get; set; } = true;
        public long DvrWindowSeconds { get; set; } = 7200;
        public bool ChatEnabled { get; set; } = true;
        public int SlowModeSeconds { get; set; }
    }

    public sealed class LiveChatMessage
    {
        public string Id { get; set; } = "";
        public string StreamId { get; set; } = "";
        public string UserId { get; set; } = "";
        public string ChannelId { get; set; } = "";
        public string AuthorName { get; set; } = "";
        public string? AuthorHandle { get; set; }
        public string? AuthorAvatar { get; set; }
        public LiveAuthorRole AuthorRole { get; set; } = LiveAuthorRole.Viewer;
        public string Text { get; set; } = "";
        public long OffsetMs { get; set; }
        public LiveChatStatus Status { get; set; } = LiveChatStatus.Published;
        public bool IsPinned { get; set; }
        public string? ModerationCategory { get; set; }
        public string CreatedAt { get; set; } = "";
    }

    public sealed class LiveChatSettings
    {
        public bool ChatEnabled { get; set; } = true;
        public int SlowModeSeconds { get; set; }
        public bool LinksAllowed { get; set; }
        public bool IsReadOnly { get; set; }
        public bool FeatureEnabled { get; set; } = true;
    }

    public sealed class LiveChatPage
    {
        public LiveChatPage(
            IReadOnlyList<LiveChatMessage> items,
            LiveChatMessage? pinnedMessage = null,
            string? nextAfterId = null,
            long? nextOffsetMs = null,
            LiveChatSettings? settings = null)
        {
            Items = items;
            PinnedMessage = pinnedMessage;
            NextAfterId = nextAfterId;
            NextOffsetMs = nextOffsetMs;
            Settings = settings ?? new LiveChatSettings();
        }

        public IReadOnlyList<LiveChatMessage> Items { get; }
        public LiveChatMessage? PinnedMessage { get; }
        public string? NextAfterId { get; }
        public long? NextOffsetMs { get; }
        public LiveChatSettings Settings { get; }
    }

    public sealed class LiveReminderStatus
    {
        public LiveReminderStatus(bool reminded, long reminderCount)
        {
            Reminded = reminded;
            ReminderCount = reminderCount;
        }

        public bool Reminded { get; }
        public long ReminderCount { get; }
    }

    public static class LiveChatFastFilter
    {
        public const int MaxMessageLength = 300;

        private static readonly Regex LinkPattern = new Regex(@"\b(?:https?://|www\.)\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex[] BlockedPatterns =
        {
            new Regex(@"\b(?:kill\s+yourself|kys)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\b(?:free\s+crypto|whatsapp\s+\+?\d{7,})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        };

        public static bool ContainsLink(string text) => !string.IsNullOrEmpty(text) && LinkPattern.IsMatch(text);

        public static bool ContainsBlockedPattern(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var p in BlockedPatterns)
            {
                if (p.IsMatch(text)) return true;
            }

            return false;
        }

        public static LiveChatValidationError? Validate(string? text, bool linksAllowed = false)
        {
            var trimmed = (text ?? "").Trim();
            if (trimmed.Length == 0) return LiveChatValidationError.Empty;
            if (trimmed.Length > MaxMessageLength) return LiveChatValidationError.TooLong;
            if (!linksAllowed && ContainsLink(trimmed)) return LiveChatValidationError.LinksNotAllowed;
            if (ContainsBlockedPattern(trimmed)) return LiveChatValidationError.BlockedContent;
            return null;
        }
    }

    /// <summary>Live streaming, premiere, chat, moderation and reminder operations (Epic 17).</summary>
    public interface ILiveCatalogClient
    {
        Task<LiveStream> GetLiveStreamAsync(string id, CancellationToken cancellationToken = default);
        Task<LiveStreamState> GetLiveStreamStateAsync(string id, CancellationToken cancellationToken = default);
        Task<LiveStreamState> SendLiveHeartbeatAsync(string id, string viewerId, CancellationToken cancellationToken = default);
        Task<LiveChatPage> GetLiveChatMessagesAsync(string streamId, string? afterId = null, long? maxOffsetMs = null, int limit = 50, CancellationToken cancellationToken = default);
        Task<LiveChatMessage> PostLiveChatMessageAsync(string streamId, string text, long? offsetMs = null, CancellationToken cancellationToken = default);
        Task<LiveChatMessage> ModerateLiveChatMessageAsync(string streamId, string messageId, LiveModerationAction action, CancellationToken cancellationToken = default);
        Task ReportLiveChatMessageAsync(string streamId, string messageId, LiveReportReason reason, string? details = null, CancellationToken cancellationToken = default);
        Task<LiveReminderStatus> GetLiveReminderStatusAsync(string streamId, CancellationToken cancellationToken = default);
        Task<LiveReminderStatus> SetLiveReminderAsync(string streamId, bool remind, CancellationToken cancellationToken = default);
    }
}
