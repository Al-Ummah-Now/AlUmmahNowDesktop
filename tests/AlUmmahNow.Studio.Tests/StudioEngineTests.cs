using System.Text;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Encode;
using AlUmmahNow.Studio.Engines;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

public sealed class CueEditingTests
{
    private static CaptionCue Cue(string id, int start, int end, params CaptionSegment[] segs) => new(id, start, end, segs);

    private static readonly CaptionSegment Q = CaptionSegment.Quran("{\"type\":\"quran\",\"range\":\"1:1\",\"status\":\"accepted\"}");

    [Fact]
    public void SplitSharesTimeByCharactersAndKeepsQuranWhole()
    {
        var cues = new[] { Cue("a", 1000, 3000, CaptionSegment.Text("abcd efgh"), Q) };
        var split = CueEditing.Split(cues, 0, 0, 4)!;
        Assert.Equal(2, split.Count);
        Assert.Equal("abcd", split[0].SpeechText);
        Assert.Equal(split[0].EndMs, split[1].StartMs);
        Assert.Equal(3000, split[1].EndMs);
        Assert.Same(Q, split[1].Segments[1]);
        Assert.NotEqual("a", split[1].Id);
        // Too short on either side, or nothing before the cut.
        Assert.Null(CueEditing.Split(new[] { Cue("x", 0, 150, CaptionSegment.Text("ab")) }, 0, 0, 1));
        Assert.Null(CueEditing.Split(new[] { Cue("x", 0, 2000, CaptionSegment.Text("ab")) }, 0, 0, 0));
    }

    [Fact]
    public void MergeJoinsSpeechAndRefusesTooLong()
    {
        var cues = new[] { Cue("a", 0, 1000, CaptionSegment.Text("بسم")), Cue("b", 1000, 2000, CaptionSegment.Text("الله")) };
        var merged = CueEditing.MergeWithNext(cues, 0)!;
        Assert.Equal("بسم الله", Assert.Single(merged).SpeechText);
        Assert.Equal(2000, merged[0].EndMs);
        var long1 = new[] { Cue("a", 0, 1000, CaptionSegment.Text("x")), Cue("b", 1000, CueEditing.MaximumMs + 1, CaptionSegment.Text("y")) };
        Assert.Null(CueEditing.MergeWithNext(long1, 0));
        Assert.Null(CueEditing.MergeWithNext(cues, 1));
    }

    [Fact]
    public void NudgeStaysBetweenNeighboursAndMinimum()
    {
        var cues = new[] { Cue("a", 0, 1000, CaptionSegment.Text("x")), Cue("b", 1200, 2000, CaptionSegment.Text("y")) };
        var moved = CueEditing.Nudge(cues, 1, CueEditing.Edge.Start, -500)!;
        Assert.Equal(1000, moved[1].StartMs);
        Assert.Equal(1900, CueEditing.Nudge(cues, 1, CueEditing.Edge.Start, 5000)![1].StartMs);
        Assert.Null(CueEditing.Nudge(cues, 0, CueEditing.Edge.Start, -100));
        Assert.Null(CueEditing.SetTimes(cues, 1, 900, 1500));
        Assert.Equal(1100, CueEditing.SetTimes(cues, 1, 1100, 1500)![1].StartMs);
    }

    [Fact]
    public void ForSavingDropsEmptySpeechAndEmptyCues()
    {
        var cues = new[] { Cue("a", 0, 1000, CaptionSegment.Text("  ")), Cue("b", 1000, 2000, CaptionSegment.Text(" y "), CaptionSegment.Text(""), Q) };
        var saved = CueEditing.ForSaving(cues);
        var only = Assert.Single(saved);
        Assert.Equal(2, only.Segments.Count);
        Assert.Equal("y", only.Segments[0].Speech);
        Assert.Equal(2, CueEditing.Delete(cues, 0).Count + 1);
        Assert.Equal("e3", CueEditing.FreshId(new[] { Cue("e1", 0, 1), Cue("e2", 1, 2) }));
    }
}

