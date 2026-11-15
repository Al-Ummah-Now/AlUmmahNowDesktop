using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Timeline;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>
/// Renders the flattened edit list with Windows' MediaComposition: the clips in timeline order, trimmed to their
/// source spans, with black for any gap, encoded to MP4 (H.264 + AAC) at the project's size and rate.
/// </summary>
public sealed class MediaCompositionRenderer : ITimelineRenderer
{
    public async Task RenderAsync(FlattenedTimeline timeline, ProjectSettings settings, string output, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var track = timeline.Tracks.FirstOrDefault(t => t.Kind == "video" && t.Clips.Count > 0) ?? throw new InvalidOperationException("Nothing to export");
        var composition = new MediaComposition();
        var at = 0L;
        foreach (var clip in track.Clips)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Math.Abs(clip.Speed - 1) > 0.001) throw new NotSupportedException("Speed changes are not supported in exports yet");
            if (clip.TimelineStartMs > at)
            {
                composition.Clips.Add(MediaClip.CreateFromColor(global::Windows.UI.Color.FromArgb(255, 0, 0, 0), TimeSpan.FromMilliseconds(clip.TimelineStartMs - at)));
            }

            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(clip.Path));
            var media = await MediaClip.CreateFromFileAsync(file);
            media.TrimTimeFromStart = TimeSpan.FromMilliseconds(clip.InMs);
            var tail = media.OriginalDuration - TimeSpan.FromMilliseconds(clip.OutMs);
            media.TrimTimeFromEnd = tail > TimeSpan.Zero ? tail : TimeSpan.Zero;
            media.Volume = clip.Volume;
            composition.Clips.Add(media);
            at = clip.TimelineStartMs + clip.TimelineDurationMs;
        }

        var outputPath = Path.GetFullPath(output);
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(outputPath)!);
        var target = await folder.CreateFileAsync(Path.GetFileName(outputPath), CreationCollisionOption.ReplaceExisting);
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Width = (uint)settings.Width;
        profile.Video.Height = (uint)settings.Height;
        (profile.Video.FrameRate.Numerator, profile.Video.FrameRate.Denominator) = MediaFoundationRungEncoder.Fraction(settings.Fps);
        profile.Audio = AudioEncodingProperties.CreateAac(48000, 2, 128000);
        var result = await composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile)
            .AsTask(cancellationToken, new Progress<double>(p => progress.Report(p / 100)));
        if (result != TranscodeFailureReason.None) throw new InvalidOperationException("Export failed: " + result);
        progress.Report(1);
    }
}
