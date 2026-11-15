using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Encode;
using AlUmmahNow.Studio.Engines;

namespace AlUmmahNow.Studio.Upload;

/// <summary>State of one upload's optional pre-encode.</summary>
public sealed record PreEncodeStatus(LocalRenditionStage Stage, double Fraction, RenditionOutcomeKind? Outcome)
{
    /// <summary>The i18n key of the queue-row text; running stages take <see cref="Percent"/> as their argument.</summary>
    public string LabelKey => Outcome switch
    {
        RenditionOutcomeKind.Ready => "studio.upload.preEncode.ready",
        RenditionOutcomeKind.TooLate => "studio.upload.preEncode.tooLate",
        RenditionOutcomeKind.Cancelled => "studio.upload.preEncode.cancelled",
        RenditionOutcomeKind.Failed => "studio.upload.preEncode.failed",
        _ => "studio.upload.preEncode." + Stage.ToString().ToLowerInvariant(),
    };

    public long Percent => (long)Math.Round(Math.Max(0, Math.Min(1, Fraction)) * 100);

    public bool IsRunning => Outcome == null;
}

/// <summary>
/// Runs <see cref="LocalRenditionJob"/> for uploads queued with "Pre-encode renditions on this computer", as soon as
/// the upload has its video id. The original upload never waits for it; whatever happens, the server encodes when
/// the renditions aren't complete in time.
/// </summary>
public sealed class PreEncodeCoordinator
{
    private static readonly Logger Logger = Log.For("preencode");

    private readonly Func<LocalRenditionJob> _jobFactory;
    private readonly string _workRoot;
    private readonly Dictionary<string, (string Path, string ChannelId)> _wanted = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = new();
    private readonly Dictionary<string, PreEncodeStatus> _status = new();
    private readonly object _gate = new();

    public PreEncodeCoordinator(Func<LocalRenditionJob> jobFactory, string workRoot)
    {
        _jobFactory = jobFactory;
        _workRoot = workRoot;
    }

    /// <summary>Raised (from any thread) when a status changes.</summary>
    public event EventHandler? Changed;

    public void Want(string uploadId, string filePath, string channelId)
    {
        lock (_gate) _wanted[uploadId] = (filePath, channelId);
    }

    public PreEncodeStatus? StatusOf(string uploadId)
    {
        lock (_gate) return _status.TryGetValue(uploadId, out var s) ? s : null;
    }

    /// <summary>Starts jobs for wanted uploads that now have a video id; cancels jobs of cancelled or failed uploads.</summary>
    public void Observe(IReadOnlyList<UploadSnapshot> uploads)
    {
        lock (_gate)
        {
            foreach (var u in uploads)
            {
                if (u.Phase is UploadPhase.Cancelled or UploadPhase.Failed && _running.TryGetValue(u.Id, out var stop))
                {
                    stop.Cancel();
                    continue;
                }

                if (u.VideoId == null || !_wanted.TryGetValue(u.Id, out var want) || _running.ContainsKey(u.Id) || _status.ContainsKey(u.Id)) continue;
                if (u.Phase is UploadPhase.Done or UploadPhase.Cancelled or UploadPhase.Failed) continue;
                var cts = new CancellationTokenSource();
                _running[u.Id] = cts;
                _status[u.Id] = new PreEncodeStatus(LocalRenditionStage.Probing, 0, null);
                _ = RunAsync(u.Id, want.Path, want.ChannelId, u.VideoId, cts.Token);
            }
        }
    }

    public void Cancel(string uploadId)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(uploadId, out var cts)) cts.Cancel();
        }
    }

    private async Task RunAsync(string uploadId, string path, string channelId, string videoId, CancellationToken ct)
    {
        var dir = Path.Combine(_workRoot, uploadId);
        RenditionOutcome outcome;
        try
        {
            var progress = new Progress(p => Set(uploadId, new PreEncodeStatus(p.Stage, p.Fraction, null)));
            outcome = await Task.Run(() => _jobFactory().RunAsync(path, videoId, channelId, dir, progress, ct), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            outcome = new RenditionOutcome(RenditionOutcomeKind.Failed, ex.Message);
        }

        Logger.Info($"pre-encode {outcome.Kind}{(outcome.Detail == null ? "" : ": " + outcome.Detail)}");
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        lock (_gate)
        {
            if (_running.Remove(uploadId, out var cts)) cts.Dispose();
            _wanted.Remove(uploadId);
        }

        Set(uploadId, new PreEncodeStatus(LocalRenditionStage.Done, 1, outcome.Kind));
    }

    private void Set(string uploadId, PreEncodeStatus status)
    {
        lock (_gate) _status[uploadId] = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Progress : IProgress<LocalRenditionProgress>
    {
        private readonly Action<LocalRenditionProgress> _report;

        public Progress(Action<LocalRenditionProgress> report) => _report = report;

        public void Report(LocalRenditionProgress value) => _report(value);
    }
}
