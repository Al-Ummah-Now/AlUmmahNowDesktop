using System.Globalization;
using System.Text;

namespace AlUmmahNow.Studio.Shell;

/// <summary>
/// Initials for a logo placeholder (channel or person). Arabic: the first letter of the first word,
/// skipping a leading «ال» («قناة النور» → «ق», «الأمة الآن» → «أ»). Scripts with case (Latin, …):
/// the first letters of the first and last words, upper-cased ("Masjid Media Team" → "MT"). Works on
/// grapheme clusters and drops Arabic tashkeel; <c>null</c> only when the name has no letters (show
/// the generic icon then).
/// </summary>
public static class Initials
{
    public static string? For(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var words = name!.Normalize(NormalizationForm.FormKC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Letters)
            .Where(w => w.Count > 0)
            .ToList();
        if (words.Count == 0) return null;

        var first = words[0];
        if (IsArabic(first[0]))
        {
            if (first.Count > 2 && first[0] == "ا" && first[1] == "ل") return first[2];
            return first[0];
        }

        var initials = first[0].ToUpperInvariant();
        var last = words[words.Count - 1];
        if (IsCased(first[0]) && words.Count > 1 && IsCased(last[0])) initials += last[0].ToUpperInvariant();
        return initials;
    }

    /// <summary>The word's letters as grapheme clusters (NFC), without Arabic tashkeel or tatweel.</summary>
    private static List<string> Letters(string word)
    {
        var letters = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(word);
        while (e.MoveNext())
        {
            var element = (string)e.Current;
            if (!char.IsLetter(element, 0) || element[0] == (char)0x0640) continue;
            var kept = new StringBuilder();
            foreach (var ch in element.Normalize(NormalizationForm.FormD))
            {
                if (!IsTashkeel(ch)) kept.Append(ch);
            }
            if (kept.Length > 0) letters.Add(kept.ToString().Normalize(NormalizationForm.FormC));
        }
        return letters;
    }

    // Harakat, tanween, shadda, sukun, superscript alef and Quranic marks. Maddah and hamza above/below
    // (U+0653-U+0655) stay: they are part of alef with maddah, alef with hamza and hamza below.
    private static bool IsTashkeel(char c) =>
        (c >= 0x0610 && c <= 0x061A) || (c >= 0x064B && c <= 0x065F && (c < 0x0653 || c > 0x0655))
        || c == 0x0670 || (c >= 0x06D6 && c <= 0x06ED);

    private static bool IsArabic(string letter)
    {
        var c = letter[0];
        return (c >= 0x0600 && c <= 0x06FF) || (c >= 0x0750 && c <= 0x077F) || (c >= 0x08A0 && c <= 0x08FF);
    }

    private static bool IsCased(string letter) => letter.ToUpperInvariant() != letter.ToLowerInvariant();
}
