using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge.Temporary
{
    /// <summary>
    /// TEMPORARY stand-in for aun-api: reads the public REST API directly.
    /// Replace with the uniffi-bindgen-cs binding when AlUmmahNowCore is released.
    /// </summary>
    public sealed class HttpCatalogClient : ICatalogClient, ILiveCatalogClient
    {
        private readonly HttpClient _http;
        private readonly Uri _baseUri;

        public HttpCatalogClient(HttpClient http, Uri baseUri)
        {
            _http = http;
            _baseUri = baseUri;
        }

        public async Task<VideoPage> ListVideosAsync(VideoQuery query, CancellationToken cancellationToken = default)
        {
            var url = "/api/videos?limit=" + query.Limit.ToString(CultureInfo.InvariantCulture)
                + "&sort=" + query.Sort.ToString().ToLowerInvariant();
            if (!string.IsNullOrEmpty(query.Category)) url += "&category=" + Uri.EscapeDataString(query.Category);
            if (!string.IsNullOrEmpty(query.Cursor)) url += "&cursor=" + Uri.EscapeDataString(query.Cursor);

            using var doc = JsonDocument.Parse(await GetTextAsync(new Uri(_baseUri, url).ToString(), cancellationToken).ConfigureAwait(false));
            var items = new List<Video>();
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in data.EnumerateArray()) items.Add(ParseVideo(element));
            }

            return new VideoPage(items, Str(doc.RootElement, "cursor"));
        }

        public async Task<Video> GetVideoAsync(string id, CancellationToken cancellationToken = default)
        {
            var url = new Uri(_baseUri, "/api/videos/" + Uri.EscapeDataString(id)).ToString();
            using var doc = JsonDocument.Parse(await GetTextAsync(url, cancellationToken).ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) root = data;
            return ParseVideo(root);
        }

        public async Task<IReadOnlyList<SubtitleTrack>> GetSubtitleTracksAsync(string videoId, CancellationToken cancellationToken = default)
        {
            var url = new Uri(_baseUri, "/api/videos/" + Uri.EscapeDataString(videoId) + "/subtitles").ToString();
            using var doc = JsonDocument.Parse(await GetTextAsync(url, cancellationToken).ConfigureAwait(false));
            var result = new List<SubtitleTrack>();
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("tracks", out var tracks)
                && tracks.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tracks.EnumerateArray())
                {
                    if (Str(t, "status") != "ready" || Str(t, "url") is not { } trackUrl) continue;
                    var language = Str(t, "language") ?? "";
                    var isDefault = t.TryGetProperty("isDefault", out var d) && d.ValueKind == JsonValueKind.True;
                    result.Add(new SubtitleTrack(language, Str(t, "label") ?? language, trackUrl, isDefault));
                }
            }

            return result;
        }

        public async Task<string> GetTextAsync(string url, CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new CatalogException("HTTP " + (int)response.StatusCode + " for " + new Uri(url).AbsolutePath);
                }

                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new CatalogException("Network error", ex);
            }
        }

        public async Task<LiveStream> GetLiveStreamAsync(string id, CancellationToken cancellationToken = default)
        {
            var url = new Uri(_baseUri, "/api/live/streams/" + Uri.EscapeDataString(id)).ToString();
            using var doc = JsonDocument.Parse(await GetTextAsync(url, cancellationToken).ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) root = data;
            return ParseLiveStream(root);
        }

        public async Task<LiveStreamState> GetLiveStreamStateAsync(string id, CancellationToken cancellationToken = default)
        {
            var url = new Uri(_baseUri, "/api/live/streams/" + Uri.EscapeDataString(id) + "/state").ToString();
            using var doc = JsonDocument.Parse(await GetTextAsync(url, cancellationToken).ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) root = data;
            return ParseLiveStreamState(root, id);
        }

        public async Task<LiveStreamState> SendLiveHeartbeatAsync(string id, string viewerId, CancellationToken cancellationToken = default)
        {
            var url = new Uri(_baseUri, "/api/live/streams/" + Uri.EscapeDataString(id) + "/heartbeat").ToString();
            var payload = JsonSerializer.Serialize(new { viewerId });
            using var doc = JsonDocument.Parse(await SendJsonAsync(HttpMethod.Post, url, payload, cancellationToken).ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) root = data;
            return ParseLiveStreamState(root, id);
        }

        public async Task<LiveChatPage> GetLiveChatMessagesAsync(
            string streamId,
            string? afterId = null,
            long? maxOffsetMs = null,
            int limit = 50,
            CancellationToken cancellationToken = default)
        {
            var path = "/api/live/streams/" + Uri.EscapeDataString(streamId) + "/chat?limit=" + limit.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(afterId)) path += "&afterId=" + Uri.EscapeDataString(afterId);
            if (maxOffsetMs.HasValue) path += "&maxOffsetMs=" + maxOffsetMs.Value.ToString(CultureInfo.InvariantCulture);
            var url = new Uri(_baseUri, path).ToString();
            using var doc = JsonDocument.Parse(await GetTextAsync(url, cancellationToken).ConfigureAwait(false));
            return ParseLiveChatPage(doc.RootElement);
        }

        public async Task<LiveChatMessage> PostLiveChatMessageAsync(
            string streamId,
            string text,
            long? offsetMs = null,
            CancellationToken cancellationToken = default)
        {
            var url = new Uri(_baseUri, "/api/live/streams/" + Uri.EscapeDataString(streamId) + "/chat").ToString();
            var payload = JsonSerializer.Serialize(new { text, offsetMs });
            using var doc = JsonDocument.Parse(await SendJsonAsync(HttpMethod.Post, url, payload, cancellationToken).ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) root = data;
            return ParseLiveChatMessage(root);
        }

        public async Task<LiveChatMessage> ModerateLiveChatMessageAsync(
            string streamId,
            string messageId,
            LiveModerationAction action,
            CancellationToken cancellationToken = default)
        {
            var url = new Uri(
                _baseUri,
                "/api/live/streams/" + Uri.EscapeDataString(streamId) + "/chat/" + Uri.EscapeDataString(messageId) + "/moderate").ToString();
            var payload = JsonSerializer.Serialize(new { action = action.ToString().ToLowerInvariant() });
            using var doc = JsonDocument.Parse(await SendJsonAsync(HttpMethod.Post, url, payload, cancellationToken).ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) root = data;
            return ParseLiveChatMessage(root);
        }

        public async Task ReportLiveChatMessageAsync(
            string streamId,
            string messageId,
            LiveReportReason reason,
            string? details = null,
            CancellationToken cancellationToken = default)
        {
            var url = new Uri(
                _baseUri,
                "/api/live/streams/" + Uri.EscapeDataString(streamId) + "/chat/" + Uri.EscapeDataString(messageId) + "/report").ToString();
            var payload = JsonSerializer.Serialize(new { reason = reason.ToString().ToLowerInvariant(), details });
            await SendJsonAsync(HttpMethod.Post, url, payload, cancellationToken).ConfigureAwait(false);
        }

        public async Task<LiveReminderStatus> GetLiveReminderStatusAsync(string streamId, CancellationToken cancellationToken = default)
        {
            var url = new Uri(_baseUri, "/api/live/streams/" + Uri.EscapeDataString(streamId) + "/reminders").ToString();
            using var doc = JsonDocument.Parse(await GetTextAsync(url, cancellationToken).ConfigureAwait(false));
            return ParseLiveReminderStatus(doc.RootElement);
        }

        public async Task<LiveReminderStatus> SetLiveReminderAsync(string streamId, bool remind, CancellationToken cancellationToken = default)
        {
            var url = new Uri(_baseUri, "/api/live/streams/" + Uri.EscapeDataString(streamId) + "/reminders").ToString();
            var method = remind ? HttpMethod.Post : HttpMethod.Delete;
            var body = await SendJsonAsync(method, url, remind ? "{}" : null, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return ParseLiveReminderStatus(doc.RootElement);
        }

        private async Task<string> SendJsonAsync(HttpMethod method, string url, string? jsonPayload, CancellationToken cancellationToken)
        {
            try
            {
                using var request = new HttpRequestMessage(method, url);
                if (jsonPayload != null)
                {
                    request.Content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");
                }

                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new CatalogException("HTTP " + (int)response.StatusCode + " for " + new Uri(url).AbsolutePath);
                }

                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new CatalogException("Network error", ex);
            }
        }

        private static Video ParseVideo(JsonElement e)
        {
            var isPublic = e.TryGetProperty("isPublic", out var p) && p.ValueKind == JsonValueKind.True;
            var isLive = e.TryGetProperty("isLive", out var l) && l.ValueKind == JsonValueKind.True;
            return new Video
            {
                Id = Str(e, "id") ?? "",
                Title = Localized(e, "title", "titleLocalized"),
                Description = Localized(e, "description", "descriptionLocalized"),
                ChannelName = Str(e, "channelName") ?? "",
                ChannelAvatarUrl = Str(e, "channelAvatarUrl"),
                ThumbnailUrl = Str(e, "thumbnailUrl"),
                PlayUrl = Str(e, "playUrl"),
                DurationSeconds = Num(e, "duration"),
                Views = (long)(Num(e, "views") ?? 0),
                PublishedAt = Date(e, "publishedAt") ?? Date(e, "dateCreated"),
                Category = Str(e, "category"),
                IsLive = isLive,
                IsPublic = isPublic,
                UploadStatus = Str(e, "uploadStatus") ?? "",
                Chapters = ParseChapters(e),
            };
        }

        private static IReadOnlyList<Chapter> ParseChapters(JsonElement e)
        {
            var result = new List<Chapter>();
            if (!e.TryGetProperty("chapters", out var outer) || outer.ValueKind != JsonValueKind.Object) return result;
            if (!outer.TryGetProperty("chapters", out var list) || list.ValueKind != JsonValueKind.Array) return result;
            foreach (var c in list.EnumerateArray())
            {
                result.Add(new Chapter(Num(c, "startTime") ?? 0, Num(c, "endTime") ?? 0, Localized(c, "title", "titleLocalized")));
            }

            return result;
        }

        private static LocalizedText Localized(JsonElement e, string plain, string localized)
        {
            var translations = new Dictionary<string, string>();
            if (e.TryGetProperty(localized, out var map) && map.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in map.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.String) translations[entry.Name] = entry.Value.GetString() ?? "";
                }
            }

            return new LocalizedText(Str(e, plain) ?? "", translations);
        }

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static double? Num(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : (double?)null;

        private static DateTimeOffset? Date(JsonElement e, string name) =>
            DateTimeOffset.TryParse(Str(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTimeOffset?)null;

        private static bool Bool(JsonElement e, string name, bool fallback = false) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
                ? v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && fallback)
                : fallback;

        private static LiveStream ParseLiveStream(JsonElement e) => new LiveStream
        {
            Id = Str(e, "id") ?? "",
            ChannelId = Str(e, "channelId") ?? "",
            ChannelName = Str(e, "channelName") ?? "",
            ChannelAvatar = Str(e, "channelAvatar"),
            Title = Localized(e, "title", "titleLocalized"),
            Description = Localized(e, "description", "descriptionLocalized"),
            ThumbnailUrl = Str(e, "thumbnailUrl"),
            Status = ParseStatus(Str(e, "status")),
            StreamKind = string.Equals(Str(e, "streamKind"), "premiere", StringComparison.OrdinalIgnoreCase)
                ? LiveStreamKind.Premiere
                : LiveStreamKind.Live,
            EventType = ParseEventType(Str(e, "eventType")),
            PremiereVideoId = Str(e, "premiereVideoId"),
            ScheduledStartAt = Date(e, "scheduledStartAt"),
            StartedAt = Date(e, "startedAt"),
            EndedAt = Date(e, "endedAt"),
            ViewerCount = (long)(Num(e, "viewerCount") ?? 0),
            PeakViewerCount = (long)(Num(e, "peakViewerCount") ?? 0),
            ChatEnabled = Bool(e, "chatEnabled", true),
            SlowModeSeconds = (int)(Num(e, "slowModeSeconds") ?? 0),
            DvrEnabled = Bool(e, "dvrEnabled", true),
            DvrWindowSeconds = (long)(Num(e, "dvrWindowSeconds") ?? 7200),
            PlaybackUrl = Str(e, "playbackUrl"),
            VodUrl = Str(e, "vodUrl"),
            Category = Str(e, "category"),
        };

        private static LiveStreamState ParseLiveStreamState(JsonElement e, string fallbackId) => new LiveStreamState
        {
            Id = Str(e, "id") ?? fallbackId,
            Status = ParseStatus(Str(e, "status")),
            StreamKind = string.Equals(Str(e, "streamKind"), "premiere", StringComparison.OrdinalIgnoreCase)
                ? LiveStreamKind.Premiere
                : LiveStreamKind.Live,
            EventType = ParseEventType(Str(e, "eventType")),
            ViewerCount = (long)(Num(e, "viewerCount") ?? 0),
            PeakViewerCount = (long)(Num(e, "peakViewerCount") ?? 0),
            ScheduledStartAt = Date(e, "scheduledStartAt"),
            StartedAt = Date(e, "startedAt"),
            EndedAt = Date(e, "endedAt"),
            PlaybackUrl = Str(e, "playbackUrl"),
            VodUrl = Str(e, "vodUrl"),
            DvrEnabled = Bool(e, "dvrEnabled", true),
            DvrWindowSeconds = (long)(Num(e, "dvrWindowSeconds") ?? 7200),
            ChatEnabled = Bool(e, "chatEnabled", true),
            SlowModeSeconds = (int)(Num(e, "slowModeSeconds") ?? 0),
        };

        private static LiveChatMessage ParseLiveChatMessage(JsonElement e) => new LiveChatMessage
        {
            Id = Str(e, "id") ?? "",
            StreamId = Str(e, "streamId") ?? "",
            UserId = Str(e, "userId") ?? "",
            ChannelId = Str(e, "channelId") ?? "",
            AuthorName = Str(e, "authorName") ?? "",
            AuthorHandle = Str(e, "authorHandle"),
            AuthorAvatar = Str(e, "authorAvatar"),
            AuthorRole = Str(e, "authorRole") switch
            {
                "owner" => LiveAuthorRole.Owner,
                "moderator" => LiveAuthorRole.Moderator,
                _ => LiveAuthorRole.Viewer,
            },
            Text = Str(e, "text") ?? "",
            OffsetMs = (long)(Num(e, "offsetMs") ?? 0),
            Status = Str(e, "status") switch
            {
                "held" => LiveChatStatus.Held,
                "removed" => LiveChatStatus.Removed,
                _ => LiveChatStatus.Published,
            },
            IsPinned = Bool(e, "isPinned"),
            ModerationCategory = Str(e, "moderationCategory"),
            CreatedAt = Str(e, "createdAt") ?? "",
        };

        private static LiveChatPage ParseLiveChatPage(JsonElement root)
        {
            var items = new List<LiveChatMessage>();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in data.EnumerateArray()) items.Add(ParseLiveChatMessage(el));
            }

            LiveChatMessage? pinned = null;
            if (root.TryGetProperty("pinnedMessage", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                pinned = ParseLiveChatMessage(p);
            }

            var settings = new LiveChatSettings();
            if (root.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                settings.ChatEnabled = Bool(s, "chatEnabled", true);
                settings.SlowModeSeconds = (int)(Num(s, "slowModeSeconds") ?? 0);
                settings.LinksAllowed = Bool(s, "linksAllowed");
                settings.IsReadOnly = Bool(s, "isReadOnly");
                settings.FeatureEnabled = Bool(s, "featureEnabled", true);
            }

            var nextOffset = Num(root, "nextOffsetMs") is { } n ? (long)n : (long?)null;
            return new LiveChatPage(items, pinned, Str(root, "nextAfterId"), nextOffset, settings);
        }

        private static LiveReminderStatus ParseLiveReminderStatus(JsonElement root)
        {
            var target = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : root;
            return new LiveReminderStatus(Bool(target, "reminded"), (long)(Num(target, "reminderCount") ?? 0));
        }

        private static LiveStreamStatus ParseStatus(string? value) => value switch
        {
            "live" => LiveStreamStatus.Live,
            "ended" => LiveStreamStatus.Ended,
            _ => LiveStreamStatus.Scheduled,
        };

        private static LiveEventType ParseEventType(string? value) => value switch
        {
            "jummah" => LiveEventType.Jummah,
            "taraweh" => LiveEventType.Taraweh,
            "eid" => LiveEventType.Eid,
            "halqa" => LiveEventType.Halqa,
            _ => LiveEventType.None,
        };
    }
}
