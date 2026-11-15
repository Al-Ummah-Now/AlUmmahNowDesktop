using System.Globalization;
using AlUmmahNow.CoreBridge.Studio;

namespace AlUmmahNow.Studio.Timeline;

/// <summary>A centred title, or a lower third (a name or caption in the bottom third of the picture).</summary>
public enum TitleKind { Title, LowerThird }

/// <summary>
/// How the account's brand kit dresses a title when it is drawn: the lower-third template (bar, box or minimal), which
/// side it sits on, the accent colour and the logo. The project keeps each title's own colours, size and position
/// (Core's <c>TitleStyle</c>); these come from the current kit, as on the Brand page preview.
/// </summary>
public sealed record TitleDecor(LowerThirdStyle Style, LowerThirdPosition Side, string Accent, byte[]? Logo)
{
    public static TitleDecor From(BrandKit kit, byte[]? logo) =>
        new(kit.LowerThirdStyle, kit.LowerThirdPosition, BrandKit.IsValidColor(kit.Primary) ? kit.Primary : BrandKit.DefaultPrimary, logo is { Length: > 0 } ? logo : null);

    public static TitleDecor Default { get; } = From(new BrandKit(), null);
}

/// <summary>New titles take their look from the brand kit, the same way the Brand page previews it.</summary>
public static class TitleTemplates
{
    /// <summary>A family with Arabic and Latin; each platform falls back to its own UI font when it isn't installed.</summary>
    public const string FontFamily = "Noto Sans Arabic";

    public const double TitleSizePt = 44;
    public const double LowerThirdSizePt = 28;

    /// <summary>The track every editor puts its titles on.</summary>
    public const string TrackId = "titles";

    public static EdlTitleStyle StyleFor(TitleKind kind, BrandKit kit)
    {
        string Color(string? value, string fallback) => BrandKit.IsValidColor(value) ? value!.ToLowerInvariant() : fallback;
        var text = Color(kit.TextColor, BrandKit.DefaultText);
        if (kind == TitleKind.Title) return new EdlTitleStyle(FontFamily, TitleSizePt, text, null, TitlePosition.Center);

        // Bar: the secondary colour with a primary stripe. Box: the primary colour. Minimal: no box, a primary underline.
        var background = kit.LowerThirdStyle switch
        {
            LowerThirdStyle.Box => Color(kit.Primary, BrandKit.DefaultPrimary),
            LowerThirdStyle.Minimal => null,
            _ => Color(kit.Secondary, BrandKit.DefaultSecondary),
        };
        return new EdlTitleStyle(FontFamily, LowerThirdSizePt, text, background, TitlePosition.LowerThird);
    }

    public static TitleKind KindOf(EdlTitle title) => title.Style.Position == TitlePosition.Center ? TitleKind.Title : TitleKind.LowerThird;

    /// <summary>The title's lines, Arabic first: what the picture shows, top to bottom.</summary>
    public static IReadOnlyList<(string Locale, string Text)> Lines(EdlTitle title)
    {
        static int Rank(string locale) => locale == "ar" ? 0 : locale == "en" ? 1 : 2;
        return title.Text
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .OrderBy(p => Rank(p.Key))
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => (p.Key, p.Value.Trim()))
            .ToList();
    }

    /// <summary>One line for lists and the timeline: the text in the interface language, else the first line.</summary>
    public static string Label(EdlTitle title, string language)
    {
        var lines = Lines(title);
        return lines.FirstOrDefault(l => l.Locale == language).Text ?? lines.FirstOrDefault().Text ?? "";
    }

    /// <summary>True when text should run right to left (it starts with Arabic, Hebrew, Syriac, Thaana or N'Ko).</summary>
    public static bool IsRtl(string text)
    {
        foreach (var c in text)
        {
            if (c >= 0x0590 && c <= 0x08FF) return true;
            if (c >= 0xFB1D && c <= 0xFDFF) return true;
            if (c >= 0xFE70 && c <= 0xFEFF) return true;
            if (char.IsLetter(c)) return false;
        }

        return false;
    }

    /// <summary>A <c>#rrggbb</c> colour as 0…1 channels.</summary>
    public static (double R, double G, double B) Rgb(string? hex, string fallback)
    {
        var v = BrandKit.IsValidColor(hex) ? hex! : fallback;
        int Part(int i) => int.Parse(v.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (Part(1) / 255.0, Part(3) / 255.0, Part(5) / 255.0);
    }
}

