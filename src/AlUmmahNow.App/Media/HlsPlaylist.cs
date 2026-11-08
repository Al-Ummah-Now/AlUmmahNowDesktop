using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AlUmmahNow.App.Media
{
    public sealed class HlsVariant
    {
        public HlsVariant(int height, long bandwidth, string url)
        {
            Height = height;
            Bandwidth = bandwidth;
            Url = url;
        }

        public int Height { get; }
        public long Bandwidth { get; }
        public string Url { get; }
        public string Label => Height > 0 ? Height.ToString(CultureInfo.InvariantCulture) + "p" : (Bandwidth / 1000).ToString(CultureInfo.InvariantCulture) + " kbps";
    }

    public static class HlsPlaylist
    {
        /// <summary>Reads variants from a master playlist, highest quality first.</summary>
        public static IReadOnlyList<HlsVariant> ParseVariants(string text, string masterUrl)
        {
            var result = new List<HlsVariant>();
            var baseUri = new Uri(masterUrl);
            string? pending = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                {
                    pending = line;
                }
                else if (pending != null && line.Length > 0 && line[0] != '#')
                {
                    result.Add(new HlsVariant(Height(pending), Bandwidth(pending), new Uri(baseUri, line).ToString()));
                    pending = null;
                }
            }

            return result.OrderByDescending(v => v.Height).ThenByDescending(v => v.Bandwidth).ToList();
        }

        private static long Bandwidth(string line)
        {
            var value = Attribute(line, "BANDWIDTH");
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : 0;
        }

        private static int Height(string line)
        {
            var parts = (Attribute(line, "RESOLUTION") ?? "").Split('x');
            return parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ? h : 0;
        }

        private static string? Attribute(string line, string name)
        {
            var body = line.Substring(line.IndexOf(':') + 1);
            var inQuotes = false;
            var start = 0;
            for (var i = 0; i <= body.Length; i++)
            {
                if (i == body.Length || (body[i] == ',' && !inQuotes))
                {
                    var pair = body.Substring(start, i - start);
                    var eq = pair.IndexOf('=');
                    if (eq > 0 && pair.Substring(0, eq) == name) return pair.Substring(eq + 1).Trim('"');
                    start = i + 1;
                }
                else if (body[i] == '"')
                {
                    inQuotes = !inQuotes;
                }
            }

            return null;
        }
    }
}
