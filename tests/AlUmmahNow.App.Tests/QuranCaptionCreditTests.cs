using AlUmmahNow.App.Media;
using Xunit;

namespace AlUmmahNow.App.Tests;

public class QuranCaptionCreditTests
{
    [Theory]
    [InlineData("Source: Quran Foundation", "en", "Source: Quran Foundation")]
    [InlineData("المصدر: مؤسسة القرآن", "ar", "المصدر: مؤسسة القرآن")]
    [InlineData("\u200fالمصدر: مؤسسة القرآن", "ar", "المصدر: مؤسسة القرآن")]
    [InlineData("\u200e\u200fالمصدر: مؤسسة القرآن  ", "ar", "المصدر: مؤسسة القرآن")]
    [InlineData("Quran data provided by Quran Foundation (https://quran.foundation/)", "en", "Source: Quran Foundation")]
    [InlineData("Quran data provided by Quran\u00a0Foundation\u00a0(https://quran.foundation/)", "en", "Source: Quran Foundation")]
    [InlineData("بيانات القرآن مقدَّمة من مؤسسة القرآن (https://quran.foundation/\u200e)", "ar", "المصدر: مؤسسة القرآن")]
    public void Splits_trailing_credit_and_preserves_caption(string line, string language, string label)
    {
        const string caption = "First line\n  Second line";
        var result = QuranCaptionCredit.SplitQuranCredit(caption + "\n" + line);
        Assert.True(result.Credited);
        Assert.Equal(language, result.Language);
        Assert.Equal(label, result.DisplayLabel);
        Assert.Equal(caption, result.Text);
        Assert.Equal("https://quran.foundation/", QuranCaptionCredit.Url.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n\t")]
    [InlineData("Source: Quran Foundation")]
    [InlineData("First line\nSource: Another foundation")]
    [InlineData("Source: Quran Foundation\nFirst line")]
    [InlineData("First line\nQuote: Source: Quran Foundation")]
    public void Leaves_other_cues_untouched(string cue)
    {
        var result = QuranCaptionCredit.SplitQuranCredit(cue);
        Assert.False(result.Credited);
        Assert.Null(result.Language);
        Assert.Null(result.DisplayLabel);
        Assert.Equal(cue, result.Text);
    }
}
