using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AlUmmahNow.CoreBridge
{
    public enum AppLinkKind
    {
        Video,
        Short,
        Live,
        ChannelHandle,
        ChannelId,
        Post,
        Playlist,
        AuthCallback,
        Studio,
    }

    public sealed class ParsedAppLink
    {
        public ParsedAppLink(
            AppLinkKind kind,
            string idOrHandle,
            string? lang = null,
            long? startSeconds = null,
            string? playlistId = null,
            int? playlistIndex = null,
            string? rawQuery = null)
        {
            Kind = kind;
            IdOrHandle = idOrHandle;
            Lang = lang;
            StartSeconds = startSeconds;
            PlaylistId = playlistId;
            PlaylistIndex = playlistIndex;
            RawQuery = rawQuery;
        }

        public AppLinkKind Kind { get; }
        public string IdOrHandle { get; }
        public string? Lang { get; }
        public long? StartSeconds { get; }
        public string? PlaylistId { get; }
        public int? PlaylistIndex { get; }
        public string? RawQuery { get; }
    }

    /// <summary>
    /// Canonical <c>alummahnow.link</c> short-link builder and parser (Epic 22 / #39), matching
    /// <c>aun-common::short_links</c> and <c>packages/shared-types/src/short-links.ts</c>.
    /// </summary>
    public static class ShortLinks
    {
        public const string ProdHost = "alummahnow.link";
        public const string QaHost = "qa.alummahnow.link";
        public const long MaxTimestampSeconds = 86_400;

        private static readonly Regex SafeIdRegex = new Regex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.Compiled);
        private static readonly Regex HmsRegex = new Regex("^(?:(\\d{1,2})h)?(?:(\\d{1,2})m)?(?:(\\d{1,2})s?)?$", RegexOptions.Compiled);

        public static bool IsSafeId(string? value) =>
            !string.IsNullOrEmpty(value) && SafeIdRegex.IsMatch(value!);

        public static bool IsSafeHandle(string? value) =>
            !string.IsNullOrEmpty(value)
            && value!.Length <= 100
            && !value.Any(c => c == '/' || c == '?' || c == '#' || char.IsWhiteSpace(c));

        public static string? Video(
            string id,
            string shortLinkHost = ProdHost,
            long? startSeconds = null,
            string? playlistId = null,
            int? playlistIndex = null,
            string? lang = null)
        {
            if (!IsSafeId(id)) return null;
            var query = new List<KeyValuePair<string, string>>();
            if (startSeconds.HasValue && startSeconds.Value >= 1 && startSeconds.Value <= MaxTimestampSeconds)
            {
                query.Add(new KeyValuePair<string, string>("t", startSeconds.Value.ToString(CultureInfo.InvariantCulture)));
            }

            if (playlistId != null && IsSafeId(playlistId))
            {
                query.Add(new KeyValuePair<string, string>("list", playlistId));
                if (playlistIndex.HasValue && playlistIndex.Value >= 0)
                {
                    query.Add(new KeyValuePair<string, string>("index", playlistIndex.Value.ToString(CultureInfo.InvariantCulture)));
                }
            }

            if (lang == "ar") query.Add(new KeyValuePair<string, string>("lang", "ar"));
            return BuildUrl(shortLinkHost, "/v/" + id, query);
        }

        public static string? Short(string id, string shortLinkHost = ProdHost, string? lang = null) =>
            IsSafeId(id) ? BuildUrl(shortLinkHost, "/s/" + id, LangQuery(lang)) : null;

        public static string? Live(string streamId, string shortLinkHost = ProdHost, long? startSeconds = null, string? lang = null)
        {
            if (!IsSafeId(streamId)) return null;
            var query = new List<KeyValuePair<string, string>>();
            if (startSeconds.HasValue && startSeconds.Value >= 1 && startSeconds.Value <= MaxTimestampSeconds)
            {
                query.Add(new KeyValuePair<string, string>("t", startSeconds.Value.ToString(CultureInfo.InvariantCulture)));
            }

            if (lang == "ar") query.Add(new KeyValuePair<string, string>("lang", "ar"));
            return BuildUrl(shortLinkHost, "/e/" + streamId, query);
        }

        public static string? ChannelHandle(string handle, string shortLinkHost = ProdHost, string? lang = null)
        {
            var clean = (handle ?? "").Trim();
            if (clean.StartsWith("@", StringComparison.Ordinal)) clean = clean.Substring(1);
            if (!IsSafeHandle(clean)) return null;
            return BuildUrl(shortLinkHost, "/@" + Uri.EscapeDataString(clean), LangQuery(lang));
        }

        public static string? ChannelId(string id, string shortLinkHost = ProdHost, string? lang = null) =>
            IsSafeId(id) ? BuildUrl(shortLinkHost, "/c/" + id, LangQuery(lang)) : null;

        public static string? Post(string id, string shortLinkHost = ProdHost, string? lang = null) =>
            IsSafeId(id) ? BuildUrl(shortLinkHost, "/p/" + id, LangQuery(lang)) : null;

        public static string? Playlist(string id, string shortLinkHost = ProdHost, string? lang = null) =>
            IsSafeId(id) ? BuildUrl(shortLinkHost, "/l/" + id, LangQuery(lang)) : null;

        public static long? ParseTimestampSeconds(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var trimmed = raw!.Trim().ToLowerInvariant();
            var plain = trimmed.EndsWith("s", StringComparison.Ordinal) ? trimmed.Substring(0, trimmed.Length - 1) : trimmed;
            if (long.TryParse(plain, NumberStyles.None, CultureInfo.InvariantCulture, out var secs))
            {
                return secs >= 1 && secs <= MaxTimestampSeconds ? secs : (long?)null;
            }

            var match = HmsRegex.Match(trimmed);
            if (!match.Success) return null;
            var h = match.Groups[1].Success ? long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0L;
            var m = match.Groups[2].Success ? long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0L;
            var s = match.Groups[3].Success ? long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0L;
            var total = (h * 3600L) + (m * 60L) + s;
            return total >= 1 && total <= MaxTimestampSeconds ? total : (long?)null;
        }

        public static ParsedAppLink? Parse(string? url, string webHost, string shortLinkHost = ProdHost)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url!.Trim(), UriKind.Absolute, out var uri)) return null;
            var rawQuery = uri.Query.StartsWith("?", StringComparison.Ordinal) ? uri.Query.Substring(1) : uri.Query;
            var query = ParseQuery(rawQuery);
            var queryLang = query.TryGetValue("lang", out var ql) && (ql == "en" || ql == "ar") ? ql : null;
            var startSeconds = query.TryGetValue("t", out var t) ? ParseTimestampSeconds(t) : null;
            var playlistId = query.TryGetValue("list", out var pl) && IsSafeId(pl) ? pl : null;
            int? playlistIndex = null;
            if (playlistId != null && query.TryGetValue("index", out var idxText) && int.TryParse(idxText, NumberStyles.None, CultureInfo.InvariantCulture, out var idx) && idx >= 0)
            {
                playlistIndex = idx;
            }

            if (string.Equals(uri.Scheme, "alummahnow", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(uri.Host, "watch", StringComparison.OrdinalIgnoreCase)) return null;
                var segs = Segments(uri.AbsolutePath);
                if (segs.Count == 0 || !IsSafeId(segs[0])) return null;
                return new ParsedAppLink(AppLinkKind.Video, segs[0], queryLang, startSeconds, playlistId, playlistIndex);
            }

            if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return null;
            if (string.Equals(uri.Host, shortLinkHost, StringComparison.OrdinalIgnoreCase))
            {
                return ParseShortPath(uri.AbsolutePath, queryLang, startSeconds, playlistId, playlistIndex);
            }

            if (string.Equals(uri.Host, webHost, StringComparison.OrdinalIgnoreCase))
            {
                return ParseWebPath(uri.AbsolutePath, rawQuery, queryLang, startSeconds, playlistId, playlistIndex);
            }

            return null;
        }

        private static ParsedAppLink? ParseShortPath(
            string rawPath,
            string? lang,
            long? startSeconds,
            string? playlistId,
            int? playlistIndex)
        {
            var segs = Segments(rawPath);
            if (segs.Count == 0) return null;
            if (segs[0].StartsWith("@", StringComparison.Ordinal) && segs.Count == 1)
            {
                var handle = segs[0].Substring(1);
                return IsSafeHandle(handle) ? new ParsedAppLink(AppLinkKind.ChannelHandle, handle, lang) : null;
            }

            if (segs.Count != 2 || !IsSafeId(segs[1])) return null;
            var id = segs[1];
            return segs[0] switch
            {
                "v" => new ParsedAppLink(AppLinkKind.Video, id, lang, startSeconds, playlistId, playlistIndex),
                "s" => new ParsedAppLink(AppLinkKind.Short, id, lang),
                "e" => new ParsedAppLink(AppLinkKind.Live, id, lang, startSeconds),
                "c" => new ParsedAppLink(AppLinkKind.ChannelId, id, lang),
                "p" => new ParsedAppLink(AppLinkKind.Post, id, lang),
                "l" => new ParsedAppLink(AppLinkKind.Playlist, id, lang),
                _ => null,
            };
        }

        private static ParsedAppLink? ParseWebPath(
            string rawPath,
            string rawQuery,
            string? queryLang,
            long? startSeconds,
            string? playlistId,
            int? playlistIndex)
        {
            var segs = Segments(rawPath);
            if (segs.Count >= 2 && segs[0] == "app")
            {
                if (segs[1] == "auth" && segs.Count == 3 && segs[2] == "callback")
                {
                    return new ParsedAppLink(AppLinkKind.AuthCallback, "", rawQuery: string.IsNullOrEmpty(rawQuery) ? null : rawQuery);
                }

                if (segs[1] == "studio")
                {
                    var channelId = segs.Count >= 3 && IsSafeId(segs[2]) ? segs[2] : "";
                    return new ParsedAppLink(AppLinkKind.Studio, channelId);
                }

                return null;
            }

            string? pathLang = null;
            var offset = 0;
            if (segs.Count > 0 && (segs[0] == "en" || segs[0] == "ar"))
            {
                pathLang = segs[0];
                offset = 1;
            }

            if (segs.Count <= offset) return null;
            var lang = pathLang ?? queryLang;
            var head = segs[offset];
            if (head.StartsWith("@", StringComparison.Ordinal))
            {
                var handle = head.Substring(1);
                return IsSafeHandle(handle) ? new ParsedAppLink(AppLinkKind.ChannelHandle, handle, lang) : null;
            }

            if (head == "studio" && pathLang != null)
            {
                var chId = segs.Count > offset + 1 && IsSafeId(segs[offset + 1]) ? segs[offset + 1] : "";
                return new ParsedAppLink(AppLinkKind.Studio, chId, pathLang);
            }

            if (segs.Count <= offset + 1 || !IsSafeId(segs[offset + 1])) return null;
            var id = segs[offset + 1];
            return head switch
            {
                "video" => new ParsedAppLink(AppLinkKind.Video, id, lang, startSeconds, playlistId, playlistIndex),
                "shorts" => new ParsedAppLink(AppLinkKind.Short, id, lang),
                "live" => new ParsedAppLink(AppLinkKind.Live, id, lang, startSeconds),
                "playlist" => new ParsedAppLink(AppLinkKind.Playlist, id, lang),
                "post" => new ParsedAppLink(AppLinkKind.Post, id, lang),
                "channel" => new ParsedAppLink(AppLinkKind.ChannelId, id, lang),
                _ => null,
            };
        }

        private static List<KeyValuePair<string, string>> LangQuery(string? lang) =>
            lang == "ar"
                ? new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("lang", "ar") }
                : new List<KeyValuePair<string, string>>();

        private static string BuildUrl(string host, string path, IReadOnlyList<KeyValuePair<string, string>> query)
        {
            var cleanHost = (host ?? ProdHost).Trim().TrimEnd('/');
            if (query.Count == 0) return "https://" + cleanHost + path;
            var qs = string.Join("&", query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
            return "https://" + cleanHost + path + "?" + qs;
        }

        private static List<string> Segments(string rawPath)
        {
            var list = new List<string>();
            foreach (var part in rawPath.Split('/'))
            {
                if (part.Length == 0) continue;
                list.Add(Decode(part));
            }

            return list;
        }

        private static Dictionary<string, string> ParseQuery(string rawQuery)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(rawQuery)) return map;
            foreach (var pair in rawQuery.Split('&'))
            {
                if (pair.Length == 0) continue;
                var idx = pair.IndexOf('=');
                var rawK = idx >= 0 ? pair.Substring(0, idx) : pair;
                var rawV = idx >= 0 ? pair.Substring(idx + 1) : "";
                var k = Decode(rawK);
                if (k.Length > 0 && !map.ContainsKey(k)) map[k] = Decode(rawV);
            }

            return map;
        }

        private static string Decode(string value) => Uri.UnescapeDataString(value.Replace("+", "%2B"));
    }
}