public sealed class CsvTests
{
    [Fact]
    public void QuotesDefusesFormulasAndStartsWithBom()
    {
        var csv = new CsvWriter(new[] { "التاريخ", "views" });
        csv.Append(new[] { "a,b", "say \"hi\"" });
        csv.Append(new[] { "=SUM(A1)", "-5" });
        csv.Append(new[] { "-x", " pad" });
        Assert.Equal("التاريخ,views\r\n\"a,b\",\"say \"\"hi\"\"\"\r\n'=SUM(A1),-5\r\n'-x,\" pad\"\r\n", csv.Text);
        var bytes = csv.Bytes;
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        Assert.Equal(csv.Text, Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public void AnalyticsExportsUseIsoDaysAndPlainNumbers()
    {
        var day = new DailyMetric { Day = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc), Views = 12345, WatchTimeSeconds = 90, Likes = 1 };
        var text = AnalyticsCsv.Daily(new[] { day }, new[] { "d", "v", "w", "l", "c", "s" }).Text;
        Assert.Contains("2026-11-03,12345,1.5,1,0,0", text, StringComparison.Ordinal);
        var curve = new RetentionCurve(50, new[] { new RetentionPoint(0, 1), new RetentionPoint(3, 0.4567) });
        Assert.Contains("15,0.46", AnalyticsCsv.Retention(curve, new[] { "p", "s" }).Text, StringComparison.Ordinal);
        Assert.Equal("2026-11-03T05:00:00Z", CsvWriter.Date(new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.FromHours(-5))));
    }
}

public sealed class SnapshotHistoryTests
{
    [Fact]
    public void UndoRedoAmendAndLimit()
    {
        var h = new SnapshotHistory<string>("a", limit: 2);
        h.Record("a");
        Assert.False(h.CanUndo);
        h.Record("b");
        h.Record("c");
        h.Record("d");
        Assert.True(h.Undo());
        Assert.True(h.Undo());
        Assert.False(h.Undo());
        Assert.Equal("b", h.Current);
        Assert.True(h.Redo());
        Assert.Equal("c", h.Current);
        h.Amend("c2");
        Assert.False(h.CanRedo);
        Assert.True(h.Undo());
        Assert.Equal("b", h.Current);
        h.Reset("z");
        Assert.False(h.CanUndo || h.CanRedo);
    }
}

public sealed class RenditionUploaderTests
{
    private sealed class FakeRenditions : IRenditionsApi
    {
        public Queue<Exception?> CreateResults { get; } = new();
        public Queue<Exception?> CompleteResults { get; } = new();
        public Dictionary<string, Queue<Exception?>> PutResults { get; } = new();
        public List<string> Log { get; } = new();

        public Task<RenditionStaging> CreateAsync(string videoId, IReadOnlyList<string> rungs, IReadOnlyList<StagedFile> files, string encoder, string? channelId, CancellationToken cancellationToken = default)
        {
            Log.Add("create");
            if (CreateResults.Count > 0 && CreateResults.Dequeue() is { } e) throw e;
            return Task.FromResult(new RenditionStaging("s1", "uploading", 1000));
        }

        public Task PutFileAsync(string videoId, string stagingId, StagedFile file, byte[] bytes, string? channelId, CancellationToken cancellationToken = default)
        {
            Log.Add("put " + file.Path);
            Assert.Equal(file.Bytes, bytes.Length);
            if (PutResults.TryGetValue(file.Path, out var q) && q.Count > 0 && q.Dequeue() is { } e) throw e;
            return Task.CompletedTask;
        }

        public Task CompleteAsync(string videoId, string stagingId, string? channelId, CancellationToken cancellationToken = default)
        {
            Log.Add("complete");
            if (CompleteResults.Count > 0 && CompleteResults.Dequeue() is { } e) throw e;
            return Task.CompletedTask;
        }

