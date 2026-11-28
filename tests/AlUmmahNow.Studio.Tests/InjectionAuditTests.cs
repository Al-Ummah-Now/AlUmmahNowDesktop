using System.Net;
using System.Net.Sockets;
using System.Text;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Encode;
using AlUmmahNow.Studio.Engines;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

public sealed class InjectionAuditTests
{
    [Fact]
    public void GstPipeline_and_RungPipeline_escape_backslashes_and_reject_injected_elements()
    {
        // Backslash before quote must escape backslash first so the quote stays inside the string literal.
        var tricky = "/tmp/video\\\" ! fakesink ! filesink location=\"/tmp/pwn.mp4";
        var pipe = GstPipeline.Build(new EncodeRequest("/tmp/in.mp4", tricky, EncodePreset.Lecture), GstPipeline.H264Encoders[^1]);
        Assert.Contains("\\\\\\\"", pipe);

        Assert.Throws<ArgumentException>(() => GstPipeline.Build(
            new EncodeRequest("/tmp/in.mp4", "/tmp/out\n.mp4", EncodePreset.Lecture),
            GstPipeline.H264Encoders[^1]));

        Assert.Throws<ArgumentException>(() => GstPipeline.Build(
            new EncodeRequest("/tmp/in.mp4", "/tmp/out.mp4", EncodePreset.Lecture),
            new EncoderChoice("x264enc ! fakesink", EncoderKind.Software)));

        var badRung = new HlsRung { Name = "720p", Width = 1280, Height = 720, Maxrate = 2_500_000, Bufsize = 4_000_000, AudioBitrate = 128_000, Profile = "high ! fakesink", SegmentSeconds = 4 };
        var info = new MediaSourceInfo { Width = 1280, Height = 720, Fps = 30, HasVideo = true, HasAudio = true };
        Assert.Throws<ArgumentException>(() => RungPipeline.Build("/tmp/in.mp4", "/tmp/out.mp4", badRung, info));
    }

    [Fact]
    public void CsvWriter_neutralizes_leading_whitespace_and_newline_formula_injection()
    {
        Assert.Equal("' =CMD|' /C calc'!A0", CsvWriter.Field(" =CMD|' /C calc'!A0"));
        Assert.Equal("\"'\n=1+1\"", CsvWriter.Field("\n=1+1"));
        Assert.Equal("-42.5", CsvWriter.Field("-42.5"));
    }

    [Fact]
    public async Task StudioHttpClient_refuses_absolute_and_cross_origin_paths()
    {
        var handler = new CountingHandler();
        using var client = new StudioHttpClient(new Uri("https://videos.api.example.net"), _ => Task.FromResult("secret-tok"), "Test/1.0", handler: handler);

        foreach (var bad in new[] { "https://evil.example/steal", "//evil.example/steal", "/api/x\\y", "/api/x\r\n" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => client.SendJsonAsync(HttpMethod.Get, bad));
        }

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task LoopbackRedirect_rejects_prefix_spoof_and_non_get_verbs()
    {
        using var redirect = new LoopbackRedirect("ok");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waitTask = redirect.WaitAsync(cts.Token);

        var spoofResp = await RawRequestAsync(redirect.Port, "GET /auth/callback-evil?code=bad HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 404", spoofResp);

        var postResp = await RawRequestAsync(redirect.Port, "POST /auth/callback?code=bad HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 404", postResp);

        var okResp = await RawRequestAsync(redirect.Port, "GET /auth/callback?code=good&state=s1 HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200", okResp);
        var url = await waitTask;
        Assert.EndsWith("/auth/callback?code=good&state=s1", url);
    }

    private static async Task<string> RawRequestAsync(int port, string payload)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var bytes = Encoding.ASCII.GetBytes(payload);
        await stream.WriteAsync(bytes);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
