using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlUmmahNow.CoreBridge.Studio;

namespace AlUmmahNow.Studio.Timeline;

/// <summary>The export size and rate (the project's <c>settings</c>).</summary>
public sealed record ProjectSettings(int Width, int Height, double Fps, int SampleRate, int Channels);

/// <summary>Renders a flattened edit list to an MP4 (H.264 + AAC) on the platform's media stack (GES, MediaComposition).</summary>
public interface ITimelineRenderer
{
    /// <summary>Progress 0…1. Throws on failure; a cancelled render throws <see cref="OperationCanceledException"/>.</summary>
    Task RenderAsync(FlattenedTimeline timeline, ProjectSettings settings, string output, IProgress<double> progress, CancellationToken cancellationToken);
}

/// <summary>
/// The parts of Core's project format (schema v1) the editor writes itself: a new project and its sources. Every
/// timeline edit goes through Core (<see cref="IEdlEditing"/>), never through these helpers.
/// </summary>
public static class EdlProject
{
    public const string VideoTrack = "v1";

    public static string New(string id, string title, DateTimeOffset now)
    {
        var ms = now.ToUnixTimeMilliseconds();
        var project = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = id,
            ["metadata"] = new JsonObject { ["title"] = title, ["createdAtMs"] = ms, ["updatedAtMs"] = ms },
            ["settings"] = new JsonObject { ["width"] = 1280, ["height"] = 720, ["fps"] = 30.0, ["sampleRate"] = 48000, ["channels"] = 2 },
            ["sources"] = new JsonArray(),
            ["tracks"] = new JsonArray { new JsonObject { ["id"] = VideoTrack, ["kind"] = "video" } },
        };
        return project.ToJsonString();
    }

    /// <summary>
    /// Adds a probed file as a source. The first video source sets the export size and rate (even dimensions, the rate
    /// capped at 60), so an export matches what was shot.
    /// </summary>
    public static string AddSource(string json, string sourceId, string path, MediaSourceInfo info)
    {
        var project = Parse(json);
        var sources = project["sources"] as JsonArray ?? new JsonArray();
        project["sources"] = sources;
        var first = !sources.Any(s => s?["video"] != null);
        var source = new JsonObject
        {
            ["id"] = sourceId,
            ["path"] = path,
            ["durationMs"] = Math.Max(0, info.DurationMs ?? 0),
        };
        if (info.HasVideo)
        {
            source["video"] = new JsonObject { ["codec"] = Or(info.VideoCodec, "h264"), ["width"] = info.Width, ["height"] = info.Height, ["fps"] = info.Fps > 0 ? info.Fps : 30.0 };
        }

        if (info.HasAudio)
        {
            source["audio"] = new JsonObject { ["codec"] = Or(info.AudioCodec, "aac"), ["sampleRate"] = info.SampleRate > 0 ? info.SampleRate : 48000, ["channels"] = info.Channels > 0 ? info.Channels : 2 };
        }

        sources.Add(source);
        if (first && info.HasVideo && info.Width > 0 && info.Height > 0)
        {
            var portrait = info.Rotation % 180 != 0;
            var settings = project["settings"] as JsonObject ?? new JsonObject();
            settings["width"] = Even(portrait ? info.Height : info.Width);
            settings["height"] = Even(portrait ? info.Width : info.Height);
            settings["fps"] = Math.Min(60, info.Fps > 0 ? info.Fps : 30.0);
            project["settings"] = settings;
        }

        return project.ToJsonString();
    }

    public static ProjectSettings Settings(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var s = doc.RootElement.TryGetProperty("settings", out var v) ? v : default;
        int I(string name, int fallback) => s.ValueKind == JsonValueKind.Object && s.TryGetProperty(name, out var p) && p.TryGetInt32(out var x) ? x : fallback;
        var fps = s.ValueKind == JsonValueKind.Object && s.TryGetProperty("fps", out var f) && f.TryGetDouble(out var d) ? d : 30;
        return new ProjectSettings(I("width", 1280), I("height", 720), fps, I("sampleRate", 48000), I("channels", 2));
    }

    public static int SourceCount(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("sources", out var s) && s.ValueKind == JsonValueKind.Array ? s.GetArrayLength() : 0;
    }

    /// <summary>The source's file name, for clip labels.</summary>
    public static IReadOnlyDictionary<string, string> SourceNames(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var names = new Dictionary<string, string>();
        if (doc.RootElement.TryGetProperty("sources", out var s) && s.ValueKind == JsonValueKind.Array)
        {
            foreach (var src in s.EnumerateArray())
            {
                if (src.TryGetProperty("id", out var id) && src.TryGetProperty("path", out var p)) names[id.GetString() ?? ""] = Path.GetFileNameWithoutExtension(p.GetString() ?? "");
            }
        }

        return names;
    }

    /// <summary>
    /// True for an existing, fully qualified local file. Project files can come from anyone, so a source on a UNC or
    /// device path (which would make Windows authenticate to that host), a URI, or a missing file is never opened.
    /// </summary>
    public static bool IsLocalMediaPath(string? path)
    {
        // The shape is checked before any file system call: even asking whether a UNC file exists contacts its host.
        if (path == null || !HasLocalShape(path)) return false;
        try
        {
            var info = new FileInfo(path);
            return info.Exists && (info.Attributes & (FileAttributes.Directory | FileAttributes.Device)) == 0;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Windows: drive-rooted (<c>C:\…</c>) and still not UNC once normalised. Elsewhere: rooted at a single
    /// <c>/</c>. Never two leading separators in any mix (<c>\\</c>, <c>//</c>, <c>\/</c>, <c>/\</c>), never a URI.
    /// </summary>
    public static bool HasLocalShape(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 2 || path.Contains("://", StringComparison.Ordinal) || path.IndexOf('\0') >= 0) return false;
        static bool Sep(char c) => c == '\\' || c == '/';
        if (Sep(path[0]) && Sep(path[1])) return false;
        if (OperatingSystem.IsWindows())
        {
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || !Sep(path[2])) return false;
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }

            return full.Length >= 3 && char.IsAsciiLetter(full[0]) && full[1] == ':' && !(Sep(full[0]) && Sep(full[1]));
        }

        return path[0] == '/' && Path.IsPathFullyQualified(path);
    }

    /// <summary>Every source path in the project.</summary>
    public static IReadOnlyList<string> SourcePaths(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var paths = new List<string>();
        if (doc.RootElement.TryGetProperty("sources", out var s) && s.ValueKind == JsonValueKind.Array)
        {
            foreach (var src in s.EnumerateArray()) paths.Add(src.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "");
        }

        return paths;
    }

    /// <summary>Stamps <c>metadata.updatedAtMs</c>.</summary>
    public static string Touch(string json, DateTimeOffset now)
    {
        var project = Parse(json);
        if (project["metadata"] is JsonObject m) m["updatedAtMs"] = now.ToUnixTimeMilliseconds();
        return project.ToJsonString();
    }

    public static string Ms(long ms) => (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);

    private static JsonObject Parse(string json) => JsonNode.Parse(json) as JsonObject ?? throw new JsonException("Project is not an object");

    private static int Even(int v) => Math.Max(2, v & ~1);

    private static string Or(string value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;
}

