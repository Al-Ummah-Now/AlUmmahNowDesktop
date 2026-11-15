using System.Text.Json;
using AlUmmahNow.Core;
using AlUmmahNow.CoreBridge.Studio;
using StagedFile = AlUmmahNow.CoreBridge.Studio.StagedFile;
using HlsRung = AlUmmahNow.CoreBridge.Studio.HlsRung;
using HlsPackage = AlUmmahNow.CoreBridge.Studio.HlsPackage;

namespace AlUmmahNow.CoreBridge.Native.Studio;

/// <summary>Core's cancel handle and progress callback for one call, tied to a <see cref="CancellationToken"/>.</summary>
internal sealed class CoreCall : IDisposable
{
    private readonly CancellationTokenRegistration _registration;

    public CoreCall(CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        Cancel = new MediaCancel();
        Progress = new FractionProgress(progress);
        _registration = cancellationToken.Register(() => Cancel.Cancel());
    }

    public MediaCancel Cancel { get; }

    public MediaProgress Progress { get; }

    public void Dispose()
    {
        _registration.Dispose();
        Cancel.Dispose();
    }

    /// <summary>Core reports done/total; features see 0…1.</summary>
    private sealed class FractionProgress : MediaProgress
    {
        private readonly IProgress<double>? _progress;

        public FractionProgress(IProgress<double>? progress) => _progress = progress;

        public void OnProgress(ulong done, ulong total)
        {
            if (total > 0) _progress?.Report(Math.Clamp((double)done / total, 0, 1));
        }
    }
}

/// <summary>Turns Core errors into the exceptions features handle: cancellation stays cancellation.</summary>
internal static class CoreErrors
{
    /// <summary>Core's message without the binding's "@detail=" prefix.</summary>
    public static string Detail(Exception ex) => ex.Message.StartsWith("@detail=", StringComparison.Ordinal) ? ex.Message.Substring(8) : ex.Message;

    public static Exception Map(AunException ex, CancellationToken cancellationToken, string what) =>
        ex is AunException.Cancelled || cancellationToken.IsCancellationRequested
            ? new OperationCanceledException(what + " cancelled", ex, cancellationToken)
            : new InvalidOperationException(what + " failed: " + Detail(ex), ex);
}

/// <summary>
/// <see cref="IOnDeviceTranscriber"/> on Core's whisper.cpp (D10): <c>ModelStore</c> keeps the models in the app's
/// data folder, <c>transcribe_on_device_json</c> runs them and <c>transcript_to_caption_segments_json</c> turns the
/// transcript into the on-device transcript body.
/// </summary>
public sealed class CoreOnDeviceTranscriber : IOnDeviceTranscriber, IDisposable
{
    /// <summary>The caption line length the server and the other apps use.</summary>
    public const int MaxLineChars = 42;

    private readonly ModelStore _store;

    public CoreOnDeviceTranscriber(string modelsDirectory)
    {
        Directory.CreateDirectory(modelsDirectory);
        _store = new ModelStore(modelsDirectory);
    }

    public bool IsAvailable
    {
        get
        {
            try
            {
                return AunCoreMethods.TranscriptionOnDeviceAvailable();
            }
            catch (DllNotFoundException)
            {
                return false;
            }
        }
    }

    public IReadOnlyList<TranscriptionModel> Models() =>
        _store.List().Select(m => new TranscriptionModel(m.Model.Id, m.Installed, (long)m.Model.SizeBytes)).ToList();

    public async Task DownloadAsync(string model, IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var call = new CoreCall(cancellationToken, progress);
        try
        {
            await _store.Download(model, call.Progress, call.Cancel).ConfigureAwait(false);
        }
        catch (AunException ex)
        {
            throw CoreErrors.Map(ex, cancellationToken, "Model download");
        }
    }

    public void DeleteModel(string id) => _store.Delete(id);

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(string model, string mediaPath, string? language, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var path = _store.Path(model) ?? throw new InvalidOperationException("Model " + model + " is not downloaded");
        using var call = new CoreCall(cancellationToken, progress);
        string transcript;
        try
        {
            transcript = await AunCoreMethods.TranscribeOnDeviceJson(path, mediaPath, language ?? "", call.Progress, call.Cancel).ConfigureAwait(false);
        }
        catch (AunException ex)
        {
            throw CoreErrors.Map(ex, cancellationToken, "Transcription");
        }

        return ParseSegments(AunCoreMethods.TranscriptToCaptionSegmentsJson(transcript, MaxLineChars));
    }

