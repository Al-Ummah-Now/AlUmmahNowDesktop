using AlUmmahNow.App.Theme;
using Xunit;

namespace AlUmmahNow.App.Tests;

public sealed class ColorMathTests
{
    [Fact]
    public void Contrast_is_wcags()
    {
        Assert.Equal(21, ColorMath.Contrast(0x000000, 0xFFFFFF), 3);
        Assert.Equal(1, ColorMath.Contrast(0xF4AE3A, 0xF4AE3A), 3);
        Assert.Equal(4.48, ColorMath.Contrast(0x777777, 0xFFFFFF), 2);
    }

    [Fact]
    public void Over_is_one_opaque_color()
    {
        Assert.Equal(0xFFFF8000u, ColorMath.Over(0xFFFF8000, 0xFF123456, 1));
        Assert.Equal(0xFF123456u, ColorMath.Over(0xFFFF8000, 0xFF123456, 0));
        Assert.Equal(0xFF7F7F7Fu, ColorMath.Over(0xFFFEFEFE, 0xFF000000, 0.5));
    }

    /// <summary>The seek bar and its thumb's edge on the viewer's page (Windows' own page colors, and the registry's of Linux).</summary>
    [Theory]
    [InlineData("Default", 0x093041)]
    [InlineData("Light", 0xF7F1E8)]
    public void The_seek_bar_is_seen_on_the_page(string theme, int windowsPage)
    {
        var c = AlUmmahNow.App.Theme.Fanous.ReadXamlColors(AlUmmahNow.App.Theme.Fanous.File("Fanous.Colors.xaml"))[theme];
        foreach (var page in new[] { (uint)windowsPage, c["FanousBackgroundColor"] })
        {
            Assert.True(ColorMath.Contrast(c["FanousAccentColor"], page) >= 3, theme + ": seek bar");
            Assert.True(ColorMath.Contrast(c["FanousPrimaryEdgeColor"], page) >= 3, theme + ": thumb's edge");
            Assert.True(ColorMath.Contrast(ColorMath.Over(c["FanousTextPrimaryColor"], page, 0.55), page) >= 3, theme + ": the rest of the bar (Linux)");
        }
    }
}
