using System.Text.Json;

namespace AlUmmahNow.Studio.Timeline;

public enum TrackKind { Video, Audio, Title, Caption }

public sealed class Clip
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Source { get; set; } = "";
    public double Start { get; set; }
    public double SourceIn { get; set; }
    public double Duration { get; set; }
    public double End => Start + Duration;
}

public sealed class Track
{
    public TrackKind Kind { get; set; }
    public List<Clip> Clips { get; set; } = new();
}

/// <summary>
/// Spike of the edit model (Epic 30.5). The project format moves to aun-media when it ships;
/// this exercises the editing operations the timeline UI needs.
/// </summary>
public sealed class TimelineProject
{
    public const double MinClipDuration = 0.1;

    public int Version { get; set; } = 1;
    public List<Track> Tracks { get; set; } = new();
    public double Duration => Tracks.SelectMany(t => t.Clips).Select(c => c.End).DefaultIfEmpty(0).Max();

    /// <summary>Splits the clip under the given time. Returns the new right half, or null.</summary>
    public Clip? Split(Track track, double time)
    {
        var clip = track.Clips.FirstOrDefault(c => time > c.Start + MinClipDuration && time < c.End - MinClipDuration);
        if (clip == null) return null;
        var offset = time - clip.Start;
        var right = new Clip { Source = clip.Source, Start = time, SourceIn = clip.SourceIn + offset, Duration = clip.Duration - offset };
        clip.Duration = offset;
        track.Clips.Insert(track.Clips.IndexOf(clip) + 1, right);
        return right;
    }

    /// <summary>Removes a clip and closes the gap by moving later clips left.</summary>
    public void RippleDelete(Track track, Clip clip)
    {
        var duration = clip.Duration;
        var end = clip.End;
        track.Clips.Remove(clip);
        foreach (var later in track.Clips.Where(c => c.Start >= end)) later.Start -= duration;
    }

    /// <summary>Moves a clip's in-point, keeping its out-point fixed in the timeline.</summary>
    public void TrimStart(Clip clip, double newStart)
    {
        var limit = clip.End - MinClipDuration;
        var earliest = clip.Start - clip.SourceIn;
        var start = Math.Min(limit, Math.Max(earliest, newStart));
        var delta = start - clip.Start;
        clip.Start = start;
        clip.SourceIn += delta;
        clip.Duration -= delta;
    }

    public void TrimEnd(Clip clip, double newEnd) => clip.Duration = Math.Max(MinClipDuration, newEnd - clip.Start);

    public string ToJson() => JsonSerializer.Serialize(this);

    public static TimelineProject FromJson(string json) =>
        JsonSerializer.Deserialize<TimelineProject>(json) ?? throw new JsonException("Empty project");
}

/// <summary>Maps timeline seconds to pixels, mirrored in RTL. Pure, shared by each platform's timeline.</summary>
public sealed record TimelineViewport(double PixelsPerSecond, double ScrollSeconds, double Width, bool IsRtl)
{
    public double ToX(double time)
    {
        var x = (time - ScrollSeconds) * PixelsPerSecond;
        return IsRtl ? Width - x : x;
    }

    public double ToTime(double x) => ((IsRtl ? Width - x : x) / PixelsPerSecond) + ScrollSeconds;
}
