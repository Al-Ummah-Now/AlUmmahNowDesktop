using System.Text.Json;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Demo;
using AlUmmahNow.Studio.Timeline;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

/// <summary>Where titles go in the picture (shared by the Linux and Windows painters) and how they reach Core.</summary>
public sealed class TitleLayoutTests
{
    private const int W = 1280;
    private const int H = 720;

    /// <summary>A stand-in for the text engine: 0.5 em per character, 1.25 em per line.</summary>
    private static (double, double) Measure(string text, bool rtl, double px, bool bold, double max) => (Math.Min(max, text.Length * px * 0.5), px * 1.25);

    private static EdlTitle Title(TitlePosition position, string? ar, string? en, string? background = "#093041")
    {
        var text = new Dictionary<string, string>();
        if (ar != null) text["ar"] = ar;
        if (en != null) text["en"] = en;
        return new EdlTitle("t1", 1000, 3000, text, new EdlTitleStyle(TitleTemplates.FontFamily, 28, "#f7f1e8", background, position), "titles");
    }

    private static TitleDecor Decor(LowerThirdStyle style = LowerThirdStyle.Bar, LowerThirdPosition side = LowerThirdPosition.Start, byte[]? logo = null) =>
        new(style, side, "#ffa037", logo);

    [Fact]
    public void AnArabicLowerThirdStartsOnTheRightWithItsStripeThere()
    {
        var p = TitleLayout.Place(Title(TitlePosition.LowerThird, "قناة النور", "Al Noor channel"), Decor(), W, H, Measure, null);
        Assert.True(p.IsRtl);
        var box = p.Background!.Value;
        Assert.Equal(W - (W * 0.05), box.Right, 3);
        Assert.Equal(H - (H * 0.08), box.Bottom, 3);
        Assert.True(box.Width >= W * 0.45);
        Assert.Equal(box.Right, p.Accent!.Value.Right, 3);
        // Arabic first, bigger and bold; both lines hug the right (start) side, inside the stripe and padding.
        Assert.Equal(new[] { "قناة النور", "Al Noor channel" }, p.Lines.Select(l => l.Text));
        Assert.True(p.Lines[0].IsRtl);
        Assert.False(p.Lines[1].IsRtl);
        Assert.True(p.Lines[0].Bold && !p.Lines[1].Bold && p.Lines[0].FontPx > p.Lines[1].FontPx);
        Assert.Equal(p.Lines[0].Bounds.Right, p.Lines[1].Bounds.Right, 3);
        Assert.True(p.Lines[0].Bounds.Right < p.Accent.Value.X);
        Assert.True(p.Lines[1].Bounds.Y > p.Lines[0].Bounds.Bottom);
        Assert.False(p.Shadow);
    }

    [Fact]
    public void AnEnglishLowerThirdMirrorsIt()
    {
        var p = TitleLayout.Place(Title(TitlePosition.LowerThird, null, "Al Noor channel"), Decor(), W, H, Measure, null);
        Assert.False(p.IsRtl);
        Assert.Equal(W * 0.05, p.Background!.Value.X, 3);
        Assert.Equal(p.Background.Value.X, p.Accent!.Value.X, 3);
        Assert.True(p.Lines[0].Bounds.X > p.Accent.Value.Right);
    }

    [Theory]
    [InlineData(LowerThirdPosition.End, false)]
    [InlineData(LowerThirdPosition.End, true)]
    [InlineData(LowerThirdPosition.Center, true)]
    public void TheKitsSideIsReadInTheTitlesDirection(LowerThirdPosition side, bool arabic)
    {
        var title = arabic ? Title(TitlePosition.LowerThird, "قناة النور", null) : Title(TitlePosition.LowerThird, null, "Al Noor");
        var box = TitleLayout.Place(title, Decor(side: side), W, H, Measure, null).Background!.Value;
        if (side == LowerThirdPosition.Center) Assert.Equal(W / 2.0, box.X + (box.Width / 2), 3);
        else if (arabic) Assert.Equal(W * 0.05, box.X, 3);
        else Assert.Equal(W - (W * 0.05), box.Right, 3);
    }

    [Fact]
    public void TheLogoSitsBetweenTheStripeAndTheText()
    {
        var p = TitleLayout.Place(Title(TitlePosition.LowerThird, "قناة النور", "Al Noor channel"), Decor(logo: new byte[] { 1 }), W, H, Measure, 1.0);
        var logo = p.Logo!.Value;
        Assert.True(logo.Right < p.Accent!.Value.X);
        Assert.True(p.Lines.All(l => l.Bounds.Right < logo.X));
        Assert.Equal(logo.Width, logo.Height, 3);
        Assert.Equal(p.Lines[^1].Bounds.Bottom - p.Lines[0].Bounds.Y, logo.Height, 3);

        // No logo without its image, and none on centred titles.
        Assert.Null(TitleLayout.Place(Title(TitlePosition.LowerThird, "أ", null), Decor(), W, H, Measure, 1.0).Logo);
        Assert.Null(TitleLayout.Place(Title(TitlePosition.Center, "أ", null), Decor(logo: new byte[] { 1 }), W, H, Measure, 1.0).Logo);
    }

