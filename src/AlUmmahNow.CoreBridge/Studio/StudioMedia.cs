using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge.Studio
{
    // The aun-media calls desktop Studio needs (Core hls_ladder, hls_package_mp4, renditions_files, edl_apply_json,
    // edl_flatten_json, ModelStore, transcribe_on_device_json). CoreBridge.Native adapts Core to these interfaces;
    // features only see these. Cancellation is a CancellationToken; adapters turn it into Core's MediaCancel.

    /// <summary>One file of a rung, as declared to <c>POST /api/videos/:id/renditions</c> (<c>&lt;rung&gt;/&lt;file&gt;</c>).</summary>
    public sealed class StagedFile : IEquatable<StagedFile>
    {
        public StagedFile(string path, long bytes)
        {
            Path = path;
            Bytes = bytes;
        }

        public string Path { get; }
        public long Bytes { get; }

        /// <summary>The rung and file name, for the PUT route; null when the path isn't exactly two parts.</summary>
        public (string Rung, string File)? Parts
        {
            get
            {
                var p = Path.Split('/');
                return p.Length == 2 && p[0].Length > 0 && p[1].Length > 0 ? (p[0], p[1]) : ((string, string)?)null;
            }
        }

        public bool Equals(StagedFile? o) => o != null && o.Path == Path && o.Bytes == Bytes;

        public override bool Equals(object? obj) => Equals(obj as StagedFile);

        public override int GetHashCode() => (Path, Bytes).GetHashCode();
    }

    public sealed class RenditionStaging
    {
        public RenditionStaging(string stagingId, string status = "uploading", long? maxFileBytes = null)
        {
            StagingId = stagingId;
            Status = status;
            MaxFileBytes = maxFileBytes;
        }

        public string StagingId { get; }
        public string Status { get; }
        public long? MaxFileBytes { get; }
    }

    /// <summary>One rung of the server's HLS ladder (Core <c>HlsRung</c>), the same one apps/media encodes.</summary>
    public sealed class HlsRung
    {
        public string Name { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }

        /// <summary>Bits per second.</summary>
        public int Maxrate { get; set; }
        public int Bufsize { get; set; }
        public int AudioBitrate { get; set; }

        /// <summary><c>high</c> or <c>main</c>.</summary>
        public string Profile { get; set; } = "high";
        public double GopSeconds { get; set; } = 2;
        public double SegmentSeconds { get; set; } = 6;
        public double MaxFps { get; set; } = 30;

        /// <summary>The encoder's average target: under the peak so the peak stays under <see cref="Maxrate"/>.</summary>
        public int AverageBitrate => (int)(Maxrate * 0.8);

        /// <summary>Frames per second for a source: capped at <see cref="MaxFps"/> only when the source is above 31.</summary>
        public double OutputFps(double source) => source > 31 ? Math.Min(source, MaxFps) : source;

        /// <summary>Frames between keyframes: a keyframe exactly every <see cref="GopSeconds"/>.</summary>
        public int KeyframeInterval(double sourceFps) => Math.Max(1, (int)Math.Round(OutputFps(sourceFps) * GopSeconds));
    }

    /// <summary>The fMP4 HLS files written for one rung (Core <c>HlsPackage</c>).</summary>
    public sealed class HlsPackage
    {
        public HlsPackage(IReadOnlyList<StagedFile> files, long durationMs)
        {
            Files = files;
            DurationMs = durationMs;
        }

        public IReadOnlyList<StagedFile> Files { get; }
        public long DurationMs { get; }
    }

    /// <summary>Ladder and packaging for pre-encoded renditions (D11).</summary>
    public interface IRenditionPackaging
    {
        IReadOnlyList<HlsRung> Ladder(int width, int height, int rotation);

        /// <summary>Writes <c>init.mp4</c>, <c>seg_00001.m4s</c>… and <c>index.m3u8</c> for a progressive MP4 into <paramref name="outputDirectory"/>.</summary>
        Task<HlsPackage> PackageAsync(string mp4, string outputDirectory, double segmentSeconds, CancellationToken cancellationToken);

        /// <summary>The file list for <c>POST /renditions</c>, from the rung folders under <paramref name="root"/>.</summary>
        IReadOnlyList<StagedFile> Files(string root, IReadOnlyList<string> rungs);
    }

    /// <summary>The edit list engine. Every edit goes through Core so timeline rules match every app.</summary>
    public interface IEdlEditing
    {
        /// <summary>Applies one edit (<see cref="EdlOp"/>). Throws <see cref="EdlEditException"/> and leaves the project unchanged on failure.</summary>
        string Apply(string projectJson, string opJson);

        FlattenedTimeline Flatten(string projectJson);

        /// <summary>Checks a project; the problems, empty when valid.</summary>
        IReadOnlyList<string> Validate(string projectJson);

        /// <summary>Reads a saved project, upgrading an older schema. Throws <see cref="EdlEditException"/> when it isn't one.</summary>
        string Load(string json);
    }

    public enum EdlErrorKind { InvalidInput, NotFound, Conflict, Other }

    public sealed class EdlEditException : Exception
    {
        public EdlEditException(EdlErrorKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;

        public EdlErrorKind Kind { get; }
    }

    /// <summary>The tagged edits <c>edl_apply_json</c> takes.</summary>
    public static class EdlOp
    {
        public static string Split(string clipId, long atMs, string newId) => Op("split", w => { w.WriteString("clipId", clipId); w.WriteNumber("atMs", atMs); w.WriteString("newId", newId); });

        public static string Trim(string clipId, long inMs, long outMs) => Op("trim", w => { w.WriteString("clipId", clipId); w.WriteNumber("inMs", inMs); w.WriteNumber("outMs", outMs); });

        public static string RippleDeleteClip(string clipId) => Op("rippleDeleteClip", w => w.WriteString("clipId", clipId));

        public static string RippleDeleteRange(long startMs, long endMs) => Op("rippleDeleteRange", w => { w.WriteNumber("startMs", startMs); w.WriteNumber("endMs", endMs); });

        public static string Join(string leftId, string rightId) => Op("join", w => { w.WriteString("leftId", leftId); w.WriteString("rightId", rightId); });

        public static string Move(string clipId, string trackId, long startMs) => Op("move", w => { w.WriteString("clipId", clipId); w.WriteString("trackId", trackId); w.WriteNumber("startMs", startMs); });

        public static string Append(string trackId, string id, string sourceId, long inMs, long outMs, long? timelineStartMs = null) => Op("append", w =>
        {
            w.WriteString("trackId", trackId);
            w.WriteStartObject("clip");
            w.WriteString("id", id);
            w.WriteString("sourceId", sourceId);
            w.WriteNumber("inMs", inMs);
            w.WriteNumber("outMs", outMs);
            if (timelineStartMs != null) w.WriteNumber("timelineStartMs", timelineStartMs.Value);
            w.WriteEndObject();
        });

        private static string Op(string name, Action<System.Text.Json.Utf8JsonWriter> fields) => CaptionJson.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("op", name);
            fields(w);
            w.WriteEndObject();
        });
    }

    /// <summary><c>edl_flatten_json</c>: clips in timeline order, ready for MediaComposition / GES.</summary>
    public sealed class FlattenedTimeline
    {
        public FlattenedTimeline(long durationMs, IReadOnlyList<FlatTrack> tracks)
        {
            DurationMs = durationMs;
            Tracks = tracks;
        }

        public long DurationMs { get; }
        public IReadOnlyList<FlatTrack> Tracks { get; }

        public static FlattenedTimeline Parse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                var tracks = J.Array(r, "tracks").Select(t => new FlatTrack(
                    J.Str(t, "id") ?? "",
                    J.Str(t, "kind") ?? "video",
                    J.Bool(t, "muted"),
                    J.Array(t, "clips").Select(c => new FlatClip(
                        J.Str(c, "clipId") ?? "", J.Str(c, "sourceId") ?? "", J.Str(c, "path") ?? "",
                        J.Long(c, "inMs"), J.Long(c, "outMs"), J.Long(c, "timelineStartMs"),
                        J.NumOrNull(c, "speed") ?? 1, J.NumOrNull(c, "volume") ?? 1)).ToList())).ToList();
                return new FlattenedTimeline(J.Long(r, "durationMs"), tracks);
            }
            catch (JsonException ex)
            {
                throw new EdlEditException(EdlErrorKind.Other, "Flattened timeline is not valid JSON", ex);
            }
        }
    }

    public sealed class FlatTrack
    {
        public FlatTrack(string id, string kind, bool muted, IReadOnlyList<FlatClip> clips)
        {
            Id = id;
            Kind = kind;
            Muted = muted;
            Clips = clips;
        }

        public string Id { get; }

        /// <summary><c>video</c> or <c>audio</c>.</summary>
        public string Kind { get; }
        public bool Muted { get; }
        public IReadOnlyList<FlatClip> Clips { get; }
    }

    public sealed class FlatClip
    {
        public FlatClip(string clipId, string sourceId, string path, long inMs, long outMs, long timelineStartMs, double speed, double volume)
        {
            ClipId = clipId;
            SourceId = sourceId;
            Path = path;
            InMs = inMs;
            OutMs = outMs;
            TimelineStartMs = timelineStartMs;
            Speed = speed;
            Volume = volume;
        }

        public string ClipId { get; }
        public string SourceId { get; }
        public string Path { get; }
        public long InMs { get; }
        public long OutMs { get; }
        public long TimelineStartMs { get; }
        public double Speed { get; }
        public double Volume { get; }

        /// <summary>Time on the timeline: the source span divided by the speed.</summary>
        public long TimelineDurationMs => (long)Math.Round((OutMs - InMs) / Math.Max(Speed, 0.01));
    }

    /// <summary>A whisper model on this computer (Core <c>ModelStatus</c>).</summary>
    public sealed class TranscriptionModel
    {
        public TranscriptionModel(string id, bool installed, long sizeBytes)
        {
            Id = id;
            Installed = installed;
            SizeBytes = sizeBytes;
        }

        public string Id { get; }
        public bool Installed { get; }
        public long SizeBytes { get; }
    }

    /// <summary>Free transcription on this computer (D10, Core's whisper.cpp).</summary>
    public interface IOnDeviceTranscriber
    {
        /// <summary>False in builds without whisper; Studio then offers server transcription only.</summary>
        bool IsAvailable { get; }

        IReadOnlyList<TranscriptionModel> Models();

        Task DownloadAsync(string model, IProgress<double> progress, CancellationToken cancellationToken);

        void DeleteModel(string id);

        /// <summary>Timed segments in the shape of the on-device transcript body. Progress 0…1.</summary>
        Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(string model, string mediaPath, string? language, IProgress<double> progress, CancellationToken cancellationToken);
    }
}

