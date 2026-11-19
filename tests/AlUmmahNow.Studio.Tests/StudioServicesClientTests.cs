using System.Text.Json;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Studio;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

public sealed class StudioServicesClientTests : IDisposable
{
    private readonly FakeHttpServer _server = new();
    private readonly StudioHttpClient _http;
    private readonly StudioServicesClient _api;
    private int _tokens;

    public StudioServicesClientTests()
    {
        _http = new StudioHttpClient(new Uri(_server.BaseUri, "v1"), _ => Task.FromResult("tok" + Interlocked.Increment(ref _tokens)), "AlUmmahNowStudio/test", "ar");
        _api = new StudioServicesClient(_http);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    [Fact]
    public async Task ListsDraftsWithClaimsAndSendsTokenAndChannel()
    {
        _server.On("GET /v1/api/videos/v1/captions/drafts", 200, """
            {"success":true,"data":{"drafts":[{"language":"ar","version":3,"origin":"on_device","edited":true,"cues":12,"pendingQuran":2,"updatedAt":"2026-11-14T10:00:00Z","publishedVersion":2,"track":"machine"}],
             "claims":[{"language":"en","status":"failed","error":"no_speech"}],"transcriptAvailable":true}}
            """);
        var list = await _api.DraftsAsync("v1", "c1");
        var d = Assert.Single(list.Drafts);
        Assert.Equal(CaptionOrigin.OnDevice, d.Origin);
        Assert.Equal(12, d.CueCount);
        Assert.True(d.IsOutdated);
        Assert.Equal(CaptionTrack.Machine, d.Track);
        Assert.Equal("studio.captions.origin.on_device", CaptionJson.OriginKey(d.Origin));
        var claim = Assert.Single(list.Claims);
        Assert.Equal(CaptionClaimStatus.Failed, claim.Status);
        Assert.True(list.TranscriptAvailable);
        var seen = Assert.Single(_server.Requests);
        Assert.Equal("Bearer tok1", seen.Headers["Authorization"]);
        Assert.Equal("c1", seen.Headers["channelId"]);
        Assert.Equal("ar", seen.Headers["Accept-Language"]);
    }

    [Fact]
    public async Task DraftKeepsQuranSegmentsExactlyWhenSaving()
    {
        _server.On("GET /v1/api/videos/v1/captions/drafts/ar", 200, """
            {"success":true,"data":{"language":"ar","version":4,"cueCount":1,"cues":[{"id":"c1","startMs":0,"endMs":2000,"segments":[
              {"type":"speech","text":"قال الله"},{"type":"quran","range":"2:255","status":"suggested","alternatives":["2:256"],"heard":"الله لا"}]}]}}
            """);
        _server.On("PUT /v1/api/videos/v1/captions/drafts/ar", 200, """{"success":true,"data":{"language":"ar","version":5,"cues":1}}""");
        var draft = await _api.DraftAsync("v1", "ar", null);
        Assert.Equal(4, draft.Summary.Version);
        var cue = Assert.Single(draft.Cues);
        Assert.True(cue.HasQuran);
        Assert.Equal("2:255", cue.Segments[1].QuranRange);
        var edited = CueEditing.SetText(draft.Cues, 0, 0, "  قال تعالى ");
        var saved = await _api.SaveDraftAsync("v1", "ar", 4, edited, null);
        Assert.Equal(5, saved.Version);
        using var body = JsonDocument.Parse(_server.Calls("PUT /v1/api/videos/v1/captions/drafts/ar")[0].Text);
        Assert.Equal(4, body.RootElement.GetProperty("baseVersion").GetInt32());
        var segs = body.RootElement.GetProperty("cues")[0].GetProperty("segments");
        Assert.Equal("قال تعالى", segs[0].GetProperty("text").GetString());
        Assert.Equal("suggested", segs[1].GetProperty("status").GetString());
        Assert.Equal("الله لا", segs[1].GetProperty("heard").GetString());
        Assert.Equal("2:256", segs[1].GetProperty("alternatives")[0].GetString());
    }

    [Fact]
    public async Task OnDeviceTranscriptBodyAndConflicts()
    {
        var calls = 0;
        _server.Routes["POST /v1/api/videos/v1/captions/drafts/ar/transcript"] = s => ++calls == 1
            ? FakeHttpServer.Answer.Json(409, """{"success":false,"error":"draft_exists"}""")
            : FakeHttpServer.Answer.Json(202, """{"success":true,"data":{"status":"queued","segments":2}}""");
        var segments = new[] { new TranscriptSegment(0, 1500, "بسم الله"), new TranscriptSegment(1500, 3000, "الحمد لله") };
        var ex = await Assert.ThrowsAsync<StudioApiException>(() => _api.SubmitTranscriptAsync("v1", "ar", new OnDeviceTranscript(segments, "base-q5_1"), "c1"));
        Assert.Equal(409, ex.Status);
        Assert.Equal("draft_exists", ex.Code);
        var count = await _api.SubmitTranscriptAsync("v1", "ar", new OnDeviceTranscript(segments, "base-q5_1", replace: true), "c1");
        Assert.Equal(2, count);
        var sent = _server.Calls("POST /v1/api/videos/v1/captions/drafts/ar/transcript");
        using var first = JsonDocument.Parse(sent[0].Text);
        Assert.False(first.RootElement.TryGetProperty("replace", out _));
        Assert.Equal("whisper.cpp", first.RootElement.GetProperty("engine").GetProperty("provider").GetString());
        Assert.Equal("base-q5_1", first.RootElement.GetProperty("engine").GetProperty("model").GetString());
        Assert.Equal("الحمد لله", first.RootElement.GetProperty("segments")[1].GetProperty("text").GetString());
        using var second = JsonDocument.Parse(sent[1].Text);
        Assert.True(second.RootElement.GetProperty("replace").GetBoolean());
    }

    [Fact]
    public async Task InvalidTranscriptCarriesReasonAndSegment()
    {
        _server.On("POST /v1/api/videos/v1/captions/drafts/en/transcript", 400, """{"success":false,"error":"invalid_transcript","reason":"overlap","segment":3}""");
        var ex = await Assert.ThrowsAsync<StudioApiException>(() => _api.SubmitTranscriptAsync("v1", "en", new OnDeviceTranscript(new[] { new TranscriptSegment(0, 1, "x") }, null), null));
        Assert.Equal("invalid_transcript", ex.Code);
        Assert.Equal("overlap", ex.Reason);
        Assert.Equal(3, ex.Segment);
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task JobsMinutesPublishAndDelete()
    {
        _server.On("POST /v1/api/videos/v1/captions/jobs", 202, """{"success":true,"data":{"job":{"id":"j1","language":"ar","status":"transcribing","progress":{"done":3,"total":6},"minutesReserved":4}}}""");
        _server.On("GET /v1/api/videos/v1/captions/minutes", 200, """{"success":true,"data":{"usage":{"unlimited":false,"via":"tier","monthLimit":60,"monthUsed":20,"remaining":40},"minutesNeeded":4}}""");
        _server.On("POST /v1/api/videos/v1/captions/drafts/ar/publish", 200, """{"success":true,"data":{"draft":{"language":"ar","version":5,"publishedVersion":5,"cues":3},"track":{}}}""");
        _server.On("DELETE /v1/api/videos/v1/captions/drafts/ar", 200, """{"success":true}""");
        var job = await _api.StartJobAsync("v1", "ar", false, null);
        Assert.Equal(CaptionJobStatus.Transcribing, job.Status);
        Assert.Equal(0.5, job.Fraction);
        Assert.True(job.IsActive);
        var minutes = await _api.MinutesAsync("v1", null);
        Assert.True(minutes.CanStart);
        Assert.Equal(4, minutes.MinutesNeeded);
        var published = await _api.PublishDraftAsync("v1", "ar", 5, false, null);
        Assert.True(published.IsPublished);
        Assert.False(published.IsOutdated);
        await _api.DeleteDraftAsync("v1", "ar", null);
        Assert.Single(_server.Calls("DELETE /v1/api/videos/v1/captions/drafts/ar"));
    }

    [Fact]
    public async Task PathSegmentsCannotEscapeTheRoute()
    {
        await Assert.ThrowsAsync<StudioApiException>(() => _api.DraftsAsync("../../admin", null));
        Assert.Equal("/v1/api/videos/_invalid_/captions/drafts", Assert.Single(_server.Requests).Path);
        Assert.Equal("_invalid_", StudioHttpClient.Segment("a/b"));
        Assert.Equal("_invalid_", StudioHttpClient.Segment(".."));
        Assert.Equal("_invalid_", StudioHttpClient.Segment("مرئي"));
        Assert.Equal("ab-1_2.x", StudioHttpClient.Segment("ab-1_2.x"));
    }

    [Fact]
    public async Task RefreshesTheTokenOnceOn401ThenAsksToSignIn()
    {
        _server.Routes["GET /v1/api/studio/brand-kit"] = s => s.Headers["Authorization"] == "Bearer tok2"
            ? FakeHttpServer.Answer.Json(200, """{"success":true,"data":{"kit":{"name":{"ar":"قناتي"},"colors":{"primary":"#112233","secondary":"#445566","text":"#ffffff","background":"#000000"},"logo":"a1","lowerThird":{"style":"box","position":"end"},"watermark":"top-end"},"saved":true,"canEdit":false}}""")
            : FakeHttpServer.Answer.Json(401, "{}");
        var state = await _api.BrandKitAsync("c9");
        Assert.False(state.CanEdit);
        Assert.Equal("قناتي", state.Kit.DisplayName("en"));
        Assert.Equal(LowerThirdStyle.Box, state.Kit.LowerThirdStyle);
        Assert.Equal(WatermarkCorner.TopEnd, state.Kit.Watermark);
        _server.Routes["GET /v1/api/studio/assets"] = _ => FakeHttpServer.Answer.Json(401, "{}");
        await Assert.ThrowsAsync<AuthRequiredException>(() => _api.AssetsAsync(null));
    }

    [Fact]
    public async Task SavesTheBrandKitAndReportsForbidden()
    {
        _server.Routes["PUT /v1/api/studio/brand-kit"] = s => FakeHttpServer.Answer.Json(200, "{\"success\":true,\"data\":{\"kit\":" + s.Text + "}}");
        var kit = new BrandKit { NameEn = " My channel ", NameAr = "", Primary = "#ABCDEF", Logo = "a1", LowerThirdPosition = LowerThirdPosition.Center, Watermark = WatermarkCorner.BottomStart };
        Assert.Null(kit.Invalid());
        var saved = await _api.SaveBrandKitAsync(kit);
        Assert.Equal("My channel", saved.NameEn);
        using var body = JsonDocument.Parse(_server.Calls("PUT /v1/api/studio/brand-kit")[0].Text);
        Assert.False(body.RootElement.GetProperty("name").TryGetProperty("ar", out _));
        Assert.Equal("center", body.RootElement.GetProperty("lowerThird").GetProperty("position").GetString());
        Assert.Equal("bottom-start", body.RootElement.GetProperty("watermark").GetString());
        _server.On("PUT /v1/api/studio/brand-kit", 403, """{"success":false,"error":"forbidden"}""");
        var ex = await Assert.ThrowsAsync<StudioApiException>(() => _api.SaveBrandKitAsync(kit));
        Assert.Equal(403, ex.Status);
        Assert.Equal("colors.primary", new BrandKit { Primary = "#12345" }.Invalid());
    }

    [Fact]
    public async Task AssetsRawUploadDownloadAndLimits()
    {
        _server.On("GET /v1/api/studio/assets", 200, """{"success":true,"data":{"assets":[{"id":"a1","kind":"video","name":"Intro","contentType":"video/mp4","bytes":1000,"createdAt":"2026-11-14T00:00:00Z"}],"bytes":1000,"limits":{"maxAssets":1,"maxAssetBytes":5000,"maxTotalBytes":9000},"canEdit":true}}""");
        _server.On("POST /v1/api/studio/assets", 201, """{"success":true,"data":{"id":"a2","kind":"image","name":"شعار","contentType":"image/png","bytes":4}}""");
        _server.Routes["GET /v1/api/studio/assets/a2"] = _ => new FakeHttpServer.Answer(200, "PNG!", "image/png");
        var lib = await _api.AssetsAsync(null);
        Assert.Equal(StudioAssetKind.Video, Assert.Single(lib.Assets).Kind);
        Assert.Equal("library_full", lib.Refusal(10));
        Assert.Equal("too_large", lib.Refusal(6000));
        Assert.Equal("empty", lib.Refusal(0));
        var added = await _api.AddAssetAsync("شعار", new byte[] { 0x89, 0x50, 0x4e, 0x47 });
        Assert.Equal("a2", added.Id);
        var post = _server.Calls("POST /v1/api/studio/assets")[0];
        Assert.Equal(4, post.Body.Length);
        Assert.Equal(4L, post.ContentLength);
        Assert.Contains("name=%D8%B4%D8%B9%D8%A7%D8%B1", post.Query, StringComparison.Ordinal);
        Assert.Equal("PNG!", System.Text.Encoding.ASCII.GetString(await _api.AssetDataAsync("a2", null)));
    }

    [Fact]
    public async Task AnalyticsSummaryDailyVideoAndRetention()
    {
        _server.On("GET /v1/api/analytics/summary", 200, """{"totalViews":1200,"totalVideos":4,"avgViewsPerVideo":300,"engagementRate":5.5}""");
        _server.On("GET /v1/api/analytics/metrics", 200, """[{"date":"2026-11-02","views":5,"watchTime":120},{"date":"2026-11-01","views":3}]""");
        _server.On("GET /v1/api/analytics/videos/v1", 200, """{"videoId":"v1","title":"مرئي","views":90,"rangeViews":10,"avgViewDuration":42,"recentMetrics":[{"date":"2026-11-01","views":10}]}""");
        _server.On("GET /v1/api/analytics/videos/v1/retention", 200, """{"starts":40,"points":[{"bucket":1,"share":0.8},{"bucket":0,"share":1}]}""");
        var from = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(7);
        var summary = await _api.SummaryAsync("c1", from, to);
        Assert.Equal(1200, summary.TotalViews);
        Assert.Equal(300, summary.AverageViewsPerVideo);
        var days = await _api.DailyAsync("c1", from, to);
        Assert.Equal(new DateTime(2026, 11, 1), days[0].Day);
        Assert.Equal(120, days[1].WatchTimeSeconds);
        var q = _server.Calls("GET /v1/api/analytics/metrics")[0].Query;
        Assert.Contains("dateFrom=2026-11-01T00%3A00%3A00Z", q, StringComparison.Ordinal);
        Assert.Contains("channelId=c1", q, StringComparison.Ordinal);
        var video = await _api.VideoAsync("v1", from, to);
        Assert.Equal(10, video.PeriodViews);
        Assert.Single(video.Daily);
        var curve = await _api.RetentionAsync("v1", from, to);
        Assert.Equal(0, curve.Points![0].Bucket);
        Assert.Equal(5, curve.Points[1].Percent);
        _server.On("GET /v1/api/analytics/videos/v1/retention", 200, """{"starts":3,"points":null}""");
        Assert.Null((await _api.RetentionAsync("v1", from, to)).Points);
    }

    [Fact]
    public async Task RenditionsDeclarePutCompleteCancel()
    {
        _server.On("POST /v1/api/videos/v1/renditions", 201, """{"success":true,"data":{"stagingId":"s1","status":"uploading","maxFileBytes":104857600}}""");
        _server.On("PUT /v1/api/videos/v1/renditions/s1/files/360p/seg_00001.m4s", 200, """{"success":true}""");
        _server.On("POST /v1/api/videos/v1/renditions/s1/complete", 409, """{"success":false,"error":"files_missing","missing":["360p/index.m3u8"]}""");
        _server.On("DELETE /v1/api/videos/v1/renditions/s1", 200, """{"success":true}""");
        var files = new[] { new StagedFile("360p/index.m3u8", 10), new StagedFile("360p/seg_00001.m4s", 3) };
        var staging = await _api.CreateAsync("v1", new[] { "360p" }, files, "Al Ummah Now Studio test", "c1");
        Assert.Equal("s1", staging.StagingId);
        Assert.Equal(104857600, staging.MaxFileBytes);
        using (var body = JsonDocument.Parse(_server.Calls("POST /v1/api/videos/v1/renditions")[0].Text))
        {
            Assert.Equal("360p", body.RootElement.GetProperty("rungs")[0].GetString());
            Assert.Equal(10, body.RootElement.GetProperty("files")[0].GetProperty("bytes").GetInt64());
            Assert.Equal("Al Ummah Now Studio test", body.RootElement.GetProperty("encoder").GetString());
        }

        await _api.PutFileAsync("v1", "s1", files[1], new byte[] { 1, 2, 3 }, "c1");
        var put = _server.Calls("PUT /v1/api/videos/v1/renditions/s1/files/360p/seg_00001.m4s")[0];
        Assert.Equal(3L, put.ContentLength);
        Assert.Equal("application/octet-stream", put.Headers["Content-Type"]);
        await Assert.ThrowsAsync<ArgumentException>(() => _api.PutFileAsync("v1", "s1", files[1], new byte[] { 1 }, "c1"));
        var ex = await Assert.ThrowsAsync<StudioApiException>(() => _api.CompleteAsync("v1", "s1", "c1"));
        Assert.Equal("files_missing", ex.Code);
        Assert.Equal(new[] { "360p/index.m3u8" }, ex.Missing);
        await _api.CancelAsync("v1", "s1", "c1");
    }

    [Fact]
    public async Task ServerErrorsAreTransientAndRateLimitsCarryRetryAfter()
    {
        _server.On("GET /v1/api/videos/v1/captions/jobs", 503, "<html>busy</html>");
        var ex = await Assert.ThrowsAsync<StudioApiException>(() => _api.JobsAsync("v1", null));
        Assert.True(ex.IsTransient);
        Assert.Null(ex.Code);
        _server.On("GET /v1/api/videos/v1/captions/jobs", 429, "{}");
        await Assert.ThrowsAsync<RateLimitedException>(() => _api.JobsAsync("v1", null));
    }

    [Fact]
    public async Task UnreachableServerIsANetworkError()
    {
        var port = new FakeHttpServer();
        var uri = port.BaseUri;
        port.Dispose();
        using var http = new StudioHttpClient(uri, _ => Task.FromResult("t"), "test");
        var ex = await Assert.ThrowsAsync<StudioApiException>(() => new StudioServicesClient(http).JobsAsync("v1", null));
        Assert.True(ex.IsNetwork);
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task FeatureOffBodyIsReadFromCode()
    {
        // Gated routes answer { success: false, code: "feature_off" } (owner rule 2026-11-16): hide, no text.
        _server.On("GET /v1/api/videos/v1/captions/jobs", 404, """{"success":false,"code":"feature_off"}""");
        var ex = await Assert.ThrowsAsync<StudioApiException>(() => _api.JobsAsync("v1", "c1"));
        Assert.Equal("feature_off", ex.Code);
        Assert.True(ex.IsFeatureOff);
        Assert.True(ex.HidesFeature);
    }
}
