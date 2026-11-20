using AlUmmahNow.App.I18n;
using Xunit;

namespace AlUmmahNow.App.Tests;

/// <summary>
/// Arabic number grammar for view counts (owner rules, matching Android): under 1,000 the noun takes the CLDR plural of
/// the full count; a compact count is counted by its scale word, so the noun stays singular («١٫٣ ألف مشاهدة», never
/// «مشاهدات»), a whole 2 takes the dual scale word («٢ ألفان») and a whole 3–10 the plural («٨ آلاف»).
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
    [InlineData(1_310, "١٫٣ ألف مشاهدة", "1.3K views")]
    [InlineData(1_999, "١٫٩ ألف مشاهدة", "1.9K views")]
    [InlineData(2_000, "٢ ألفان مشاهدة", "2K views")]
    [InlineData(8_020, "٨ آلاف مشاهدة", "8K views")]
    [InlineData(10_000, "١٠ آلاف مشاهدة", "10K views")]
    [InlineData(12_000, "١٢ ألف مشاهدة", "12K views")]
    [InlineData(1_200_000, "١٫٢ مليون مشاهدة", "1.2M views")]
    [InlineData(2_000_000, "٢ مليونان مشاهدة", "2M views")]
    [InlineData(3_000_000, "٣ ملايين مشاهدة", "3M views")]
    [InlineData(15_000_000, "١٥ مليون مشاهدة", "15M views")]
    [InlineData(1_000_000_000, "١ مليار مشاهدة", "1B views")]
    [InlineData(5_000_000_000, "٥ مليارات مشاهدة", "5B views")]
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
    [InlineData(950, "٩٥٠", "950")]
    [InlineData(1_000, "١ ألف", "1K")]
    [InlineData(1_310, "١٫٣ ألف", "1.3K")]
    [InlineData(2_000, "٢ ألفان", "2K")]
    [InlineData(8_020, "٨ آلاف", "8K")]
    [InlineData(12_000, "١٢ ألف", "12K")]
    [InlineData(3_000_000, "٣ ملايين", "3M")]
    [InlineData(15_000_000, "١٥ مليون", "15M")]
    public void Axis_labels_take_the_scale_word_of_the_number_shown(double value, string ar, string en)
    {
        Assert.Equal(ar, new Localizer("ar").Compact(value));
        Assert.Equal(en, new Localizer("en").Compact(value));
    }
}
