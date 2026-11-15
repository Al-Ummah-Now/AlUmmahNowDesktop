using AlUmmahNow.Studio.Encode;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>Encodes through Media Foundation via MediaTranscoder, hardware first.</summary>
public sealed class MediaFoundationEncodeBackend : IEncodeBackend
{
    public IReadOnlyList<EncoderChoice> AvailableEncoders() => new[]
    {
        new EncoderChoice("mediafoundation-hardware", EncoderKind.Hardware),
        new EncoderChoice("mediafoundation-software", EncoderKind.Software),
    };

    public async Task EncodeAsync(EncodeRequest request, EncoderChoice encoder, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var source = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(request.Source));
        var outputPath = Path.GetFullPath(request.Output);
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(outputPath)!);
        var output = await folder.CreateFileAsync(Path.GetFileName(outputPath), CreationCollisionOption.ReplaceExisting);

        MediaEncodingProfile profile;
        if (request.Preset.AudioOnly)
        {
            profile = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High);
        }
        else
        {
            profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            var video = await source.Properties.GetVideoPropertiesAsync();
            if (video.Height > 0)
            {
                // Never upscale: keep the source size, capped at the preset height, on even dimensions.
                var height = Math.Min(video.Height, (uint)request.Preset.MaxHeight);
                profile.Video.Height = height & ~1u;
                profile.Video.Width = (uint)(video.Width * (double)height / video.Height) & ~1u;
            }

            profile.Video.Bitrate = (uint)request.Preset.VideoKbps * 1000;
        }

        profile.Audio.Bitrate = (uint)request.Preset.AudioKbps * 1000;

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = encoder.Kind == EncoderKind.Hardware };
        var prepared = await transcoder.PrepareFileTranscodeAsync(source, output, profile);
        if (!prepared.CanTranscode) throw new InvalidOperationException("Cannot transcode: " + prepared.FailureReason);

        await prepared.TranscodeAsync().AsTask(cancellationToken, new Progress<double>(p => progress.Report(p / 100)));
    }
}

/// <summary>
/// Pre-encodes one rung of the server ladder (D11) with Media Foundation's software H.264 encoder: the rung's size,
/// profile and frame rate, a keyframe every <see cref="AlUmmahNow.CoreBridge.Studio.HlsRung.GopSeconds"/>
/// (<c>MF_MT_MAX_KEYFRAME_SPACING</c>; the software encoder adds no scene-cut keyframes), a constant rate under the
/// rung's peak, and AAC stereo 48 kHz when the source has audio. The server checks the result before using it.
/// </summary>
public sealed class MediaFoundationRungEncoder : IRungEncoder
{
    // mfapi.h: MF_MT_MAX_KEYFRAME_SPACING, the H.264 encoder's GOP size.
    private static readonly Guid MaxKeyframeSpacing = new("c16eb52b-73a1-476f-8d62-839d6a020652");

    public async Task EncodeAsync(string source, string output, AlUmmahNow.CoreBridge.Studio.HlsRung rung, AlUmmahNow.CoreBridge.Studio.MediaSourceInfo info, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var input = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(source));
        var outputPath = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(outputPath)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(outputPath), CreationCollisionOption.ReplaceExisting);

        var sourceFps = info.Fps > 0 ? info.Fps : 30;
        var fps = rung.OutputFps(sourceFps);
        var video = VideoEncodingProperties.CreateH264();
        video.Width = (uint)rung.Width;
        video.Height = (uint)rung.Height;
        video.Bitrate = (uint)rung.AverageBitrate;
        video.ProfileId = rung.Profile == "main" ? H264ProfileIds.Main : H264ProfileIds.High;
        (video.FrameRate.Numerator, video.FrameRate.Denominator) = Fraction(fps);
        video.PixelAspectRatio.Numerator = 1;
        video.PixelAspectRatio.Denominator = 1;
        video.Properties[MaxKeyframeSpacing] = (uint)rung.KeyframeInterval(sourceFps);

        var profile = new MediaEncodingProfile
        {
            Container = new ContainerEncodingProperties { Subtype = MediaEncodingSubtypes.Mpeg4 },
            Video = video,
            Audio = info.HasAudio ? AudioEncodingProperties.CreateAac(48000, 2, (uint)rung.AudioBitrate) : null,
        };

        // Software only: hardware encoders don't guarantee fixed keyframes.
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = false, AlwaysReencode = true };
        var prepared = await transcoder.PrepareFileTranscodeAsync(input, file, profile);
        if (!prepared.CanTranscode) throw new InvalidOperationException("Cannot encode " + rung.Name + ": " + prepared.FailureReason);
        await prepared.TranscodeAsync().AsTask(cancellationToken, new Progress<double>(p => progress.Report(p / 100)));
        if (new FileInfo(outputPath).Length == 0) throw new InvalidOperationException("Encoder produced no output");
    }

    /// <summary>29.97 and friends as n/1001, whole rates as n/1.</summary>
    internal static (uint, uint) Fraction(double fps)
    {
        var whole = Math.Round(fps);
        return Math.Abs(fps - whole) < 0.01 ? ((uint)whole, 1u) : ((uint)Math.Round(fps * 1.001) * 1000, 1001u);
    }
}
