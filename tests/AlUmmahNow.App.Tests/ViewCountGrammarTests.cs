using AlUmmahNow.App.I18n;
using Xunit;

namespace AlUmmahNow.App.Tests;

/// <summary>
/// Arabic number grammar for view counts (owner's final rule, the same on Android, Apple, Desktop and web). Under 1,000
/// the noun takes the CLDR plural of the full count. A compact count is counted by its scale word, so the noun stays
/// singular. Exactly 1 is the bare scale word («ألف مشاهدة»), and exactly 2 the construct dual with no digit
/// («ألفا مشاهدة»): a digit is never followed by the dual. A whole 3–10 takes the plural («٣ آلاف»), and 11+ or any
/// decimal takes the singular («١١ ألف», «١٫٣ ألف»).
/// </summary>
public class ViewCountGrammarTests
{
    [Theory]
    [InlineData(1, "مشاهدة واحدة", "1 view")]
    [InlineData(2, "مشاهدتان", "2 views")]
    [InlineData(3, "٣ مشاهدات", "3 views")]
    [InlineData(8, "٨ مشاهدات", "8 views")]
    [InlineData(10, "١٠ مشاهدات", "10 views")]
    [InlineData(11, "١١ مشاهدة", "11 views")]
    [InlineData(100, "١٠٠ مشاهدة", "100 views")]
    [InlineData(103, "١٠٣ مشاهدات", "103 views")]
    [InlineData(1_000, "ألف مشاهدة", "1K views")]
    [InlineData(1_050, "ألف مشاهدة", "1K views")]
    [InlineData(1_300, "١٫٣ ألف مشاهدة", "1.3K views")]
    [InlineData(1_310, "١٫٣ ألف مشاهدة", "1.3K views")]
    [InlineData(1_999, "١٫٩ ألف مشاهدة", "1.9K views")]
    [InlineData(2_000, "ألفا مشاهدة", "2K views")]
    [InlineData(2_500, "٢٫٥ ألف مشاهدة", "2.5K views")]
    [InlineData(3_000, "٣ آلاف مشاهدة", "3K views")]
    [InlineData(8_000, "٨ آلاف مشاهدة", "8K views")]
    [InlineData(8_020, "٨ آلاف مشاهدة", "8K views")]
    [InlineData(10_000, "١٠ آلاف مشاهدة", "10K views")]
    [InlineData(11_000, "١١ ألف مشاهدة", "11K views")]
    [InlineData(12_000, "١٢ ألف مشاهدة", "12K views")]
    [InlineData(1_000_000, "مليون مشاهدة", "1M views")]
    [InlineData(1_200_000, "١٫٢ مليون مشاهدة", "1.2M views")]
    [InlineData(2_000_000, "مليونا مشاهدة", "2M views")]
    [InlineData(3_000_000, "٣ ملايين مشاهدة", "3M views")]
    [InlineData(15_000_000, "١٥ مليون مشاهدة", "15M views")]
    [InlineData(1_000_000_000, "مليار مشاهدة", "1B views")]
    [InlineData(2_000_000_000, "مليارا مشاهدة", "2B views")]
    [InlineData(3_000_000_000, "٣ مليارات مشاهدة", "3B views")]
    public void View_counts_agree_in_both_languages(long count, string ar, string en)
    {
        Assert.Equal(ar, new Localizer("ar").Views(count));
        Assert.Equal(en, new Localizer("en").Views(count));
    }

    [Fact]
    public void Zero_views_is_a_phrase_in_arabic()
    {
        Assert.Equal("لا مشاهدات", new Localizer("ar").Views(0));
        Assert.Equal("0 views", new Localizer("en").Views(0));
    }

    [Theory]
    [InlineData(500, "٥٠٠", "500")]
    [InlineData(1_000, "ألف", "1K")]
    [InlineData(1_310, "١٫٣ ألف", "1.3K")]
    [InlineData(1_500, "١٫٥ ألف", "1.5K")]
    [InlineData(2_000, "ألفان", "2K")]
    [InlineData(3_000, "٣ آلاف", "3K")]
    [InlineData(8_020, "٨ آلاف", "8K")]
    [InlineData(12_000, "١٢ ألف", "12K")]
    [InlineData(1_000_000, "مليون", "1M")]
    [InlineData(2_000_000, "مليونان", "2M")]
    [InlineData(3_000_000, "٣ ملايين", "3M")]
    [InlineData(15_000_000, "١٥ مليون", "15M")]
    public void Axis_labels_take_the_scale_word_of_the_number_shown(double value, string ar, string en)
    {
        Assert.Equal(ar, new Localizer("ar").Compact(value));
        Assert.Equal(en, new Localizer("en").Compact(value));
    }

    /// <summary>English cuts to one decimal exactly as Arabic does (and as Android and Apple do): never "34K" or "3K".</summary>
    [Theory]
    [InlineData(1_299, "1.2K")]
    [InlineData(2_999, "2.9K")]
    [InlineData(12_000, "12K")]
    [InlineData(20_600, "20.6K")]
    [InlineData(33_600, "33.6K")]
    [InlineData(999_999, "999.9K")]
    [InlineData(1_250_000, "1.2M")]
    [InlineData(15_000_000, "15M")]
    public void English_truncates_like_arabic(long count, string compact)
    {
        var en = new Localizer("en");
        Assert.Equal(compact + " views", en.Views(count));
        Assert.Equal(compact, en.Compact(count));
    }
}
