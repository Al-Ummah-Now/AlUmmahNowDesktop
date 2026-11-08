using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    /// <summary>
    /// TEMPORARY stand-in for aun-upload: TUS 1.0.0 through the /api/videos/tus proxy, falling back
    /// to storage directly when the proxy's one-hour session has expired. New videos start private.
    /// </summary>
    public sealed class TusUploadEngine : IUploadEngine
    {
        private const string TusVersion = "1.0.0";
        private const int DefaultChunk = 5 * 1024 * 1024;
        private const int MinChunk = 256 * 1024;
        private const int MaxChunk = 50 * 1024 * 1024;
        private const int MaxThrottleWaits = 20;

        private readonly HttpClient _http;
        private readonly Uri _api;
        private readonly IAuthSession _auth;
        private readonly Func<CancellationToken, Task<string>> _channelId;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        public TusUploadEngine(HttpClient http, Uri api, IAuthSession auth, Func<CancellationToken, Task<string>> channelId, Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            _http = http;
            _api = api;
            _auth = auth;
            _channelId = channelId;
            _delay = delay ?? Task.Delay;
        }

        public async Task UploadAsync(UploadRequest request, IProgress<long> progress, CancellationToken cancellationToken)
        {
            var info = new FileInfo(request.FilePath);
            if (!info.Exists || info.Length != request.Size) throw new IOException("The file changed after it was queued");

            var session = request.Session;
            var route = new Route(this, session?.Direct == true ? await ResumeHeadersAsync(session.VideoId, cancellationToken).ConfigureAwait(false) : null);
            long offset;
            if (session == null)
            {
                session = await InitiateAsync(info, cancellationToken).ConfigureAwait(false);
                offset = 0;
                request.SessionChanged?.Invoke(session);
            }
            else
            {
                var found = await HeadOffsetAsync(session.Location, route, cancellationToken).ConfigureAwait(false);
                if (found == null)
                {
                    // The proxy session is gone; the signed direct route still works.
                    route = await SwitchToDirectAsync(session, info, cancellationToken).ConfigureAwait(false);
                    request.SessionChanged?.Invoke(session);
                    found = await HeadOffsetAsync(session.Location, route, cancellationToken).ConfigureAwait(false) ?? 0;
                }

                offset = found.Value;
            }

            progress.Report(offset);
            await SendChunksAsync(session, route, info, offset, progress, cancellationToken).ConfigureAwait(false);
            await CompleteAsync(session, info.Length, cancellationToken).ConfigureAwait(false);
        }

        private async Task<UploadSession> InitiateAsync(FileInfo file, CancellationToken cancellationToken)
        {
            var channel = await _channelId(cancellationToken).ConfigureAwait(false);
            var body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["title"] = Path.GetFileNameWithoutExtension(file.Name),
                ["fileSize"] = file.Length,
                ["mimeType"] = MimeType(file.Extension),
                ["isPublic"] = false,
            });

            using var doc = await PostJsonAsync("/api/videos/upload/initiate", body, channel, cancellationToken).ConfigureAwait(false);
            var result = doc.RootElement.GetProperty("result");
            var session = new UploadSession
            {
                VideoId = result.GetProperty("videoId").GetString() ?? "",
                ChunkSize = result.TryGetProperty("chunkSize", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : DefaultChunk,
            };
            var tusSessionId = result.GetProperty("tusSessionId").GetString() ?? "";

            using var create = new HttpRequestMessage(HttpMethod.Post, new Uri(_api, "/api/videos/tus"));
            await AddBearerAsync(create, cancellationToken).ConfigureAwait(false);
            create.Headers.TryAddWithoutValidation("channelId", channel);
            AddTusCreateHeaders(create, file, ("tusSessionId", tusSessionId));
            using var response = await SendThrottledAsync(create, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, "create");
            session.Location = ResolveLocation(response, new Uri(_api, "/api/videos/tus"));
            return session;
        }

        private async Task<Route> SwitchToDirectAsync(UploadSession session, FileInfo file, CancellationToken cancellationToken)
        {
            var (endpoint, headers) = await ResumeRouteAsync(session.VideoId, cancellationToken).ConfigureAwait(false);
            var route = new Route(this, headers);
            using var create = new HttpRequestMessage(HttpMethod.Post, endpoint);
            await route.ApplyAsync(create, cancellationToken).ConfigureAwait(false);
            AddTusCreateHeaders(create, file);
            using var response = await SendThrottledAsync(create, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, "direct create");
            session.Location = ResolveLocation(response, endpoint);
            session.Direct = true;
            return route;
        }

        private async Task<Dictionary<string, string>> ResumeHeadersAsync(string videoId, CancellationToken cancellationToken) =>
            (await ResumeRouteAsync(videoId, cancellationToken).ConfigureAwait(false)).Headers;

        private async Task<(Uri Endpoint, Dictionary<string, string> Headers)> ResumeRouteAsync(string videoId, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_api, "/api/videos/upload/resume/" + Uri.EscapeDataString(videoId)));
            await AddBearerAsync(request, cancellationToken).ConfigureAwait(false);
            using var response = await SendThrottledAsync(request, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, "resume");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var result = doc.RootElement.GetProperty("result");
            var headers = result.GetProperty("tusHeaders").EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
            return (new Uri(result.GetProperty("tusEndpoint").GetString() ?? ""), headers);
        }

        /// <returns>The server's offset, or null when the upload is unknown there.</returns>
        private async Task<long?> HeadOffsetAsync(string location, Route route, CancellationToken cancellationToken)
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, location);
            await route.ApplyAsync(head, cancellationToken).ConfigureAwait(false);
            head.Headers.TryAddWithoutValidation("Tus-Resumable", TusVersion);
            using var response = await SendThrottledAsync(head, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone) return null;
            EnsureSuccess(response, "head");
            return ReadOffset(response);
        }

        private async Task SendChunksAsync(UploadSession session, Route route, FileInfo file, long start, IProgress<long> progress, CancellationToken cancellationToken)
        {
            var chunk = Math.Min(MaxChunk, Math.Max(MinChunk, session.ChunkSize > 0 ? session.ChunkSize : DefaultChunk));
            var buffer = new byte[chunk];
            var offset = start;
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1, useAsync: true);
            while (offset < file.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Seek(offset, SeekOrigin.Begin);
                var read = await ReadFullyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new IOException("The file ended early");

                using var patch = new HttpRequestMessage(new HttpMethod("PATCH"), session.Location);
                await route.ApplyAsync(patch, cancellationToken).ConfigureAwait(false);
                patch.Headers.TryAddWithoutValidation("Tus-Resumable", TusVersion);
                patch.Headers.TryAddWithoutValidation("Upload-Offset", offset.ToString(CultureInfo.InvariantCulture));
                patch.Content = new ByteArrayContent(buffer, 0, read);
                patch.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
                using var response = await SendThrottledAsync(patch, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    // Our offset is stale, usually after a lost response: ask the server.
                    offset = await HeadOffsetAsync(session.Location, route, cancellationToken).ConfigureAwait(false)
                        ?? throw new IOException("The upload disappeared from the server");
                    progress.Report(offset);
                    continue;
                }

                EnsureSuccess(response, "patch");
                offset = ReadOffset(response) ?? offset + read;
                progress.Report(offset);
            }
        }

        private async Task CompleteAsync(UploadSession session, long size, CancellationToken cancellationToken)
        {
            var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["videoId"] = session.VideoId, ["fileSize"] = size });
            var channel = await _channelId(cancellationToken).ConfigureAwait(false);
            using var _ = await PostJsonAsync("/api/videos/upload/complete", body, channel, cancellationToken).ConfigureAwait(false);
        }

        private async Task<JsonDocument> PostJsonAsync(string path, string json, string channel, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_api, path)) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            await AddBearerAsync(request, cancellationToken).ConfigureAwait(false);
            request.Headers.TryAddWithoutValidation("channelId", channel);
            using var response = await SendThrottledAsync(request, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, path);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }

        private async Task AddBearerAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = await _auth.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        private static void AddTusCreateHeaders(HttpRequestMessage request, FileInfo file, params (string Key, string Value)[] extra)
        {
            request.Headers.TryAddWithoutValidation("Tus-Resumable", TusVersion);
            request.Headers.TryAddWithoutValidation("Upload-Length", file.Length.ToString(CultureInfo.InvariantCulture));
            var metadata = new[] { ("filename", file.Name), ("filetype", MimeType(file.Extension)) }.Concat(extra)
                .Select(m => m.Item1 + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(m.Item2)));
            request.Headers.TryAddWithoutValidation("Upload-Metadata", string.Join(",", metadata));
            request.Content = new ByteArrayContent(Array.Empty<byte>());
        }

        /// <summary>Waits out 429 and Retry-After replies, which do not count as failures.</summary>
        private async Task<HttpResponseMessage> SendThrottledAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            for (var attempt = 0; ; attempt++)
            {
                HttpResponseMessage response;
                using (var copy = await CloneAsync(request).ConfigureAwait(false))
                {
                    response = await _http.SendAsync(copy, cancellationToken).ConfigureAwait(false);
                }

                if ((int)response.StatusCode != 429 || attempt >= MaxThrottleWaits) return response;
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(60, 2 << Math.Min(attempt, 5)));
                response.Dispose();
                await _delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
        {
            var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (request.Content != null)
            {
                copy.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
                foreach (var header in request.Content.Headers) copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return copy;
        }

        private static void EnsureSuccess(HttpResponseMessage response, string step)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new AuthRequiredException("The server rejected the session");
            if (!response.IsSuccessStatusCode) throw new IOException("Upload " + step + " failed: HTTP " + (int)response.StatusCode);
        }

        private static long? ReadOffset(HttpResponseMessage response) =>
            response.Headers.TryGetValues("Upload-Offset", out var values)
            && long.TryParse(values.First(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset) ? offset : (long?)null;

        private static string ResolveLocation(HttpResponseMessage response, Uri baseUri)
        {
            var location = response.Headers.Location ?? throw new IOException("The server did not return an upload location");
            return (location.IsAbsoluteUri ? location : new Uri(baseUri, location)).ToString();
        }

        private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer, total, buffer.Length - total, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
            }

            return total;
        }

        private static string MimeType(string extension)
        {
            switch (extension.ToLowerInvariant())
            {
                case ".mov": return "video/quicktime";
                case ".mkv": return "video/x-matroska";
                case ".webm": return "video/webm";
                case ".avi": return "video/x-msvideo";
                case ".ts":
                case ".mts": return "video/mp2t";
                default: return "video/mp4";
            }
        }

        /// <summary>How one upload authenticates: bearer through the proxy, or signed headers direct.</summary>
        private sealed class Route
        {
            private readonly TusUploadEngine _engine;
            private readonly Dictionary<string, string>? _direct;

            public Route(TusUploadEngine engine, Dictionary<string, string>? direct)
            {
                _engine = engine;
                _direct = direct;
            }

            public async Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (_direct == null)
                {
                    await _engine.AddBearerAsync(request, cancellationToken).ConfigureAwait(false);
                    return;
                }

                foreach (var header in _direct) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
    }

    public sealed class HttpChannelClient : IChannelClient
    {
        private readonly HttpClient _http;
        private readonly Uri _api;
        private readonly IAuthSession _auth;

        public HttpChannelClient(HttpClient http, Uri api, IAuthSession auth)
        {
            _http = http;
            _api = api;
            _auth = auth;
        }

        public async Task<IReadOnlyList<ChannelInfo>> ListMineAsync(CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_api, "/api/channels?limit=50&scope=mine"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _auth.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false));
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new AuthRequiredException("The server rejected the session");
            if (!response.IsSuccessStatusCode) throw new CatalogException("Channels returned HTTP " + (int)response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var result = new List<ChannelInfo>();
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in data.EnumerateArray())
                {
                    var isMain = c.TryGetProperty("isMain", out var m) && m.ValueKind == JsonValueKind.True;
                    result.Add(new ChannelInfo(c.GetProperty("id").GetString() ?? "", c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", isMain));
                }
            }

            return result;
        }
    }
}
