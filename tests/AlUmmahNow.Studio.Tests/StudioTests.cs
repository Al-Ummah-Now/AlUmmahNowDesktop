using AlUmmahNow.Studio.Encode;
using AlUmmahNow.Studio.Timeline;
using AlUmmahNow.Studio.Upload;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

public sealed class FolderWatchTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void FolderWatch_waits_for_stable_size_and_filters_types()
    {
        var w = new FolderWatch(_dir);
        var video = Path.Combine(_dir, "a.mp4");
        File.WriteAllBytes(video, new byte[5]);
        File.WriteAllBytes(Path.Combine(_dir, "notes.txt"), new byte[5]);
        Assert.Empty(w.Scan());
        File.WriteAllBytes(video, new byte[9]);
        Assert.Empty(w.Scan());
        Assert.Equal((video, 9L), Assert.Single(w.Scan()));
        Assert.Empty(w.Scan());
    }
}

public class EncodeTests
{
    private sealed class Backend : IEncodeBackend
    {
        public HashSet<string> Failing = new();
        public List<string> Tried = new();
        public IReadOnlyList<EncoderChoice> Encoders = new[] { new EncoderChoice("hw", EncoderKind.Hardware), new EncoderChoice("sw", EncoderKind.Software) };
        public TaskCompletionSource? Gate;
        public IReadOnlyList<EncoderChoice> AvailableEncoders() => Encoders;

        public async Task EncodeAsync(EncodeRequest r, EncoderChoice e, IProgress<double> p, CancellationToken ct)
        {
            Tried.Add(e.Name);
            if (Gate != null) await Gate.Task.WaitAsync(ct);
            if (Failing.Contains(e.Name)) throw new InvalidOperationException(e.Name + " failed");
            p.Report(0.5);
        }
    }

    private static EncodeRequest R => new("in.mp4", "out.mp4", EncodePreset.Lecture);

    [Fact]
    public async Task Hardware_failure_falls_back_to_software()
    {
        var b = new Backend { Failing = { "hw" } };
        var q = new EncodeQueue(b);
        var job = q.Add(R);
        await q.WhenIdleAsync();
        Assert.Equal(EncodeStatus.Completed, job.Status);
        Assert.Equal("sw", job.UsedEncoder!.Name);
        Assert.Equal(new[] { "hw", "sw" }, b.Tried);
    }

    [Fact]
    public async Task All_encoders_failing_reports_failure()
    {
        var q = new EncodeQueue(new Backend { Failing = { "hw", "sw" } });
        var job = q.Add(R);
        await q.WhenIdleAsync();
        Assert.Equal(EncodeStatus.Failed, job.Status);
        Assert.Equal("sw failed", job.Error);
    }

    [Fact]
    public async Task No_encoders_is_a_failure()
    {
        var q = new EncodeQueue(new Backend { Encoders = Array.Empty<EncoderChoice>() });
        var job = q.Add(R);
        await q.WhenIdleAsync();
        Assert.Equal(EncodeStatus.Failed, job.Status);
    }

    [Fact]
    public async Task Cancel_does_not_try_the_fallback()
    {
        var b = new Backend { Gate = new TaskCompletionSource() };
        var q = new EncodeQueue(b);
        var job = q.Add(R);
        while (job.Status != EncodeStatus.Running) await Task.Delay(5);
        q.CancelCurrent();
        await q.WhenIdleAsync();
        Assert.Equal(EncodeStatus.Canceled, job.Status);
        Assert.Single(b.Tried);
    }

    [Fact]
    public async Task Pause_holds_queued_jobs_until_resume()
    {
        var q = new EncodeQueue(new Backend());
        q.Pause();
        var job = q.Add(R);
        await q.WhenIdleAsync();
        Assert.Equal(EncodeStatus.Queued, job.Status);
        q.Resume();
        await q.WhenIdleAsync();
        Assert.Equal(EncodeStatus.Completed, job.Status);
    }