    /// <summary>Reads <c>[{startMs, endMs, text}]</c>, dropping empty or backwards segments.</summary>
    public static IReadOnlyList<TranscriptSegment> ParseSegments(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Caption segments are not a list");
        var list = new List<TranscriptSegment>();
        foreach (var s in doc.RootElement.EnumerateArray())
        {
            var start = s.TryGetProperty("startMs", out var a) && a.TryGetInt64(out var av) ? av : -1;
            var end = s.TryGetProperty("endMs", out var b) && b.TryGetInt64(out var bv) ? bv : -1;
            var text = s.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()!.Trim() : "";
            if (start < 0 || end <= start || text.Length == 0) continue;
            list.Add(new TranscriptSegment((int)Math.Min(start, int.MaxValue), (int)Math.Min(end, int.MaxValue), text));
        }

        return list;
    }

    public void Dispose() => _store.Dispose();
}

/// <summary><see cref="IRenditionPackaging"/> on Core: the server's ladder, fMP4 HLS packaging and the file list (D11).</summary>
public sealed class CoreRenditionPackaging : IRenditionPackaging
{
    public IReadOnlyList<HlsRung> Ladder(int width, int height, int rotation) =>
        AunCoreMethods.HlsLadder((uint)Math.Max(0, width), (uint)Math.Max(0, height), rotation).Select(ToRung).ToList();

    public async Task<HlsPackage> PackageAsync(string mp4, string outputDirectory, double segmentSeconds, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        using var call = new CoreCall(cancellationToken);
        try
        {
            var package = await AunCoreMethods.HlsPackageMp4(mp4, outputDirectory, (uint)Math.Max(1, Math.Round(segmentSeconds)), call.Cancel).ConfigureAwait(false);
            return new HlsPackage(package.Files.Select(ToFile).ToList(), (long)package.DurationMs);
        }
        catch (AunException ex)
        {
            throw CoreErrors.Map(ex, cancellationToken, "Packaging");
        }
    }

    public IReadOnlyList<StagedFile> Files(string root, IReadOnlyList<string> rungs) =>
        AunCoreMethods.RenditionsFiles(root, rungs.ToArray()).Select(ToFile).ToList();

    internal static HlsRung ToRung(AlUmmahNow.Core.HlsRung r) => new()
    {
        Name = r.Name,
        Width = (int)r.Width,
        Height = (int)r.Height,
        Maxrate = (int)r.Maxrate,
        Bufsize = (int)r.Bufsize,
        AudioBitrate = (int)r.AudioBitrate,
        Profile = r.Profile,
        GopSeconds = r.GopSeconds,
        SegmentSeconds = r.SegmentSeconds,
        MaxFps = r.MaxFps,
    };

    private static StagedFile ToFile(AlUmmahNow.Core.StagedFile f) => new(f.Path, (long)f.Bytes);
}

/// <summary><see cref="IEdlEditing"/> on Core's <c>edl_apply_json</c>, <c>edl_flatten_json</c> and <c>edl_validate</c>.</summary>
public sealed class CoreEdlEditing : IEdlEditing
{
    public string Apply(string projectJson, string opJson)
    {
        try
        {
            return AunCoreMethods.EdlApplyJson(projectJson, opJson);
        }
        catch (AunException ex)
        {
            throw Map(ex);
        }
    }

    public FlattenedTimeline Flatten(string projectJson)
    {
        try
        {
            return FlattenedTimeline.Parse(AunCoreMethods.EdlFlattenJson(projectJson));
        }
        catch (AunException ex)
        {
            throw Map(ex);
        }
    }

    public IReadOnlyList<string> Validate(string projectJson)
    {
        try
        {
            return AunCoreMethods.EdlValidate(projectJson).Select(i => i.Code + (string.IsNullOrEmpty(i.Item) ? "" : " " + i.Item) + ": " + i.Message).ToList();
        }
        catch (AunException ex)
        {
            return new[] { CoreErrors.Detail(ex) };
        }
    }

    /// <summary>Upgrades an older project file to the current schema.</summary>
    public string Load(string json)
    {
        try
        {
            return AunCoreMethods.EdlLoadProject(json);
        }
        catch (AunException ex)
        {
            throw Map(ex);
        }
    }

    private static EdlEditException Map(AunException ex) => new(ex switch
    {
        AunException.InvalidInput => EdlErrorKind.InvalidInput,
        AunException.NotFound => EdlErrorKind.NotFound,
        AunException.Conflict => EdlErrorKind.Conflict,
        _ => EdlErrorKind.Other,
    }, CoreErrors.Detail(ex), ex);
}
