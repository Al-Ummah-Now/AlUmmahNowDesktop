using System.Globalization;
using System.Runtime.InteropServices;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Timeline;

namespace AlUmmahNow.Studio.Linux;

/// <summary>
/// Renders the flattened edit list with GStreamer Editing Services: one layer of URI clips at their timeline
/// positions (gaps render black and silent), encoded to MP4 with H.264 and AAC at the project's size and rate. Titles
/// are picture-sized transparent PNGs on a layer above the video. The same timeline gives the editor's preview frames.
/// Gir.Core 0.8.1 has no GES binding, so the few calls needed are declared here.
/// </summary>
public sealed class GesTimelineRenderer : ITimelineRenderer
{
    private const ulong NsPerMs = 1_000_000;
    private const int PipelineModePreview = 3; // GES_PIPELINE_MODE_PREVIEW_AUDIO | GES_PIPELINE_MODE_PREVIEW_VIDEO
    private const int PipelineModeRender = 1 << 2; // GES_PIPELINE_MODE_RENDER
    private const int StatePaused = 3;
    private const int SeekFlush = 1 << 0;
    private const int SeekAccurate = 1 << 1;
    private const int StateChangeFailure = 0;
    private const int StatePlaying = 4;
    private const int StateNull = 1;
    private const int FormatTime = 3;
    private const int MessageEos = 1 << 0;
    private const int MessageError = 1 << 1;