    [Fact]
    public void Pipeline_uses_the_chosen_encoder_and_quotes_paths()
    {
        var req = new EncodeRequest("/tmp/my clip.mov", "/tmp/out.mp4", EncodePreset.Lecture);
        var p = GstPipeline.Build(req, new EncoderChoice("x264enc", EncoderKind.Software));
        Assert.Contains("x264enc bitrate=4000", p);
        Assert.Contains("format=I420", p);
        Assert.Contains("my%20clip.mov", p);
        Assert.Contains("location=\"/tmp/out.mp4\"", p);
    }

    [Fact]
    public void Audio_only_preset_has_no_video_branch()
    {
        var p = GstPipeline.Build(new EncodeRequest("a.mp4", "b.m4a", EncodePreset.RecitationAudio), GstPipeline.H264Encoders[^1]);
        Assert.DoesNotContain("x264enc", p);
        Assert.Contains("avenc_aac bitrate=192000", p);
    }

    [Fact]
    public void Software_fallback_is_last() => Assert.Equal(EncoderKind.Software, GstPipeline.H264Encoders[^1].Kind);
}

public class TimelineTests
{
    [Theory]
    [InlineData(false, 112)]
    [InlineData(true, 888)]
    public void Viewport_maps_and_mirrors(bool rtl, double expectedX)
    {
        var v = new TimelineViewport(0.01, 1000, rtl, 12);
        Assert.Equal(expectedX, v.ToX(10_000), 6);
        Assert.Equal(10_000, v.ToMs(expectedX));
    }

    [Fact]
    public void Fit_keeps_the_whole_timeline_inside_the_insets()
    {
        var v = TimelineViewport.Fit(60_000, 1000, isRtl: false);
        Assert.Equal(12, v.ToX(0), 6);
        Assert.Equal(988, v.ToX(60_000), 6);
        var rtl = TimelineViewport.Fit(60_000, 1000, isRtl: true);
        Assert.Equal(988, rtl.ToX(0), 6);
    }

    [Theory]
    [InlineData(0, "0:00.0")]
    [InlineData(61_250, "1:01.2")]
    [InlineData(3_723_400, "1:02:03.4")]
    [InlineData(-5, "0:00.0")]
    public void Editor_times_use_latin_digits(long ms, string expected) => Assert.Equal(expected, EditorTime.Format(ms));

    [Fact]
    public void First_video_source_sets_even_export_size_and_capped_rate()
    {
        var json = EdlProject.New("p", "", DateTimeOffset.FromUnixTimeMilliseconds(0));
        var info = new AlUmmahNow.CoreBridge.Studio.MediaSourceInfo { DurationMs = 5000, Width = 1081, Height = 1921, Fps = 120, HasVideo = true };
        json = EdlProject.AddSource(json, "s1", "/m/portrait.mp4", info);
        json = EdlProject.AddSource(json, "s2", "/m/wide.mp4", new AlUmmahNow.CoreBridge.Studio.MediaSourceInfo { DurationMs = 5000, Width = 1920, Height = 1080, Fps = 30, HasVideo = true });
        Assert.Equal(new ProjectSettings(1080, 1920, 60, 48000, 2), EdlProject.Settings(json));
        Assert.Equal(2, EdlProject.SourceCount(json));
        Assert.Equal("portrait", EdlProject.SourceNames(json)["s1"]);
    }

    [Fact]
    public void Rotated_video_swaps_the_export_size()
    {
        var json = EdlProject.New("p", "", DateTimeOffset.FromUnixTimeMilliseconds(0));
        json = EdlProject.AddSource(json, "s1", "/m/phone.mp4", new AlUmmahNow.CoreBridge.Studio.MediaSourceInfo { DurationMs = 5000, Width = 1920, Height = 1080, Rotation = 90, Fps = 30, HasVideo = true });
        var s = EdlProject.Settings(json);
        Assert.Equal((1080, 1920), (s.Width, s.Height));
    }
}