namespace AlUmmahNow.CoreBridge.Studio
{
    /// <summary>What local encoding needs to know about a source file (Core <c>media_probe_json</c>).</summary>
    public sealed class MediaSourceInfo
    {
        public long? DurationMs { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        /// <summary>Degrees clockwise; 0 when the probe doesn't report it.</summary>
        public int Rotation { get; set; }
        public double Fps { get; set; }
        public bool HasVideo { get; set; }
        public bool HasAudio { get; set; }
        public string VideoCodec { get; set; } = "";
        public string AudioCodec { get; set; } = "";
        public int SampleRate { get; set; }
        public int Channels { get; set; }

        /// <summary>Reads Core's probe JSON (<c>{duration_ms, tracks:[{kind, codec, width, height, fps, sample_rate, channels}]}</c>).</summary>
        public static MediaSourceInfo FromProbeJson(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var tracks = J.Array(r, "tracks").ToList();
            var video = tracks.FirstOrDefault(t => J.Str(t, "kind") == "video");
            var info = new MediaSourceInfo
            {
                DurationMs = J.NumOrNull(r, "duration_ms") is double d ? (long)d : (long?)null,
                HasAudio = tracks.Any(t => J.Str(t, "kind") == "audio"),
                HasVideo = video.ValueKind == JsonValueKind.Object,
            };
            if (info.HasVideo)
            {
                info.Width = (int)J.Num(video, "width");
                info.Height = (int)J.Num(video, "height");
                info.Fps = J.NumOrNull(video, "fps") ?? 30;
                info.Rotation = (int)J.Num(video, "rotation");
                info.VideoCodec = J.Str(video, "codec") ?? "";
            }

            var audio = tracks.FirstOrDefault(t => J.Str(t, "kind") == "audio");
            if (info.HasAudio)
            {
                info.AudioCodec = J.Str(audio, "codec") ?? "";
                info.SampleRate = (int)(J.NumOrNull(audio, "sample_rate") ?? 48000);
                info.Channels = (int)(J.NumOrNull(audio, "channels") ?? 2);
            }

            return info;
        }
    }

    public interface IMediaProbe
    {
        Task<MediaSourceInfo> ProbeAsync(string path, CancellationToken cancellationToken);
    }
}
