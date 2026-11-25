using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace AlUmmahNow.Studio.Encode;

public enum EncoderKind { Hardware, Software }

public sealed record EncoderChoice(string Name, EncoderKind Kind);

public sealed record EncodePreset(string Key, int MaxHeight, int VideoKbps, int AudioKbps, bool AudioOnly)
{
    public static readonly EncodePreset Lecture = new("lecture", 1080, 4000, 128, false);
    public static readonly EncodePreset Conference = new("conference", 1080, 6000, 192, false);
    public static readonly EncodePreset RecitationAudio = new("recitation", 0, 0, 192, true);
    public static readonly IReadOnlyList<EncodePreset> All = new[] { Lecture, Conference, RecitationAudio };
}

public sealed record EncodeRequest(string Source, string Output, EncodePreset Preset);

/// <summary>Runs one encode on a platform media stack: GStreamer on Linux, Media Foundation on Windows.</summary>
public interface IEncodeBackend
{
    /// <summary>Encoders usable on this machine, hardware first, ending in the software fallback.</summary>
    IReadOnlyList<EncoderChoice> AvailableEncoders();

    Task EncodeAsync(EncodeRequest request, EncoderChoice encoder, IProgress<double> progress, CancellationToken cancellationToken);
}

public enum EncodeStatus { Queued, Running, Completed, Failed, Canceled }

public sealed class EncodeJob
{
    public EncodeJob(EncodeRequest request) => Request = request;
    public EncodeRequest Request { get; }
    public EncodeStatus Status { get; internal set; }
    public double Progress { get; internal set; }
    public EncoderChoice? UsedEncoder { get; internal set; }
    public string? Error { get; internal set; }

    /// <summary>When the job started running.</summary>
    public DateTimeOffset? StartedAt { get; internal set; }

    /// <summary>
    /// Time left while running, from the pace so far (elapsed × (1 − progress) / progress). Null before
    /// there is enough progress to tell, and once the job ends.
    /// </summary>
    public TimeSpan? Remaining { get; internal set; }
}

/// <summary>Runs jobs one at a time. A failing hardware encoder falls back to the next choice.</summary>
public sealed class EncodeQueue
{
    private readonly IEncodeBackend _backend;
    private readonly Queue<EncodeJob> _pending = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private Task _running = Task.CompletedTask;
    private bool _pumping;
    private bool _paused;

    private readonly Func<DateTimeOffset> _now;

