using AlUmmahNow.Studio.Shell;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

public sealed class InitialsTests
{
    [Theory]
    [InlineData("الأمة الآن", "أ")]
    [InlineData("قناة النور", "ق")]
    [InlineData("حلقة التفسير", "ح")]
    [InlineData("النور", "ن")]
    [InlineData("ال", "ا")]
    [InlineData("الْأُمَّةُ الآن", "أ")] // tashkeel
    [InlineData("قَنَاةُ النُّور", "ق")]
    [InlineData("أمة", "أ")] // alef + combining hamza above, composed
    [InlineData("ـقناة", "ق")] // tatweel is not a letter
    [InlineData("ﻗﻨﺎﺓ", "ق")] // presentation forms
    [InlineData("آمنة", "آ")] // maddah is part of the letter
    [InlineData("إسلام", "إ")]
    [InlineData("  قناة   ", "ق")]
    [InlineData("123 قناة", "ق")]
    public void ArabicTakesTheFirstLetterOfTheFirstWord(string name, string expected) => Assert.Equal(expected, Initials.For(name));

    [Theory]
    [InlineData("Masjid Media Team", "M")]
    [InlineData("omar", "O")]
    [InlineData("amina studio", "A")]
    [InlineData("Émile Zola", "É")] // decomposed accent stays with its letter
    [InlineData("Masjid النور", "M")]
    [InlineData("#1 Channel", "C")]
    public void OtherScriptsTakeOneUpperCaseLetter(string name, string expected) => Assert.Equal(expected, Initials.For(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123 456")]
    [InlineData("🎥 !!")]
    public void NoLettersMeansTheIcon(string? name) => Assert.Null(Initials.For(name));

    [Theory]
    [InlineData("قناة النور")]
    [InlineData("Masjid Media Team")]
    [InlineData("x")]
    public void NeverEmpty(string name) => Assert.False(string.IsNullOrEmpty(Initials.For(name)));
}