        public Task CancelAsync(string videoId, string stagingId, string? channelId, CancellationToken cancellationToken = default)
        {
            Log.Add("cancel");
            return Task.CompletedTask;
        }
    }

    private static readonly StagedFile[] Files = { new("360p/index.m3u8", 2), new("360p/init.mp4", 3), new("360p/seg_00001.m4s", 4) };

    private static RenditionUploader Make(FakeRenditions api, List<TimeSpan>? pauses = null) => new(api, "v1", "c1", "/root", new[] { "360p" }, Files, "test")
    {
        Read = path => new byte[Files.First(f => path.EndsWith(f.Path.Split('/')[1], StringComparison.Ordinal)).Bytes],
        Pause = (d, _) => { pauses?.Add(d); return Task.CompletedTask; },
    };

    private static StudioApiException Http(int status, string? code = null, params string[] missing) => StudioApiException.FromServer("x", status, code, missing);

    [Fact]
    public async Task DeclaresPutsEveryFileAndCompletes()
    {
        var api = new FakeRenditions();
        var progress = new List<RenditionProgress>();
        var outcome = await Make(api).RunAsync(new SyncProgress<RenditionProgress>(progress.Add));
        Assert.Equal(RenditionOutcomeKind.Ready, outcome.Kind);
        Assert.Equal(new[] { "create", "put 360p/index.m3u8", "put 360p/init.mp4", "put 360p/seg_00001.m4s", "complete" }, api.Log);
        Assert.Equal(1.0, progress[^1].Fraction);
        Assert.Equal(3, progress[^1].FilesDone);
    }

