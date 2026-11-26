using System;

namespace AlUmmahNow.App.Media
{
    public sealed record QuranCaptionCredit(string Text, string? Language, string? DisplayLabel)
    {
        private const string English = "Source: Quran Foundation";
        private const string Arabic = "المصدر: مؤسسة القرآن";
        private const string LegacyEnglish = "Quran data provided by Quran Foundation (https://quran.foundation/)";
        private const string LegacyArabic = "بيانات القرآن مقدَّمة من مؤسسة القرآن (https://quran.foundation/\u200e)";

        public bool Credited => Language != null;
        public static Uri Url { get; } = new Uri("https://quran.foundation/");

        public static QuranCaptionCredit SplitQuranCredit(string cueText)
        {
            var newline = cueText.LastIndexOf('\n');
            if (newline < 0) return new QuranCaptionCredit(cueText, null, null);

            var last = cueText.Substring(newline + 1).TrimStart('\u200e', '\u200f').Trim();
            return last switch
            {
                English or LegacyEnglish or "Quran data provided by Quran\u00a0Foundation\u00a0(https://quran.foundation/)"
                    => new QuranCaptionCredit(cueText.Substring(0, newline), "en", English),
                Arabic or LegacyArabic or "بيانات القرآن مقدَّمة من مؤسسة\u00a0القرآن\u00a0(https://quran.foundation/\u200e)"
                    => new QuranCaptionCredit(cueText.Substring(0, newline), "ar", Arabic),
                _ => new QuranCaptionCredit(cueText, null, null),
            };
        }
    }
}
