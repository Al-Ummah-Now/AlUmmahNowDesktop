using System.Net;
using System.Net.Sockets;
using System.Text;
using AlUmmahNow.App.Media;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Temporary;
using Xunit;

namespace AlUmmahNow.App.Tests;

public sealed class InjectionAuditTests
{
    private sealed class CaptureSink : ILogSink
    {
        public readonly List<LogEntry> Entries = new();
        public void Write(LogEntry entry) { lock (Entries) Entries.Add(entry); }
    }

    private sealed class RejectHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    [Fact]
    public void LogScrubber_neutralizes_crlf_and_control_char_log_injection()
    {
        var sink = new CaptureSink();
        Log.AddSink(sink);
        try
        {
            Log.For("auth\r\nforged").Warn("ok\n2026-11-28T00:00:00.000+00:00 INFO [admin] forged\r\u0000");
        }
        finally
        {
            Log.RemoveSink(sink);
        }

        var entry = Assert.Single(sink.Entries, e => e.Message.Contains("forged"));
        Assert.DoesNotContain("\n", entry.Category, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", entry.Category, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\0', entry.Message);
    }

    [Fact]
    public void HlsPlaylist_rejects_non_http_and_downgraded_variant_schemes()
    {
        var manifest = string.Join("\n",
            "#EXTM3U",
            "#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=1280x720",
            "file:///etc/passwd",
            "#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=854x480",
            "http://cdn.example.net/downgrade.m3u8",
            "#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360",
            "https://cdn.example.net/360p/index.m3u8");
        var variants = HlsPlaylist.ParseVariants(manifest, "https://cdn.example.net/master.m3u8");
        var only = Assert.Single(variants);
        Assert.Equal("https://cdn.example.net/360p/index.m3u8", only.Url);
    }

    [Fact]
    public void ShortLinks_rejects_host_injection_and_dot_or_control_handles()
    {
        Assert.Null(ShortLinks.Video("vid1", "evil.example/path"));
        Assert.Null(ShortLinks.Video("vid1", "alummahnow.link\r\nX-Injected: 1"));
        Assert.Null(ShortLinks.Video("vid1", "user@alummahnow.link"));
        Assert.False(ShortLinks.IsSafeHandle(".."));
        Assert.False(ShortLinks.IsSafeHandle("."));
        Assert.False(ShortLinks.IsSafeHandle("bad\nhandle"));
        Assert.False(ShortLinks.IsSafeHandle("a\\b"));
    }

    [Fact]
    public async Task Temporary_http_clients_reject_dot_segments_and_insecure_text_urls()
    {
        var handler = new RejectHandler();
        var http = new HttpClient(handler);
        var catalog = new HttpCatalogClient(http, new Uri("https://videos.api.example.net"));
        var live = new HttpLiveClient(http, new Uri("https://videos.api.example.net"), _ => Task.FromResult<string?>("tok"));

        await Assert.ThrowsAsync<ArgumentException>(() => catalog.GetVideoAsync(".."));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.GetSubtitleTracksAsync("."));
        await Assert.ThrowsAsync<CatalogException>(() => catalog.GetTextAsync("file:///etc/passwd"));
        await Assert.ThrowsAsync<CatalogException>(() => catalog.GetTextAsync("http://cdn.example.net/sub.vtt"));
        await Assert.ThrowsAsync<ArgumentException>(() => live.GetStreamAsync(".."));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task PkceAuthSession_verifies_state_before_error_and_rejects_duplicate_params()
    {
        var browser = new ScriptedBrowser();
        var session = new PkceAuthSession(new HttpClient(new RejectHandler()), new Uri("https://auth.test"), "client-1", new MemoryStore(), browser, "done", TimeSpan.FromSeconds(5));

        // Forged error with wrong state must fail with State mismatch (CSRF), not the attacker's error string.
        browser.CallbackPath = _ => "/auth/callback?error=attacker_spoof&state=wrong";
        var ex = await Assert.ThrowsAsync<AuthFailedException>(() => session.SignInAsync());
        Assert.Contains("State mismatch", ex.Message);

        // Duplicate state parameter pollution is rejected.
        browser.CallbackPath = state => "/auth/callback?code=abc&state=wrong&state=" + state;
        var exDup = await Assert.ThrowsAsync<AuthFailedException>(() => session.SignInAsync());
        Assert.Contains("Duplicate", exDup.Message);
    }

    private sealed class MemoryStore : ISecureStore
    {
        public byte[]? Get(string key) => null;
        public void Set(string key, byte[] value) { }
        public void Delete(string key) { }
        public string[] List(string prefix) => Array.Empty<string>();
    }

    private sealed class ScriptedBrowser : IBrowserLauncher
    {
        public Func<string, string> CallbackPath { get; set; } = _ => "/auth/callback";

        public void Open(Uri uri)
        {
            var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var port = new Uri(q["redirect_uri"]!).Port;
            var state = q["state"]!;
            var path = CallbackPath(state);
            _ = Task.Run(async () =>
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                var stream = client.GetStream();
                var req = Encoding.ASCII.GetBytes("GET " + path + " HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
                await stream.WriteAsync(req);
                await new StreamReader(stream).ReadToEndAsync();
            });
        }
    }
}