    [Fact]
    public async Task RetriesTransientErrorsWithBackoff()
    {
        var api = new FakeRenditions();
        api.PutResults["360p/init.mp4"] = new Queue<Exception?>(new Exception?[] { Http(503), StudioApiException.Network("off", new IOException()) });
        var pauses = new List<TimeSpan>();
        var outcome = await Make(api, pauses).RunAsync();
        Assert.Equal(RenditionOutcomeKind.Ready, outcome.Kind);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, pauses);
        Assert.Equal(3, api.Log.Count(l => l == "put 360p/init.mp4"));
    }

    [Fact]
    public async Task ResendsMissingFilesThenCompletes()
    {
        var api = new FakeRenditions();
        api.CompleteResults.Enqueue(Http(409, "files_missing", "360p/init.mp4"));
        var outcome = await Make(api).RunAsync();
        Assert.Equal(RenditionOutcomeKind.Ready, outcome.Kind);
        Assert.Equal(new[] { "create", "put 360p/index.m3u8", "put 360p/init.mp4", "put 360p/seg_00001.m4s", "complete", "put 360p/init.mp4", "complete" }, api.Log);
    }

    [Fact]
    public async Task TooLateIsASkipWithoutCancelling()
    {
        var api = new FakeRenditions();
        api.CreateResults.Enqueue(Http(409, "too_late"));
        Assert.Equal(RenditionOutcomeKind.TooLate, (await Make(api).RunAsync()).Kind);
        api.Log.Clear();
        api.CompleteResults.Enqueue(Http(409, "too_late"));
        Assert.Equal(RenditionOutcomeKind.TooLate, (await Make(api).RunAsync()).Kind);
        Assert.DoesNotContain("cancel", api.Log);
    }

    [Fact]
    public async Task FinalFailureDropsTheStagingAndNeverThrows()
    {
        var api = new FakeRenditions();
        api.PutResults["360p/index.m3u8"] = new Queue<Exception?>(new Exception?[] { Http(400, "size_mismatch") });
        var outcome = await Make(api).RunAsync();
        Assert.Equal(RenditionOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("cancel", api.Log[^1]);
        Assert.Equal(1, api.Log.Count(l => l.StartsWith("put", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task OversizedFileFailsBeforeSending()
    {
        var api = new FakeRenditions();
        var big = new RenditionUploader(api, "v1", null, "/r", new[] { "1080p" }, new[] { new StagedFile("1080p/seg_00001.m4s", 5000) }, "t") { Pause = (_, _) => Task.CompletedTask };
        var outcome = await big.RunAsync();
        Assert.Equal(RenditionOutcomeKind.Failed, outcome.Kind);
        Assert.DoesNotContain(api.Log, l => l.StartsWith("put", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelReportsCancelledAndDropsStaging()
    {
        var api = new FakeRenditions();
        using var cts = new CancellationTokenSource();
        var uploader = Make(api);
        uploader.Read = _ => { cts.Cancel(); return new byte[2]; };
        var outcome = await uploader.RunAsync(null, cts.Token);
        Assert.Equal(RenditionOutcomeKind.Cancelled, outcome.Kind);
        Assert.Equal("cancel", api.Log[^1]);
    }
}

/// <summary>An IProgress that reports on the calling thread, so tests see every value in order.</summary>
public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _report;

    public SyncProgress(Action<T> report) => _report = report;

    public void Report(T value) => _report(value);
}

public sealed class ClaimPollerTests
{
    private sealed class FakeCaptions : ICaptionsApi
    {
        public Queue<Func<CaptionDraftList>> Lists { get; } = new();

        public Task<CaptionDraftList> DraftsAsync(string videoId, string? channelId, CancellationToken cancellationToken = default) => Task.FromResult(Lists.Dequeue()());

        public Task<CaptionDraft> DraftAsync(string videoId, string language, string? channelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CaptionDraftSummary> SaveDraftAsync(string videoId, string language, int baseVersion, IReadOnlyList<CaptionCue> cues, string? channelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteDraftAsync(string videoId, string language, string? channelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CaptionDraftSummary> PublishDraftAsync(string videoId, string language, int baseVersion, bool replaceUpload, string? channelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CaptionJob>> JobsAsync(string videoId, string? channelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CaptionJob> StartJobAsync(string videoId, string language, bool replace, string? channelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CaptionJob> CancelJobAsync(string videoId, string jobId, string? channelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CaptionMinutes> MinutesAsync(string videoId, string? channelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> SubmitTranscriptAsync(string videoId, string language, OnDeviceTranscript transcript, string? channelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public void BacksOffTwoFourEightUpToThirty()
    {
        Assert.Equal(new[] { 2, 4, 8, 16, 30, 30 }, Enumerable.Range(0, 6).Select(i => (int)ClaimPoller.Delay(i).TotalSeconds));
    }

    [Fact]
    public async Task WaitsUntilTheClaimClearsAndTheDraftAppears()
    {
        var api = new FakeCaptions();
        api.Lists.Enqueue(() => new CaptionDraftList { Claims = new[] { new CaptionClaim("ar", CaptionClaimStatus.Queued) } });
        api.Lists.Enqueue(() => throw StudioApiException.Network("off", new IOException()));
        api.Lists.Enqueue(() => new CaptionDraftList { Drafts = new[] { new CaptionDraftSummary { Language = "ar", Origin = CaptionOrigin.OnDevice } } });
        var pauses = new List<TimeSpan>();
        var poller = new ClaimPoller(api) { Pause = (d, _) => { pauses.Add(d); return Task.CompletedTask; } };
        var (result, list, _) = await poller.WaitAsync("v1", "ar", null, null);
        Assert.Equal(ClaimResult.Ready, result);
        Assert.Equal(CaptionOrigin.OnDevice, list!.Drafts[0].Origin);
        Assert.Equal(3, pauses.Count);
    }

    [Fact]
    public async Task FailedClaimEndsWithItsError()
    {
        var api = new FakeCaptions();
        api.Lists.Enqueue(() => new CaptionDraftList { Claims = new[] { new CaptionClaim("ar", CaptionClaimStatus.Failed, "no_speech") } });
        var (result, _, error) = await new ClaimPoller(api) { Pause = (_, _) => Task.CompletedTask }.WaitAsync("v1", "ar", null, null);
        Assert.Equal(ClaimResult.Failed, result);
        Assert.Equal("no_speech", error);
    }
}

public sealed class RenditionEncodingTests
{
    private static readonly HlsRung R720 = new() { Name = "720p", Width = 1280, Height = 720, Maxrate = 3_000_000, Bufsize = 6_000_000, AudioBitrate = 128_000, Profile = "high", GopSeconds = 2 };

    [Fact]
    public void RungPipelineFixesKeyframesCapsFpsAndAddsAudio()
    {
        var p = RungPipeline.Build("/in/a b.mov", "/out/720p.mp4", R720, new MediaSourceInfo { Width = 1920, Height = 1080, Fps = 59.94, HasVideo = true, HasAudio = true });
        Assert.Contains("key-int-max=60", p, StringComparison.Ordinal);
        Assert.Contains("scenecut=0:keyint=60:min-keyint=60:vbv-maxrate=3000:vbv-bufsize=6000", p, StringComparison.Ordinal);
        Assert.Contains("bitrate=2400", p, StringComparison.Ordinal);
        Assert.Contains("framerate=30/1", p, StringComparison.Ordinal);
        Assert.Contains("format=I420,width=1280,height=720", p, StringComparison.Ordinal);
        Assert.Contains("video/x-h264,profile=high", p, StringComparison.Ordinal);
        Assert.Contains("audio/x-raw,rate=48000,channels=2 ! avenc_aac bitrate=128000", p, StringComparison.Ordinal);
        Assert.Contains("file:///in/a%20b.mov", p, StringComparison.Ordinal);
    }

    [Fact]
    public void LowFpsSourceKeepsItsRateAndSilentSourceHasNoAudio()
    {
        var p = RungPipeline.Build("/a.mp4", "/o.mp4", R720, new MediaSourceInfo { Width = 1280, Height = 720, Fps = 25, HasVideo = true });
        Assert.DoesNotContain("videorate", p, StringComparison.Ordinal);
        Assert.Contains("key-int-max=50", p, StringComparison.Ordinal);
        Assert.DoesNotContain("avenc_aac", p, StringComparison.Ordinal);
        Assert.Equal(30, R720.OutputFps(60));
        Assert.Equal(31, R720.OutputFps(31));
        var ntsc = RungPipeline.Build("/a.mp4", "/o.mp4", new HlsRung { Name = "720p", Width = 1280, Height = 720, MaxFps = 29.97 }, new MediaSourceInfo { Width = 1280, Height = 720, Fps = 59.94, HasVideo = true });
        Assert.Contains("framerate=30000/1001", ntsc, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsCoreProbeJson()
    {
        var info = MediaSourceInfo.FromProbeJson("""{"container":"mp4","duration_ms":61000,"tracks":[{"kind":"audio","codec":"aac"},{"kind":"video","codec":"h264","width":1920,"height":1080,"fps":29.97}]}""");
        Assert.True(info.HasAudio && info.HasVideo);
        Assert.Equal(1920, info.Width);
        Assert.Equal(29.97, info.Fps);
        Assert.Equal(61000, info.DurationMs);
    }

    private sealed class FakeProbe : IMediaProbe
    {
        public Task<MediaSourceInfo> ProbeAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new MediaSourceInfo { Width = 1280, Height = 720, Fps = 30, HasVideo = true, HasAudio = true });
    }

    private sealed class FakeEncoder : IRungEncoder
    {
        public List<string> Rungs { get; } = new();
        public bool Fail { get; set; }

        public Task EncodeAsync(string source, string output, HlsRung rung, MediaSourceInfo info, IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (Fail) throw new InvalidOperationException("encoder missing");
            Rungs.Add(rung.Name);
            File.WriteAllBytes(output, new byte[] { 1 });
            progress.Report(1);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePackaging : IRenditionPackaging
    {
        public IReadOnlyList<HlsRung> Ladder(int width, int height, int rotation) => new[] { new HlsRung { Name = "360p" }, R720 };

        public Task<HlsPackage> PackageAsync(string mp4, string outputDirectory, double segmentSeconds, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllBytes(Path.Combine(outputDirectory, "index.m3u8"), new byte[] { 1, 2 });
            return Task.FromResult(new HlsPackage(new[] { new StagedFile("index.m3u8", 2) }, 1000));
        }

        public IReadOnlyList<StagedFile> Files(string root, IReadOnlyList<string> rungs) =>
            rungs.Select(r => new StagedFile(r + "/index.m3u8", new FileInfo(Path.Combine(root, r, "index.m3u8")).Length)).ToList();
    }

    private sealed class RecordingApi : IRenditionsApi
    {
        public List<string> Puts { get; } = new();
        public Task<RenditionStaging> CreateAsync(string videoId, IReadOnlyList<string> rungs, IReadOnlyList<StagedFile> files, string encoder, string? channelId, CancellationToken cancellationToken = default) => Task.FromResult(new RenditionStaging("s"));
        public Task PutFileAsync(string videoId, string stagingId, StagedFile file, byte[] bytes, string? channelId, CancellationToken cancellationToken = default) { Puts.Add(file.Path); return Task.CompletedTask; }
        public Task CompleteAsync(string videoId, string stagingId, string? channelId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CancelAsync(string videoId, string stagingId, string? channelId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task LocalJobEncodesEachRungPackagesAndUploads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-rend-" + Guid.NewGuid().ToString("N"));
        try
        {
            var encoder = new FakeEncoder();
            var api = new RecordingApi();
            var job = new LocalRenditionJob(new FakeProbe(), encoder, new FakePackaging(), api);
            var outcome = await job.RunAsync("/src.mp4", "v1", "c1", dir);
            Assert.Equal(RenditionOutcomeKind.Ready, outcome.Kind);
            Assert.Equal(new[] { "360p", "720p" }, encoder.Rungs);
            Assert.Equal(new[] { "360p/index.m3u8", "720p/index.m3u8" }, api.Puts);
            Assert.False(File.Exists(Path.Combine(dir, "360p.mp4")));
            encoder.Fail = true;
            var failed = await job.RunAsync("/src.mp4", "v1", "c1", dir);
            Assert.Equal(RenditionOutcomeKind.Failed, failed.Kind);
            Assert.Equal("encoder missing", failed.Detail);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void EdlOpsAreTaggedJson()
    {
        Assert.Equal("{\"op\":\"split\",\"clipId\":\"c1\",\"atMs\":1500,\"newId\":\"c2\"}", EdlOp.Split("c1", 1500, "c2"));
        Assert.Equal("{\"op\":\"rippleDeleteRange\",\"startMs\":0,\"endMs\":10}", EdlOp.RippleDeleteRange(0, 10));
        Assert.Equal("{\"op\":\"append\",\"trackId\":\"v\",\"clip\":{\"id\":\"c\",\"sourceId\":\"s\",\"inMs\":0,\"outMs\":5}}", EdlOp.Append("v", "c", "s", 0, 5));
        var flat = FlattenedTimeline.Parse("""{"durationMs":4000,"tracks":[{"id":"v1","kind":"video","clips":[{"clipId":"a","sourceId":"s","path":"/a.mp4","inMs":0,"outMs":4000,"timelineStartMs":0,"speed":2}]}]}""");
        Assert.Equal(2000, flat.Tracks[0].Clips[0].TimelineDurationMs);
        Assert.Equal(1, flat.Tracks[0].Clips[0].Volume);
    }
}
