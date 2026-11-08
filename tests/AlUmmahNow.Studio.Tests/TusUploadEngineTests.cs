using System.Net;
using System.Text;
using System.Text.Json;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Temporary;
using AlUmmahNow.Studio.Upload;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

public sealed class TusUploadEngineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class Auth : IAuthSession
    {
        public event EventHandler? AccountChanged { add { } remove { } }
        public AuthAccount? Account => new("u", null);
        public bool Rejected;
        public void Restore() { }
        public Task SignInAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Rejected ? throw new AuthRequiredException("no") : Task.FromResult("tok");
        public void SignOut() { }
    }

    /// <summary>An in-memory TUS server plus the api-videos endpoints.</summary>
    private sealed class Server : HttpMessageHandler
    {
        public readonly List<string> Log = new();
        public readonly List<HttpRequestMessage> Requests = new();
        public MemoryStream Stored = new();
        public long? Length;
        public int Chunk = 300_000;
        public int Throttle;
        public int ConflictOnce;
        public bool ProxyGone;
        public int CompleteFailures;
        public int PatchFailuresAfterStore;
        public string? InitiateBody;
        public string? UploadMetadata;
        public HttpStatusCode? InitiateStatus;

        private static HttpResponseMessage R(HttpStatusCode c, string? body = null) => new(c) { Content = new StringContent(body ?? "") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var path = r.RequestUri!.AbsolutePath;
            Log.Add(r.Method + " " + path);
            lock (Requests) Requests.Add(r);

            if (Throttle > 0 && r.Method.Method == "PATCH")
            {
                Throttle--;
                var t = R((HttpStatusCode)429);
                t.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return t;
            }

            switch (r.Method.Method, path)
            {
                case ("POST", "/api/videos/upload/initiate"):
                    if (InitiateStatus is { } s) return R(s);
                    InitiateBody = await r.Content!.ReadAsStringAsync(ct);
                    return R(HttpStatusCode.OK, "{\"success\":true,\"result\":{\"videoId\":\"v1\",\"tusSessionId\":\"sess\",\"chunkSize\":" + Chunk + ",\"resumable\":true}}");
                case ("POST", "/api/videos/tus"):
                case ("POST", "/direct"):
                    Length = long.Parse(r.Headers.GetValues("Upload-Length").First());
                    UploadMetadata = r.Headers.GetValues("Upload-Metadata").First();
                    Stored = new MemoryStream();
                    var created = R(HttpStatusCode.Created);
                    created.Headers.Location = new Uri(path == "/direct" ? "https://storage.test/direct/abc" : "/api/videos/tus/abc", UriKind.RelativeOrAbsolute);
                    return created;
                case ("GET", "/api/videos/upload/resume/v1"):
                    return R(HttpStatusCode.OK, """{"success":true,"result":{"videoId":"v1","tusEndpoint":"https://storage.test/direct","tusHeaders":{"AuthorizationSignature":"sig","VideoId":"v1"}}}""");
                case ("HEAD", _):
                    if (ProxyGone && path.StartsWith("/api/videos/tus")) return R(HttpStatusCode.NotFound);
                    var head = R(HttpStatusCode.OK);
                    head.Headers.TryAddWithoutValidation("Upload-Offset", Stored.Length.ToString());
                    return head;
                case ("PATCH", _):
                    if (ConflictOnce > 0)
                    {
                        ConflictOnce--;
                        return R(HttpStatusCode.Conflict);
                    }

                    var body = await r.Content!.ReadAsByteArrayAsync(ct);
                    Assert.Equal("application/offset+octet-stream", r.Content.Headers.ContentType!.MediaType);
                    Assert.Equal(Stored.Length, long.Parse(r.Headers.GetValues("Upload-Offset").First()));
                    Stored.Write(body);
                    if (PatchFailuresAfterStore > 0)
                    {
                        PatchFailuresAfterStore--;
                        return R(HttpStatusCode.BadGateway);
                    }

                    var ok = R(HttpStatusCode.NoContent);
                    ok.Headers.TryAddWithoutValidation("Upload-Offset", Stored.Length.ToString());
                    return ok;
                case ("POST", "/api/videos/upload/complete"):
                    if (CompleteFailures > 0)
                    {
                        CompleteFailures--;
                        return R(HttpStatusCode.InternalServerError);
                    }

                    return R(HttpStatusCode.OK, """{"success":true,"result":{"videoId":"v1","status":"processing","message":"ok"}}""");
                default:
                    return R(HttpStatusCode.NotFound);
            }
        }
    }

    private sealed class Sink : IProgress<long>
    {
        public readonly List<long> Values = new();
        public void Report(long value) => Values.Add(value);
    }

    private readonly List<TimeSpan> _waits = new();

    private (TusUploadEngine Engine, Server Server, Auth Auth) Make()
    {
        var server = new Server();
        var auth = new Auth();
        var engine = new TusUploadEngine(new HttpClient(server), new Uri("https://api.test"), auth, _ => Task.FromResult("chan-1"), (w, _) => { _waits.Add(w); return Task.CompletedTask; });
        return (engine, server, auth);
    }

    private (string Path, byte[] Bytes) File(int size, string name = "My Lecture.mov")
    {
        var bytes = new byte[size];
        new Random(7).NextBytes(bytes);
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllBytes(path, bytes);
        return (path, bytes);
    }

    [Fact]
    public async Task Uploads_a_new_file_end_to_end_as_private()
    {
        var (engine, server, _) = Make();
        var (path, bytes) = File(700_000);
        var sink = new Sink();
        UploadSession? saved = null;
        await engine.UploadAsync(new UploadRequest(path, bytes.Length, 0, null, s => saved = s), sink, default);

        Assert.Equal(bytes, server.Stored.ToArray());
        Assert.Equal("POST /api/videos/upload/complete", server.Log[^1]);
        Assert.Equal(3, server.Log.Count(l => l.StartsWith("PATCH")));
        Assert.Equal(700_000, sink.Values[^1]);
        Assert.Equal("v1", saved!.VideoId);
        Assert.Equal("https://api.test/api/videos/tus/abc", saved.Location);

        using var body = JsonDocument.Parse(server.InitiateBody!);
        Assert.Equal("My Lecture", body.RootElement.GetProperty("title").GetString());
        Assert.False(body.RootElement.GetProperty("isPublic").GetBoolean());
        Assert.Equal("video/quicktime", body.RootElement.GetProperty("mimeType").GetString());
        Assert.Contains("tusSessionId " + Convert.ToBase64String(Encoding.UTF8.GetBytes("sess")), server.UploadMetadata);
        Assert.All(server.Requests, r => Assert.Equal("Bearer tok", r.Headers.Authorization!.ToString()));
        Assert.Contains(server.Requests, r => r.Headers.TryGetValues("channelId", out var v) && v.Single() == "chan-1");
    }

    [Fact]
    public async Task Resume_trusts_the_server_offset()
    {
        var (engine, server, _) = Make();
        var (path, bytes) = File(600_000);
        server.Length = bytes.Length;
        server.Stored.Write(bytes, 0, 300_000);
        var session = new UploadSession { VideoId = "v1", Location = "https://api.test/api/videos/tus/abc", ChunkSize = 300_000 };
        await engine.UploadAsync(new UploadRequest(path, bytes.Length, 12_345, session), new Sink(), default);
        Assert.Equal(bytes, server.Stored.ToArray());
        Assert.Equal(1, server.Log.Count(l => l.StartsWith("PATCH")));
        Assert.DoesNotContain(server.Log, l => l.Contains("initiate"));
    }

    [Fact]
    public async Task Finished_bytes_with_a_failed_complete_only_retries_complete()
    {
        var (engine, server, _) = Make();
        var (path, bytes) = File(300_000);
        server.Stored.Write(bytes);
        server.CompleteFailures = 0;
        var session = new UploadSession { VideoId = "v1", Location = "https://api.test/api/videos/tus/abc", ChunkSize = 300_000 };
        await engine.UploadAsync(new UploadRequest(path, bytes.Length, bytes.Length, session), new Sink(), default);
        Assert.DoesNotContain(server.Log, l => l.StartsWith("PATCH"));
        Assert.Equal("POST /api/videos/upload/complete", server.Log[^1]);
    }

    [Fact]
    public async Task Conflict_resyncs_from_the_server()
    {
        var (engine, server, _) = Make();
        var (path, bytes) = File(600_000);
        server.ConflictOnce = 1;
        await engine.UploadAsync(new UploadRequest(path, bytes.Length, 0), new Sink(), default);
        Assert.Equal(bytes, server.Stored.ToArray());
        Assert.Contains(server.Log, l => l.StartsWith("HEAD"));
    }

    [Fact]
    public async Task Rate_limit_waits_as_told_without_failing()
    {
        var (engine, server, _) = Make();
        var (path, bytes) = File(300_000);
        server.Throttle = 2;
        await engine.UploadAsync(new UploadRequest(path, bytes.Length, 0), new Sink(), default);
        Assert.Equal(new[] { TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7) }, _waits);
        Assert.Equal(bytes, server.Stored.ToArray());
    }

    [Fact]
    public async Task Expired_proxy_session_falls_back_to_signed_direct_upload()
    {
        var (engine, server, _) = Make();
        var (path, bytes) = File(400_000);
        server.ProxyGone = true;
        var session = new UploadSession { VideoId = "v1", Location = "https://api.test/api/videos/tus/abc", ChunkSize = 300_000 };
        UploadSession? saved = null;
        await engine.UploadAsync(new UploadRequest(path, bytes.Length, 0, session, s => saved = s), new Sink(), default);

        Assert.True(saved!.Direct);
        Assert.Equal("https://storage.test/direct/abc", saved.Location);
        Assert.Equal(bytes, server.Stored.ToArray());
        var direct = server.Requests.Where(r => r.RequestUri!.Host == "storage.test").ToList();
        Assert.All(direct, r => Assert.Equal("sig", r.Headers.GetValues("AuthorizationSignature").Single()));
        Assert.All(direct, r => Assert.Null(r.Headers.Authorization));
    }

    [Fact]
    public async Task A_direct_session_gets_fresh_signatures_after_restart()
    {
        var (engine, server, _) = Make();
        var (path, bytes) = File(300_000);
        var session = new UploadSession { VideoId = "v1", Location = "https://storage.test/direct/abc", ChunkSize = 300_000, Direct = true };
        await engine.UploadAsync(new UploadRequest(path, bytes.Length, 0, session), new Sink(), default);
        Assert.Contains("GET /api/videos/upload/resume/v1", server.Log);
        Assert.Equal(bytes, server.Stored.ToArray());
    }

    [Fact]
    public async Task Changed_file_is_refused()
    {
        var (engine, _, _) = Make();
        var (path, _) = File(10);
        await Assert.ThrowsAsync<IOException>(() => engine.UploadAsync(new UploadRequest(path, 99, 0), new Sink(), default));
    }

    [Fact]
    public async Task Unauthorized_requires_sign_in()
    {
        var (engine, server, _) = Make();
        var (path, bytes) = File(10);
        server.InitiateStatus = HttpStatusCode.Unauthorized;
        await Assert.ThrowsAsync<AuthRequiredException>(() => engine.UploadAsync(new UploadRequest(path, bytes.Length, 0), new Sink(), default));
    }

    [Fact]
    public async Task Queue_retries_a_dropped_chunk_and_resumes_without_a_new_session()
    {
        var (engine, server, _) = Make();
        var (path, bytes) = File(600_000);
        server.PatchFailuresAfterStore = 1;
        var queue = new UploadQueue(engine, Path.Combine(_dir, "q.json"), 1, 3, _ => TimeSpan.Zero);
        queue.Enqueue(path, bytes.Length);
        await queue.WhenIdleAsync();

        Assert.Equal(UploadStatus.Completed, queue.Snapshot()[0].Status);
        Assert.Equal(bytes, server.Stored.ToArray());
        Assert.Equal(1, server.Log.Count(l => l.Contains("initiate")));
        Assert.NotNull(queue.Snapshot()[0].Session);
    }

    [Fact]
    public async Task Queue_stops_retrying_when_sign_in_is_needed()
    {
        var (engine, server, auth) = Make();
        var (path, bytes) = File(10);
        auth.Rejected = true;
        var queue = new UploadQueue(engine, Path.Combine(_dir, "q.json"), 1, 5, _ => TimeSpan.Zero);
        queue.Enqueue(path, bytes.Length);
        await queue.WhenIdleAsync();
        Assert.Equal(UploadStatus.Failed, queue.Snapshot()[0].Status);
        Assert.Empty(server.Log);
    }

    private sealed class Channels : IChannelClient
    {
        public IReadOnlyList<ChannelInfo> Items = Array.Empty<ChannelInfo>();
        public Task<IReadOnlyList<ChannelInfo>> ListMineAsync(CancellationToken ct = default) => Task.FromResult(Items);
    }

    [Fact]
    public async Task Context_prefers_the_main_channel_and_reports_none()
    {
        var channels = new Channels { Items = new[] { new ChannelInfo("a", "A", false), new ChannelInfo("b", "B", true) } };
        var context = new UploadContext(channels);
        Assert.Equal("b", await context.ChannelIdAsync());
        channels.Items = Array.Empty<ChannelInfo>();
        context.Reset();
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.ChannelIdAsync());
    }
}
