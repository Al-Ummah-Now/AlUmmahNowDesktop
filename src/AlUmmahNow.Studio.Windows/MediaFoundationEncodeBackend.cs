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
