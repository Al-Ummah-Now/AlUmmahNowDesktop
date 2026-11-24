using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge
{
    public enum LiveStatus { Scheduled, Live, Ended, Cancelled }

    public enum LiveKind { Live, Premiere }

    /// <summary>The four fixed event tags (shared-types <c>LIVE_EVENT_TYPES</c>).</summary>
    public enum LiveEventType { Jumuah, Taraweeh, Conference, Halqa }

    public enum LiveChatRole { Owner, Moderator }

    public sealed class LiveStream
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public LiveStatus Status { get; set; }
        public LiveKind Kind { get; set; }

        /// <summary>null while event tags are off.</summary>
        public LiveEventType? EventType { get; set; }
        public string ChannelName { get; set; } = "";
        public string? ThumbnailUrl { get; set; }
        public DateTimeOffset? ScheduledStart { get; set; }
        public long? Viewers { get; set; }
    }

    /// <summary><c>GET /api/live-streams/:id/state</c>. What an older server leaves out is off.</summary>
    public sealed class LiveState
    {
        public LiveStatus Status { get; set; }
        public string? SessionStatus { get; set; }
        public LiveKind Kind { get; set; }
        public string? PlaybackUrl { get; set; }
        public string? VodUrl { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? ScheduledStart { get; set; }

        /// <summary>The video a premiere shows, once it is on or over.</summary>
        public string? PremiereVideoId { get; set; }
        public double? DurationSeconds { get; set; }
        public LiveEventType? EventType { get; set; }
        public long? Viewers { get; set; }
        public bool ChatEnabled { get; set; }
        public bool ChatReplay { get; set; }

        /// <summary>A reminder can be set: reminders are on, and the stream is public and has not started.</summary>
        public bool ReminderEnabled { get; set; }
        public bool ReminderSet { get; set; }

        /// <summary>How far the server's clock is ahead of this computer's.</summary>
        public TimeSpan ClockOffset { get; set; }

        /// <summary>When the server wants this read again; null when it does not say.</summary>
        public TimeSpan? PollAfter { get; set; }
    }

    public sealed class LiveChatMessage
    {
        public string Id { get; set; } = "";
        public string AuthorName { get; set; } = "";
        public string Text { get; set; } = "";

        /// <summary>Milliseconds from the stream's start.</summary>
        public long OffsetMs { get; set; }
        public LiveChatRole? Role { get; set; }
    }

    public sealed class LiveChatPage
    {
        public LiveChatPage(IReadOnlyList<LiveChatMessage> messages, LiveChatMessage? pinned = null, bool truncated = false, TimeSpan? pollAfter = null)
        {
            PollAfter = pollAfter;
            Messages = messages;
            Pinned = pinned;
            Truncated = truncated;
        }

        public IReadOnlyList<LiveChatMessage> Messages { get; }
        public LiveChatMessage? Pinned { get; }

        /// <summary>A replay window holds more than one answer.</summary>
        public bool Truncated { get; }

        /// <summary>When the server wants the chat read again; null when it does not say.</summary>
        public TimeSpan? PollAfter { get; }
    }

    /// <summary>What viewers are offered; everything off when it cannot be read.</summary>
    public sealed class LiveFeatures
    {
        public bool Premieres { get; set; }
        public bool Reminders { get; set; }
        public bool Events { get; set; }

        /// <summary>The coming list is Epic 17's: premieres, reminders and event tags.</summary>
        public bool HasComing => Premieres || Reminders || Events;
    }

    /// <summary>Live streams and premieres for viewers (wiki E17): what is on, what is coming, and the chat beside it.</summary>
    /// <remarks>
    /// The chat is for reading: an account's first message and every report need a web check the apps cannot answer
    /// (AlUmmahNow#1912).
    /// </remarks>
    public interface ILiveClient
    {
        Task<LiveFeatures> GetFeaturesAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<LiveStream>> ListAsync(LiveStatus status, LiveEventType? eventType = null, CancellationToken cancellationToken = default);
        Task<LiveStream> GetStreamAsync(string id, CancellationToken cancellationToken = default);
        Task<LiveState> GetStateAsync(string id, CancellationToken cancellationToken = default);

        /// <summary>The newest messages.</summary>
        Task<LiveChatPage> GetChatAsync(string id, CancellationToken cancellationToken = default);

        /// <summary>The messages of a recording between two moments, at most 60 seconds apart.</summary>
        Task<LiveChatPage> GetChatReplayAsync(string id, long fromMs, long toMs, CancellationToken cancellationToken = default);

        /// <summary>Counts this window among those watching, and answers how many there are; null when the server does not know.</summary>
        Task<long?> BeatAsync(string id, string viewerId, bool leaving, CancellationToken cancellationToken = default);

        /// <exception cref="AuthRequiredException">Not signed in.</exception>
        Task SetReminderAsync(string id, bool on, CancellationToken cancellationToken = default);
    }
}
