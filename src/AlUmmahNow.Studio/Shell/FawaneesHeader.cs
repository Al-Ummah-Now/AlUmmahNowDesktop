using System.Globalization;
using AlUmmahNow.Fanous;

namespace AlUmmahNow.Studio.Shell;

/// <summary>An sRGB colour with alpha.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static Rgba Parse(string hex)
    {
        var h = hex.Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(c => new string(c, 2)));
        var v = uint.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return h.Length == 8 ? new((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v) : new((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    /// <summary>CSS <c>color-mix(in srgb, this p%, other)</c>, rounded to the nearest channel value.</summary>
    public Rgba Mix(Rgba other, double p) => new(Ch(R, other.R, p), Ch(G, other.G, p), Ch(B, other.B, p), Ch(A, other.A, p));

    /// <summary>CSS <c>color-mix(in srgb, this p%, transparent)</c>: the same colour at p alpha.</summary>
    public Rgba WithAlpha(double p) => this with { A = (byte)Math.Round(A * p) };

    /// <summary>"#rrggbb" (alpha dropped).</summary>
    public string Hex => $"#{R:x2}{G:x2}{B:x2}";

    /// <summary>A GTK CSS colour: "rgba(r,g,b,a)".</summary>
    public string Css => A == 255 ? Hex : FormattableString.Invariant($"rgba({R},{G},{B},{A / 255.0:0.###})");

    private static byte Ch(byte a, byte b, double p) => (byte)Math.Round((a * p) + (b * (1 - p)));
}

/// <summary>
/// The web's Fawanees header, colour for colour (owner rule 2026-11-18: the app header matches it
/// exactly, Light and Dark; the header is dark in both; no art, no glow). From the shared native
/// registry (FanousThemes: the header material and the nav parts), which mirrors the web header CSS: the Fawanees material <c>--m-header</c> (apps/web/src/theme/materials.css) is
/// <c>linear-gradient(90deg, nav-bg, color-mix(in srgb, nav-bg 84%, primary) 50%, nav-bg)</c> with
/// <c>--m-header-edge: 0 1px 0 color-mix(in srgb, primary 66%, transparent)</c> under the shared
/// header's <c>border-block-end: 1px solid nav-border</c> (packages/fanous-tokens/nav/aun-nav.css).
/// Text and icons: nav-text and nav-icon; hover: nav-hover; current item: nav-active-text on
/// nav-active-bg with the nav-indicator; search: nav-search-*.
/// </summary>
public sealed record FawaneesHeader(
    Rgba Background,
    Rgba Middle,
    Rgba Border,
    Rgba GoldRule,
    Rgba Text,
    Rgba Icon,
    Rgba Hover,
    Rgba ActiveBackground,
    Rgba ActiveText,
    Rgba Indicator,
    Rgba SearchBackground,
    Rgba SearchBorder,
    Rgba SearchText,
    Rgba SearchPlaceholder,
    Rgba SearchFocus,
    double SearchRadius)
{
    /// <summary>
    /// The web header search (<c>.ah-search</c>, apps/web/src/theme/system.css): a 40 px pill
    /// (<c>border-radius: var(--r-pill)</c>) with a 1 px inset nav-search-border, text 15 px with 16 px
    /// at its start, and a 52 px search button at its end behind a 1 px nav-search-border divider; on
    /// focus a 2 px ring in c-upload-focus.
    /// </summary>
    public const double SearchHeight = 40;

    /// <summary>The web field grows to 640 px (<c>.ah-search{max-width:640px}</c>); narrow windows (the rail) take 480.</summary>
    public const double SearchMaxWidth = 640;
    public const double SearchNarrowWidth = 480;
    public const double SearchButtonWidth = 52;
    public const double SearchFontSize = 15;
    public const double SearchTextInset = 16;

    /// <summary>The header for Dark (<c>dark: true</c>) or Light, from the shared native registry (FanousThemes).</summary>
    public static FawaneesHeader For(bool dark)
    {
        var mode = FanousThemes.Get(FanousThemeId.Fawanees).Mode(dark);
        var parts = mode.Parts;
        // The header material is one 90° linear layer: nav-bg, the gold-tinted middle, nav-bg.
        var stops = mode.Material.Header[0].Stops;
        static Rgba R(FanousRgba c) => new(c.R, c.G, c.B, c.Alpha);
        return new FawaneesHeader(
            R(stops[0].Color),
            R(stops[1].Color),
            R(parts.NavBorder),
            R(mode.Colors.Primary).WithAlpha(0.66),
            R(parts.NavText),
            R(parts.NavIcon),
            R(parts.NavHover),
            R(parts.NavActiveBg),
            R(parts.NavActiveText),
            R(parts.NavIndicator),
            R(parts.NavSearchBg),
            R(parts.NavSearchBorder),
            R(parts.NavSearchText),
            R(parts.NavSearchPlaceholder),
            R(parts.UploadFocus),
            FanousThemes.Get(FanousThemeId.Fawanees).Radius.Pill);
    }

    /// <summary>The Fawanees page (the registry's page material: one solid colour since AlUmmahNow#1596).</summary>
    public static Rgba Page(bool dark)
    {
        var page = FanousThemes.Get(FanousThemeId.Fawanees).Mode(dark).Material.Page;
        var fill = page[page.Length - 1];
        var c = fill.Kind == FanousFillKind.Solid ? fill.Color : fill.Stops[0].Color;
        return new Rgba(c.R, c.G, c.B, c.Alpha);
    }
}