    [Fact]
    public void BoxAndMinimalTemplates()
    {
        var box = TitleLayout.Place(Title(TitlePosition.LowerThird, null, "Al Noor"), Decor(LowerThirdStyle.Box), W, H, Measure, null);
        Assert.NotNull(box.Background);
        Assert.Null(box.Accent);
        Assert.True(box.Background!.Value.Width < W * 0.45);

        var minimal = TitleLayout.Place(Title(TitlePosition.LowerThird, null, "Al Noor"), Decor(LowerThirdStyle.Minimal), W, H, Measure, null);
        Assert.Null(minimal.Background);
        Assert.True(minimal.Shadow);
        // The underline in the accent colour sits under the text.
        Assert.True(minimal.Accent!.Value.Y >= minimal.Lines[0].Bounds.Bottom);
        Assert.Equal(minimal.Lines[0].Bounds.X, minimal.Accent.Value.X, 3);
    }

    [Fact]
    public void ACentredTitleIsCentredBothWays()
    {
        var p = TitleLayout.Place(Title(TitlePosition.Center, "سوق دمشق", "Damascus market", background: null), Decor(), W, H, Measure, null);
        Assert.True(p.Centered);
        Assert.Null(p.Background);
        Assert.True(p.Shadow);
        foreach (var line in p.Lines) Assert.Equal(W / 2.0, line.Bounds.X + (line.Bounds.Width / 2), 3);
        var top = p.Lines[0].Bounds.Y;
        var bottom = p.Lines[^1].Bounds.Bottom;
        Assert.Equal(H / 2.0, (top + bottom) / 2, 3);
    }

    [Fact]
    public void SizesScaleWithThePicture()
    {
        Assert.Equal(TitleLayout.FontPx(28, 720) * 1.5, TitleLayout.FontPx(28, 1080), 6);
        var small = TitleLayout.Place(Title(TitlePosition.LowerThird, "أ", null), Decor(), 640, 360, Measure, null);
        var big = TitleLayout.Place(Title(TitlePosition.LowerThird, "أ", null), Decor(), 1280, 720, Measure, null);
        Assert.Equal(big.Background!.Value.Height / 2, small.Background!.Value.Height, 3);
    }

    [Fact]
    public void LongTextWrapsInsideTheLowerThird()
    {
        var wraps = new List<double>();
        TitleLayout.Place(Title(TitlePosition.LowerThird, new string('ب', 400), null), Decor(), W, H, (t, r, px, b, max) =>
        {
            wraps.Add(max);
            return (max, px * 1.25 * 3);
        }, null);
        Assert.All(wraps, max => Assert.True(max <= W * 0.62));
    }

    [Fact]
    public void ExportedTitlesAreCutToTheVideo()
    {
        var titles = new[]
        {
            Title(TitlePosition.LowerThird, "أ", null).With(startMs: 1000, durationMs: 2000),
            Title(TitlePosition.LowerThird, "ب", null).With(startMs: 7000, durationMs: 5000),
            Title(TitlePosition.LowerThird, "ج", null).With(startMs: 9000, durationMs: 1000),
        };
        var flat = new FlattenedTimeline(8000, Array.Empty<FlatTrack>(), titles);
        Assert.Equal(new[] { (1000L, 2000L), (7000L, 1000L) }, TitleLayout.ForExport(flat).Select(t => (t.StartMs, t.DurationMs)));
        Assert.Equal(new[] { "أ" }, TitleLayout.At(titles, 1500).Select(t => t.In("ar")));
        Assert.Empty(TitleLayout.At(titles, 3000));
    }

    [Theory]
    [InlineData("قناة النور", true)]
    [InlineData("  «قناة»", true)]
    [InlineData("123 قناة", true)]
    [InlineData("Al Noor قناة", false)]
    [InlineData("", false)]
    public void DirectionFollowsTheFirstLetter(string text, bool rtl) => Assert.Equal(rtl, TitleTemplates.IsRtl(text));

