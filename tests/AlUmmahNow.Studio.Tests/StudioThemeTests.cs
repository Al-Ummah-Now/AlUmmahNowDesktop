using AlUmmahNow.Studio.Shell;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

/// <summary>Lantern Glass assets ship inside the Studio assembly, from the shared Fawanees registry.</summary>
public sealed class StudioThemeTests
{
    [Theory]
    [InlineData("Fanous.Colors.xaml", "FanousPrimaryColor")]
    [InlineData("Fanous.Tokens.xaml", "FanousRadiusLg")]
    [InlineData("style.css", "@define-color fanous_primary #f4ae3a")]
    [InlineData("style-dark.css", "@define-color fanous_primary #f4ae3a")]
    public void FawaneesTokensAreBundled(string file, string expected) => Assert.Contains(expected, StudioTheme.Fanous(file));

    [Fact]
    public void BothThemesAreInTheWinUiColours()
    {
        var colors = StudioTheme.Fanous("Fanous.Colors.xaml");
        Assert.Contains("x:Key=\"Default\"", colors);
        Assert.Contains("x:Key=\"Light\"", colors);
    }

    [Fact]
    public void WinUiColoursReadAsXmlForBothThemes()
    {
        var themes = StudioTheme.ReadXamlColors(StudioTheme.Fanous("Fanous.Colors.xaml"));
        Assert.Equal(0xFFF4AE3Au, themes["Default"]["FanousPrimaryColor"]);
        Assert.Equal(0xFF09151Eu, themes["Default"]["FanousBackgroundColor"]);
        Assert.Equal(0xFFF3EEE5u, themes["Light"]["FanousBackgroundColor"]);
        // Every colour the Windows Studio reads exists in both themes.
        foreach (var name in new[] { "Primary", "OnPrimary", "Surface", "SurfaceRaised", "Background", "TextPrimary", "TextSecondary", "TextMuted", "Link", "PrimaryEdge", "PrimaryHover", "PrimarySheen", "PrimaryPressed", "PrimaryDisabled", "OnPrimaryDisabled", "FocusRing", "ErrorText" })
        {
            Assert.Contains("Fanous" + name + "Color", themes["Default"].Keys);
            Assert.Contains("Fanous" + name + "Color", themes["Light"].Keys);
        }
    }

    [Fact]
    public void GtkStylesUseTheTokensAndTheLanternArt()
    {
        var css = StudioTheme.Studio("glass.css");
        Assert.Contains("@fanous_primary", css);
        Assert.Contains("headerbar.night", css);
    }

    [Fact]
    public void LanternStringIsAPng()
    {
        using var art = StudioTheme.LanternString();
        var head = new byte[8];
        Assert.Equal(8, art.Read(head, 0, 8));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, head);
    }
}
