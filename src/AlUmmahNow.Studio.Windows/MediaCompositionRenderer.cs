using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Timeline;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>
/// Renders the flattened edit list with Windows' MediaComposition: the clips in timeline order, trimmed to their
/// source spans, with black for any gap, encoded to MP4 (H.264 + AAC) at the project's size and rate. Titles are
/// picture-sized transparent PNG overlays, each on its own layer. The same composition gives the preview frames.
/// </summary>
public sealed class MediaCompositionRenderer : ITimelineRenderer
{
    public async Task RenderAsync(FlattenedTimeline timeline, ProjectSettings settings, IReadOnlyList<TitleOverlay> titles, string output, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var composition = await ComposeAsync(timeline, cancellationToken);
        foreach (var title in titles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(title.ImagePath));
            var clip = await MediaClip.CreateFromImageFileAsync(image, TimeSpan.FromMilliseconds(title.DurationMs));
            var overlay = new MediaOverlay(clip)
            {
                Position = new global::Windows.Foundation.Rect(0, 0, settings.Width, settings.Height),
                Delay = TimeSpan.FromMilliseconds(title.StartMs),
                Opacity = 1,
            };
            var layer = new MediaOverlayLayer();
            layer.Overlays.Add(overlay);
            composition.OverlayLayers.Add(layer);
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

    /// <summary>The picture at <paramref name="ms"/> (the editor's preview; titles are drawn over it live), as an image stream.</summary>
    public static async Task<IRandomAccessStream> FrameAsync(FlattenedTimeline timeline, long ms, int width, int height, CancellationToken cancellationToken)
    {
        var composition = await ComposeAsync(timeline, cancellationToken);
        var at = TimeSpan.FromMilliseconds(Math.Clamp(ms, 0, Math.Max(0, timeline.DurationMs - 1)));
        return await composition.GetThumbnailAsync(at, width, height, VideoFramePrecision.NearestFrame).AsTask(cancellationToken);
    }

    private static async Task<MediaComposition> ComposeAsync(FlattenedTimeline timeline, CancellationToken cancellationToken)
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

            if (!EdlProject.IsLocalMediaPath(clip.Path)) throw new InvalidOperationException("Not a local video file: " + Path.GetFileName(clip.Path));
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(clip.Path));
            var media = await MediaClip.CreateFromFileAsync(file);
            media.TrimTimeFromStart = TimeSpan.FromMilliseconds(clip.InMs);
            var tail = media.OriginalDuration - TimeSpan.FromMilliseconds(clip.OutMs);
            media.TrimTimeFromEnd = tail > TimeSpan.Zero ? tail : TimeSpan.Zero;
            media.Volume = clip.Volume;
            composition.Clips.Add(media);
            at = clip.TimelineStartMs + clip.TimelineDurationMs;
        }

        return composition;
    }
}