/// <summary>A rectangle in picture pixels.</summary>
public readonly record struct TitleRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>One line of a title, laid out: its text, whether it shapes right to left, its size and its box.</summary>
public sealed record TitleLine(string Text, bool IsRtl, double FontPx, bool Bold, TitleRect Bounds);

/// <summary>Everything a platform needs to draw a title over a picture of the given size.</summary>
public sealed record TitlePlacement(
    TitleRect? Background,
    string? BackgroundColor,
    TitleRect? Accent,
    string AccentColor,
    TitleRect? Logo,
    IReadOnlyList<TitleLine> Lines,
    double WrapWidth,
    string TextColor,
    bool Shadow,
    bool IsRtl,
    bool Centered);

/// <summary>Measures a line wrapped at <c>maxWidth</c> pixels; returns its pixel size.</summary>
public delegate (double Width, double Height) MeasureLine(string text, bool isRtl, double fontPx, bool bold, double maxWidth);

/// <summary>
/// Places a title in the picture, the same way on every platform, so the preview and the export match. Each line shapes
/// in its own direction (Arabic right to left); the block's start side follows the first line, so an Arabic lower third
/// starts on the right. The time axis is unaffected: that is the timeline's business.
/// </summary>
public static class TitleLayout
{
    /// <summary>Points are 1/72 inch at 96 dpi on a 720-line picture, scaled with the picture height.</summary>
    public static double FontPx(double sizePt, int height) => sizePt * 96 / 72 * height / 720.0;

    public static TitlePlacement Place(EdlTitle title, TitleDecor decor, int width, int height, MeasureLine measure, double? logoAspect)
    {
        var style = title.Style;
        var px = FontPx(style.SizePt, height);
        var lines = TitleTemplates.Lines(title);
        var rtl = lines.Count > 0 && TitleTemplates.IsRtl(lines[0].Text);
        var centered = style.Position == TitlePosition.Center;
        var minimal = decor.Style == LowerThirdStyle.Minimal && !centered;
        var hasBox = style.Background != null && !minimal;
        var safeX = width * 0.05;
        var pad = hasBox ? px * 0.45 : 0;
        var accentW = !centered && hasBox && decor.Style == LowerThirdStyle.Bar ? Math.Max(2, px * 0.16) : 0;
        var gap = px * 0.15;
        var logoGap = px * 0.4;
        var aspect = Math.Clamp(logoAspect ?? 0, 0, 2.5);
        var withLogo = !centered && decor.Logo != null && aspect > 0;

        // The text block is at most 62% of the width for lower thirds (they leave the picture visible), the whole safe
        // width for centred titles; an estimate of the logo's width is reserved first.
        var estimate = (px * 1.25) + (lines.Count > 1 ? px * 0.72 * 1.25 * (lines.Count - 1) : 0);
        var reserved = withLogo ? (estimate * aspect) + logoGap : 0;
        var limit = centered ? width - (2 * safeX) : Math.Min(width * 0.62, width - (2 * safeX));
        var wrap = Math.Max(px * 2, limit - (2 * pad) - accentW - reserved);

        var measured = new List<(string Text, bool Rtl, double Px, bool Bold, double W, double H)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var linePx = i == 0 ? px : px * 0.72;
            var lineRtl = TitleTemplates.IsRtl(lines[i].Text);
            var (w, h) = measure(lines[i].Text, lineRtl, linePx, i == 0, wrap);
            measured.Add((lines[i].Text, lineRtl, linePx, i == 0, Math.Min(w, wrap), h));
        }

        var textW = measured.Count == 0 ? 0 : measured.Max(m => m.W);
        var textH = measured.Sum(m => m.H) + (Math.Max(0, measured.Count - 1) * gap);
        var logoH = withLogo ? textH : 0;
        var logoW = withLogo ? logoH * aspect : 0;
        var contentW = accentW + pad + (withLogo ? logoW + logoGap : 0) + textW + pad;
        var boxW = decor.Style == LowerThirdStyle.Bar && hasBox ? Math.Max(contentW, Math.Min(width * 0.45, width - (2 * safeX))) : contentW;
        var boxH = textH + (2 * pad);

