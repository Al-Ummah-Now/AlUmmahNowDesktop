using System.Globalization;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Engines;

namespace AlUmmahNow.Studio.Encode;

/// <summary>Encodes one rung of the server ladder to a progressive MP4 on the platform's media stack.</summary>
public interface IRungEncoder
{
    /// <summary>
    /// H.264 yuv420p at the rung's size and profile, a keyframe exactly every <see cref="HlsRung.GopSeconds"/> and no
    /// scene-cut keyframes, peak bitrate under <see cref="HlsRung.Maxrate"/>, fps capped at <see cref="HlsRung.MaxFps"/>
    /// above 31; AAC stereo 48 kHz when the source has audio. Progress 0…1.
    /// </summary>
    Task EncodeAsync(string source, string output, HlsRung rung, MediaSourceInfo info, IProgress<double> progress, CancellationToken cancellationToken);
}

/// <summary>Builds the GStreamer pipeline for one rung. Pure, so it is unit tested; Linux runs it.</summary>
public static class RungPipeline
{
    public static string Build(string source, string output, HlsRung rung, MediaSourceInfo info)
    {
        var src = Quote(new Uri(Path.GetFullPath(source)).AbsoluteUri);
        var sink = Quote(Path.GetFullPath(output));
        var fps = rung.OutputFps(info.Fps > 0 ? info.Fps : 30);
        var keyint = rung.KeyframeInterval(info.Fps > 0 ? info.Fps : 30).ToString(CultureInfo.InvariantCulture);
        var avgKbps = I(rung.AverageBitrate / 1000);
        var maxKbps = I(rung.Maxrate / 1000);
        var bufKbits = I(rung.Bufsize / 1000);
        var rate = info.Fps > 31 ? $" ! videorate ! video/x-raw,framerate={Fraction(fps)}" : "";
        // Software x264 only: hardware encoders don't guarantee fixed keyframes without scene cuts.
        var encoder = $"x264enc bitrate={avgKbps} key-int-max={keyint} speed-preset=veryfast " +
            $"option-string=\"scenecut=0:keyint={keyint}:min-keyint={keyint}:vbv-maxrate={maxKbps}:vbv-bufsize={bufKbits}:force-cfr=1\"";
        var video = $"queue ! videoflip video-direction=auto ! videoconvert ! videoscale{rate} ! " +
            $"video/x-raw,format=I420,width={I(rung.Width)},height={I(rung.Height)},pixel-aspect-ratio=1/1 ! {encoder} ! " +
            $"video/x-h264,profile={rung.Profile} ! h264parse";
        var pipeline = $"uridecodebin uri={src} name=d d. ! {video} ! mp4mux name=m faststart=true ! filesink location={sink}";
        if (info.HasAudio)
        {
            pipeline += $" d. ! queue ! audioconvert ! audioresample ! audio/x-raw,rate=48000,channels=2 ! avenc_aac bitrate={I(rung.AudioBitrate)} ! m.";
        }

        return pipeline;
    }

    private static string Fraction(double fps)
    {
        // 29.97 and friends as exact fractions, whole rates as n/1.
        var whole = Math.Round(fps);
        if (Math.Abs(fps - whole) < 0.01) return I((int)whole) + "/1";
        return I((int)Math.Round(fps * 1.001) * 1000) + "/1001";
    }

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

public enum LocalRenditionStage { Probing, Encoding, Packaging, Uploading, Done }

public sealed record LocalRenditionProgress(LocalRenditionStage Stage, string? Rung, double Fraction);

/// <summary>
/// The opt-in "Pre-encode renditions on this computer" run (E19 19.3, D11): probe, encode each rung, package it with
/// Core, then send it with <see cref="RenditionUploader"/>. Never throws and never blocks the normal upload: any
/// failure ends in a <see cref="RenditionOutcomeKind.Failed"/> outcome and the server encodes as usual.
/// </summary>
public sealed class LocalRenditionJob
{
    private readonly IMediaProbe _probe;
    private readonly IRungEncoder _encoder;
    private readonly IRenditionPackaging _packaging;
    private readonly IRenditionsApi _api;

    public LocalRenditionJob(IMediaProbe probe, IRungEncoder encoder, IRenditionPackaging packaging, IRenditionsApi api)
    {
        _probe = probe;
        _encoder = encoder;
        _packaging = packaging;
        _api = api;
    }

    /// <summary>"Al Ummah Now Studio 1.2.3 (Linux)", sent as <c>encoder</c>.</summary>
    public string EncoderName { get; set; } = "Al Ummah Now Studio";

    /// <summary>Lets tests replace the uploader's file reads and retry pauses.</summary>
    public Action<RenditionUploader>? ConfigureUploader { get; set; }

    public async Task<RenditionOutcome> RunAsync(string source, string videoId, string? channelId, string workDir, IProgress<LocalRenditionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            progress?.Report(new LocalRenditionProgress(LocalRenditionStage.Probing, null, 0));
            var info = await _probe.ProbeAsync(source, cancellationToken).ConfigureAwait(false);
            if (!info.HasVideo || info.Width <= 0 || info.Height <= 0) return new RenditionOutcome(RenditionOutcomeKind.Failed, "no video track");
            var ladder = _packaging.Ladder(info.Width, info.Height, info.Rotation);
            if (ladder.Count == 0) return new RenditionOutcome(RenditionOutcomeKind.Failed, "empty ladder");
            Directory.CreateDirectory(workDir);
            var root = Path.Combine(workDir, "hls");
            for (var i = 0; i < ladder.Count; i++)
            {
                var rung = ladder[i];
                var mp4 = Path.Combine(workDir, rung.Name + ".mp4");
                var share = 1.0 / ladder.Count;
                var at = i * share;
                await _encoder.EncodeAsync(source, mp4, rung, info, new Progress<double>(p => progress?.Report(new LocalRenditionProgress(LocalRenditionStage.Encoding, rung.Name, at + (p * share)))), cancellationToken).ConfigureAwait(false);
                progress?.Report(new LocalRenditionProgress(LocalRenditionStage.Packaging, rung.Name, at + share));
                await _packaging.PackageAsync(mp4, Path.Combine(root, rung.Name), rung.SegmentSeconds, cancellationToken).ConfigureAwait(false);
                TryDelete(mp4);
            }

            var rungs = ladder.Select(r => r.Name).ToList();
            var files = _packaging.Files(root, rungs);
            var uploader = new RenditionUploader(_api, videoId, channelId, root, rungs, files, EncoderName);
            ConfigureUploader?.Invoke(uploader);
            var sendProgress = new Progress<RenditionProgress>(p => progress?.Report(new LocalRenditionProgress(LocalRenditionStage.Uploading, null, p.Fraction)));
            var outcome = await uploader.RunAsync(sendProgress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new LocalRenditionProgress(LocalRenditionStage.Done, null, 1));
            return outcome;
        }
        catch (OperationCanceledException)
        {
            return new RenditionOutcome(RenditionOutcomeKind.Cancelled);
        }
        catch (Exception ex)
        {
            return new RenditionOutcome(RenditionOutcomeKind.Failed, ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
