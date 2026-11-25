using AlUmmahNow.Studio.Demo;
using AlUmmahNow.Studio.Encode;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

/// <summary>A running encode shows the time left from its pace so far.</summary>
public sealed class EncodeRemainingTests
{
    [Fact]
    public async Task TheDemoQueueHasAFinishedJobAndARunningOneWithTimeLeft()
    {
        var queue = new EncodeQueue(new DemoEncodeBackend("vaapih264enc"), DemoEncodeBackend.Clock());
        var isRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Changed += (_, _) =>
        {
            if (queue.Jobs.Count == 2 && queue.Jobs[1].Remaining != null) isRunning.TrySetResult();
        };
        DemoEncodeBackend.Seed(queue, Path.GetTempPath());
        await isRunning.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var finished = queue.Jobs[0];
        var running = queue.Jobs[1];
        Assert.Equal(EncodeStatus.Completed, finished.Status);
        Assert.Null(finished.Remaining);
        Assert.Equal(EncodeStatus.Running, running.Status);
        Assert.Equal(DemoEncodeBackend.RunningProgress, running.Progress);
        // Four minutes for 62 %: the remaining 38 % takes about 2:27.
        Assert.Equal(147, Math.Round(running.Remaining!.Value.TotalSeconds));
        queue.CancelCurrent();
    }

    [Fact]
    public async Task NoEstimateBeforeThereIsProgressToGoOn()
    {
        var at = DateTimeOffset.UnixEpoch;
        var backend = new StepBackend();
        var queue = new EncodeQueue(backend, () => at);
        var job = queue.Add(new EncodeRequest("a.mp4", "b.mp4", EncodePreset.Lecture));
        await backend.Started.Task;
        at = at.AddSeconds(30);
        backend.Report(0.01);
        Assert.Null(job.Remaining);
        at = at.AddSeconds(30);
        backend.Report(0.5);
        Assert.Equal(TimeSpan.FromSeconds(60), job.Remaining);
        backend.Finish();
        await queue.WhenIdleAsync();
        Assert.Null(job.Remaining);
    }

    private sealed class StepBackend : IEncodeBackend
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IProgress<double>? _progress;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<EncoderChoice> AvailableEncoders() => new[] { new EncoderChoice("x264enc", EncoderKind.Software) };

        public Task EncodeAsync(EncodeRequest request, EncoderChoice encoder, IProgress<double> progress, CancellationToken cancellationToken)
        {
            _progress = progress;
            Started.TrySetResult();
            return _done.Task;
        }

        public void Report(double p) => _progress!.Report(p);

        public void Finish() => _done.TrySetResult();
    }
}
