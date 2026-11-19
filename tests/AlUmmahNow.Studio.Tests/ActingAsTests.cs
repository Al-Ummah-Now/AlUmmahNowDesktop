using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Shell;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

/// <summary>The identity menu's channel wording (YouTube/Facebook pattern, same as the website).</summary>
public sealed class ActingAsTests
{
    private static StudioChannel Channel(ChannelRole role, string name = "Masjid Media Team") =>
        new("c1", name, "masjidmedia", null, false, role, Array.Empty<string>(), false);

    [Theory]
    [InlineData("en", "Acting as", "Channel")]
    [InlineData("ar", "تتصرّف باسم", "قناة")]
    public void HeadingAndChannelTag(string language, string heading, string tag)
    {
        var l = new Localizer(language);
        Assert.Equal(heading, l[ActingAs.HeadingKey]);
        Assert.Equal(heading + " Masjid Media Team", l.Format(ActingAs.ButtonNameKey, "Masjid Media Team"));
        Assert.Equal(tag, ActingAs.ChannelTag(Channel(ChannelRole.Owner), k => l[k], l.Separator));
    }

    [Theory]
    [InlineData("en", "Channel · Editor")]
    [InlineData("ar", "قناة • محرّر")]
    public void NonOwnersSeeTheirRoleAfterTheTag(string language, string expected)
    {
        var l = new Localizer(language);
        Assert.Equal(expected, ActingAs.ChannelTag(Channel(ChannelRole.Editor), k => l[k], l.Separator));
    }

    [Fact]
    public void OptionsNeverShowTheHandle()
    {
        var l = new Localizer("en");
        var channel = Channel(ChannelRole.Admin);
        foreach (var row in IdentityMenu.Channels(new[] { channel }, channel, k => l[k], l.Separator))
        {
            Assert.DoesNotContain("@", row.Name + row.Tag + row.AccessibleName);
            Assert.DoesNotContain("masjidmedia", row.Name + row.Tag + row.AccessibleName);
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void NoIdentityIsCalledNone(string language)
    {
        var l = new Localizer(language);
        foreach (var key in new[] { ActingAs.HeadingKey, ActingAs.ButtonNameKey, ActingAs.ChannelTagKey })
        {
            var text = l[key];
            Assert.NotEqual(key, text);
            Assert.DoesNotContain("no channel", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("without", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("بدون", text);
        }
    }

    [Fact]
    public void ArabicNeverSaysFidyu() =>
        Assert.DoesNotContain(new Localizer().Bundle("ar").Values, v => v.Contains("فيديو"));
}

public sealed class StudioWindowSizeTests
{
    [Theory]
    [InlineData(null, 1280, 800)]
    [InlineData("", 1280, 800)]
    [InlineData("nonsense", 1280, 800)]
    [InlineData("400x800", 1024, 800)]
    [InlineData("1600x900", 1600, 900)]
    [InlineData("1100x720", 1100, 720)]
    [InlineData("1024x600", 1024, 700)]
    public void InitialSizeNeverGoesBelowTheMinimum(string? value, int width, int height) =>
        Assert.Equal((width, height), StudioWindowSize.Initial(value));

    [Theory]
    [InlineData(1024, true)]
    [InlineData(1199, true)]
    [InlineData(1200, false)]
    [InlineData(1920, false)]
    public void NarrowWindowsShowTheRail(double width, bool rail) => Assert.Equal(rail, StudioWindowSize.IsRail(width));
}
