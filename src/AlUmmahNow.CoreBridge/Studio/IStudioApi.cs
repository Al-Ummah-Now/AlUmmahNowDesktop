using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge.Studio
{
    /// <summary>
    /// The Studio's calls to the videos API, always as the active account and in the given channel.
    /// Backed by aun-core's ApiClient. Throws <see cref="AuthRequiredException"/> when the account
    /// must sign in again and <see cref="StudioApiException"/> for other failures.
    /// </summary>
    public interface IStudioApi
    {
        Task<IReadOnlyList<StudioChannel>> ListMyChannelsAsync(CancellationToken cancellationToken = default);

        Task<StudioVideoPage> ListVideosAsync(StudioVideoQuery query, CancellationToken cancellationToken = default);

        Task<StudioVideo> GetVideoAsync(string channelId, string videoId, CancellationToken cancellationToken = default);

        Task UpdateVideoAsync(string channelId, string videoId, VideoChanges changes, CancellationToken cancellationToken = default);

        /// <summary>Applies visibility, category and tag changes to up to 50 videos in one call.</summary>
        Task BulkUpdateAsync(string channelId, IReadOnlyList<string> videoIds, BulkVideoChanges changes, CancellationToken cancellationToken = default);

        /// <summary>Soft delete: the videos leave the channel at once and stay restorable for 30 days.</summary>
        Task<BulkOutcome> DeleteVideosAsync(string channelId, IReadOnlyList<string> videoIds, CancellationToken cancellationToken = default);

        /// <summary>The channel's Recently deleted list, newest first.</summary>
        Task<IReadOnlyList<DeletedVideo>> ListDeletedAsync(string channelId, CancellationToken cancellationToken = default);

        Task<BulkOutcome> RestoreVideosAsync(string channelId, IReadOnlyList<string> videoIds, CancellationToken cancellationToken = default);

        /// <summary>"Delete permanently": queues deleted videos for the hourly cleanup. Can't be undone.</summary>
        Task<BulkOutcome> PurgeVideosAsync(string channelId, IReadOnlyList<string> videoIds, CancellationToken cancellationToken = default);

        /// <summary>Schedules a private video to publish at <paramref name="at"/> (UTC on the wire).</summary>
        Task ScheduleAsync(string channelId, string videoId, DateTimeOffset at, string timeZone, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<StudioPlaylist>> ListPlaylistsAsync(string channelId, CancellationToken cancellationToken = default);

        Task<StudioPlaylist> CreatePlaylistAsync(string channelId, string title, bool isPublic, CancellationToken cancellationToken = default);

        Task AddToPlaylistAsync(string channelId, string playlistId, IReadOnlyList<string> videoIds, CancellationToken cancellationToken = default);

        Task<ChannelAnalytics> ChannelAnalyticsAsync(string channelId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

        Task<AnalyticsTotals> VideoAnalyticsAsync(string videoId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

        /// <summary>Live autocomplete for the channel's videos. One attempt; 429 throws <see cref="RateLimitedException"/>.</summary>
        Task<IReadOnlyList<Suggestion>> SuggestAsync(string text, string? channelId, CancellationToken cancellationToken = default);
    }
}
