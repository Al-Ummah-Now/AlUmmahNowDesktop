using AlUmmahNow.App.Theme;

namespace AlUmmahNow.Studio.Shell;

/// <summary>The role of each of the Studio's own brushes; one without a role would keep the theme's color.</summary>
public static class HighContrast
{
    public static readonly IReadOnlyDictionary<string, HighContrastRole> Roles = new Dictionary<string, HighContrastRole>(StringComparer.Ordinal)
    {
        ["StudioTextBrush"] = HighContrastRole.Text,
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
        ["StudioPickedCardBrush"] = HighContrastRole.Window,
        ["StudioPickedEdgeBrush"] = HighContrastRole.Highlight,
        ["StudioChartBrush"] = HighContrastRole.Highlight,
        ["StudioChartEdgeBrush"] = HighContrastRole.Highlight,
        // Not the highlight, which is the default button beside it
        ["StudioDangerFillBrush"] = HighContrastRole.Window,
        ["StudioDangerEdgeBrush"] = HighContrastRole.Text,
        ["StudioOnDangerFillBrush"] = HighContrastRole.Text,
        ["StudioOnGoldBrush"] = HighContrastRole.HighlightText,
    };
}
