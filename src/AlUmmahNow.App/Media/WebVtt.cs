using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AlUmmahNow.App.Media
{
    public sealed class Cue
    {
        public Cue(double start, double end, string text)
        {
            Start = start;
            End = end;
            Text = text;
        }

        public double Start { get; }
        public double End { get; }
        public string Text { get; }
    }

    public static class WebVtt
    {
        private static readonly Regex Tags = new Regex("<[^>]+>", RegexOptions.Compiled);

        public static IReadOnlyList<Cue> Parse(string text)
        {
            var cues = new List<Cue>();
            var blocks = text.Replace("\r\n", "\n").Replace('\r', '\n').Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var block in blocks)
            {
                var lines = block.Split('\n').Where(l => l.Length > 0).ToList();
                var timing = lines.FindIndex(l => l.Contains("-->"));
                if (timing < 0) continue;
                var parts = lines[timing].Split(new[] { "-->" }, StringSplitOptions.None);
                var endField = parts[1].Trim().Split(' ')[0];
                if (!TryTime(parts[0].Trim(), out var start) || !TryTime(endField, out var end)) continue;
                var body = string.Join("\n", lines.Skip(timing + 1));
                cues.Add(new Cue(start, end, Tags.Replace(body, "")));
            }

            return cues.OrderBy(c => c.Start).ToList();
        }

        /// <summary>Returns the cue active at the position, or null between cues.</summary>
        public static Cue? At(IReadOnlyList<Cue> cues, double position)
        {
            int low = 0, high = cues.Count - 1;
            while (low <= high)
            {
                var mid = (low + high) / 2;
                if (position < cues[mid].Start) high = mid - 1;
                else if (position >= cues[mid].End) low = mid + 1;
                else return cues[mid];
            }

            return null;
        }

        private static bool TryTime(string value, out double seconds)
        {
            seconds = 0;
            var fields = value.Split(':');
            if (fields.Length < 2 || fields.Length > 3) return false;
            double total = 0;
            for (var i = 0; i < fields.Length; i++)
            {
                if (!double.TryParse(fields[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var part)) return false;
                total = (total * 60) + part;
            }

            seconds = total;
            return true;
        }
    }
}