        double x;
        if (centered || decor.Side == LowerThirdPosition.Center)
        {
            x = (width - boxW) / 2;
        }
        else
        {
            var atLeft = (decor.Side == LowerThirdPosition.Start) != rtl;
            x = atLeft ? safeX : width - safeX - boxW;
        }

        var y = style.Position switch
        {
            TitlePosition.Center => (height - boxH) / 2,
            TitlePosition.Top => height * 0.08,
            _ => height - (height * 0.08) - boxH,
        };

        // From the start side: the accent stripe, the logo, then the text.
        var startEdge = rtl ? x + boxW : x;
        double Inward(double offset, double size) => rtl ? startEdge - offset - size : startEdge + offset;
        TitleRect? accent = accentW > 0 ? new TitleRect(Inward(0, accentW), y, accentW, boxH) : null;
        if (minimal) accent = new TitleRect(x, y + boxH + (px * 0.12), Math.Max(textW, px), Math.Max(2, px * 0.08));
        TitleRect? logo = withLogo ? new TitleRect(Inward(accentW + pad, logoW), y + pad, logoW, logoH) : null;
        var textOffset = accentW + pad + (withLogo ? logoW + logoGap : 0);

        var placed = new List<TitleLine>();
        var top = y + pad;
        foreach (var m in measured)
        {
            double lx;
            if (centered) lx = x + ((boxW - m.W) / 2);
            else if (decor.Side == LowerThirdPosition.Center && !hasBox) lx = x + ((boxW - m.W) / 2);
            else lx = rtl ? startEdge - textOffset - m.W : startEdge + textOffset;
            placed.Add(new TitleLine(m.Text, m.Rtl, m.Px, m.Bold, new TitleRect(lx, top, m.W, m.H)));
            top += m.H + gap;
        }

        return new TitlePlacement(
            hasBox ? new TitleRect(x, y, boxW, boxH) : null,
            hasBox ? style.Background : null,
            accent,
            decor.Accent,
            logo,
            placed,
            wrap,
            style.Color,
            !hasBox,
            rtl,
            centered);
    }

    /// <summary>The titles on screen at a moment, in the order they are drawn.</summary>
    public static IReadOnlyList<EdlTitle> At(IEnumerable<EdlTitle> titles, long ms) =>
        titles.Where(t => ms >= t.StartMs && ms < t.EndMs).ToList();

    /// <summary>Titles cut to the picture: a title is drawn only while there is video (Core never lengthens the export).</summary>
    public static IReadOnlyList<EdlTitle> ForExport(FlattenedTimeline timeline) =>
        timeline.Titles
            .Where(t => t.StartMs < timeline.DurationMs && t.DurationMs > 0 && TitleTemplates.Lines(t).Count > 0)
            .Select(t => t.EndMs > timeline.DurationMs ? t.With(durationMs: timeline.DurationMs - t.StartMs) : t)
            .ToList();
}

/// <summary>A title drawn to a transparent PNG at the export size, shown from <see cref="StartMs"/>.</summary>
public sealed record TitleOverlay(string ImagePath, long StartMs, long DurationMs);

/// <summary>Draws a title, with the platform's text engine, onto a transparent picture-sized PNG.</summary>
public interface ITitlePainter
{
    void WritePng(EdlTitle title, TitleDecor decor, int width, int height, string path);
}

/// <summary>Which part of a title's block on the timeline a pointer is over.</summary>
public enum TitleGrip { None, Body, Start, End }

public static class TitleHit
{
    /// <summary>Edges within <paramref name="edgePx"/> pixels retime; the rest moves.</summary>
    public static TitleGrip At(TimelineViewport v, EdlTitle title, double x, double edgePx = 6)
    {
        var a = v.ToX(title.StartMs);
        var b = v.ToX(title.EndMs);
        var left = Math.Min(a, b);
        var right = Math.Max(a, b);
        if (x < left - edgePx || x > right + edgePx) return TitleGrip.None;
        if (right - left > 3 * edgePx)
        {
            if (Math.Abs(x - a) <= edgePx) return TitleGrip.Start;
            if (Math.Abs(x - b) <= edgePx) return TitleGrip.End;
        }

        return x >= left && x <= right ? TitleGrip.Body : TitleGrip.None;
    }
}
