namespace AlUmmahNow.Studio.Timeline;

/// <summary>
/// The editor's CI scene (both platforms): after the cut, a bilingual lower third with the demo channel's name and a
/// centred title naming the second clip. The checks sample the export at <see cref="PreviewMs"/>.
/// </summary>
public static class EditorScenes
{
    public const long PreviewMs = 1500;

    public static IReadOnlyList<(long StartMs, TitleKind Kind, string Arabic, string English)> Titles { get; } = new[]
    {
        (600L, TitleKind.LowerThird, "قناة النور", "Al Noor channel"),
        (5200L, TitleKind.Title, "سوق دمشق", "Damascus market"),
    };
}

/// <summary>CI: is the lower third in the exported picture? Its box's bottom padding is plain background colour.</summary>
public static class ExportCheck
{
    public static bool TitleDrawn(EditorViewModel vm, Func<int, int, (byte R, byte G, byte B)> pixel, out string detail)
    {
        var s = vm.Settings;
        var title = TitleLayout.At(vm.Titles, EditorScenes.PreviewMs).FirstOrDefault(t => t.Style.Position == CoreBridge.Studio.TitlePosition.LowerThird);
        if (title?.Style.Background is not { } background)
        {
            detail = "no boxed lower third at the checked time";
            return false;
        }

        // The box's bottom edge and padding don't depend on the text, so a rough measure places the sample.
        var placement = TitleLayout.Place(title, vm.Decor, s.Width, s.Height, (_, _, px, _, max) => (Math.Min(max, s.Width * 0.2), px * 1.3), 1);
        var box = placement.Background!.Value;
        var pad = TitleLayout.FontPx(title.Style.SizePt, s.Height) * 0.45;
        var inside = ((int)(box.X + (box.Width / 2)), (int)(box.Bottom - (pad * 0.35)));
        var outside = (s.Width / 2, (int)(s.Height * 0.3));
        var want = TitleTemplates.Rgb(background, background);
        var got = pixel(inside.Item1, inside.Item2);
        var away = pixel(outside.Item1, outside.Item2);
        bool Near((byte R, byte G, byte B) p) =>
            Math.Abs(p.R - (want.R * 255)) <= 48 && Math.Abs(p.G - (want.G * 255)) <= 48 && Math.Abs(p.B - (want.B * 255)) <= 48;
        var ok = Near(got) && !Near(away) && away.R + away.G + away.B > 60;
        detail = $"want {background} at {inside} got {got}; video at {outside} {away}; {(ok ? "ok" : "MISSING")}";
        return ok;
    }
}
