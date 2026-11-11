using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge.Studio
{
    /// <summary>Maps <see cref="IStudioApi"/> onto api-videos operations and their JSON.</summary>
    public sealed class StudioApiClient : IStudioApi
    {
        /// <summary>The server's limit for one bulk call.</summary>
        public const int BulkLimit = 50;

        private readonly IApiTransport _transport;

        public StudioApiClient(IApiTransport transport) => _transport = transport;

        public async Task<IReadOnlyList<StudioChannel>> ListMyChannelsAsync(CancellationToken cancellationToken = default)
        {
            using var doc = await SendAsync(new ApiRequest("get_ChannelAccessList"), cancellationToken).ConfigureAwait(false);
            var list = new List<StudioChannel>();
            foreach (var c in Array(doc.RootElement, "data"))
            {
                var id = Str(c, "id");
                if (string.IsNullOrEmpty(id)) continue;
                var caps = Array(c, "capabilities").Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "").Where(x => x.Length > 0).ToList();
                list.Add(new StudioChannel(id!, Str(c, "name") ?? id!, Str(c, "handle"), Str(c, "avatarUrl"), Bool(c, "isMain"), Role(Str(c, "role")), caps, Bool(c, "isSuspended")));
            }

            return list;
        }

        public async Task<StudioVideoPage> ListVideosAsync(StudioVideoQuery query, CancellationToken cancellationToken = default)
        {
            var r = new ApiRequest("get_VideosList");
            r.Query["channelId"] = query.ChannelId;
            r.Headers["channelId"] = query.ChannelId;
            r.Query["limit"] = query.Limit.ToString(CultureInfo.InvariantCulture);
            r.Query["sort"] = "date";
            r.Query["filter"] = query.Visibility == StudioVisibilityFilter.Public ? "public" : query.Visibility == StudioVisibilityFilter.Private ? "private" : "all";
            if (!string.IsNullOrWhiteSpace(query.Text)) r.Query["q"] = query.Text!.Trim();
            if (!string.IsNullOrEmpty(query.Cursor)) r.Query["cursor"] = query.Cursor!;
            using var doc = await SendAsync(r, cancellationToken).ConfigureAwait(false);
            var items = Array(doc.RootElement, "data").Select(ReadVideo).Where(v => v.ChannelId.Length == 0 || v.ChannelId == query.ChannelId).ToList();
            var cursor = Str(doc.RootElement, "cursor");
            return new StudioVideoPage(items, string.IsNullOrEmpty(cursor) ? null : cursor);
        }

        public async Task<StudioVideo> GetVideoAsync(string channelId, string videoId, CancellationToken cancellationToken = default)
        {
            var r = new ApiRequest("get_VideoFetch");
            r.PathParams["id"] = videoId;
            r.Headers["channelId"] = channelId;
            using var doc = await SendAsync(r, cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object) root = result;
            return ReadVideo(root);
        }

        public async Task UpdateVideoAsync(string channelId, string videoId, VideoChanges changes, CancellationToken cancellationToken = default)
        {
            if (changes.IsEmpty) return;
            var body = new Dictionary<string, object?>();
            if (changes.Title != null) body["title"] = changes.Title;
            if (changes.Description != null) body["description"] = changes.Description;
            if (changes.IsPublic != null) body["isPublic"] = changes.IsPublic.Value;
            if (changes.Category != null) body["category"] = changes.Category.Length == 0 ? null : changes.Category;
            if (changes.Language != null) body["language"] = changes.Language;
            if (changes.Tags != null) body["tags"] = Tags(changes.Tags);
            var r = new ApiRequest("put_VideoUpdate") { BodyJson = JsonSerializer.Serialize(body) };
            r.PathParams["id"] = videoId;
            r.Headers["channelId"] = channelId;
            (await SendAsync(r, cancellationToken).ConfigureAwait(false)).Dispose();
        }

        public async Task BulkUpdateAsync(string channelId, IReadOnlyList<string> videoIds, BulkVideoChanges changes, CancellationToken cancellationToken = default)
        {
            if (changes.IsEmpty || videoIds.Count == 0) return;
            var server = new Dictionary<string, object?>();
            if (changes.IsPublic != null) server["isPublic"] = changes.IsPublic.Value;
            if (changes.Category != null) server["category"] = changes.Category.Length == 0 ? null : changes.Category;
            if (changes.AddTags.Count > 0) server["addTags"] = Tags(changes.AddTags);
            if (changes.RemoveTags.Count > 0) server["removeTags"] = Tags(changes.RemoveTags);
            if (server.Count > 0)
            {
                for (var i = 0; i < videoIds.Count; i += BulkLimit)
                {
                    var batch = videoIds.Skip(i).Take(BulkLimit).ToList();
                    var r = new ApiRequest("post_VideoBulkUpdate")
                    {
                        BodyJson = JsonSerializer.Serialize(new Dictionary<string, object?> { ["videoIds"] = batch, ["changes"] = server }),
                        IdempotencyKey = Guid.NewGuid().ToString("N"),
                    };
                    r.Headers["channelId"] = channelId;
                    (await SendAsync(r, cancellationToken).ConfigureAwait(false)).Dispose();
                }
            }

            // The bulk route has no language: set it per video.
            if (changes.Language != null)
            {
                foreach (var id in videoIds)
                {
                    await UpdateVideoAsync(channelId, id, new VideoChanges { Language = changes.Language }, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public async Task DeleteVideoAsync(string channelId, string videoId, CancellationToken cancellationToken = default)
        {
            var r = new ApiRequest("delete_VideoDelete");
            r.PathParams["id"] = videoId;
            r.Headers["channelId"] = channelId;
            (await SendAsync(r, cancellationToken).ConfigureAwait(false)).Dispose();
        }

        public async Task ScheduleAsync(string channelId, string videoId, DateTimeOffset at, string timeZone, CancellationToken cancellationToken = default)
        {
            var r = new ApiRequest("post_VideoScheduleCreate")
            {
                BodyJson = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["scheduledPublishDate"] = at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    ["timezone"] = timeZone,
                }),
                IdempotencyKey = Guid.NewGuid().ToString("N"),
            };
            r.PathParams["id"] = videoId;
            r.Headers["channelId"] = channelId;
            (await SendAsync(r, cancellationToken).ConfigureAwait(false)).Dispose();
        }

        public async Task<IReadOnlyList<StudioPlaylist>> ListPlaylistsAsync(string channelId, CancellationToken cancellationToken = default)
        {
            var r = new ApiRequest("get_PlaylistsList");
            r.Query["channelId"] = channelId;
            r.Query["limit"] = "100";
            r.Headers["channelId"] = channelId;
            using var doc = await SendAsync(r, cancellationToken).ConfigureAwait(false);
            return Array(doc.RootElement, "data").Select(ReadPlaylist).Where(p => p.Id.Length > 0).ToList();
        }

        public async Task<StudioPlaylist> CreatePlaylistAsync(string channelId, string title, bool isPublic, CancellationToken cancellationToken = default)
        {
            var r = new ApiRequest("post_PlaylistCreate")
            {
                BodyJson = JsonSerializer.Serialize(new Dictionary<string, object> { ["title"] = title, ["isPublic"] = isPublic }),
                IdempotencyKey = Guid.NewGuid().ToString("N"),
            };
            r.Headers["channelId"] = channelId;
            using var doc = await SendAsync(r, cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.TryGetProperty("result", out var result) && result.TryGetProperty("playlist", out var p)) return ReadPlaylist(p);
            throw new StudioApiException("The new playlist was not returned");
        }

        public async Task AddToPlaylistAsync(string channelId, string playlistId, IReadOnlyList<string> videoIds, CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < videoIds.Count; i += BulkLimit)
            {
                var r = new ApiRequest("post_PlaylistVideosBulkAdd")
                {
                    BodyJson = JsonSerializer.Serialize(new Dictionary<string, object> { ["videoIds"] = videoIds.Skip(i).Take(BulkLimit).ToList() }),
                    IdempotencyKey = Guid.NewGuid().ToString("N"),
                };
                r.PathParams["id"] = playlistId;
                r.Headers["channelId"] = channelId;
                (await SendAsync(r, cancellationToken).ConfigureAwait(false)).Dispose();
            }
        }

        public async Task<ChannelAnalytics> ChannelAnalyticsAsync(string channelId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
        {
            var r = new ApiRequest("get_AnalyticsChannelGet");
            r.PathParams["channelId"] = channelId;
            r.Query["dateFrom"] = Iso(from);
            r.Query["dateTo"] = Iso(to);
            using var doc = await SendAsync(r, cancellationToken).ConfigureAwait(false);
            var root = Unwrap(doc.RootElement);
            var daily = Array(root, "recentMetrics")
                .Select(m => (Date: Date(Str(m, "date")), Views: Long(m, "views"), Watch: Num(m, "watchTime") / 60.0))
                .Where(m => m.Date != null)
                .OrderBy(m => m.Date)
                .Select(m => new AnalyticsDay(m.Date!.Value, m.Views, m.Watch))
                .ToList();
            var top = Array(root, "topPerformingVideos").Select(v => new TopVideo(Str(v, "videoId") ?? "", Str(v, "title") ?? "", Long(v, "views"))).Where(v => v.Id.Length > 0).ToList();
            return new ChannelAnalytics
            {
                Totals = new AnalyticsTotals
                {
                    Views = Long(root, "totalViews"),
                    Likes = Long(root, "totalLikes"),
                    Comments = Long(root, "totalComments"),
                    Subscribers = Long(root, "totalSubscribers"),
                    WatchTimeMinutes = daily.Sum(d => d.WatchTimeMinutes),
                },
                Daily = daily,
                TopVideos = top,
            };
        }

        public async Task<AnalyticsTotals> VideoAnalyticsAsync(string videoId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
        {
            var r = new ApiRequest("get_AnalyticsVideoGet");
            r.PathParams["videoId"] = videoId;
            r.Query["dateFrom"] = Iso(from);
            r.Query["dateTo"] = Iso(to);
            using var doc = await SendAsync(r, cancellationToken).ConfigureAwait(false);
            var root = Unwrap(doc.RootElement);
            return new AnalyticsTotals
            {
                Views = Long(root, "views"),
                Likes = Long(root, "likes"),
                Comments = Long(root, "comments"),
                WatchTimeMinutes = Num(root, "watchTime") / 60.0,
                AverageViewSeconds = Num(root, "avgViewDuration"),
            };
        }

        public Task<IReadOnlyList<Suggestion>> SuggestAsync(string text, string? channelId, CancellationToken cancellationToken = default) =>
            _transport.SuggestAsync(text, channelId, 8, cancellationToken);

        internal static StudioVideo ReadVideo(JsonElement v)
        {
            var localized = new Dictionary<string, string>();
            if (v.TryGetProperty("titleLocalized", out var tl) && tl.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in tl.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String) localized[p.Name] = p.Value.GetString() ?? "";
                }
            }

            return new StudioVideo
            {
                Id = Str(v, "id") ?? "",
                Title = Str(v, "title") ?? "",
                Description = Str(v, "description") ?? "",
                TitleLocalized = localized,
                ThumbnailUrl = Str(v, "thumbnailUrl"),
                ChannelId = Str(v, "channelId") ?? "",
                IsPublic = Bool(v, "isPublic"),
                Category = Str(v, "category"),
                Language = Str(v, "language"),
                Tags = Array(v, "tags").Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString() ?? "").ToList(),
                Status = Status(Str(v, "uploadStatus")),
                DurationSeconds = v.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : (double?)null,
                Views = Long(v, "views"),
                Likes = Long(v, "likesCount"),
                Comments = Long(v, "commentsCount"),
                CreatedAt = DateTimeOffsetOrNull(Str(v, "dateCreated")),
                ScheduledFor = DateTimeOffsetOrNull(Str(v, "scheduledPublishDate")),
                IsShort = Bool(v, "isShort"),
            };
        }

        private async Task<JsonDocument> SendAsync(ApiRequest request, CancellationToken cancellationToken)
        {
            var body = await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
            try
            {
                return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            }
            catch (JsonException ex)
            {
                throw new StudioApiException(request.OperationId + " returned invalid JSON", null, ex);
            }
        }

        private static StudioPlaylist ReadPlaylist(JsonElement p)
        {
            var count = p.TryGetProperty("videosCount", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : Array(p, "videos").Count();
            return new StudioPlaylist(Str(p, "id") ?? "", Str(p, "title") ?? Str(p, "name") ?? "", count, Bool(p, "isPublic"));
        }

        private static List<string> Tags(IEnumerable<string> tags) =>
            tags.Select(t => t.Trim()).Where(t => t.Length > 0).Select(t => t.Length > 50 ? t.Substring(0, 50) : t).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();

        private static JsonElement Unwrap(JsonElement root) =>
            root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object ? r
            : root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d
            : root;

        private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        private static ChannelRole Role(string? role) => role switch
        {
            "admin" => ChannelRole.Admin,
            "editor" => ChannelRole.Editor,
            "moderator" => ChannelRole.Moderator,
            _ => ChannelRole.Owner,
        };

        private static StudioVideoStatus Status(string? s) => s switch
        {
            "initiated" => StudioVideoStatus.Initiated,
            "uploading" => StudioVideoStatus.Uploading,
            "processing" => StudioVideoStatus.Processing,
            "ready" => StudioVideoStatus.Ready,
            "failed" => StudioVideoStatus.Failed,
            _ => StudioVideoStatus.Unknown,
        };

        private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : Enumerable.Empty<JsonElement>();

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool Bool(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        private static double Num(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

        private static long Long(JsonElement e, string name) => (long)Math.Round(Num(e, name));

        private static DateTimeOffset? DateTimeOffsetOrNull(string? s) =>
            DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : (DateTimeOffset?)null;

        private static DateTime? Date(string? s) => DateTimeOffsetOrNull(s)?.UtcDateTime.Date;
    }
}
