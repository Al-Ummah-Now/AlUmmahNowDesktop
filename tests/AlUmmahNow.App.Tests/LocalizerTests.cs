using AlUmmahNow.App.I18n;
using Xunit;

namespace AlUmmahNow.App.Tests;

public class LocalizerTests
{
    [Fact]
    public void Bundles_have_identical_keys_and_no_blanks()
    {
        var l = new Localizer();
        Assert.Equal(l.Bundle("en").Keys.OrderBy(k => k), l.Bundle("ar").Keys.OrderBy(k => k));
        Assert.All(l.Bundle("ar").Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
    }

    [Fact]
    public void Arabic_is_default_and_rtl()
    {
        var l = new Localizer();
        Assert.Equal("ar", l.Language);
        Assert.True(l.IsRtl);
        l.Language = "en-GB";
        Assert.False(l.IsRtl);
    }

    [Fact]
    public void Language_change_raises_event_once()
    {
        var l = new Localizer("en");
        var count = 0;
        l.LanguageChanged += (_, _) => count++;
        l.Language = "ar";
        l.Language = "ar";
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData(65, "1:05")]
    [InlineData(3725, "1:02:05")]
    [InlineData(-3, "0:00")]
    public void Duration_is_isolated(double seconds, string expected) =>
        Assert.Equal("⁨" + expected + "⁩", new Localizer("en").Duration(seconds));

    [Fact]
    public void Arabic_always_uses_eastern_arabic_numerals()
    {
        var l = new Localizer("ar");
        Assert.Equal("١٢٣", l.Number(123));
        l.Language = "en";
        Assert.Equal("123", l.Number(123));
    }

    [Fact]
    public void Arabic_uses_eastern_digits_by_default()
    {
        Assert.Equal("١٢٣٤", new Localizer("ar").Number(1234));
        Assert.Equal("1,234", new Localizer("en").Number(1234));
    }

    [Fact]
    public void Missing_key_returns_the_key() => Assert.Equal("nope", new Localizer()["nope"]);

    [Fact]
    public void Chart_numbers_are_compact_and_localized()
    {
        var en = new Localizer("en");
        Assert.Equal("950", en.Compact(950));
        Assert.Equal("1.2K", en.Compact(1200));
        Assert.Equal("2M", en.Compact(2_000_000));
        Assert.Equal("0.1", en.Decimal(0.05));
        var ar = new Localizer("ar");
        Assert.Equal("١٫٢ ألف", ar.Compact(1200));
    }

    [Fact]
    public void Chart_dates_use_gregorian_month_names()
    {
        Assert.Equal("Nov 11", new Localizer("en").ShortDate(new DateTime(2026, 11, 11)));
        Assert.Equal("١١ نوفمبر", new Localizer("ar").ShortDate(new DateTime(2026, 11, 11)));
    }

    // The website's formatBytes (Intl short units, 1024 steps); the expected text is what the web shows.
    [Theory]
    [InlineData("en", 512, null, "512 byte")]
    [InlineData("en", 1536, null, "1.5 kB")]
    [InlineData("en", 2 * 1024 * 1024, null, "2 MB")]
    [InlineData("en", 1_500_000, 2, "1.43 MB")]
    [InlineData("en", 1_536_000, 2, "1.46 MB")]
    [InlineData("ar", 1_500_000, 2, "١٫٤٣ م.ب")]
    [InlineData("ar", 1536, null, "١٫٥ كيلوبايت")]
    [InlineData("ar", 3L * 1024 * 1024 * 1024, null, "٣ غ.ب")]
    public void File_sizes_read_as_the_website(string language, long bytes, int? decimals, string expected) =>
        Assert.Equal(expected, new Localizer(language).Bytes(bytes, decimals));
}

public sealed class FanousThemeTests
{
    [Fact]
    public void RegistryColoursShipInTheAppLayerForStudioAndTheViewer()
    {
        var themes = AlUmmahNow.App.Theme.Fanous.ReadXamlColors(AlUmmahNow.App.Theme.Fanous.File("Fanous.Colors.xaml"));
        Assert.Equal(0xFFF4AE3Au, themes["Default"]["FanousPrimaryColor"]);
        Assert.Equal(0xFFF4AE3Au, themes["Light"]["FanousPrimaryColor"]);
        Assert.Contains("@define-color fanous_primary", AlUmmahNow.App.Theme.Fanous.File("style-dark.css"));
    }

    [Fact]
    public void ThereIsNoSystemThemeChoice()
    {
        Assert.DoesNotContain("settings.theme.system", new AlUmmahNow.App.I18n.Localizer("en").Bundle("en").Keys);
        Assert.DoesNotContain("settings.theme.system", new AlUmmahNow.App.I18n.Localizer("ar").Bundle("ar").Keys);
    }
}
