using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge.Temporary
{
    /// <summary>The live endpoints of api-videos, until the Rust core has them.</summary>
    public sealed class HttpLiveClient : ILiveClient
    {
        private const int ListLimit = 50;

        private readonly HttpClient _http;
        private readonly Uri _baseUri;
        private readonly Func<CancellationToken, Task<string?>> _accessToken;
        private readonly Func<DateTimeOffset> _now;

        /// <param name="accessToken">The signed-in person's token, or null when nobody is.</param>
        public HttpLiveClient(HttpClient http, Uri baseUri, Func<CancellationToken, Task<string?>> accessToken, Func<DateTimeOffset>? now = null)
        {
            _http = http;
            _baseUri = baseUri;
            _accessToken = accessToken;
            _now = now ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>A token source for <paramref name="session"/> that answers null when nobody is signed in.</summary>
        public static Func<CancellationToken, Task<string?>> TokenOf(IAuthSession session) => async ct =>
        {
            if (session.Account == null) return null;
            try
            {
                return await session.GetAccessTokenAsync(ct).ConfigureAwait(false);
            }
            catch (AuthRequiredException)
            {
                return null;
            }
        };

        public async Task<LiveFeatures> GetFeaturesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var doc = await SendAsync(HttpMethod.Get, "/api/me/features", null, false, cancellationToken).ConfigureAwait(false);
                if (!doc.RootElement.TryGetProperty("features", out var flags)) return new LiveFeatures();
                return new LiveFeatures
                {
                    Premieres = Flag(flags, "livePremieres"),
                    Reminders = Flag(flags, "liveReminders"),
                    Events = Flag(flags, "liveEvents"),
                };
            }
            catch (CatalogException)
            {
                return new LiveFeatures();
            }
        }

        public async Task<IReadOnlyList<LiveStream>> ListAsync(LiveStatus status, LiveEventType? eventType = null, CancellationToken cancellationToken = default)
        {
            var path = "/api/live-streams?status=" + Wire(status) + "&limit=" + ListLimit.ToString(CultureInfo.InvariantCulture);
            if (eventType is { } e) path += "&eventType=" + Wire(e);
            using var doc = await SendAsync(HttpMethod.Get, path, null, false, cancellationToken).ConfigureAwait(false);
            var result = Result(doc);
            if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return Array.Empty<LiveStream>();
            // One stream this app cannot read (a newer status) never takes the list with it
            return data.EnumerateArray().Select(ParseStream).Where(s => s != null).Select(s => s!).ToList();
        }

        public async Task<LiveStream> GetStreamAsync(string id, CancellationToken cancellationToken = default)
        {
            using var doc = await SendAsync(HttpMethod.Get, Path(id), null, true, cancellationToken).ConfigureAwait(false);
            return (Result(doc).TryGetProperty("stream", out var stream) ? ParseStream(stream) : null) ?? throw new CatalogException("Unreadable live stream");
        }

        public async Task<LiveState> GetStateAsync(string id, CancellationToken cancellationToken = default)
        {
            var token = await _accessToken(cancellationToken).ConfigureAwait(false);
            var sentAt = _now();
            using var doc = await SendAsync(HttpMethod.Get, Path(id) + "/state", null, token, cancellationToken).ConfigureAwait(false);
            var receivedAt = _now();
            var r = Result(doc);
            var offset = TimeSpan.Zero;
            if (Num(r, "serverNow") is { } serverNow)
            {
                // The answer's serverNow belongs to the middle of the request
                offset = TimeSpan.FromMilliseconds(serverNow - ((sentAt.ToUnixTimeMilliseconds() + receivedAt.ToUnixTimeMilliseconds()) / 2.0));
            }

            r.TryGetProperty("chat", out var chat);
            r.TryGetProperty("reminder", out var reminder);
            return new LiveState
            {
                Status = Parse<LiveStatus>(Str(r, "status")) ?? throw new CatalogException("Unreadable live state"),
                SessionStatus = Str(r, "sessionStatus"),
                Kind = Parse<LiveKind>(Str(r, "kind")) ?? LiveKind.Live,
                PlaybackUrl = Str(r, "playbackUrl"),
                VodUrl = Str(r, "vodUrl"),
                StartedAt = Date(r, "startedAt"),
                ScheduledStart = Date(r, "scheduledStartTime"),
                PremiereVideoId = Str(r, "premiereVideoId"),
                DurationSeconds = Num(r, "durationSeconds"),
                EventType = Parse<LiveEventType>(Str(r, "eventType")),
                Viewers = (long?)Num(r, "viewers"),
                ChatEnabled = Flag(chat, "enabled"),
                ChatReplay = Flag(chat, "replay"),
                ReminderEnabled = Flag(reminder, "enabled"),
                ReminderSet = Flag(reminder, "set"),
                ClockOffset = offset,
                PollAfter = PollAfter(r),
            };
        }

        public Task<LiveChatPage> GetChatAsync(string id, CancellationToken cancellationToken = default) => ChatAsync(Path(id) + "/chat", cancellationToken);

        public Task<LiveChatPage> GetChatReplayAsync(string id, long fromMs, long toMs, CancellationToken cancellationToken = default) =>
            ChatAsync(Path(id) + "/chat?fromMs=" + fromMs.ToString(CultureInfo.InvariantCulture) + "&toMs=" + toMs.ToString(CultureInfo.InvariantCulture), cancellationToken);

        public async Task<long?> BeatAsync(string id, string viewerId, bool leaving, CancellationToken cancellationToken = default)
        {
            var body = JsonSerializer.Serialize(new { viewerId, leaving });
            using var doc = await SendAsync(HttpMethod.Post, Path(id) + "/viewers", body, (string?)null, cancellationToken, "text/plain").ConfigureAwait(false);
            return (long?)Num(Result(doc), "viewers");
        }

        public async Task SetReminderAsync(string id, bool on, CancellationToken cancellationToken = default)
        {
            var token = await _accessToken(cancellationToken).ConfigureAwait(false) ?? throw new AuthRequiredException("Sign in to be reminded");
            using var doc = await SendAsync(on ? HttpMethod.Post : HttpMethod.Delete, Path(id) + "/reminders", null, token, cancellationToken).ConfigureAwait(false);
        }

        private async Task<LiveChatPage> ChatAsync(string path, CancellationToken cancellationToken)
        {
            using var doc = await SendAsync(HttpMethod.Get, path, null, false, cancellationToken).ConfigureAwait(false);
            var r = Result(doc);
            var messages = r.TryGetProperty("messages", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(ParseMessage).ToList()
                : new List<LiveChatMessage>();
            var pinned = r.TryGetProperty("pinned", out var p) && p.ValueKind == JsonValueKind.Object ? ParseMessage(p) : null;
            return new LiveChatPage(messages, pinned, Flag(r, "truncated"), PollAfter(r));
        }

        private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string? body, bool signed, CancellationToken cancellationToken)
        {
            var token = signed ? await _accessToken(cancellationToken).ConfigureAwait(false) : null;
            return await SendAsync(method, path, body, token, cancellationToken).ConfigureAwait(false);
        }

        private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string? body, string? token, CancellationToken cancellationToken, string contentType = "application/json")
        {
            using var request = new HttpRequestMessage(method, SharedHttpHelper.ResolveSameOrigin(_baseUri, path));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null) request.Content = new StringContent(body, Encoding.UTF8, contentType);
            try
            {
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized && token != null) throw new AuthRequiredException("Unauthorized");
                if (!response.IsSuccessStatusCode) throw new CatalogException("HTTP " + (int)response.StatusCode + " for " + request.RequestUri!.AbsolutePath, retryAfter: RetryAfter(response));
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            }
            catch (HttpRequestException ex)
            {
                throw new CatalogException("Network error", ex);
            }
            catch (JsonException ex)
            {
                throw new CatalogException("Unreadable answer", ex);
            }
            catch (InvalidOperationException ex)
            {
                // An answer in a charset this computer does not know
                throw new CatalogException("Unreadable answer", ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new CatalogException("Timed out", ex);
            }
        }

        /// <summary>Seconds, or a date: from the answer's own Date, so a computer's clock that is wrong does not matter.</summary>
        private TimeSpan? RetryAfter(HttpResponseMessage response)
        {
            if ((int)response.StatusCode != 429 && response.StatusCode != HttpStatusCode.ServiceUnavailable) return null;
            var said = response.Headers.RetryAfter;
            if (said?.Date is not { } at) return said?.Delta;
            var wait = at - (response.Headers.Date ?? _now());
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        /// <summary>A whole answer is never lost to a bad value here: what is not a number is as if it were absent.</summary>
        private static TimeSpan? PollAfter(JsonElement e) =>
            Num(e, "pollAfterMs") is { } ms && ms > 0 && !double.IsInfinity(ms) ? TimeSpan.FromMilliseconds(Math.Min(ms, 86_400_000)) : (TimeSpan?)null;

        private static string Path(string id) => "/api/live-streams/" + SharedHttpHelper.SafePathSegment(id, nameof(id));

        private static JsonElement Result(JsonDocument doc) =>
            doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object
                ? r
                : throw new CatalogException("Unreadable answer");

        private static LiveStream? ParseStream(JsonElement e)
        {
            if (Str(e, "id") is not { } id || Parse<LiveStatus>(Str(e, "status")) is not { } status) return null;
            var thumbnail = Str(e, "thumbnailUrl");
            return new LiveStream
            {
                Id = id,
                Title = Str(e, "title") ?? "",
                Description = Str(e, "description") ?? "",
                Status = status,
                Kind = Parse<LiveKind>(Str(e, "kind")) ?? LiveKind.Live,
                EventType = Parse<LiveEventType>(Str(e, "eventType")),
                ChannelName = Str(e, "channelName") ?? "",
                ThumbnailUrl = string.IsNullOrWhiteSpace(thumbnail) ? null : thumbnail,
                ScheduledStart = Date(e, "scheduledStartTime"),
                Viewers = (long?)Num(e, "currentViewers"),
            };
        }

        private static LiveChatMessage ParseMessage(JsonElement e) => new LiveChatMessage
        {
            Id = Str(e, "id") ?? "",
            AuthorName = Str(e, "authorName") ?? "",
            Text = Str(e, "text") ?? "",
            OffsetMs = (long)(Num(e, "offsetMs") ?? 0),
            Role = Parse<LiveChatRole>(Str(e, "authorRole")),
        };

        private static string Wire<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

        /// <summary>The server's lower-case value, or null when this app does not know it.</summary>
        private static T? Parse<T>(string? value) where T : struct, Enum
        {
            foreach (T known in Enum.GetValues(typeof(T)))
            {
                if (Wire(known) == value) return known;
            }

            return null;
        }

        private static bool Flag(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static double? Num(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : (double?)null;

        private static DateTimeOffset? Date(JsonElement e, string name) =>
            DateTimeOffset.TryParse(Str(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTimeOffset?)null;
    }
}