    [Fact]
    public void TemplatesTakeTheKitsColours()
    {
        var kit = new BrandKit { Primary = "#AA0000", Secondary = "#00BB00", TextColor = "#0000CC", LowerThirdStyle = LowerThirdStyle.Bar };
        var bar = TitleTemplates.StyleFor(TitleKind.LowerThird, kit);
        Assert.Equal(("#0000cc", "#00bb00", TitlePosition.LowerThird), (bar.Color, bar.Background, bar.Position));
        kit.LowerThirdStyle = LowerThirdStyle.Box;
        Assert.Equal("#aa0000", TitleTemplates.StyleFor(TitleKind.LowerThird, kit).Background);
        kit.LowerThirdStyle = LowerThirdStyle.Minimal;
        Assert.Null(TitleTemplates.StyleFor(TitleKind.LowerThird, kit).Background);
        var title = TitleTemplates.StyleFor(TitleKind.Title, kit);
        Assert.Equal((TitlePosition.Center, (string?)null), (title.Position, title.Background));
        Assert.True(title.SizePt > bar.SizePt);
        // A broken colour falls back to the server's default.
        Assert.Equal(BrandKit.DefaultText, TitleTemplates.StyleFor(TitleKind.Title, new BrandKit { TextColor = "teal" }).Color);
        Assert.Equal(new TitleDecor(LowerThirdStyle.Bar, LowerThirdPosition.Start, BrandKit.DefaultPrimary, null), TitleDecor.From(new BrandKit { Primary = "x" }, Array.Empty<byte>()) with { Logo = null });
    }

    [Fact]
    public void TitleOpsMatchCoresContract()
    {
        var title = new EdlTitle("lt1", 1000, 3000, new Dictionary<string, string> { ["en"] = "Al Noor", ["ar"] = "قناة النور", ["fr"] = "" },
            new EdlTitleStyle("Noto Sans Arabic", 28, "#f7f1e8", "#093041", TitlePosition.LowerThird));
        using var add = JsonDocument.Parse(EdlOp.AddTitle("titles", title));
        Assert.Equal("addTitle", add.RootElement.GetProperty("op").GetString());
        Assert.Equal("titles", add.RootElement.GetProperty("trackId").GetString());
        var t = add.RootElement.GetProperty("title");
        Assert.Equal(new[] { "id", "startMs", "durationMs", "text", "style" }, t.EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "ar", "en" }, t.GetProperty("text").EnumerateObject().Select(p => p.Name));
        Assert.Equal("lowerThird", t.GetProperty("style").GetProperty("position").GetString());
        Assert.Equal(28, t.GetProperty("style").GetProperty("sizePt").GetDouble());

        var plain = title.With(style: new EdlTitleStyle("Noto Sans Arabic", 44, "#ffffff", null, TitlePosition.Center));
        using var set = JsonDocument.Parse(EdlOp.SetTitle(plain));
        Assert.False(set.RootElement.GetProperty("title").GetProperty("style").TryGetProperty("background", out _));
        Assert.Equal("center", set.RootElement.GetProperty("title").GetProperty("style").GetProperty("position").GetString());
        Assert.Equal("""{"op":"removeTitle","titleId":"lt1"}""", EdlOp.RemoveTitle("lt1"));
    }

    [Fact]
    public void FlattenedTitlesAreRead()
    {
        var flat = FlattenedTimeline.Parse("""
        {"durationMs": 8000, "tracks": [],
         "titles": [{"titleId": "lt1", "trackId": "brand", "startMs": 600, "durationMs": 4000,
           "text": {"ar": "قناة النور", "en": "Al Noor channel"},
           "style": {"fontFamily": "Noto Naskh Arabic", "sizePt": 28.0, "color": "#f7f1e8", "background": "#093041", "position": "lowerThird"}}]}
        """);
        var t = Assert.Single(flat.Titles);
        Assert.Equal(("lt1", "brand", 600L, 4600L), (t.Id, t.TrackId, t.StartMs, t.EndMs));
        Assert.Equal("قناة النور", TitleTemplates.Label(t, "ar"));
        Assert.Equal("Al Noor channel", TitleTemplates.Label(t, "en"));
        Assert.Equal(new EdlTitleStyle("Noto Naskh Arabic", 28, "#f7f1e8", "#093041", TitlePosition.LowerThird), t.Style);
        Assert.Empty(FlattenedTimeline.Parse("""{"durationMs": 1, "tracks": []}""").Titles);
    }

    [Fact]
    public void TitleBlocksMoveInTheMiddleAndRetimeAtTheEdges()
    {
        var v = TimelineViewport.Fit(10_000, 1012, isRtl: false);
        var t = Title(TitlePosition.LowerThird, "أ", null);
        Assert.Equal(TitleGrip.Start, TitleHit.At(v, t, v.ToX(1000) + 2));
        Assert.Equal(TitleGrip.End, TitleHit.At(v, t, v.ToX(4000) - 3));
        Assert.Equal(TitleGrip.Body, TitleHit.At(v, t, v.ToX(2500)));
        Assert.Equal(TitleGrip.None, TitleHit.At(v, t, v.ToX(6000)));
    }

    [Fact]
    public async Task TheDemoKitHasALogoForTheTitles()
    {
        var demo = new DemoStudioServices();
        var logo = await demo.AssetDataAsync("asset-logo", null);
        Assert.True(logo.Length > 100);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4e, 0x47 }, logo.Take(4));
        Assert.Empty(await demo.AssetDataAsync("asset-intro", null));
    }
}
