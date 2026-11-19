namespace AlUmmahNow.Studio.Shell;

/// <summary>
/// The Studio window's size limits. The Studio is a desktop editor laid out for wide windows (sidebar
/// with the identity menu and the pages; two-column pages), so instead of a phone layout it has a
/// minimum size at which nothing is clipped or truncated. Below <see cref="RailBelow"/> the sidebar
/// shrinks to an icon rail.
/// </summary>
public static class StudioWindowSize
{
    public const int MinWidth = 1024;
    public const int MinHeight = 700;
    public const int DefaultWidth = 1280;
    public const int DefaultHeight = 800;

    /// <summary>Windows narrower than this show the sidebar as an icon rail.</summary>
    public const int RailBelow = 1200;

    public static bool IsRail(double width) => width < RailBelow;

    /// <summary>CI screenshots: "WIDTHxHEIGHT" (AUN_WINDOW_SIZE), clamped to the minimum; else the default.</summary>
    public static (int Width, int Height) Initial(string? value)
    {
        if (value != null && value.Split('x') is { Length: 2 } wh && int.TryParse(wh[0], out var w) && int.TryParse(wh[1], out var h))
            return (Math.Max(w, MinWidth), Math.Max(h, MinHeight));
        return (DefaultWidth, DefaultHeight);
    }
}
