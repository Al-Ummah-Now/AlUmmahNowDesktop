using AlUmmahNow.App.I18n;
using Xunit;

namespace AlUmmahNow.App.Tests;

public sealed class TextFitTests
{
    private static string In(int room, string text) => TextFit.InWords(text, t => t.Length <= room);

    [Theory]
    [InlineData(40, "Reflections on Surah Al-Kahf, part 2", "Reflections on Surah Al-Kahf, part 2")]
    [InlineData(35, "Reflections on Surah Al-Kahf, part 2", "Reflections on Surah Al-Kahf, part…")]
    [InlineData(34, "Reflections on Surah Al-Kahf, part 2", "Reflections on Surah Al-Kahf…")]
    [InlineData(30, "تأملات في سورة الكهف، الجزء الثاني", "تأملات في سورة الكهف، الجزء…")]
    [InlineData(22, "تأملات في سورة الكهف، الجزء الثاني", "تأملات في سورة الكهف…")]
    [InlineData(3, "Reflections on Surah", "Reflections…")]
    [InlineData(3, "Reflections", "Reflections")]
    [InlineData(3, "", "")]
    public void A_title_is_cut_between_words(int room, string title, string expected) => Assert.Equal(expected, In(room, title));
}