    public Task RenderAsync(FlattenedTimeline timeline, ProjectSettings settings, IReadOnlyList<TitleOverlay> titles, string output, IProgress<double> progress, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            if (!ges_init()) throw new InvalidOperationException("GStreamer Editing Services is not available");
            var gesTimeline = Build(timeline, titles, cancellationToken);
            var pipeline = IntPtr.Zero;
            try
            {
                pipeline = ges_pipeline_new();
                ges_pipeline_set_timeline(pipeline, gesTimeline);
                gesTimeline = IntPtr.Zero; // the pipeline owns it now
                var profile = Mp4Profile(settings);
                var outputUri = new Uri(Path.GetFullPath(output)).AbsoluteUri;
                if (!ges_pipeline_set_render_settings(pipeline, outputUri, profile)) throw new InvalidOperationException("Could not set up the MP4 encoder");
                g_object_unref(profile); // an encoding profile is a plain GObject
                if (!ges_pipeline_set_mode(pipeline, PipelineModeRender)) throw new InvalidOperationException("Could not start rendering");

                var bus = gst_element_get_bus(pipeline);
                try
                {
                    gst_element_set_state(pipeline, StatePlaying);
                    var total = Math.Max(1, timeline.DurationMs) * (long)NsPerMs;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var eos = gst_bus_timed_pop_filtered(bus, 0, MessageEos);
                        if (eos != IntPtr.Zero)
                        {
                            gst_mini_object_unref(eos);
                            break;
                        }

                        var error = gst_bus_timed_pop_filtered(bus, 0, MessageError);
                        if (error != IntPtr.Zero)
                        {
                            var text = ReadError(error);
                            gst_mini_object_unref(error);
                            throw new InvalidOperationException(text);
                        }

                        if (gst_element_query_position(pipeline, FormatTime, out var position)) progress.Report(Math.Clamp((double)position / total, 0, 1));
                        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    gst_element_set_state(pipeline, StateNull);
                    gst_object_unref(bus);
                }
            }
            finally
            {
                if (pipeline != IntPtr.Zero) gst_object_unref(pipeline);
                if (gesTimeline != IntPtr.Zero) gst_object_unref(gesTimeline);
            }

            if (!File.Exists(output) || new FileInfo(output).Length == 0) throw new InvalidOperationException("The export produced no file");
            progress.Report(1);
        }, cancellationToken);

    /// <summary>
    /// Saves the picture at <paramref name="ms"/> as a PNG of the given size (the editor's preview; titles are drawn
    /// over it live). Throws when the frame can't be made.
    /// </summary>
    public static void SaveFrame(FlattenedTimeline timeline, IReadOnlyList<TitleOverlay> titles, long ms, int width, int height, string png, CancellationToken cancellationToken)
    {
        if (!ges_init()) throw new InvalidOperationException("GStreamer Editing Services is not available");
        var gesTimeline = Build(timeline, titles, cancellationToken);
        var pipeline = ges_pipeline_new();
        try
        {
            ges_pipeline_set_timeline(pipeline, gesTimeline);
            gesTimeline = IntPtr.Zero;
            ges_pipeline_preview_set_video_sink(pipeline, gst_element_factory_make("fakesink", null));
            ges_pipeline_preview_set_audio_sink(pipeline, gst_element_factory_make("fakesink", null));
            if (!ges_pipeline_set_mode(pipeline, PipelineModePreview)) throw new InvalidOperationException("Could not start the preview");
            const ulong wait = 10_000_000_000; // 10 s
            gst_element_set_state(pipeline, StatePaused);
            if (gst_element_get_state(pipeline, out _, out _, wait) == StateChangeFailure) throw new InvalidOperationException("The preview did not start");
            cancellationToken.ThrowIfCancellationRequested();
            gst_element_seek_simple(pipeline, FormatTime, SeekFlush | SeekAccurate, Math.Max(0, ms) * (long)NsPerMs);
            if (gst_element_get_state(pipeline, out _, out _, wait) == StateChangeFailure) throw new InvalidOperationException("The preview did not seek");
            if (!ges_pipeline_save_thumbnail(pipeline, width, height, "image/png", png, IntPtr.Zero)) throw new InvalidOperationException("No preview frame");
        }
        finally
        {
            gst_element_set_state(pipeline, StateNull);
            gst_object_unref(pipeline);
            if (gesTimeline != IntPtr.Zero) gst_object_unref(gesTimeline);
        }
    }

    /// <summary>Titles on the top layer (GES draws lower priorities over higher), the clips under them.</summary>
    private static IntPtr Build(FlattenedTimeline timeline, IReadOnlyList<TitleOverlay> titles, CancellationToken cancellationToken)
    {
        var track = timeline.Tracks.FirstOrDefault(t => t.Kind == "video" && t.Clips.Count > 0) ?? throw new InvalidOperationException("Nothing to export");
        var gesTimeline = ges_timeline_new_audio_video();
        try
        {
            var titleLayer = titles.Count > 0 ? ges_timeline_append_layer(gesTimeline) : IntPtr.Zero;
            var layer = ges_timeline_append_layer(gesTimeline);
            foreach (var clip in track.Clips)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Math.Abs(clip.Speed - 1) > 0.001) throw new NotSupportedException("Speed changes are not supported in exports yet");
                if (!EdlProject.IsLocalMediaPath(clip.Path)) throw new InvalidOperationException("Not a local video file: " + Path.GetFileName(clip.Path));
                Place(layer, clip.Path, clip.TimelineStartMs, clip.InMs, clip.OutMs - clip.InMs, clip.ClipId);
            }

            foreach (var title in titles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Place(titleLayer, title.ImagePath, title.StartMs, 0, title.DurationMs, "title");
            }

            ges_timeline_commit_sync(gesTimeline);
            return gesTimeline;
        }
        catch
        {
            gst_object_unref(gesTimeline);
            throw;
        }
    }

    private static void Place(IntPtr layer, string path, long startMs, long inMs, long durationMs, string what)
    {
        var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
        var ges = ges_uri_clip_new(uri);
        if (ges == IntPtr.Zero) throw new InvalidOperationException("Could not open " + Path.GetFileName(path));
        ges_timeline_element_set_start(ges, (ulong)startMs * NsPerMs);
        ges_timeline_element_set_inpoint(ges, (ulong)inMs * NsPerMs);
        ges_timeline_element_set_duration(ges, (ulong)durationMs * NsPerMs);
        if (!ges_layer_add_clip(layer, ges)) throw new InvalidOperationException("Could not place " + what);
    }

    /// <summary>MP4 (ISO) with H.264 at the project's size and rate and AAC stereo at the project's sample rate.</summary>
    public static string VideoRestriction(ProjectSettings s) =>
        string.Create(CultureInfo.InvariantCulture, $"video/x-raw,width={s.Width},height={s.Height},framerate={Rate(s.Fps)},pixel-aspect-ratio=1/1");

    public static string AudioRestriction(ProjectSettings s) =>
        string.Create(CultureInfo.InvariantCulture, $"audio/x-raw,rate={(s.SampleRate > 0 ? s.SampleRate : 48000)},channels=2");

    private static IntPtr Mp4Profile(ProjectSettings s)
    {
        var containerCaps = gst_caps_from_string("video/quicktime,variant=iso");
        var container = gst_encoding_container_profile_new("mp4", null, containerCaps, null);
        var videoCaps = gst_caps_from_string("video/x-h264,profile=high");
        var videoRestriction = gst_caps_from_string(VideoRestriction(s));
        gst_encoding_container_profile_add_profile(container, gst_encoding_video_profile_new(videoCaps, null, videoRestriction, 0));
        var audioCaps = gst_caps_from_string("audio/mpeg,mpegversion=4");
        var audioRestriction = gst_caps_from_string(AudioRestriction(s));
        gst_encoding_container_profile_add_profile(container, gst_encoding_audio_profile_new(audioCaps, null, audioRestriction, 0));
        foreach (var caps in new[] { containerCaps, videoCaps, videoRestriction, audioCaps, audioRestriction }) gst_mini_object_unref(caps);
        return container;
    }

    private static string Rate(double fps)
    {
        var whole = Math.Round(fps);
        if (Math.Abs(fps - whole) < 0.01) return ((int)whole).ToString(CultureInfo.InvariantCulture) + "/1";
        return ((int)Math.Round(fps * 1.001) * 1000).ToString(CultureInfo.InvariantCulture) + "/1001";
    }

    private static string ReadError(IntPtr message)
    {
        gst_message_parse_error(message, out var error, out _);
        return error != IntPtr.Zero ? Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "Export failed" : "Export failed";
    }

    private const string Ges = "libges-1.0.so.0";
    private const string GstLib = "libgstreamer-1.0.so.0";
    private const string Pbutils = "libgstpbutils-1.0.so.0";

    [DllImport(Ges)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ges_init();
    [DllImport(Ges)] private static extern IntPtr ges_timeline_new_audio_video();
    [DllImport(Ges)] private static extern IntPtr ges_timeline_append_layer(IntPtr timeline);
    [DllImport(Ges)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ges_timeline_commit_sync(IntPtr timeline);
    [DllImport(Ges, CharSet = CharSet.Ansi)] private static extern IntPtr ges_uri_clip_new([MarshalAs(UnmanagedType.LPUTF8Str)] string uri);
    [DllImport(Ges)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ges_timeline_element_set_start(IntPtr element, ulong start);
    [DllImport(Ges)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ges_timeline_element_set_inpoint(IntPtr element, ulong inpoint);
    [DllImport(Ges)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ges_timeline_element_set_duration(IntPtr element, ulong duration);
    [DllImport(Ges)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ges_layer_add_clip(IntPtr layer, IntPtr clip);
    [DllImport(Ges)] private static extern IntPtr ges_pipeline_new();
    [DllImport(Ges)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ges_pipeline_set_timeline(IntPtr pipeline, IntPtr timeline);
    [DllImport(Ges)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ges_pipeline_set_render_settings(IntPtr pipeline, [MarshalAs(UnmanagedType.LPUTF8Str)] string outputUri, IntPtr profile);
    [DllImport(Ges)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ges_pipeline_set_mode(IntPtr pipeline, int mode);
    [DllImport(Ges)] private static extern void ges_pipeline_preview_set_video_sink(IntPtr pipeline, IntPtr sink);
    [DllImport(Ges)] private static extern void ges_pipeline_preview_set_audio_sink(IntPtr pipeline, IntPtr sink);
    [DllImport(Ges)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ges_pipeline_save_thumbnail(IntPtr pipeline, int width, int height, [MarshalAs(UnmanagedType.LPUTF8Str)] string format, [MarshalAs(UnmanagedType.LPUTF8Str)] string location, IntPtr error);
    [DllImport(GstLib)] private static extern IntPtr gst_element_factory_make([MarshalAs(UnmanagedType.LPUTF8Str)] string factory, [MarshalAs(UnmanagedType.LPUTF8Str)] string? name);
    [DllImport(GstLib)] private static extern int gst_element_get_state(IntPtr element, out int state, out int pending, ulong timeout);
    [DllImport(GstLib)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool gst_element_seek_simple(IntPtr element, int format, int flags, long position);
    [DllImport(GstLib)] private static extern IntPtr gst_caps_from_string([MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(GstLib)] private static extern int gst_element_set_state(IntPtr element, int state);
    [DllImport(GstLib)] private static extern IntPtr gst_element_get_bus(IntPtr element);
    [DllImport(GstLib)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool gst_element_query_position(IntPtr element, int format, out long position);
    [DllImport(GstLib)] private static extern IntPtr gst_bus_timed_pop_filtered(IntPtr bus, ulong timeout, int types);
    [DllImport(GstLib)] private static extern void gst_message_parse_error(IntPtr message, out IntPtr error, out IntPtr debug);
    [DllImport(GstLib)] private static extern void gst_mini_object_unref(IntPtr obj);
    [DllImport(GstLib)] private static extern void gst_object_unref(IntPtr obj);
    [DllImport("libgobject-2.0.so.0")] private static extern void g_object_unref(IntPtr obj);
    [DllImport(Pbutils)] private static extern IntPtr gst_encoding_container_profile_new([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string? description, IntPtr format, [MarshalAs(UnmanagedType.LPUTF8Str)] string? preset);
    [DllImport(Pbutils)] private static extern IntPtr gst_encoding_video_profile_new(IntPtr format, [MarshalAs(UnmanagedType.LPUTF8Str)] string? preset, IntPtr restriction, uint presence);
    [DllImport(Pbutils)] private static extern IntPtr gst_encoding_audio_profile_new(IntPtr format, [MarshalAs(UnmanagedType.LPUTF8Str)] string? preset, IntPtr restriction, uint presence);
    [DllImport(Pbutils)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool gst_encoding_container_profile_add_profile(IntPtr container, IntPtr profile);
}
