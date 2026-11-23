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
using AlUmmahNow.CoreBridge.Studio;

namespace AlUmmahNow.CoreBridge.Temporary
{
    public sealed class HttpNotificationsClient : INotificationsClient
    {
        private readonly HttpClient _http;
        private readonly Uri _baseUri;
        private readonly Func<CancellationToken, Task<string?>> _accessToken;

        public HttpNotificationsClient(HttpClient http, Uri baseUri, Func<CancellationToken, Task<string?>> accessToken)
        {
            _http = http;
            _baseUri = baseUri;
            _accessToken = accessToken;
        }

        public async Task<UserFeatures> GetViewerFeaturesAsync(CancellationToken cancellationToken = default)
        {
            var token = await _accessToken(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token)) return UserFeatures.None;
            using var doc = await SendJsonAsync(HttpMethod.Get, "/api/me/features", null, token, cancellationToken).ConfigureAwait(false);
            return UserFeatures.Parse(doc.RootElement.GetRawText());
        }

        public async Task<NotificationBadge> GetBadgeAsync(CancellationToken cancellationToken = default)
        {
            var token = await _accessToken(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token)) return new NotificationBadge();
            using var doc = await SendJsonAsync(HttpMethod.Get, "/api/notifications/unread-count", null, token, cancellationToken).ConfigureAwait(false);
            return ParseBadge(UnwrapResult(doc.RootElement));
        }

        public async Task<NotificationsPage> ListAsync(int limit = 20, CancellationToken cancellationToken = default)
        {
            var token = await _accessToken(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token)) return new NotificationsPage(Array.Empty<NotificationEntry>(), new NotificationBadge());
            var clamped = Math.Max(1, Math.Min(50, limit));
            var path = "/api/notifications?limit=" + clamped.ToString(CultureInfo.InvariantCulture);
            using var doc = await SendJsonAsync(HttpMethod.Get, path, null, token, cancellationToken).ConfigureAwait(false);
            var res = UnwrapResult(doc.RootElement);
            var items = new List<NotificationEntry>();
            if (res.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var key = Str(item, "notificationKey") ?? "";
                    var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
                    string? title = null;
                    if (item.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in p.EnumerateObject())
                        {
                            parameters[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                                ? (prop.Value.GetString() ?? "")
                                : prop.Value.GetRawText();
                        }

                        title = Str(p, "videoTitle") ?? Str(p, "streamTitle") ?? Str(p, "channelName");
                    }

                    var isUnread = !item.TryGetProperty("readAt", out var r) || r.ValueKind == JsonValueKind.Null;
                    var createdAt = item.TryGetProperty("createdAt", out var c) && c.ValueKind == JsonValueKind.Number ? (long)c.GetDouble() : 0L;
                    items.Add(new NotificationEntry(
                        Str(item, "id") ?? "",
                        Str(item, "category") ?? "",
                        Str(item, "notificationType") ?? "",
                        key,
                        title ?? "",
                        isUnread,
                        createdAt,
                        parameters));
                }
            }

            return new NotificationsPage(items, ParseBadge(res), Str(res, "nextCursor"));
        }

        public async Task<NotificationBadge> MarkSeenAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
        {
            var token = await _accessToken(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token)) return new NotificationBadge();
            var body = JsonSerializer.Serialize(new { ids = ids.Take(100).ToArray() });
            using var doc = await SendJsonAsync(HttpMethod.Post, "/api/notifications/seen", body, token, cancellationToken).ConfigureAwait(false);
            return ParseBadge(UnwrapResult(doc.RootElement));
        }

        public async Task<NotificationBadge> MarkAllReadAsync(CancellationToken cancellationToken = default)
        {
            var token = await _accessToken(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token)) return new NotificationBadge();
            using var doc = await SendJsonAsync(HttpMethod.Post, "/api/notifications/read-all", "{}", token, cancellationToken).ConfigureAwait(false);
            return ParseBadge(UnwrapResult(doc.RootElement));
        }

        public async Task<NotificationQuietHours> GetQuietHoursAsync(CancellationToken cancellationToken = default)
        {
            var token = await _accessToken(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token)) return new NotificationQuietHours();
            using var doc = await SendJsonAsync(HttpMethod.Get, "/api/notifications/settings", null, token, cancellationToken).ConfigureAwait(false);
            var res = UnwrapResult(doc.RootElement);
            return res.TryGetProperty("quietHours", out var qh) && qh.ValueKind == JsonValueKind.Object
                ? ParseQuietHours(qh)
                : new NotificationQuietHours();
        }

        public async Task<NotificationQuietHours> UpdateQuietHoursAsync(NotificationQuietHours quietHours, CancellationToken cancellationToken = default)
        {
            var token = await _accessToken(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token)) throw new AuthRequiredException("Sign in required");
            var body = JsonSerializer.Serialize(new
            {
                quietHours = new
                {
                    enabled = quietHours.Enabled,
                    timezone = quietHours.Timezone,
                    startMinute = quietHours.StartMinute,
                    endMinute = quietHours.EndMinute,
                    prayerQuietEnabled = quietHours.PrayerQuietEnabled,
                    prayerWindows = quietHours.PrayerWindows.Select(w => new { startMinute = w.StartMinute, endMinute = w.EndMinute }).ToArray(),
                },
            });
            using var doc = await SendJsonAsync(HttpMethod.Put, "/api/notifications/settings", body, token, cancellationToken).ConfigureAwait(false);
            var res = UnwrapResult(doc.RootElement);
            return res.TryGetProperty("quietHours", out var qh) && qh.ValueKind == JsonValueKind.Object
                ? ParseQuietHours(qh)
                : quietHours;
        }

        private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, string? body, string? token, CancellationToken ct) =>
            await SharedHttpHelper.SendJsonAsync(_http, _baseUri, method, path, body, token, ct).ConfigureAwait(false);

        private static NotificationBadge ParseBadge(JsonElement e)
        {
            var unread = Int(e, "unreadCount") ?? 0;
            var unseen = Int(e, "unseenCount");
            var mode = Str(e, "menuOpen") == "read" ? NotificationMenuOpenMode.Read : NotificationMenuOpenMode.Seen;
            return new NotificationBadge(unread, unseen, mode);
        }

        private static NotificationQuietHours ParseQuietHours(JsonElement e)
        {
            var windows = new List<PrayerQuietWindow>();
            if (e.TryGetProperty("prayerWindows", out var pw) && pw.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in pw.EnumerateArray())
                {
                    windows.Add(new PrayerQuietWindow(Int(w, "startMinute") ?? 0, Int(w, "endMinute") ?? 0));
                }
            }

            return new NotificationQuietHours(
                e.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True,
                Str(e, "timezone") ?? "UTC",
                Int(e, "startMinute") ?? NotificationQuietHours.DefaultStartMinute,
                Int(e, "endMinute") ?? NotificationQuietHours.DefaultEndMinute,
                e.TryGetProperty("prayerQuietEnabled", out var pq) && pq.ValueKind == JsonValueKind.True,
                windows);
        }

        private static JsonElement UnwrapResult(JsonElement root) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var r) ? r : root;

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int? Int(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : (int?)null;
    }

    public sealed class HttpEventsClient : IEventsClient
    {
        private readonly HttpClient _http;
        private readonly Uri _baseUri;
        private readonly Func<CancellationToken, Task<string?>> _accessToken;

        public HttpEventsClient(HttpClient http, Uri baseUri, Func<CancellationToken, Task<string?>>? accessToken = null)
        {
            _http = http;
            _baseUri = baseUri;
            _accessToken = accessToken ?? (_ => Task.FromResult<string?>(null));
        }

        public async Task<UserFeatures> GetViewerFeaturesAsync(CancellationToken cancellationToken = default)
        {
            var token = await _accessToken(cancellationToken).ConfigureAwait(false);
            using var doc = await SharedHttpHelper.SendJsonAsync(_http, _baseUri, HttpMethod.Get, "/api/me/features", null, token, cancellationToken).ConfigureAwait(false);
            return UserFeatures.Parse(doc.RootElement.GetRawText());
        }

        public async Task<IReadOnlyList<CalendarEvent>> ListUpcomingAsync(string? eventType = null, int limit = 24, CancellationToken cancellationToken = default)
        {
            var token = await _accessToken(cancellationToken).ConfigureAwait(false);
            var clamped = Math.Max(1, Math.Min(50, limit));
            var path = "/api/events?limit=" + clamped.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(eventType)) path += "&eventType=" + Uri.EscapeDataString(eventType!);
            using var doc = await SharedHttpHelper.SendJsonAsync(_http, _baseUri, HttpMethod.Get, path, null, token, cancellationToken).ConfigureAwait(false);
            var list = new List<CalendarEvent>();
            if (doc.RootElement.TryGetProperty("result", out var res)
                && res.ValueKind == JsonValueKind.Object
                && res.TryGetProperty("items", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    list.Add(new CalendarEvent(
                        Str(e, "streamId") ?? "",
                        Str(e, "channelId") ?? "",
                        Str(e, "channelName") ?? "",
                        Str(e, "channelNameAr"),
                        Str(e, "channelHandle"),
                        Str(e, "title") ?? "",
                        Str(e, "description"),
                        Str(e, "kind") ?? "live",
                        Str(e, "eventType") ?? "live",
                        Str(e, "status") ?? "scheduled",
                        Str(e, "scheduledStartAt"),
                        Str(e, "startedAt"),
                        Str(e, "watchPath") ?? ""));
                }
            }

            return list;
        }

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    public sealed class HttpAppPushRegistrar : IAppPushRegistrar
    {
        private readonly HttpClient _http;
        private readonly Uri _baseUri;
        private readonly Func<CancellationToken, Task<string?>> _accessToken;

        public HttpAppPushRegistrar(HttpClient http, Uri baseUri, Func<CancellationToken, Task<string?>> accessToken)
        {
            _http = http;
            _baseUri = baseUri;
            _accessToken = accessToken;
        }

        public async Task RegisterAsync(string app, string installId, string channelUri, string locale, string appVersion, CancellationToken cancellationToken = default)
        {
            if (!ShortLinks.IsSafeId(installId) || installId.Length < 8) throw new CatalogException("Invalid installId");
            var token = await _accessToken(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token)) return;
            var trimmedVersion = appVersion.Length > 32 ? appVersion.Substring(0, 32) : appVersion;
            var body = JsonSerializer.Serialize(new
            {
                app,
                provider = "wns",
                platform = "windows",
                token = channelUri,
                locale,
                appVersion = trimmedVersion,
            });
            using var doc = await SharedHttpHelper.SendJsonAsync(_http, _baseUri, HttpMethod.Put, "/api/devices/" + installId, body, token, cancellationToken).ConfigureAwait(false);
        }

        public async Task UnregisterAsync(string installId, CancellationToken cancellationToken = default)
        {
            if (!ShortLinks.IsSafeId(installId) || installId.Length < 8) return;
            var token = await _accessToken(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token)) return;
            using var doc = await SharedHttpHelper.SendJsonAsync(_http, _baseUri, HttpMethod.Delete, "/api/devices/" + installId, null, token, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static class SharedHttpHelper
    {
        internal static async Task<JsonDocument> SendJsonAsync(
            HttpClient http,
            Uri baseUri,
            HttpMethod method,
            string path,
            string? body,
            string? bearerToken,
            CancellationToken ct)
        {
            using var req = new HttpRequestMessage(method, new Uri(baseUri, path));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(bearerToken))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }

            if (body != null)
            {
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            try
            {
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new AuthRequiredException("Unauthorized");
                }

                if (!resp.IsSuccessStatusCode)
                {
                    throw new CatalogException("HTTP " + (int)resp.StatusCode + " for " + path);
                }

                var raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            }
            catch (HttpRequestException ex)
            {
                throw new CatalogException("Network error", ex);
            }
        }
    }
}
