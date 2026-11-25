using System;
using System.Linq;

namespace AlUmmahNow.App.I18n
{
    /// <summary>A text made to fit a room, cut between words.</summary>
    public static class TextFit
    {
        /// <summary>The text if it fits; else as many of its words as do, with an ellipsis; at least the first one.</summary>
        public static string InWords(string text, Func<string, bool> fits)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            if (fits == null) throw new ArgumentNullException(nameof(fits));
            if (fits(text)) return text;
            var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (var count = words.Length - 1; count > 1; count--)
            {
                var cut = Cut(words, count);
                if (fits(cut)) return cut;
            }

            return words.Length > 1 ? Cut(words, 1) : text;
        }

        private static string Cut(string[] words, int count) => string.Join(" ", words.Take(count)).TrimEnd(',', '،', ':', ';', '؛', '-', '–', '—', ' ') + "…";
    }
}
