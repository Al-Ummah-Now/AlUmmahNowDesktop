using AlUmmahNow.Studio.Shell;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

/// <summary>The app header uses the web Fawanees header's colours exactly (owner rule 2026-11-18).</summary>
public sealed class FawaneesHeaderTests
{
    [Fact]
    public void DarkMatchesTheWebHeader()
    {
        var h = FawaneesHeader.For(dark: true);
        Assert.Equal("#0b1d2a", h.Background.Hex);
        // color-mix(in srgb, #0b1d2a 84%, #f4ae3a): 0.84*11+0.16*244 = 48.28 → 48, 52.2 → 52, 44.56 → 45
        Assert.Equal("#30342d", h.Middle.Hex);
        Assert.Equal("#184456", h.Border.Hex);
        Assert.Equal(new Rgba(0xF4, 0xAE, 0x3A, 168), h.GoldRule);
        Assert.Equal("#f7f1e8", h.Text.Hex);
        Assert.Equal("#d6dfe2", h.Icon.Hex);
        Assert.Equal("#184456", h.Hover.Hex);
        Assert.Equal("#ffc46b", h.ActiveText.Hex);
        Assert.Equal("#ffa037", h.Indicator.Hex);
        Assert.Equal("#09151e", h.SearchBackground.Hex);
        Assert.Equal("#8ba3ae", h.SearchBorder.Hex);
        Assert.Equal("#b0c0c8", h.SearchPlaceholder.Hex);
        Assert.Equal("#f5cf92", h.SearchFocus.Hex);
        // The web search is a pill: --r-pill is 999px for Fawanees.
        Assert.Equal(999, h.SearchRadius);
    }

    [Fact]
    public void LightKeepsADarkHeaderWithItsOwnNavColours()
    {
        var h = FawaneesHeader.For(dark: false);
        Assert.Equal("#0e2b3d", h.Background.Hex);
        Assert.Equal("#33403d", h.Middle.Hex);
        Assert.Equal("#fbefd0", h.Text.Hex);
        Assert.Equal("#f5cf92", h.Icon.Hex);
        Assert.Equal("#fbc661", h.ActiveText.Hex);
        Assert.Equal("#f4ae3a", h.Indicator.Hex);
        Assert.Equal("#184456", h.SearchBackground.Hex);
        Assert.Equal("#d6dfe2", h.SearchPlaceholder.Hex);
        Assert.Equal("#fbc661", h.SearchFocus.Hex);
    }

    [Fact]
    public void ThePageIsPlainCreamInLightAndNightInDark()
    {
        // The registry's page material is one solid colour since AlUmmahNow#1596 (as the web).
        Assert.Equal("#f3eee5", FawaneesHeader.Page(dark: false).Hex);
        Assert.Equal("#09151e", FawaneesHeader.Page(dark: true).Hex);
    }

    [Fact]
    public void CssColoursAreExact()
    {
        Assert.Equal("rgba(244,174,58,0.659)", new Rgba(0xF4, 0xAE, 0x3A, 168).Css);
        Assert.Equal("#0b1d2a", Rgba.Parse("#0B1D2A").Css);
    }
}
