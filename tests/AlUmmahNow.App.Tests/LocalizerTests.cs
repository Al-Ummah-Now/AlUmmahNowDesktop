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
    public void Eastern_numerals_only_apply_in_arabic()
    {
        var l = new Localizer("ar") { EasternNumerals = true };
        Assert.Equal("١٢٣", l.Number(123));
        l.Language = "en";
        Assert.Equal("123", l.Number(123));
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
        var ar = new Localizer("ar") { EasternNumerals = true };
        Assert.Equal("١٫٢ ألف", ar.Compact(1200));
    }

    [Fact]
    public void Chart_dates_use_gregorian_month_names()
    {
        Assert.Equal("11 Nov", new Localizer("en").ShortDate(new DateTime(2026, 11, 11)));
        Assert.Equal("١١ نوفمبر", new Localizer("ar") { EasternNumerals = true }.ShortDate(new DateTime(2026, 11, 11)));
    }
}
