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

namespace AlUmmahNow.CoreBridge.Studio
{
    /// <summary>
    /// Plain HTTP to api-videos for the Studio routes the core's ApiClient can't carry: raw file bodies
    /// (renditions, assets), binary answers, and the error codes and lists in 4xx bodies.
    /// Bearer token from the active account on every call; one fresh token on 401, then <see cref="AuthRequiredException"/>.
    /// </summary>
    public sealed class StudioHttpClient : IDisposable
    {
        private static readonly TimeSpan JsonTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan BytesTimeout = TimeSpan.FromMinutes(10);

        private readonly HttpClient _http;
        private readonly Uri _base;
        private readonly Func<CancellationToken, Task<string>> _token;
        private readonly bool _ownsHttp;

        public StudioHttpClient(Uri videosApi, Func<CancellationToken, Task<string>> accessToken, string userAgent, string? locale = null, HttpMessageHandler? handler = null)
        {
            _base = new Uri(videosApi.AbsoluteUri.TrimEnd('/') + "/");
            _token = accessToken;
            _ownsHttp = true;
            _http = handler == null ? new HttpClient() : new HttpClient(handler, true);
            _http.Timeout = Timeout.InfiniteTimeSpan;
            _http.DefaultRequestHeaders.UserAgent.TryParseAdd(userAgent);
            if (!string.IsNullOrEmpty(locale)) _http.DefaultRequestHeaders.AcceptLanguage.TryParseAdd(locale);
        }

        /// <summary>
        /// One path segment. Server ids and language codes are <c>[A-Za-z0-9_.-]</c>; anything else (a "/", "..")
        /// becomes "_invalid_" so an id can never reach another route.
        /// </summary>
        public static string Segment(string? value)
        {
            if (string.IsNullOrEmpty(value) || value == "." || value == "..") return "_invalid_";
            foreach (var ch in value!)
            {
                var ok = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_' || ch == '.';
                if (!ok) return "_invalid_";
            }

            return value;
        }

        /// <summary>Sends JSON (or nothing) and returns the parsed answer ("{}" when empty).</summary>
        public async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, string? channelId = null, string? bodyJson = null, IReadOnlyDictionary<string, string>? query = null, CancellationToken cancellationToken = default)
        {
            var bytes = await SendAsync(method, path, channelId, query, bodyJson == null ? null : Encoding.UTF8.GetBytes(bodyJson), "application/json", JsonTimeout, cancellationToken).ConfigureAwait(false);
            return Parse(bytes, method + " " + path);
        }

        /// <summary>Sends a raw body with an exact Content-Length and returns the parsed answer.</summary>
        public async Task<JsonDocument> SendBytesAsync(HttpMethod method, string path, byte[] body, string? channelId = null, IReadOnlyDictionary<string, string>? query = null, CancellationToken cancellationToken = default)
        {
            var bytes = await SendAsync(method, path, channelId, query, body, "application/octet-stream", BytesTimeout, cancellationToken).ConfigureAwait(false);
            return Parse(bytes, method + " " + path);
        }

        /// <summary>Returns the raw answer (an asset's file).</summary>
        public Task<byte[]> GetBytesAsync(string path, string? channelId = null, CancellationToken cancellationToken = default) =>
            SendAsync(HttpMethod.Get, path, channelId, null, null, null, BytesTimeout, cancellationToken);

        private async Task<byte[]> SendAsync(HttpMethod method, string path, string? channelId, IReadOnlyDictionary<string, string>? query, byte[]? body, string? contentType, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var what = method + " " + path;
            for (var attempt = 0; ; attempt++)
            {
                var token = await _token(cancellationToken).ConfigureAwait(false);
                using var request = new HttpRequestMessage(method, Url(path, query));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (!string.IsNullOrEmpty(channelId)) request.Headers.TryAddWithoutValidation("channelId", channelId);
                if (body != null)
                {
                    var content = new ByteArrayContent(body);
                    content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/octet-stream");
                    content.Headers.ContentLength = body.Length;
                    request.Content = content;
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);
                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    throw StudioApiException.Network(what + " timed out", ex);
                }
                catch (HttpRequestException ex)
                {
                    throw StudioApiException.Network(what + ": network unavailable", ex);
                }

                using (response)
                {
                    var bytes = response.Content == null ? new byte[0] : await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    var status = (int)response.StatusCode;
                    if (status >= 200 && status < 300) return bytes;
                    if (status == 401)
                    {
                        if (attempt == 0) continue;
                        throw new AuthRequiredException(what + ": sign in again");
                    }

                    if (status == 429)
                    {
                        var after = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                        throw new RateLimitedException(after);
                    }

                    throw Error(what, status, bytes);
                }
            }
        }

        private Uri Url(string path, IReadOnlyDictionary<string, string>? query)
        {
            var sb = new StringBuilder(path.TrimStart('/'));
            if (query != null && query.Count > 0)
            {
                sb.Append('?');
                sb.Append(string.Join("&", query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value))));
            }

            return new Uri(_base, sb.ToString());
        }

        internal static StudioApiException Error(string what, int status, byte[] body)
        {
            string? code = null;
            string? reason = null;
            int? segment = null;
            var missing = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(body.Length == 0 ? Encoding.UTF8.GetBytes("{}") : body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) code = e.GetString();
                    // Gated routes answer { "success": false, "code": "feature_off" }.
                    if (code == null && root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString();
                    if (root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String) reason = r.GetString();
                    if (root.TryGetProperty("segment", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var si)) segment = si;
                    if (root.TryGetProperty("missing", out var m) && m.ValueKind == JsonValueKind.Array)
                    {
                        missing.AddRange(m.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").Where(x => x.Length > 0));
                    }
                }
            }
            catch (JsonException)
            {
                // Not JSON (a proxy's page): the status alone.
            }

            var message = what + " failed: HTTP " + status.ToString(CultureInfo.InvariantCulture) + (code == null ? "" : " " + code);
            return StudioApiException.FromServer(message, status, code, missing, reason, segment);
        }

        private static JsonDocument Parse(byte[] body, string what)
        {
            try
            {
                return JsonDocument.Parse(body.Length == 0 ? Encoding.UTF8.GetBytes("{}") : body);
            }
            catch (JsonException ex)
            {
                throw new StudioApiException(what + " returned invalid JSON", null, ex);
            }
        }

        public void Dispose()
        {
            if (_ownsHttp) _http.Dispose();
        }
    }
}
