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
    public sealed class HttpCatalogClient : ICatalogClient
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
    }
}
