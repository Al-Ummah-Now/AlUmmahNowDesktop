namespace AlUmmahNow.Studio.Timeline;

/// <summary>The timeline's ruler, from the top: the labels' room, the playhead's handle under it, the ticks at its foot.</summary>
public static class TimelineRuler
{
    /// <summary>Nothing but the labels: the playhead, its glow and its handle begin under it.</summary>
    public const double LabelRoom = 22;
    public const double HandleHeight = 14;
    public const double Tick = 6;
    public const double Height = LabelRoom + HandleHeight + 4;
}
