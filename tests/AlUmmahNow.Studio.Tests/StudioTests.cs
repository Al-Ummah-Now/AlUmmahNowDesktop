using AlUmmahNow.CoreBridge;
using AlUmmahNow.Studio.Encode;
using AlUmmahNow.Studio.Timeline;
using AlUmmahNow.Studio.Upload;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

public sealed class UploadQueueTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;
    private string State => Path.Combine(_dir, "queue.json");

    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class Engine : IUploadEngine
    {
        public int FailuresLeft;
        public int MaxConcurrent, Current;
        public List<long> Offsets = new();
        public TaskCompletionSource? Gate;

        public async Task UploadAsync(UploadRequest r, IProgress<long> p, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref Current);
            lock (Offsets)
            {
                MaxConcurrent = Math.Max(MaxConcurrent, now);
                Offsets.Add(r.Offset);
            }

            try
            {
                p.Report(r.Offset + 1);
                if (Gate != null) await Gate.Task.WaitAsync(ct);
                else await Task.Delay(5, ct);
                if (Interlocked.Decrement(ref FailuresLeft) >= 0) throw new IOException("network");
                p.Report(r.Size);
            }
            finally
            {
                Interlocked.Decrement(ref Current);
            }
        }
    }

    private UploadQueue Make(Engine e, int parallel = 2, int attempts = 3) =>
        new(e, State, parallel, attempts, _ => TimeSpan.Zero);

    [Fact]
    public async Task Completes_within_the_parallel_limit()
    {
        var e = new Engine();
        var q = Make(e);
        for (var i = 0; i < 6; i++) q.Enqueue("f" + i, 10);
        await q.WhenIdleAsync();
        Assert.All(q.Snapshot(), i => Assert.Equal(UploadStatus.Completed, i.Status));
        Assert.InRange(e.MaxConcurrent, 1, 2);
    }

    [Fact]
    public void Duplicate_path_and_size_is_ignored()
    {
        var q = Make(new Engine { Gate = new TaskCompletionSource() });
        Assert.NotNull(q.Enqueue("a", 1));
        Assert.Null(q.Enqueue("a", 1));
    }

    [Fact]
    public async Task Retries_then_succeeds_resuming_from_the_saved_offset()
    {
        var e = new Engine { FailuresLeft = 1 };
        var q = Make(e);
        q.Enqueue("a", 10);
        await q.WhenIdleAsync();
        Assert.Equal(UploadStatus.Completed, q.Snapshot()[0].Status);
        Assert.Equal(new long[] { 0, 1 }, e.Offsets);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts()
    {
        var q = Make(new Engine { FailuresLeft = 99 });
        q.Enqueue("a", 10);
        await q.WhenIdleAsync();
        var item = q.Snapshot()[0];
        Assert.Equal(UploadStatus.Failed, item.Status);
        Assert.Equal("network", item.Error);
    }

    [Fact]
    public async Task Pause_keeps_progress_and_resume_continues()
    {
        var e = new Engine { Gate = new TaskCompletionSource() };
        var q = Make(e);
        var item = q.Enqueue("a", 10)!;
        while (q.Snapshot()[0].BytesSent == 0) await Task.Delay(5);
        q.Pause(item.Id);
        await q.WhenIdleAsync();
        Assert.Equal(UploadStatus.Paused, q.Snapshot()[0].Status);
        e.Gate = null;
        q.Resume(item.Id);
        await q.WhenIdleAsync();
        Assert.Equal(UploadStatus.Completed, q.Snapshot()[0].Status);
        Assert.Equal(1, e.Offsets[^1]);
    }

    [Fact]
    public async Task Interrupted_upload_resumes_after_restart()
    {
        var blocked = new Engine { Gate = new TaskCompletionSource() };
        var first = Make(blocked);
        first.Enqueue("a", 10);
        while (first.Snapshot()[0].BytesSent == 0) await Task.Delay(5);
        // Saved state as if the process died mid-upload.
        var saved = File.ReadAllText(State);
        blocked.Gate.SetCanceled();
        await first.WhenIdleAsync();
        File.WriteAllText(State, saved.Replace("\"BytesSent\":0", "\"BytesSent\":4"));

        var e = new Engine();
        var second = Make(e);
        Assert.Equal(UploadStatus.Queued, second.Snapshot()[0].Status);
        second.Pump();
        await second.WhenIdleAsync();
        Assert.Equal(4, Assert.Single(e.Offsets));
    }

    [Fact]
    public void Corrupt_state_starts_empty()
    {
        File.WriteAllText(State, "{not json");
        Assert.Empty(Make(new Engine()).Snapshot());
    }

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
