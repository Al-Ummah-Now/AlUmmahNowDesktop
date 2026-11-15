using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Studio;

namespace AlUmmahNow.Studio.Engines;

public enum RenditionOutcomeKind
{
    /// <summary>The renditions wait for the original upload; the server verifies them before use.</summary>
    Ready,

    /// <summary>The server already started encoding (<c>too_late</c>): nothing to do.</summary>
    TooLate,

    Cancelled,

    /// <summary>Something failed; the server encodes instead. <see cref="RenditionOutcome.Detail"/> is for the log.</summary>
    Failed,
}

public sealed record RenditionOutcome(RenditionOutcomeKind Kind, string? Detail = null);

public sealed record RenditionProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal)
{
    public double Fraction => BytesTotal > 0 ? (double)BytesDone / BytesTotal : 0;
}

/// <summary>
/// Sends renditions encoded on this computer (D11): declare every file, PUT each one, complete. Never throws:
/// whatever goes wrong, the original upload carries on and the server encodes the video as usual.
/// </summary>
public sealed class RenditionUploader
{
    private readonly IRenditionsApi _api;
    private readonly string _videoId;
    private readonly string? _channelId;
    private readonly string _root;
    private readonly IReadOnlyList<string> _rungs;
    private readonly IReadOnlyList<StagedFile> _files;
    private readonly string _encoder;

    public RenditionUploader(IRenditionsApi api, string videoId, string? channelId, string root, IReadOnlyList<string> rungs, IReadOnlyList<StagedFile> files, string encoder)
    {
        _api = api;
        _videoId = videoId;
        _channelId = channelId;
        _root = root;
        _rungs = rungs;
        _files = files;
        _encoder = encoder;
    }

    /// <summary>How a file's bytes are read; tests read from memory.</summary>
    public Func<string, byte[]> Read { get; set; } = File.ReadAllBytes;

    /// <summary>Waits between retries; tests skip it.</summary>
    public Func<TimeSpan, CancellationToken, Task> Pause { get; set; } = Task.Delay;

    /// <summary>Attempts per request for errors worth retrying (offline, 5xx, 429).</summary>
    public int Attempts { get; set; } = 3;

    public async Task<RenditionOutcome> RunAsync(IProgress<RenditionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        RenditionStaging staging;
        try
        {
            staging = await Retrying(ct => _api.CreateAsync(_videoId, _rungs, _files, _encoder, _channelId, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Outcome(ex, cancellationToken);
        }

        var result = await SendAsync(staging, progress, cancellationToken).ConfigureAwait(false);
        if (result.Kind is not RenditionOutcomeKind.Ready and not RenditionOutcomeKind.TooLate)
        {
            // Best effort: the server drops stale staging on its own too.
            try
            {
                await _api.CancelAsync(_videoId, staging.StagingId, _channelId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Nothing to do: the original upload continues either way.
            }
        }

        return result;
    }

    private async Task<RenditionOutcome> SendAsync(RenditionStaging staging, IProgress<RenditionProgress>? progress, CancellationToken ct)
    {
        var total = _files.Sum(f => f.Bytes);
        int filesDone = 0;
        long bytesDone = 0;
        try
        {
            if (staging.MaxFileBytes is long limit && _files.FirstOrDefault(f => f.Bytes > limit) is { } large)
            {
                return new RenditionOutcome(RenditionOutcomeKind.Failed, large.Path + " is over the server's file limit");
            }

            foreach (var file in _files)
            {
                await PutAsync(file, staging, ct).ConfigureAwait(false);
                filesDone++;
                bytesDone += file.Bytes;
                progress?.Report(new RenditionProgress(filesDone, _files.Count, bytesDone, total));
            }

            // files_missing lists files the server lacks (a lost PUT): send those again, twice at most.
            for (var round = 0; round <= 2; round++)
            {
                try
                {
                    await Retrying(async c => { await _api.CompleteAsync(_videoId, staging.StagingId, _channelId, c).ConfigureAwait(false); return true; }, ct).ConfigureAwait(false);
                    return new RenditionOutcome(RenditionOutcomeKind.Ready);
                }
                catch (StudioApiException ex) when (ex.Code == "files_missing" && round < 2 && ex.Missing.Count > 0)
                {
                    foreach (var path in ex.Missing)
                    {
                        var file = _files.FirstOrDefault(f => f.Path == path);
                        if (file == null) return new RenditionOutcome(RenditionOutcomeKind.Failed, "unknown " + path);
                        await PutAsync(file, staging, ct).ConfigureAwait(false);
                    }
                }
            }

            return new RenditionOutcome(RenditionOutcomeKind.Failed, "files still missing");
        }
        catch (Exception ex)
        {
            return Outcome(ex, ct);
        }
    }

    private async Task PutAsync(StagedFile file, RenditionStaging staging, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var parts = file.Parts ?? throw new InvalidOperationException("Not a <rung>/<file> path: " + file.Path);
        var bytes = Read(Path.Combine(_root, parts.Rung, parts.File));
        await Retrying(async c => { await _api.PutFileAsync(_videoId, staging.StagingId, file, bytes, _channelId, c).ConfigureAwait(false); return true; }, ct).ConfigureAwait(false);
    }

    /// <summary>Retries offline, 5xx and 429 answers with 2 s, 4 s… between attempts. Everything else is final.</summary>
    private async Task<T> Retrying<T>(Func<CancellationToken, Task<T>> body, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await body(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < Attempts && IsTransient(ex))
            {
                await Pause(TimeSpan.FromSeconds(1 << attempt), ct).ConfigureAwait(false);
            }
        }
    }

    public static bool IsTransient(Exception ex) => ex is RateLimitedException || (ex is StudioApiException s && s.IsTransient);

    private static RenditionOutcome Outcome(Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException || ct.IsCancellationRequested) return new RenditionOutcome(RenditionOutcomeKind.Cancelled);
        if (ex is StudioApiException { Code: "too_late" }) return new RenditionOutcome(RenditionOutcomeKind.TooLate);
        return new RenditionOutcome(RenditionOutcomeKind.Failed, ex.Message);
    }
}
