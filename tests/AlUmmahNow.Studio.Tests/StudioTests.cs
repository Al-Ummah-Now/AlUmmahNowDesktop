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

        /// <summary>Does not look at being cancelled.</summary>
        public TaskCompletionSource? Stuck { get; init; }

        public TaskCompletionSource HasBegun { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<EncoderChoice> AvailableEncoders() => Encoders;

        public async Task EncodeAsync(EncodeRequest r, EncoderChoice e, IProgress<double> p, CancellationToken ct)
        {
            Tried.Add(e.Name);
            HasBegun.TrySetResult();
            if (Stuck != null) await Stuck.Task;
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
        await b.HasBegun.Task;
        q.CancelCurrent();
        await q.WhenIdleAsync();
        Assert.Equal(EncodeStatus.Canceled, job.Status);
        Assert.Single(b.Tried);
    }

    [Fact]
    public async Task Stopping_cancels_what_runs_forgets_what_waits_and_waits_for_the_encoder()
    {
        var b = new Backend { Gate = new TaskCompletionSource() };
        var q = new EncodeQueue(b);
        var running = q.Add(R);
        var waiting = q.Add(R);
        // Which one runs is known before Add returns
        Assert.Equal((EncodeStatus.Running, EncodeStatus.Queued), (running.Status, waiting.Status));
        Assert.True(q.IsBusy);
        await b.HasBegun.Task;
        await q.StopAsync();
        Assert.False(q.IsBusy);
        Assert.Equal(EncodeStatus.Canceled, running.Status);
        Assert.Equal(EncodeStatus.Canceled, waiting.Status);
        Assert.Single(b.Tried);
        // Nothing begins after it
        Assert.Equal(EncodeStatus.Queued, q.Add(R).Status);
        await q.StopAsync();
        Assert.Single(b.Tried);
    }

    /// <summary>Added on the UI thread, whose message loop is then blocked by the end, or gone. The backend awaits as one does.</summary>
    [Fact]
    public async Task Stopping_needs_no_message_loop()
    {
        var b = new Backend { Gate = new TaskCompletionSource() };
        var q = new EncodeQueue(b);
        EncodeJob job = null!;
        var loop = DeadLoop.Around(() => job = q.Add(R));
        await b.HasBegun.Task;
        await Task.Run(() => q.StopAsync());
        Assert.Equal(0, loop.Posted);
        Assert.Equal(EncodeStatus.Canceled, job.Status);
        Assert.False(q.IsBusy);
    }

    /// <summary>However the end and the queue's own thread meet: nothing runs after it, and nothing is left as running.</summary>
    [Fact]
    public async Task Stopping_right_after_adding_leaves_nothing_running()
    {
        for (var i = 0; i < 200; i++)
        {
            var q = new EncodeQueue(new Backend { Gate = new TaskCompletionSource() });
            var jobs = new[] { q.Add(R), q.Add(R) };
            await q.StopAsync();
            Assert.False(q.IsBusy);
            Assert.All(jobs, j => Assert.Equal(EncodeStatus.Canceled, j.Status));
        }
    }

    /// <summary>A queue that stays busy for good would keep every update, and the end, waiting.</summary>
    [Fact]
    public async Task A_backend_that_cannot_even_list_its_encoders_does_not_keep_the_queue_busy()
    {
        var q = new EncodeQueue(new Backend { Encoders = null! });
        var job = q.Add(R);
        await q.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(EncodeStatus.Failed, job.Status);
        Assert.False(q.IsBusy);
    }

    [Fact]
    public async Task Stopping_an_encoder_that_does_not_let_go_is_bounded()
    {
        var b = new Backend { Stuck = new TaskCompletionSource() };
        var q = new EncodeQueue(b);
        q.Add(R);
        await b.HasBegun.Task;
        using var late = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => q.StopAsync(late.Token));
        Assert.True(q.IsBusy);
        b.Stuck.SetResult();
        await q.StopAsync();
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
    private static (TimelineProject P, Track T) Make()
    {
        var t = new Track { Kind = TrackKind.Video };
        t.Clips.Add(new Clip { Source = "a", Start = 0, Duration = 10 });
        t.Clips.Add(new Clip { Source = "b", Start = 10, Duration = 5 });
        var p = new TimelineProject();
        p.Tracks.Add(t);
        return (p, t);
    }

    [Fact]
    public void Split_divides_time_and_source_offset()
    {
        var (p, t) = Make();
        var right = p.Split(t, 4)!;
        Assert.Equal(4, t.Clips[0].Duration);
        Assert.Equal(4, right.SourceIn);
        Assert.Equal(6, right.Duration);
        Assert.Equal(15, p.Duration);
    }

    [Fact]
    public void Split_on_a_boundary_does_nothing()
    {
        var (p, t) = Make();
        Assert.Null(p.Split(t, 10));
    }

    [Fact]
    public void Ripple_delete_closes_the_gap()
    {
        var (p, t) = Make();
        p.RippleDelete(t, t.Clips[0]);
        Assert.Equal(0, t.Clips[0].Start);
        Assert.Equal(5, p.Duration);
    }

    [Fact]
    public void Trim_start_is_limited_by_available_source()
    {
        var (p, t) = Make();
        var clip = t.Clips[1];
        clip.SourceIn = 2;
        p.TrimStart(clip, 1);
        Assert.Equal(8, clip.Start);
        Assert.Equal(0, clip.SourceIn);
        Assert.Equal(7, clip.Duration);
    }

    [Fact]
    public void Trim_keeps_a_minimum_length()
    {
        var (p, t) = Make();
        p.TrimEnd(t.Clips[0], -5);
        Assert.Equal(TimelineProject.MinClipDuration, t.Clips[0].Duration);
    }

    [Fact]
    public void Json_round_trips()
    {
        var (p, _) = Make();
        var back = TimelineProject.FromJson(p.ToJson());
        Assert.Equal(15, back.Duration);
        Assert.Equal(TrackKind.Video, back.Tracks[0].Kind);
    }

    [Theory]
    [InlineData(false, 100)]
    [InlineData(true, 900)]
    public void Viewport_maps_and_mirrors(bool rtl, double expectedX)
    {
        var v = new TimelineViewport(10, 0, 1000, rtl);
        Assert.Equal(expectedX, v.ToX(10));
        Assert.Equal(10, v.ToTime(expectedX));
    }
}