    public EncodeQueue(IEncodeBackend backend, Func<DateTimeOffset>? now = null)
    {
        _backend = backend;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The estimate needs this much progress first (the first moments say little about the pace).</summary>
    public const double EstimateFrom = 0.02;

    public event EventHandler? Changed;

    /// <summary>A job is running, or the next one is about to.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate) return _pumping;
        }
    }

    public List<EncodeJob> Jobs { get; } = new();

    public EncodeJob Add(EncodeRequest request)
    {
        var job = new EncodeJob(request);
        lock (_gate)
        {
            Jobs.Add(job);
            _pending.Enqueue(job);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        Pump();
        return job;
    }

    /// <summary>Holds queued jobs. The running job finishes.</summary>
    public void Pause()
    {
        lock (_gate) _paused = true;
    }

    public void Resume()
    {
        lock (_gate) _paused = false;
        Pump();
    }

    public void CancelCurrent()
    {
        CancellationTokenSource? current;
        lock (_gate) current = _current;
        try
        {
            current?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It has just ended
        }
    }

    /// <summary>Cancels what runs, forgets what waits, and waits until the encoder has let go.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task running;
        lock (_gate)
        {
            _paused = true;
            foreach (var job in _pending) job.Status = EncodeStatus.Canceled;
            _pending.Clear();
            running = _running;
        }

        // What runs was decided under the same lock: it is this one, or none
        CancelCurrent();
        await running.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            lock (_gate)
            {
                if (!_pumping && (_pending.Count == 0 || _paused)) return;
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The next job runs, if none does: from here on it is the running one. Its work is on no thread of the app's: the end waits
    /// for it, with a message loop that may be blocked or gone.
    /// </summary>
    private void Pump()
    {
        lock (_gate)
        {
            if (_pumping || !TakeNext(out var job, out var cancel)) return;
            _pumping = true;
            _running = Task.Run(() => PumpAsync(job, cancel));
        }
    }

    /// <summary>Under the lock.</summary>
    private bool TakeNext([NotNullWhen(true)] out EncodeJob? job, [NotNullWhen(true)] out CancellationTokenSource? cancel)
    {
        job = null;
        cancel = null;
        if (_paused || _pending.Count == 0) return false;
        job = _pending.Dequeue();
        job.Status = EncodeStatus.Running;
        cancel = _current = new CancellationTokenSource();
        return true;
    }

    private async Task PumpAsync(EncodeJob job, CancellationTokenSource cancel)
    {
        while (true)
        {
            try
            {
                await RunAsync(job, cancel.Token).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Whatever it is, it is this job's: the queue goes on, and is never busy for good.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                job.Error = ex.Message;
                job.Status = EncodeStatus.Failed;
            }

            EncodeJob? next;
            CancellationTokenSource? nextCancel;
            lock (_gate)
            {
                _current = null;
                if (!TakeNext(out next, out nextCancel)) _pumping = false;
            }

            cancel.Dispose();
            if (next == null || nextCancel == null) return;
            (job, cancel) = (next, nextCancel);
        }
    }

    private async Task RunAsync(EncodeJob job, CancellationToken cancellationToken)
    {
        job.StartedAt = _now();
        job.Remaining = null;
        Changed?.Invoke(this, EventArgs.Empty);
        var progress = new Progress(p =>
        {
            job.Progress = p;
            job.Remaining = p >= EstimateFrom && p < 1 && job.StartedAt is { } started
                ? TimeSpan.FromTicks((long)((_now() - started).Ticks * (1 - p) / p))
                : null;
            Changed?.Invoke(this, EventArgs.Empty);
        });

        foreach (var encoder in _backend.AvailableEncoders())
        {
            job.UsedEncoder = encoder;
            job.Error = null;
            try
            {
                // Canceled before it began, it does not touch the output
                cancellationToken.ThrowIfCancellationRequested();
                await _backend.EncodeAsync(job.Request, encoder, progress, cancellationToken).ConfigureAwait(false);
                job.Status = EncodeStatus.Completed;
                job.Progress = 1;
                break;
            }
            catch (OperationCanceledException)
            {
                job.Status = EncodeStatus.Canceled;
                break;
            }
#pragma warning disable CA1031 // Any backend failure moves on to the next encoder.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                job.Error = ex.Message;
                job.Status = EncodeStatus.Failed;
            }
        }

        if (job.UsedEncoder == null)
        {
            job.Status = EncodeStatus.Failed;
            job.Error = "No encoder available";
        }

        job.Remaining = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Progress : IProgress<double>
    {
        private readonly Action<double> _report;
        public Progress(Action<double> report) => _report = report;
        public void Report(double value) => _report(value);
    }
}

/// <summary>Builds GStreamer pipelines from a neutral request. Pure, so it is unit tested.</summary>
public static class GstPipeline
{
    /// <summary>H.264 encoders in preference order. The last entry is the software fallback.</summary>
    public static readonly IReadOnlyList<EncoderChoice> H264Encoders = new[]
    {
        new EncoderChoice("nvh264enc", EncoderKind.Hardware),
        new EncoderChoice("vah264enc", EncoderKind.Hardware),
        new EncoderChoice("vaapih264enc", EncoderKind.Hardware),
        new EncoderChoice("x264enc", EncoderKind.Software),
    };

    public static string Build(EncodeRequest request, EncoderChoice encoder)
    {
        var src = Quote(new Uri(Path.GetFullPath(request.Source)).AbsoluteUri);
        var sink = Quote(Path.GetFullPath(request.Output));
        var kbps = request.Preset.AudioKbps * 1000;
        var audio = $"queue ! audioconvert ! audioresample ! avenc_aac bitrate={kbps.ToString(CultureInfo.InvariantCulture)}";
        if (request.Preset.AudioOnly)
        {
            return $"uridecodebin uri={src} name=d d. ! {audio} ! mp4mux ! filesink location={sink}";
        }

        var height = request.Preset.MaxHeight.ToString(CultureInfo.InvariantCulture);
        // Mezzanines must be 4:2:0: without this x264 negotiates 4:4:4, which many decoders refuse.
        var format = encoder.Kind == EncoderKind.Software ? "I420" : "NV12";
        var video = $"queue ! videoconvert ! videoscale ! video/x-raw,format={format},height=[1,{height}] ! {EncoderElement(encoder, request.Preset.VideoKbps)} ! h264parse";
        return $"uridecodebin uri={src} name=d d. ! {video} ! mp4mux name=m ! filesink location={sink} d. ! {audio} ! m.";
    }

    private static string EncoderElement(EncoderChoice encoder, int kbps)
    {
        var rate = kbps.ToString(CultureInfo.InvariantCulture);
        return encoder.Name switch
        {
            "x264enc" => $"x264enc bitrate={rate} speed-preset=veryfast",
            "vaapih264enc" => $"vaapih264enc bitrate={rate}",
            _ => $"{encoder.Name} bitrate={rate}",
        };
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
