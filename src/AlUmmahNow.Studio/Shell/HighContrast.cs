namespace AlUmmahNow.Studio.Shell;

/// <summary>What a Studio brush of Windows is in high contrast, where the system's colors are the only ones.</summary>
public enum HighContrastRole
{
    Text,
    Window,
    Link,
    Highlight,
    HighlightText,

    /// <summary>A decoration that is not drawn.</summary>
    None,
}

/// <summary>The role of each of the Studio's own brushes; one without a role would keep the theme's color.</summary>
public static class HighContrast
{
    public static readonly IReadOnlyDictionary<string, HighContrastRole> Roles = new Dictionary<string, HighContrastRole>(StringComparer.Ordinal)
    {
        ["StudioMutedBrush"] = HighContrastRole.Text,
        ["StudioDangerBrush"] = HighContrastRole.Text,
        ["StudioSelectedTextBrush"] = HighContrastRole.Text,
        ["StudioEdgeBrush"] = HighContrastRole.Text,
        ["StudioEdgeStrongBrush"] = HighContrastRole.Text,
        ["StudioLinkBrush"] = HighContrastRole.Link,
        ["StudioPageBrush"] = HighContrastRole.Window,
        ["StudioSidebarBrush"] = HighContrastRole.Window,
        ["StudioCardBrush"] = HighContrastRole.Window,
        ["StudioMenuBrush"] = HighContrastRole.Window,
        ["StudioSheenBrush"] = HighContrastRole.None,
        ["StudioSelectedBrush"] = HighContrastRole.None,
        ["StudioDangerFillBrush"] = HighContrastRole.Highlight,
        ["StudioOnDangerFillBrush"] = HighContrastRole.HighlightText,
        ["StudioOnGoldBrush"] = HighContrastRole.HighlightText,
    };
}