/// <summary>Editor times and file names, the same on every platform.</summary>
public static class EditorTime
{
    /// <summary>
    /// The time axis is never mirrored: 0:00 sits on the left in Arabic too, as on the Mac editor and in video editors
    /// generally. Only the toolbars, the clip list and labels follow the interface direction.
    /// </summary>
    public const bool TimeIsRtl = false;

    /// <summary>Saved projects are Core's EDL JSON.</summary>
    public const string ProjectExtension = ".aunedit";

    /// <summary>m:ss.t (or h:mm:ss.t), always with Latin digits so times line up.</summary>
    public static string Format(long ms)
    {
        ms = Math.Max(0, ms);
        var tenths = ms / 100 % 10;
        var seconds = ms / 1000;
        var h = seconds / 3600;
        var m = seconds / 60 % 60;
        var s = seconds % 60;
        return h > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3}", h, m, s, tenths)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}.{2}", m, s, tenths);
    }
}

/// <summary>Maps timeline milliseconds to pixels, mirrored in RTL. Pure, shared by each platform's timeline.</summary>
public sealed record TimelineViewport(double PixelsPerMs, double Width, bool IsRtl, double Inset = 0)
{
    /// <summary>Fits <paramref name="durationMs"/> into <paramref name="width"/> with an inset at both ends.</summary>
    public static TimelineViewport Fit(long durationMs, double width, bool isRtl, double inset = 12) =>
        new(Math.Max(1e-6, (width - (2 * inset)) / Math.Max(1000, durationMs)), width, isRtl, inset);

    public double ToX(long ms)
    {
        var x = Inset + (ms * PixelsPerMs);
        return IsRtl ? Width - x : x;
    }

    public long ToMs(double x) => (long)Math.Round(((IsRtl ? Width - x : x) - Inset) / PixelsPerMs);
}
