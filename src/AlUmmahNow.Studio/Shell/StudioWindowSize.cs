namespace AlUmmahNow.Studio.Shell;

/// <summary>
/// The Studio window's size limits. The Studio is a desktop editor laid out for wide windows (header
/// with five pages, the Acting as picker and the account menu; two-column forms), so instead of a
/// phone layout it has a minimum size at which nothing is clipped or truncated.
/// </summary>
public static class StudioWindowSize
{
    public const int MinWidth = 1100;
    public const int MinHeight = 720;
    public const int DefaultWidth = 1280;
    public const int DefaultHeight = 800;

    /// <summary>CI screenshots: "WIDTHxHEIGHT" (AUN_WINDOW_SIZE), clamped to the minimum; else the default.</summary>
    public static (int Width, int Height) Initial(string? value)
    {
        if (value != null && value.Split('x') is { Length: 2 } wh && int.TryParse(wh[0], out var w) && int.TryParse(wh[1], out var h))
            return (Math.Max(w, MinWidth), Math.Max(h, MinHeight));
        return (DefaultWidth, DefaultHeight);
    }
}
