using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AlUmmahNow.Core;
using AlUmmahNow.CoreBridge.Logging;

namespace AlUmmahNow.CoreBridge.Native;

/// <summary>
/// Wraps <see cref="TelemetryClient"/> from <c>aun-core</c> behind <see cref="ITelemetryService"/>.
/// Consent defaults to off; opting out immediately blocks collection in the wrapper, revokes native
/// consent (halting any further native batches and purging memory and disk), cancels the managed
/// flush wait, and defers re-enabling or native disposal until any already-dispatched native HTTP
/// request completes and cleans up (the UniFFI <see cref="TelemetryClient.Flush"/> API does not
/// accept a cancellation handle). Disposal defers releasing the native handle until any in-flight
/// flush completes without deleting a retryable buffer unless consent was revoked.
/// </summary>
public sealed class CoreTelemetryService : ITelemetryService
{
    private static readonly Regex ParenPathRegex = new(@"(\()(?:/|~/|[A-Za-z]:[\\/]|\\\\)[^()\r\n\u001E]+(\))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ParenRelativeSlashPathRegex = new(@"(\()(?![ \t]*(?:HTTP|TLS|SSL)/\d)(?![ \t]*\d+/\d+[.,;:!?\s]*\))[^\s/\\""'<>()\[\]{}:,;=\u001E\u001F]+/[^()\r\n\u001E]+(\))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WindowsPathRegex = new(@"(?:[A-Za-z]:[\\/]|\\\\)[^\S\r\n]*[^\r\n\u001E\s](?:[^\r\n\u001E]*[^\r\n\u001E\s])?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RelativeBackslashPathRegex = new(@"(^|[\s(\[{""'=,:;\u001F])[^\s\\/""'<>()\[{:,;=\u001E\u001F]+\\[^\r\n\u001E\u001F\s](?:[^\r\n\u001E\u001F]*[^\r\n\u001E\u001F\s])?", RegexOptions.Compiled);
    private static readonly Regex QuotedSpacedRootPathRegex = new(@"[""']/[ \t]+[^\r\n\u001E]+", RegexOptions.Compiled);
    private static readonly Regex UnixPathRegex = new(@"(?<![\p{L}\p{M}\p{N}._/-])(?:~/|//+(?=[^\S\r\n]*[^\s/])|/(?=[^\S\r\n]*[^\s/]))[^\S\r\n]*[^\r\n\u001E\s](?:[^\r\n\u001E]*[^\r\n\u001E\s])?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RelativeSlashPathRegex = new(@"(^|[\s(\[{""'=,:;\u001F])(?!(?<!['""])(?:HTTP|TLS|SSL)/\d)(?!(?<!['""])\d+/\d+(?:$|[\s)\]},;:!?]|\.(?![A-Za-z0-9])))[^\s/\\""'<>()\[\]{}:,;=\u001E\u001F]+/[^\r\n\u001E\u001F\s](?:[^\r\n\u001E\u001F]*[^\r\n\u001E\u001F\s])?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex KeywordQuotedPathRegex = new(@"(\b(?:file|path|directory|folder|document)[^\S\r\n]+)([""'])(?!\u001E)[^'""\r\n]+\2", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex KeywordFilenamePrefixRegex = new(@"\b(?:open|opening|read|reading|write|writing|delete|deleting|remove|removing|rename|stat|create|creating|load|loading|save|saving|access|accessing|unlink|file|path|directory|folder|document|filename|filepath)\b(?:[^\S\r\n]*[:=][^\S\r\n]*|[^\S\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> SourceLineExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "kt", "java", "swift", "cs", "cpp", "cc", "c", "h", "hpp", "rs", "py", "js", "ts", "go", "rb", "m", "mm"
    };
    private static readonly Regex KeywordAssignedPathRegex = new(@"(\b(?:file|path|filename|filepath)\s*[:=]\s*)(?!\u001E)[^\r\n\u001E\u001F]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BareFilenameRegex = new(@"(^|[\r\n:=;\u001F])([ \t]*)(?=[^\r\n:=;\u001E\u001F \t])[^\r\n:=;\u001E\u001F]*\.(?!ts:\d+)(?:mov|mp4|m4v|mkv|webm|avi|ts|m3u8|mpd|mp3|m4a|wav|aac|flac|ogg|opus|jpg|jpeg|png|gif|webp|heic|svg|pdf|txt|md|rtf|doc|docx|xls|xlsx|ppt|pptx|csv|json|xml|yaml|yml|toml|ini|cfg|conf|log|srt|vtt|ass|ssa|zip|tar|gz|tgz|bz2|xz|7z|rar|dmg|pkg|msi|exe|apk|aab|ipa|pem|key|crt|cer|p12|pfx|jks|keystore|db|sqlite|sqlite3|bin|dat|tmp|bak|proj|aun)\b(?!\()", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BearerTokenRegex = new(@"\bBearer\s+\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly TelemetryAppInfo _app;
    private readonly string? _ingestUrl;
    private readonly string _userAgent;
    private readonly string _clientHeader;
    private readonly string? _bufferPath;
    private readonly Func<string?>? _externalConsentToken;
    private readonly Func<IDisposable?>? _acquireExternalConsentLock;
    private readonly object _gate = new();
    private TelemetryClient _client;
    private string? _activeBufferPath;
    private TelemetryConsent _consent = TelemetryConsent.Off;
    private string? _observedExternalToken;
    private CancellationTokenSource _flushCts = new();
    private int _inFlightFlushes;
    private long _consentEpoch;
    private int _revokedFlushesRemaining;
    private bool _disposeRequested;
    private bool _clientDisposed;
    private bool _lastPurgeFailed;
    internal Func<Task>? FlushOverrideForTesting { get; set; }
    internal int ExternalPollIntervalMsForTesting { get; set; } = 50;
    internal string? ActiveBufferPathForTesting
    {
        get
        {
            lock (_gate) return _activeBufferPath;
        }
    }
    internal bool IsClientDisposedForTesting
    {
        get
        {
            lock (_gate) return _clientDisposed;
        }
    }
    internal TelemetryConsentState NativeConsentForTesting
    {
        get
        {
            lock (_gate) return _clientDisposed ? TelemetryConsentState.Denied : _client.Consent();
        }
    }

    public CoreTelemetryService(
        string platform,
        string appVersion,
        string locale,
        string? videosApiBaseUrl,
        string userAgent,
        string? bufferPath,
        bool initialEnabled = false,
        Func<string?>? externalConsentToken = null,
        Func<IDisposable?>? acquireExternalConsentLock = null)
    {
        _bufferPath = bufferPath;
        _externalConsentToken = externalConsentToken;
        _acquireExternalConsentLock = acquireExternalConsentLock;
        var os = AunClient.CurrentOs();
        _app = new TelemetryAppInfo(
            platform,
            appVersion,
            string.IsNullOrEmpty(os.Version) ? os.Name : $"{os.Name} {os.Version}",
            "desktop",
            locale);

        _ingestUrl = ResolveIngestUrl(videosApiBaseUrl);
        _userAgent = userAgent;
        _clientHeader = AunClient.FromUserAgent(userAgent, os);
        var initialToken = initialEnabled && _externalConsentToken != null ? ReadExternalTokenSafe() : null;
        _activeBufferPath = ResolveBufferPathForToken(initialToken);
        EnsureBufferLockFileExists();
        _client = CreateClient(_activeBufferPath);
        if (initialEnabled)
        {
            SetConsent(TelemetryConsent.Full);
        }
        else
        {
            // Startup with telemetry disabled revokes this instance and cleans up any orphaned
            // buffers under the shared cross-process buffer lock, preserving any active generation
            // currently recorded in external consent.
            _consent = TelemetryConsent.Off;
            _observedExternalToken = null;
            using var crossProcessLock = AcquireCrossProcessBufferLock();
            try
            {
                _client.SetConsent(TelemetryConsentState.Denied);
                if (crossProcessLock != null || string.IsNullOrWhiteSpace(_bufferPath))
                {
                    var keepPath = ResolveForeignKeepPathLocked();
                    _lastPurgeFailed = !(TryDeleteForeignGenerationBuffers(keepPath) && TryDeleteSiblingTempFiles(keepPath));
                }
                else
                {
                    TryDeleteSingleBufferAndTemps(_activeBufferPath);
                    _lastPurgeFailed = true;
                }
            }
            catch (AunException ex)
            {
                if (crossProcessLock != null)
                {
                    TryDeleteBufferFallback(ResolveForeignKeepPathLocked());
                }
                else
                {
                    TryDeleteSingleBufferAndTemps(_activeBufferPath);
                }
                _lastPurgeFailed = true;
                Log.For("telemetry").Error("telemetry disk purge failed on startup opt-out: " + ex.Message);
            }
        }
    }

    private TelemetryClient CreateClient(string? bufferPath)
    {
        var config = new TelemetryClientConfig(
            _app,
            _ingestUrl,
            _userAgent,
            _clientHeader,
            bufferPath,
            null,
            null);
        return new TelemetryClient(config);
    }

    /// <summary>
    /// Creates a live telemetry service, falling back to <see cref="NullTelemetryService.Instance"/>
    /// in demo mode or if the native library cannot be loaded on the current platform.
    /// </summary>
    public static ITelemetryService CreateOrNull(
        string platform,
        string appVersion,
        string locale,
        string? videosApiBaseUrl,
        string userAgent,
        string? bufferPath,
        bool initialEnabled,
        bool demo = false,
        Func<string?>? externalConsentToken = null,
        Func<IDisposable?>? acquireExternalConsentLock = null)
    {
        if (demo) return NullTelemetryService.Instance;
        try
        {
            return new CoreTelemetryService(
                platform, appVersion, locale, videosApiBaseUrl, userAgent, bufferPath,
                initialEnabled, externalConsentToken, acquireExternalConsentLock);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or AunException)
        {
            Log.For("telemetry").Warn("telemetry unavailable, continuing with no-op service: " + ex.Message);
            return NullTelemetryService.Instance;
        }
    }

    /// <summary>
    /// Constructs <c>https://&lt;host&gt;[:port]/api/v1/telemetry/batches</c> from a parsed HTTPS URI,
    /// rejecting embedded credentials and stripping any path, query, or fragment.
    /// Returns <c>null</c> (no-op sink) for empty, credential-bearing, or non-HTTPS URLs.
    /// </summary>
    public static string? ResolveIngestUrl(string? videosApiBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(videosApiBaseUrl)) return null;
        if (!Uri.TryCreate(videosApiBaseUrl.Trim(), UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;
        var host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.DnsSafeHost}]" : uri.IdnHost;
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"https://{host}{port}/api/v1/telemetry/batches";
    }

    /// <summary>
    /// Scrubs local filesystem paths and file:// URIs before crash or breadcrumb text enters telemetry.
    /// </summary>
    private const string VaultPrefix = "\u001EAUNURL";

    internal static string SanitizeLocalPaths(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var escaped = EscapeSentinels(text, out var hadSentinels);
        var scrubbed = VaultNonFileUrls(escaped, VaultPrefix, out var vaultedUrls);
        scrubbed = RedactFileUris(scrubbed, VaultPrefix);
        scrubbed = ParenPathRegex.Replace(scrubbed, "$1[redacted-path]$2");
        scrubbed = ParenRelativeSlashPathRegex.Replace(scrubbed, "$1[redacted-path]$2");
        scrubbed = WindowsPathRegex.Replace(scrubbed, "[redacted-path]");
        scrubbed = RelativeBackslashPathRegex.Replace(scrubbed, "$1[redacted-path]");
        scrubbed = QuotedSpacedRootPathRegex.Replace(scrubbed, "[redacted-path]");
        scrubbed = UnixPathRegex.Replace(scrubbed, "[redacted-path]");
        scrubbed = RelativeSlashPathRegex.Replace(scrubbed, "$1[redacted-path]");
        scrubbed = RedactQuotedFilenames(scrubbed);
        scrubbed = KeywordQuotedPathRegex.Replace(scrubbed, "$1[redacted-path]");
        scrubbed = RedactKeywordUnquotedFilenames(scrubbed);
        scrubbed = KeywordAssignedPathRegex.Replace(scrubbed, "$1[redacted-path]");
        if (scrubbed.Contains('.'))
        {
            scrubbed = BareFilenameRegex.Replace(scrubbed, "$1$2[redacted-path]");
        }
        scrubbed = RestoreVaultedUrls(scrubbed, VaultPrefix, vaultedUrls);
        scrubbed = RedactEmails(scrubbed);
        scrubbed = BearerTokenRegex.Replace(scrubbed, "Bearer [redacted]");
        return hadSentinels ? UnescapeSentinels(scrubbed) : scrubbed;
    }

    private static string VaultNonFileUrls(string text, string prefix, out List<string> vaultedUrls)
    {
        vaultedUrls = new List<string>();
        if (text.IndexOf("://", StringComparison.Ordinal) < 0) return text;
        var tracker = new VaultLineTracker();
        StringBuilder? sb = null;
        var copied = 0;
        var searchFrom = 0;
        while (searchFrom < text.Length)
        {
            var sep = text.IndexOf("://", searchFrom, StringComparison.Ordinal);
            if (sep < 0) break;
            if (!TryFindNonFileSchemeStart(text, sep, copied, out var start))
            {
                searchFrom = sep + 3;
                continue;
            }
            if (tracker.IsEmbeddedInPath(text, start))
            {
                searchFrom = sep + 3;
                continue;
            }
            if (!TryScanUrlAfterScheme(text, sep, out var baseEnd, out var totalEnd, out var hasQf))
            {
                searchFrom = sep + 3;
                continue;
            }
            string url;
            string tail;
            if (hasQf)
            {
                var qf = TrimQueryOrFragmentDelimiters(text.Substring(baseEnd, totalEnd - baseEnd), text, start, out tail);
                url = text.Substring(start, baseEnd - start) + qf[0] + "[redacted]";
            }
            else
            {
                url = text.Substring(start, baseEnd - start);
                tail = string.Empty;
                if (url.EndsWith(';'))
                {
                    url = url[..^1];
                    tail = ";";
                }
                if (start > 0 && text[start - 1] == '\'' && url.EndsWith('\''))
                {
                    url = url[..^1];
                    tail = "'" + tail;
                }
            }
            sb ??= new StringBuilder(text.Length);
            sb.Append(text, copied, start - copied);
            var idx = vaultedUrls.Count;
            vaultedUrls.Add(url);
            tracker.OnVaultedUrl(totalEnd);
            sb.Append(prefix).Append(idx).Append('\u001F').Append(tail);
            copied = totalEnd;
            searchFrom = totalEnd;
        }
        if (sb == null) return text;
        sb.Append(text, copied, text.Length - copied);
        return sb.ToString();
    }

    private static bool TryFindNonFileSchemeStart(string text, int sep, int minStart, out int start)
    {
        start = sep;
        while (start > minStart && IsUrlSchemeChar(text[start - 1])) start--;
        if (sep - start < 2 || !char.IsAsciiLetter(text[start])) return false;
        if (start > 0 && (IsUrlSchemeChar(text[start - 1]) || text[start - 1] is '/' or '\\')) return false;
        return !text.AsSpan(start, sep - start).Equals("file", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryScanUrlAfterScheme(
        string text, int sep, out int baseEnd, out int totalEnd, out bool hasQf)
    {
        baseEnd = 0;
        totalEnd = 0;
        hasQf = false;
        var p = sep + 3;
        if (!TryScanUrlHost(text, ref p)) return false;
        if (p < text.Length && text[p] == ':')
        {
            var portStart = p + 1;
            var portEnd = portStart;
            while (portEnd < text.Length && char.IsAsciiDigit(text[portEnd])) portEnd++;
            if (portEnd == portStart) return false;
            p = portEnd;
        }
        var truncatedPath = false;
        if (p < text.Length && text[p] == '/')
        {
            p = ScanUrlPath(text, p, out truncatedPath);
        }
        baseEnd = p;
        if (!truncatedPath && p < text.Length && text[p] is '?' or '#')
        {
            hasQf = true;
            p++;
            while (p < text.Length && !char.IsWhiteSpace(text[p]) && text[p] is not ('"' or '<' or '>' or '\r' or '\n'))
            {
                p++;
            }
        }
        totalEnd = p;
        return totalEnd == text.Length || IsUrlEndBoundary(text, totalEnd);
    }

    private static bool TryScanUrlHost(string text, ref int p)
    {
        if (p >= text.Length) return false;
        if (text[p] == '[')
        {
            var close = p + 1;
            while (close < text.Length)
            {
                var ch = text[close];
                if (ch == ']') break;
                if (char.IsWhiteSpace(ch) || ch is '[' or '"' or '\'' or '<' or '>' or '\\' or '{' or '}' or '\r' or '\n')
                {
                    return false;
                }
                close++;
            }
            if (close >= text.Length || text[close] != ']' || close == p + 1) return false;
            var ipv6 = text.Substring(p + 1, close - p - 1);
            if (!IPAddress.TryParse(ipv6, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
            p = close + 1;
            return true;
        }
        var hostStart = p;
        while (p < text.Length)
        {
            var ch = text[p];
            if (IsUrlHostChar(ch))
            {
                p++;
            }
            else if (ch == '%' && p + 2 < text.Length && char.IsAsciiHexDigit(text[p + 1]) && char.IsAsciiHexDigit(text[p + 2]))
            {
                p += 3;
            }
            else
            {
                break;
            }
        }
        return p > hostStart;
    }

    private static bool IsUrlHostChar(char ch) =>
        IsAsciiAlphaNum(ch) || ch is '.' or '_' or '~' or '!' or '$' or '&' or '\'' or '*' or '+' or ';' or '=' or '-';

    private static int ScanUrlPath(string text, int p, out bool truncated)
    {
        truncated = false;
        var openPositions = new List<int>();
        var openChars = new List<char>();
        while (p < text.Length)
        {
            if (IsFileSchemeAt(text, p) || IsWindowsDriveAt(text, p)) break;
            var ch = text[p];
            if (char.IsWhiteSpace(ch) || ch is '"' or '<' or '>' or '\\' or '{' or '}' or ',' or '?' or '#' or '\r' or '\n')
            {
                break;
            }
            if (ch is '(' or '[')
            {
                if (StartsEmbeddedPathInsideGroup(text, p + 1)) break;
                openPositions.Add(p);
                openChars.Add(ch);
                p++;
                continue;
            }
            if (ch is ')' or ']')
            {
                var expected = ch == ')' ? '(' : '[';
                if (openChars.Count == 0 || openChars[^1] != expected) break;
                openPositions.RemoveAt(openPositions.Count - 1);
                openChars.RemoveAt(openChars.Count - 1);
                p++;
                continue;
            }
            if (openChars.Count > 0 && ch == ':') break;
            p++;
        }
        if (openPositions.Count > 0)
        {
            truncated = true;
            return openPositions[0];
        }
        return p;
    }

    private static bool StartsEmbeddedPathInsideGroup(string text, int nextPos)
    {
        if (nextPos >= text.Length) return true;
        var ch = text[nextPos];
        if (ch is '/' or '\\' or ':') return true;
        if (ch == '~' && nextPos + 1 < text.Length && text[nextPos + 1] == '/') return true;
        return IsFileSchemeAt(text, nextPos) || IsWindowsDriveAt(text, nextPos);
    }

    private static bool IsWindowsDriveAt(string text, int pos) =>
        pos + 2 < text.Length && char.IsAsciiLetter(text[pos]) && text[pos + 1] == ':' && text[pos + 2] is '/' or '\\';

    private static bool IsUrlEndBoundary(string text, int pos)
    {
        var ch = text[pos];
        if (char.IsWhiteSpace(ch) || ch is '"' or '\'' or '<' or '>' or '\\' or '{' or '}' or ',' or ';' or '(' or ')' or '[' or ']')
        {
            return true;
        }
        return IsFileSchemeAt(text, pos) || IsWindowsDriveAt(text, pos);
    }

    private static string RedactQuotedFilenames(string text)
    {
        if (!text.Contains('.')) return text;
        var result = text.Contains('"') ? RedactDoubleQuotedFilenames(text) : text;
        return result.Contains('\'') ? RedactSingleQuotedFilenames(result) : result;
    }

    private static string RedactDoubleQuotedFilenames(string text)
    {
        StringBuilder? sb = null;
        var copied = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '"') { i++; continue; }
            var j = i + 1;
            while (j < text.Length && text[j] is not ('"' or '\r' or '\n' or '/' or '\\' or '\u001E' or '\u001F')) j++;
            if (j < text.Length && text[j] == '"' && EndsWithFilenameExt(text, i + 1, j))
            {
                sb ??= new StringBuilder(text.Length);
                sb.Append(text, copied, i - copied).Append("[redacted-path]");
                copied = j + 1;
                i = j + 1;
            }
            else
            {
                i = j < text.Length && text[j] == '"' ? j : j + 1;
            }
        }
        if (sb == null) return text;
        return sb.Append(text, copied, text.Length - copied).ToString();
    }

    private static string RedactSingleQuotedFilenames(string text)
    {
        StringBuilder? sb = null;
        var copied = 0;
        var segStart = 0;
        while (segStart < text.Length)
        {
            var segEnd = segStart;
            while (segEnd < text.Length && text[segEnd] is not ('\r' or '\n' or '/' or '\\' or '\u001E' or '\u001F')) segEnd++;
            var open = -1;
            var candidateClose = -1;
            for (var p = segStart; p < segEnd; p++)
            {
                if (text[p] != '\'') continue;
                var isOpener = p == segStart || !char.IsLetterOrDigit(text[p - 1]);
                if (isOpener)
                {
                    if (open < 0 || candidateClose >= 0)
                    {
                        open = p;
                        candidateClose = -1;
                    }
                }
                else if (open >= 0 && (p + 1 == segEnd || !char.IsLetterOrDigit(text[p + 1])))
                {
                    if (EndsWithFilenameExt(text, open + 1, p))
                    {
                        sb ??= new StringBuilder(text.Length);
                        sb.Append(text, copied, open - copied).Append("[redacted-path]");
                        copied = p + 1;
                        open = -1;
                        candidateClose = -1;
                    }
                    else
                    {
                        candidateClose = p;
                    }
                }
            }
            segStart = segEnd + 1;
        }
        if (sb == null) return text;
        return sb.Append(text, copied, text.Length - copied).ToString();
    }

    private static bool EndsWithFilenameExt(string text, int start, int end)
    {
        var extStart = end;
        while (extStart > start && end - extStart < 17 && IsAsciiAlphaNum(text[extStart - 1])) extStart--;
        var extLen = end - extStart;
        return extLen >= 1 && extLen <= 16 && extStart > start && text[extStart - 1] == '.';
    }

    private static string RedactKeywordUnquotedFilenames(string text)
    {
        if (!text.Contains('.')) return text;
        StringBuilder? sb = null;
        var copied = 0;
        var segStart = 0;
        while (segStart < text.Length)
        {
            var segEnd = segStart;
            while (segEnd < text.Length && text[segEnd] is not ('\r' or '\n' or '\u001E' or '\u001F'))
            {
                segEnd++;
            }
            var (lastDot, lastExtEnd) = FindLastUnquotedFilenameExt(text, segStart, segEnd);
            if (lastDot >= 0)
            {
                var kwMatch = KeywordFilenamePrefixRegex.Match(text, segStart, lastDot - segStart);
                if (kwMatch.Success)
                {
                    var kwEnd = kwMatch.Index + kwMatch.Length;
                    sb ??= new StringBuilder(text.Length);
                    sb.Append(text, copied, kwEnd - copied);
                    sb.Append("[redacted-path]");
                    copied = lastExtEnd;
                }
            }
            segStart = segEnd + 1;
        }
        if (sb == null) return text;
        sb.Append(text, copied, text.Length - copied);
        return sb.ToString();
    }

    private static (int Dot, int ExtEnd) FindLastUnquotedFilenameExt(string text, int segStart, int segEnd)
    {
        for (var dot = segEnd - 1; dot >= segStart; dot--)
        {
            if (text[dot] != '.') continue;
            var extStart = dot + 1;
            var extEnd = extStart;
            while (extEnd < segEnd && extEnd - extStart < 17 && IsAsciiAlphaNum(text[extEnd]))
            {
                extEnd++;
            }
            var extLen = extEnd - extStart;
            if (extLen < 1 || extLen > 16) continue;
            if (extEnd < text.Length && (IsAsciiWordChar(text[extEnd]) || text[extEnd] == '(')) continue;
            if (extEnd + 1 < text.Length && text[extEnd] == ':' && char.IsAsciiDigit(text[extEnd + 1]) &&
                SourceLineExtensions.Contains(text.Substring(extStart, extLen)))
            {
                continue;
            }
            return (dot, extEnd);
        }
        return (-1, -1);
    }

    private static bool IsAsciiAlphaNum(char ch) =>
        (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9');

    private static string RedactEmails(string text)
    {
        if (text.IndexOf('@') < 0 && text.IndexOf("%40", StringComparison.Ordinal) < 0)
        {
            return text;
        }
        StringBuilder? sb = null;
        var copied = 0;
        var i = 0;
        while (i < text.Length)
        {
            var atLen = text[i] == '@'
                ? 1
                : (text[i] == '%' && i + 2 < text.Length && text[i + 1] == '4' && text[i + 2] == '0' ? 3 : 0);
            if (atLen == 0)
            {
                i++;
                continue;
            }
            var localStart = i;
            while (localStart > copied && IsEmailLocalChar(text, localStart - 1))
            {
                localStart--;
            }
            var domStart = i + atLen;
            var domEnd = localStart < i ? FindEmailDomainEnd(text, domStart) : -1;
            if (domEnd > 0)
            {
                sb ??= new StringBuilder(text.Length);
                sb.Append(text, copied, localStart - copied);
                sb.Append("[redacted-email]");
                copied = domEnd;
                i = domEnd;
            }
            else
            {
                i += atLen;
            }
        }
        if (sb == null) return text;
        sb.Append(text, copied, text.Length - copied);
        return sb.ToString();
    }

    private static bool IsEmailLocalChar(string text, int pos)
    {
        var ch = text[pos];
        if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') ||
            ch is '.' or '_' or '+' or '-')
        {
            return true;
        }
        return ch == '%' && !(pos + 2 < text.Length && text[pos + 1] == '4' && text[pos + 2] == '0');
    }

    private static int FindEmailDomainEnd(string text, int domStart)
    {
        var k = domStart;
        var lastDot = -1;
        var lettersSinceDot = 0;
        var bestEnd = -1;
        while (k < text.Length)
        {
            var ch = text[k];
            var isLetter = (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');
            var isDigit = ch >= '0' && ch <= '9';
            if (isLetter)
            {
                lettersSinceDot = lastDot >= 0 ? lettersSinceDot + 1 : 0;
            }
            else if (ch == '.')
            {
                lastDot = k > domStart ? k : -1;
                lettersSinceDot = 0;
            }
            else if (isDigit || ch == '-')
            {
                lettersSinceDot = -1;
            }
            else
            {
                break;
            }
            k++;
            if (lastDot > domStart && lettersSinceDot >= 2 && (k == text.Length || !IsAsciiWordChar(text[k])))
            {
                bestEnd = k;
            }
        }
        return bestEnd;
    }

    private static bool IsAsciiWordChar(char ch) =>
        (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_';

    private static string EscapeSentinels(string text, out bool hadSentinels)
    {
        var first = text.AsSpan().IndexOfAny('\u0018', '\u001E', '\u001F');
        if (first < 0)
        {
            hadSentinels = false;
            return text;
        }
        hadSentinels = true;
        var sb = new StringBuilder(text.Length + 8);
        sb.Append(text, 0, first);
        for (var i = first; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\u0018': sb.Append("\u0018\u0018"); break;
                case '\u001E': sb.Append("\u0018\u0019"); break;
                case '\u001F': sb.Append("\u0018\u001A"); break;
                default: sb.Append(text[i]); break;
            }
        }
        return sb.ToString();
    }

    private static string UnescapeSentinels(string text)
    {
        var first = text.IndexOf('\u0018');
        if (first < 0) return text;
        var sb = new StringBuilder(text.Length);
        sb.Append(text, 0, first);
        var i = first;
        while (i < text.Length)
        {
            if (text[i] == '\u0018' && i + 1 < text.Length)
            {
                switch (text[i + 1])
                {
                    case '\u0018': sb.Append('\u0018'); i += 2; continue;
                    case '\u0019': sb.Append('\u001E'); i += 2; continue;
                    case '\u001A': sb.Append('\u001F'); i += 2; continue;
                }
            }
            sb.Append(text[i++]);
        }
        return sb.ToString();
    }

    private struct VaultLineTracker
    {
        private int _cursor;
        private bool _hasPathOnLine;
        private char _openQuote;
        private char _pathQuote;
        private bool _escapedBackslash;
        private int _tokenStart;
        private int _tokenEnd;
        private int _tokenFirstSlash;
        private int _tokenSlashCount;
        private bool _tokenHasBackslash;
        private bool _tokenAllDigitsBeforeSlash;
        private bool _tokenHasDigitAfterSlash;
        private bool _tokenAllDigitsAfterSlash;
        private bool _tokenSawPunctAfterSlash;
        private bool _inWrapperRun;
        private bool _wsBeforeRun;

        public VaultLineTracker()
        {
            _tokenStart = -1;
            _tokenEnd = -1;
            _tokenFirstSlash = -1;
            _tokenAllDigitsBeforeSlash = true;
            _tokenAllDigitsAfterSlash = true;
        }

        public bool IsEmbeddedInPath(string text, int matchStart)
        {
            AdvanceTo(text, matchStart);
            if (_pathQuote != '\0' || (_hasPathOnLine && (!_wsBeforeRun || _inWrapperRun))) return true;
            return IsCurrentTokenPath(text);
        }

        public void OnVaultedUrl(int matchEnd)
        {
            _cursor = matchEnd;
            ResetLineState();
        }

        private void ResetTokenState()
        {
            _tokenStart = -1;
            _tokenEnd = -1;
            _tokenFirstSlash = -1;
            _tokenSlashCount = 0;
            _tokenHasBackslash = false;
            _tokenAllDigitsBeforeSlash = true;
            _tokenHasDigitAfterSlash = false;
            _tokenAllDigitsAfterSlash = true;
            _tokenSawPunctAfterSlash = false;
        }

        private void ResetLineState()
        {
            _hasPathOnLine = false;
            _openQuote = '\0';
            _pathQuote = '\0';
            _escapedBackslash = false;
            ResetTokenState();
            _inWrapperRun = false;
            _wsBeforeRun = false;
        }

        private void AdvanceTo(string text, int target)
        {
            while (_cursor < target)
            {
                var ch = text[_cursor];
                if (ch is '\r' or '\n')
                {
                    ResetLineState();
                }
                else
                {
                    ProcessLineChar(text, ch);
                }
                _cursor++;
            }
        }

        private void ProcessLineChar(string text, char ch)
        {
            if (!_escapedBackslash && _openQuote != '\0' && ch == _openQuote && !IsInnerApostrophe(text, _cursor, ch))
            {
                if (ch == '\'' && _hasPathOnLine)
                {
                    // Single-quoted paths may contain unescaped apostrophes; fail closed on this line.
                    _escapedBackslash = false;
                    return;
                }
                if (_pathQuote == ch || !_hasPathOnLine)
                {
                    ResetLineState();
                    return;
                }
                _openQuote = '\0';
                ResetTokenState();
                _inWrapperRun = false;
                _wsBeforeRun = false;
                _escapedBackslash = false;
                return;
            }
            var atTokenStart = _tokenStart < 0 || _inWrapperRun;
            if (IsFileSchemeAt(text, _cursor) || StartsLocalPathAt(text, _cursor, atTokenStart))
            {
                _hasPathOnLine = true;
                if (_pathQuote == '\0' && _openQuote != '\0')
                {
                    _pathQuote = _openQuote;
                }
            }
            if (char.IsWhiteSpace(ch))
            {
                if (!_inWrapperRun)
                {
                    PromoteTokenPath(text);
                    ResetTokenState();
                    _wsBeforeRun = true;
                }
            }
            else if (IsOpeningWrapper(ch))
            {
                if (!_inWrapperRun) PromoteTokenPath(text);
                if (!_escapedBackslash && _openQuote == '\0' && CanOpenQuote(text, _cursor, ch))
                {
                    _openQuote = ch;
                }
                _inWrapperRun = true;
            }
            else
            {
                if (_tokenStart < 0 || _inWrapperRun)
                {
                    ResetTokenState();
                    _tokenStart = _cursor;
                }
                _tokenEnd = _cursor + 1;
                TrackTokenChar(ch);
                _inWrapperRun = false;
                _wsBeforeRun = false;
            }
            _escapedBackslash = ch == '\\' ? !_escapedBackslash : false;
        }

        private void TrackTokenChar(char ch)
        {
            if (ch == '\\')
            {
                _tokenHasBackslash = true;
            }
            else if (ch == '/')
            {
                if (_tokenSlashCount == 0) _tokenFirstSlash = _cursor;
                _tokenSlashCount++;
            }
            else if (_tokenSlashCount == 0)
            {
                if (!char.IsAsciiDigit(ch)) _tokenAllDigitsBeforeSlash = false;
            }
            else if (_tokenSlashCount == 1)
            {
                if (ch is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}')
                {
                    _tokenSawPunctAfterSlash = true;
                }
                else if (char.IsAsciiDigit(ch))
                {
                    if (_tokenSawPunctAfterSlash) _tokenAllDigitsAfterSlash = false;
                    else _tokenHasDigitAfterSlash = true;
                }
                else
                {
                    _tokenAllDigitsAfterSlash = false;
                }
            }
        }

        private bool IsCurrentTokenPath(string text)
        {
            if (_tokenSlashCount == 0 && !_tokenHasBackslash) return false;
            if (_openQuote != '\0' || _tokenStart < 0 || _tokenSlashCount != 1 || _tokenHasBackslash) return true;
            var beforeLen = _tokenFirstSlash - _tokenStart;
            if (beforeLen <= 0 || _tokenFirstSlash + 1 >= _tokenEnd) return true;
            if (_tokenAllDigitsBeforeSlash && _tokenHasDigitAfterSlash && _tokenAllDigitsAfterSlash) return false;
            if ((beforeLen == 3 || beforeLen == 4) && char.IsAsciiDigit(text[_tokenFirstSlash + 1]))
            {
                var before = text.AsSpan(_tokenStart, beforeLen);
                if (before.Equals("HTTP", StringComparison.OrdinalIgnoreCase) ||
                    before.Equals("TLS", StringComparison.OrdinalIgnoreCase) ||
                    before.Equals("SSL", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }

        private void PromoteTokenPath(string text)
        {
            if (IsCurrentTokenPath(text))
            {
                _hasPathOnLine = true;
                if (_pathQuote == '\0' && _openQuote != '\0')
                {
                    _pathQuote = _openQuote;
                }
            }
        }

        private static bool CanOpenQuote(string text, int pos, char ch)
        {
            if (ch == '"') return true;
            if (ch != '\'') return false;
            return pos == 0 || !char.IsLetterOrDigit(text[pos - 1]);
        }

        private static bool IsInnerApostrophe(string text, int pos, char ch) =>
            ch == '\'' && pos > 0 && pos + 1 < text.Length &&
            char.IsLetterOrDigit(text[pos - 1]) && char.IsLetter(text[pos + 1]);
    }

    private static bool StartsLocalPathAt(string text, int pos, bool atTokenStart)
    {
        if (!atTokenStart) return false;
        var ch = text[pos];
        if (ch == '/') return true;
        if (ch == '~' && pos + 1 < text.Length && text[pos + 1] == '/') return true;
        if (ch == '\\' && pos + 1 < text.Length && text[pos + 1] == '\\') return true;
        return ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z')) &&
            pos + 2 < text.Length && text[pos + 1] == ':' && text[pos + 2] is '/' or '\\';
    }

    private static bool IsUrlSchemeChar(char ch) =>
        (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch is '+' or '.' or '-';

    private static bool IsOpeningWrapper(char ch) => ch is '(' or '[' or '{' or '"' or '\'';

    private static bool IsFileSchemeAt(string text, int pos) =>
        pos + 7 <= text.Length &&
        (pos == 0 || !IsUrlSchemeChar(text[pos - 1])) &&
        text.AsSpan(pos, 7).Equals("file://", StringComparison.OrdinalIgnoreCase);

    private static string RedactFileUris(string text, string vaultPrefix)
    {
        if (text.Length < 7) return text;
        StringBuilder? sb = null;
        var copyStart = 0;
        var pos = 0;
        while (pos < text.Length)
        {
            if (IsFileSchemeAt(text, pos))
            {
                // Double-quoted file:// URIs terminate at the next unescaped '"'. Unquoted and
                // single-quoted file:// URIs fail closed to the next vaulted URL or line break so
                // unescaped apostrophes inside filenames (e.g. "O' Connor") can never leak suffixes.
                var isDoubleQuoted = pos > 0 && text[pos - 1] == '"';
                var schemeEnd = pos + 7;
                var rawEnd = schemeEnd;
                var stoppedAtVault = false;
                var escapedBackslash = false;
                while (rawEnd < text.Length)
                {
                    var ch = text[rawEnd];
                    if (ch is '\r' or '\n') break;
                    if (ch == '\u001E' &&
                        rawEnd + vaultPrefix.Length <= text.Length &&
                        text.AsSpan(rawEnd, vaultPrefix.Length).Equals(vaultPrefix, StringComparison.Ordinal))
                    {
                        stoppedAtVault = true;
                        break;
                    }
                    if (isDoubleQuoted && ch == '"' && !escapedBackslash)
                    {
                        break;
                    }
                    escapedBackslash = ch == '\\' ? !escapedBackslash : false;
                    rawEnd++;
                }
                var trimmedEnd = rawEnd;
                if (stoppedAtVault)
                {
                    while (trimmedEnd > schemeEnd && (IsOpeningWrapper(text[trimmedEnd - 1]) || char.IsWhiteSpace(text[trimmedEnd - 1])))
                    {
                        trimmedEnd--;
                    }
                }
                sb ??= new StringBuilder(text.Length);
                sb.Append(text, copyStart, pos - copyStart);
                sb.Append("[redacted-path]");
                sb.Append(text, trimmedEnd, rawEnd - trimmedEnd);
                copyStart = rawEnd;
                pos = rawEnd;
                continue;
            }
            pos++;
        }
        if (sb == null) return text;
        sb.Append(text, copyStart, text.Length - copyStart);
        return sb.ToString();
    }

    private static string RestoreVaultedUrls(string text, string prefix, List<string> vaultedUrls)
    {
        if (vaultedUrls.Count == 0) return text;
        var sb = new StringBuilder(text.Length);
        var pos = 0;
        while (pos < text.Length)
        {
            var idx = text.IndexOf(prefix, pos, StringComparison.Ordinal);
            if (idx < 0)
            {
                sb.Append(text, pos, text.Length - pos);
                break;
            }
            sb.Append(text, pos, idx - pos);
            var numStart = idx + prefix.Length;
            var numEnd = text.IndexOf('\u001F', numStart);
            if (numEnd > numStart &&
                int.TryParse(text.AsSpan(numStart, numEnd - numStart), out var urlIndex) &&
                (uint)urlIndex < (uint)vaultedUrls.Count)
            {
                sb.Append(vaultedUrls[urlIndex]);
                pos = numEnd + 1;
            }
            else
            {
                sb.Append(prefix);
                pos = numStart;
            }
        }
        return sb.ToString();
    }

    private static string TrimQueryOrFragmentDelimiters(string qf, string fullText, int matchStart, out string tail)
    {
        var openParens = 0;
        var closeParens = 0;
        var openBrackets = 0;
        var closeBrackets = 0;
        var openBraces = 0;
        var closeBraces = 0;
        foreach (var ch in qf)
        {
            switch (ch)
            {
                case '(': openParens++; break;
                case ')': closeParens++; break;
                case '[': openBrackets++; break;
                case ']': closeBrackets++; break;
                case '{': openBraces++; break;
                case '}': closeBraces++; break;
            }
        }
        var end = qf.Length;
        var precededBySingleQuote = matchStart > 0 && fullText[matchStart - 1] == '\'';
        while (end > 1)
        {
            var last = qf[end - 1];
            if (last is '.' or ';' or ',')
            {
                end--;
            }
            else if (last == '\'' && precededBySingleQuote)
            {
                end--;
            }
            else if (last == ')' && closeParens > openParens)
            {
                closeParens--;
                end--;
            }
            else if (last == ']' && closeBrackets > openBrackets)
            {
                closeBrackets--;
                end--;
            }
            else if (last == '}' && closeBraces > openBraces)
            {
                closeBraces--;
                end--;
            }
            else
            {
                break;
            }
        }
        tail = end < qf.Length ? qf[end..] : string.Empty;
        return end < qf.Length ? qf[..end] : qf;
    }

    public TelemetryConsent Consent
    {
        get
        {
            CancellationTokenSource? toCancel = null;
            TelemetryConsent current;
            lock (_gate)
            {
                if (!_disposeRequested && !_clientDisposed && _consent != TelemetryConsent.Off && _externalConsentToken != null)
                {
                    EnsureExternalConsentLocked(out toCancel);
                }
                current = _disposeRequested ? TelemetryConsent.Off : _consent;
            }
            if (toCancel != null) CancelQuietly(toCancel);
            return current;
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                if (_disposeRequested || _clientDisposed || _consent == TelemetryConsent.Off) return 0;
                return (int)_client.PendingCount();
            }
        }
    }

    public bool LastPurgeFailed
    {
        get
        {
            lock (_gate) return _lastPurgeFailed;
        }
    }

    public event Action<bool>? PurgeFailureChanged;
    public event Action<TelemetryConsent>? ConsentChanged;

    public bool SetConsent(TelemetryConsent consent)
    {
        var state = consent switch
        {
            TelemetryConsent.Full => TelemetryConsentState.Analytics,
            TelemetryConsent.CrashOnly => TelemetryConsentState.CrashOnly,
            _ => TelemetryConsentState.Denied,
        };

        CancellationTokenSource? toCancel = null;
        Action<bool>? purgeNotify = null;
        bool purgeNotifyValue = false;
        Action<TelemetryConsent>? consentNotify = null;
        TelemetryConsent consentNotifyValue = TelemetryConsent.Off;
        bool result;
        IDisposable? externalConsentLock = null;
        if (consent != TelemetryConsent.Off && _acquireExternalConsentLock != null)
        {
            externalConsentLock = _acquireExternalConsentLock();
            if (externalConsentLock == null)
            {
                return false;
            }
        }
        using (externalConsentLock)
        {
            var needsBufferLock = !string.IsNullOrWhiteSpace(_bufferPath);
            IDisposable? crossProcessLock = needsBufferLock ? AcquireCrossProcessBufferLock() : null;
            if (consent != TelemetryConsent.Off && needsBufferLock && crossProcessLock == null)
            {
                return false;
            }
            using (crossProcessLock)
            {
                lock (_gate)
                {
                var priorConsent = _consent;
                if (consent == TelemetryConsent.Off)
                {
                    if (needsBufferLock && crossProcessLock == null)
                    {
                        // Lock unavailable during opt-out: revoke local collection and remove our own
                        // buffer, but never report clean global purge without holding the lock.
                        RevokeLocked(
                            out toCancel,
                            "telemetry disk purge failed on opt-out (lock unavailable): ",
                            ref purgeNotify,
                            ref purgeNotifyValue,
                            keepForeignGenerationPath: null,
                            ownBufferOnly: true);
                        UpdatePurgeFailedLocked(true, ref purgeNotify, ref purgeNotifyValue);
                        result = false;
                    }
                    else
                    {
                        result = RevokeLocked(
                            out toCancel,
                            "telemetry disk purge failed on opt-out: ",
                            ref purgeNotify,
                            ref purgeNotifyValue,
                            ResolveForeignKeepPathLocked());
                    }
                }
                else if (_disposeRequested || _clientDisposed)
                {
                    return false;
                }
                else if (_revokedFlushesRemaining > 0 || (_lastPurgeFailed && !TryDeleteBufferFallback(ResolveForeignKeepPathLocked())))
                {
                    return false;
                }
                else
                {
                    string? externalToken = null;
                    if (_externalConsentToken != null)
                    {
                        externalToken = ReadExternalTokenSafe();
                        if (string.IsNullOrEmpty(externalToken))
                        {
                            return false;
                        }
                        var targetBufferPath = ResolveBufferPathForToken(externalToken);
                        if (!TryDeleteForeignGenerationBuffers(targetBufferPath))
                        {
                            return false;
                        }
                        if (!string.Equals(_activeBufferPath, targetBufferPath, StringComparison.Ordinal))
                        {
                            if (_inFlightFlushes > 0) return false;
                            TelemetryClient? nextClient = null;
                            try
                            {
                                nextClient = CreateClient(targetBufferPath);
                                _client.Dispose();
                                _client = nextClient;
                                _activeBufferPath = targetBufferPath;
                                nextClient = null;
                            }
                            catch (AunException ex)
                            {
                                nextClient?.Dispose();
                                Log.For("telemetry").Warn("failed to bind generation telemetry buffer: " + ex.Message);
                                return false;
                            }
                        }
                    }
                    if (_lastPurgeFailed)
                    {
                        UpdatePurgeFailedLocked(false, ref purgeNotify, ref purgeNotifyValue);
                    }
                    try
                    {
                        _client.SetConsent(state);
                        _consent = consent;
                        _observedExternalToken = externalToken;
                        UpdatePurgeFailedLocked(false, ref purgeNotify, ref purgeNotifyValue);
                        result = true;
                    }
                    catch (AunException ex)
                    {
                        // Fail closed in BOTH managed and native state so an earlier native consent update
                        // (before an I/O failure) cannot leave native telemetry enabled for a queued flush.
                        RevokeLocked(out toCancel, "telemetry rollback purge failed: ", ref purgeNotify, ref purgeNotifyValue);
                        Log.For("telemetry").Warn("failed to update telemetry consent; failing closed to Off: " + ex.Message);
                        result = false;
                    }
                }
                if (_consent != priorConsent)
                {
                    consentNotify = ConsentChanged;
                    consentNotifyValue = _consent;
                }
            }
            }
        }
        if (toCancel != null) CancelQuietly(toCancel);
        NotifyConsentChangedQuietly(consentNotify, consentNotifyValue);
        NotifyPurgeFailureQuietly(purgeNotify, purgeNotifyValue);
        return result;
    }

    private void WithPersistingConsent(bool requiresFull, Action nativeAction)
    {
        lock (_gate)
        {
            if (_disposeRequested || _clientDisposed || _consent == TelemetryConsent.Off ||
                (requiresFull && _consent != TelemetryConsent.Full))
            {
                return;
            }
        }
        CancellationTokenSource? toCancel = null;
        IDisposable? externalConsentLock = null;
        if (_acquireExternalConsentLock != null)
        {
            externalConsentLock = _acquireExternalConsentLock();
            if (externalConsentLock == null)
            {
                lock (_gate)
                {
                    EnsureExternalConsentLocked(out toCancel);
                }
                if (toCancel != null) CancelQuietly(toCancel);
                return;
            }
        }
        try
        {
            lock (_gate)
            {
                if (!EnsureExternalConsentLocked(out toCancel)) goto CancelAndReturn;
                if (_disposeRequested || _clientDisposed || _consent == TelemetryConsent.Off ||
                    (requiresFull && _consent != TelemetryConsent.Full))
                {
                    return;
                }
                nativeAction();
            }
        }
        finally
        {
            externalConsentLock?.Dispose();
        }
        return;
    CancelAndReturn:
        if (toCancel != null) CancelQuietly(toCancel);
    }

    public void Track(string name, IReadOnlyDictionary<string, object>? properties = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var json = SerializeProperties(properties);
        WithPersistingConsent(requiresFull: true, () =>
        {
            try
            {
                _client.Track(name, json);
            }
            catch (AunException ex)
            {
                Log.For("telemetry").Warn("telemetry track skipped: " + ex.Message);
            }
        });
    }

    public void TrackError(string code, IReadOnlyDictionary<string, object>? properties = null)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        var json = SerializeProperties(properties);
        WithPersistingConsent(requiresFull: true, () =>
        {
            try
            {
                _client.TrackError(code, json);
            }
            catch (AunException ex)
            {
                Log.For("telemetry").Warn("telemetry error skipped: " + ex.Message);
            }
        });
    }

    public void AddBreadcrumb(string category, string message, TelemetryBreadcrumbSeverity level = TelemetryBreadcrumbSeverity.Info)
    {
        if (string.IsNullOrWhiteSpace(category)) return;
        var mapped = level switch
        {
            TelemetryBreadcrumbSeverity.Debug => TelemetryBreadcrumbLevel.Debug,
            TelemetryBreadcrumbSeverity.Warning => TelemetryBreadcrumbLevel.Warning,
            TelemetryBreadcrumbSeverity.Error => TelemetryBreadcrumbLevel.Error,
            _ => TelemetryBreadcrumbLevel.Info,
        };
        var safeMessage = SanitizeLocalPaths(message ?? "");
        CancellationTokenSource? toCancel = null;
        lock (_gate)
        {
            if (!EnsureExternalConsentLocked(out toCancel)) goto CancelAndReturn;
            if (_disposeRequested || _clientDisposed || _consent == TelemetryConsent.Off) return;
            _client.AddBreadcrumb(category, safeMessage, mapped);
        }
        return;
    CancelAndReturn:
        if (toCancel != null) CancelQuietly(toCancel);
    }

    public void ReportCrash(string kind, string message, string? stack = null, bool fromPreviousSession = false)
    {
        var safeMessage = string.IsNullOrWhiteSpace(message) ? "Unhandled exception" : SanitizeLocalPaths(message);
        var safeStack = stack == null ? null : SanitizeLocalPaths(stack);
        var report = new TelemetryCrashReport(
            string.IsNullOrWhiteSpace(kind) ? "UnhandledException" : kind,
            safeMessage,
            safeStack,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            fromPreviousSession,
            null);
        WithPersistingConsent(requiresFull: false, () =>
        {
            try
            {
                _client.ReportCrash(report);
                _client.Persist();
            }
            catch (AunException ex)
            {
                Log.For("telemetry").Warn("telemetry crash record skipped: " + ex.Message);
            }
        });
    }

    public void Persist()
    {
        WithPersistingConsent(requiresFull: false, () =>
        {
            try
            {
                _client.Persist();
            }
            catch (AunException ex)
            {
                Log.For("telemetry").Warn("telemetry persist failed: " + ex.Message);
            }
        });
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return;

        CancellationToken revokeToken;
        long flushEpoch;
        string? flushBufferPath;
        CancellationTokenSource? toCancel = null;
        Task? nativeFlush = null;
        CancellationTokenSource? monitorCts = null;
        var flushOverride = FlushOverrideForTesting;
        IDisposable? externalConsentLock = null;
        if (_acquireExternalConsentLock != null)
        {
            externalConsentLock = _acquireExternalConsentLock();
            if (externalConsentLock == null)
            {
                lock (_gate)
                {
                    EnsureExternalConsentLocked(out toCancel);
                }
                if (toCancel != null) CancelQuietly(toCancel);
                return;
            }
        }
        try
        {
            lock (_gate)
            {
                if (!EnsureExternalConsentLocked(out toCancel))
                {
                    if (toCancel != null) goto CancelAndReturn;
                    return;
                }
                if (cancellationToken.IsCancellationRequested || _disposeRequested || _clientDisposed || _consent == TelemetryConsent.Off)
                {
                    return;
                }
                revokeToken = _flushCts.Token;
                if (revokeToken.IsCancellationRequested) return;
                flushEpoch = _consentEpoch;
                flushBufferPath = _activeBufferPath;
                _inFlightFlushes++;
                if (_externalConsentToken != null)
                {
                    monitorCts = new CancellationTokenSource();
                    _ = MonitorExternalConsentWhileFlushingAsync(monitorCts.Token);
                }
                try
                {
                    // Dispatch immediately under the cross-process consent lock and `_gate` right after
                    // verifying external consent so another process cannot persist opt-out before dispatch.
                    nativeFlush = flushOverride != null ? flushOverride() : _client.Flush();
                }
                catch (Exception ex) when (ex is AunException or ObjectDisposedException or OperationCanceledException)
                {
                    if (!cancellationToken.IsCancellationRequested && !revokeToken.IsCancellationRequested)
                    {
                        Log.For("telemetry").Warn("telemetry flush skipped: " + ex.Message);
                    }
                }
            }
        }
        finally
        {
            externalConsentLock?.Dispose();
        }

        if (nativeFlush == null)
        {
            if (monitorCts != null) CancelQuietly(monitorCts);
            ReleaseInFlightFlush(flushEpoch, flushBufferPath);
            return;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, revokeToken);
            var monitorToStop = monitorCts;
            // Defer decrementing `_inFlightFlushes` (and any pending native `_client.Dispose()`) until
            // `nativeFlush` actually finishes, even if `WaitWithCancellationAsync` returns early on cancel/revoke.
            _ = nativeFlush.ContinueWith(
                static (t, state) =>
                {
                    _ = t.Exception;
                    var (svc, cts, epoch, bufPath) = ((CoreTelemetryService, CancellationTokenSource?, long, string?))state!;
                    if (cts != null) CancelQuietly(cts);
                    svc.ReleaseInFlightFlush(epoch, bufPath);
                },
                (this, monitorToStop, flushEpoch, flushBufferPath),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            await WaitWithCancellationAsync(nativeFlush, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AunException or ObjectDisposedException or OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested && !revokeToken.IsCancellationRequested)
            {
                Log.For("telemetry").Warn("telemetry flush skipped: " + ex.Message);
            }
        }
        return;
    CancelAndReturn:
        CancelQuietly(toCancel);
    }

    private async Task MonitorExternalConsentWhileFlushingAsync(CancellationToken stopToken)
    {
        var intervalMs = Math.Max(10, ExternalPollIntervalMsForTesting);
        while (!stopToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(intervalMs, stopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (stopToken.IsCancellationRequested) return;
            CancellationTokenSource? toCancel = null;
            bool revokedNow;
            lock (_gate)
            {
                revokedNow = !EnsureExternalConsentLocked(out toCancel);
            }
            if (toCancel != null) CancelQuietly(toCancel);
            if (revokedNow) return;
        }
    }

    /// <summary>
    /// Re-checks shared persisted settings when an external consent probe is wired (for multi-instance apps).
    /// If another instance revoked consent, revokes this instance immediately before any write or network send.
    /// Must be called while holding <see cref="_gate"/>.
    /// </summary>
    private bool EnsureExternalConsentLocked(out CancellationTokenSource? toCancel)
    {
        toCancel = null;
        if (_disposeRequested || _clientDisposed || _consent == TelemetryConsent.Off || _externalConsentToken == null)
        {
            return true;
        }
        string? currentToken;
        try
        {
            currentToken = _externalConsentToken();
        }
        catch
        {
            currentToken = null;
        }
        if (!string.IsNullOrEmpty(currentToken) && string.Equals(currentToken, _observedExternalToken, StringComparison.Ordinal))
        {
            return true;
        }

        Action<bool>? purgeNotify = null;
        bool purgeNotifyValue = false;
        var priorConsent = _consent;
        RevokeLocked(
            out toCancel,
            "telemetry disk purge failed after cross-instance opt-out: ",
            ref purgeNotify,
            ref purgeNotifyValue,
            keepForeignGenerationPath: null,
            ownBufferOnly: true);
        var consentNotify = _consent != priorConsent ? ConsentChanged : null;
        var consentNotifyValue = _consent;
        if (consentNotify != null || purgeNotify != null)
        {
            var notifyConsent = consentNotify;
            var consentValue = consentNotifyValue;
            var notify = purgeNotify;
            var value = purgeNotifyValue;
            _ = Task.Run(() =>
            {
                NotifyConsentChangedQuietly(notifyConsent, consentValue);
                NotifyPurgeFailureQuietly(notify, value);
            });
        }
        return false;
    }

    /// <summary>
    /// Revokes managed and native consent (unless already disposed) and cleans up both the buffer
    /// file and sibling atomic-persist temp files. Must be called while holding <see cref="_gate"/>.
    /// </summary>
    private bool RevokeLocked(
        out CancellationTokenSource? toCancel,
        string failureLogPrefix,
        ref Action<bool>? purgeNotify,
        ref bool purgeNotifyValue,
        string? keepForeignGenerationPath = null,
        bool ownBufferOnly = false)
    {
        _consent = TelemetryConsent.Off;
        _observedExternalToken = null;
        _consentEpoch++;
        _revokedFlushesRemaining = _inFlightFlushes;
        toCancel = _flushCts;
        _flushCts = new CancellationTokenSource();
        if (_clientDisposed)
        {
            var fallbackOk = ownBufferOnly
                ? TryDeleteSingleBufferAndTemps(_activeBufferPath)
                : TryDeleteBufferFallback(keepForeignGenerationPath);
            UpdatePurgeFailedLocked(!fallbackOk, ref purgeNotify, ref purgeNotifyValue);
            return !_lastPurgeFailed;
        }
        try
        {
            _client.SetConsent(TelemetryConsentState.Denied);
            var cleanOk = ownBufferOnly
                ? TryDeleteSingleBufferAndTemps(_activeBufferPath)
                : TryDeleteForeignGenerationBuffers(keepForeignGenerationPath) && TryDeleteSiblingTempFiles(keepForeignGenerationPath);
            UpdatePurgeFailedLocked(!cleanOk, ref purgeNotify, ref purgeNotifyValue);
        }
        catch (AunException ex)
        {
            _ = ownBufferOnly
                ? TryDeleteSingleBufferAndTemps(_activeBufferPath)
                : TryDeleteBufferFallback(keepForeignGenerationPath);
            UpdatePurgeFailedLocked(true, ref purgeNotify, ref purgeNotifyValue);
            Log.For("telemetry").Error(failureLogPrefix + ex.Message);
        }
        return !_lastPurgeFailed;
    }

    private void UpdatePurgeFailedLocked(bool failed, ref Action<bool>? purgeNotify, ref bool purgeNotifyValue)
    {
        if (_lastPurgeFailed == failed) return;
        _lastPurgeFailed = failed;
        purgeNotify = PurgeFailureChanged;
        purgeNotifyValue = failed;
    }

    private static void NotifyConsentChangedQuietly(Action<TelemetryConsent>? handler, TelemetryConsent consent)
    {
        if (handler == null) return;
        try
        {
            handler(consent);
        }
        catch (Exception ex)
        {
            Log.For("telemetry").Warn("telemetry consent listener failed: " + ex.Message);
        }
    }

    private string? GetBufferLockPath()
    {
        if (string.IsNullOrWhiteSpace(_bufferPath)) return null;
        var dir = Path.GetDirectoryName(_bufferPath);
        var stem = Path.GetFileNameWithoutExtension(_bufferPath);
        if (string.IsNullOrEmpty(stem)) return null;
        // Keep the lock file in the same shared state directory as the buffer files so processes
        // with different TMPDIR values coordinate on the exact same lock file.
        return string.IsNullOrEmpty(dir) ? $".{stem}.lock" : Path.Combine(dir, $".{stem}.lock");
    }

    private void EnsureBufferLockFileExists()
    {
        var lockPath = GetBufferLockPath();
        if (string.IsNullOrEmpty(lockPath)) return;
        try
        {
            var dir = Path.GetDirectoryName(lockPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !File.Exists(lockPath))
            {
                using var _ = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private IDisposable? AcquireCrossProcessBufferLock()
    {
        var lockPath = GetBufferLockPath();
        if (string.IsNullOrEmpty(lockPath)) return null;
        try
        {
            var dir = Path.GetDirectoryName(lockPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (true)
            {
                try
                {
                    return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(10);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void NotifyPurgeFailureQuietly(Action<bool>? handler, bool failed)
    {
        if (handler == null) return;
        try
        {
            handler(failed);
        }
        catch (Exception ex)
        {
            Log.For("telemetry").Warn("telemetry purge-failure listener failed: " + ex.Message);
        }
    }

    private void ReleaseInFlightFlush(long flushEpoch, string? flushBufferPath)
    {
        Action<bool>? purgeNotify = null;
        bool purgeNotifyValue = false;
        lock (_gate)
        {
            _inFlightFlushes--;
            if (flushEpoch < _consentEpoch)
            {
                _revokedFlushesRemaining = Math.Max(0, _revokedFlushesRemaining - 1);
                if (!string.IsNullOrWhiteSpace(flushBufferPath))
                {
                    // A flush already in flight when consent was revoked may finish its final
                    // persist rename after SetConsent(Denied) deleted the file; remove only the
                    // revoked flush's own generation buffer and sibling temps so a concurrently
                    // enabled newer generation in another process can never be touched.
                    // Evaluate deletion unconditionally so a prior purge failure never skips cleanup.
                    var deleteSucceeded = TryDeleteSingleBufferAndTemps(flushBufferPath);
                    UpdatePurgeFailedLocked(
                        _lastPurgeFailed || !deleteSucceeded,
                        ref purgeNotify,
                        ref purgeNotifyValue);
                }
            }
            if (_disposeRequested && _inFlightFlushes == 0 && !_clientDisposed)
            {
                _clientDisposed = true;
                _client.Dispose();
            }
        }
        NotifyPurgeFailureQuietly(purgeNotify, purgeNotifyValue);
    }

    public void Dispose()
    {
        CancellationTokenSource toCancel;
        lock (_gate)
        {
            if (_disposeRequested) return;
            _disposeRequested = true;
            _consent = TelemetryConsent.Off;
            toCancel = _flushCts;
            if (_inFlightFlushes == 0 && !_clientDisposed)
            {
                _clientDisposed = true;
                _client.Dispose();
            }
        }
        CancelQuietly(toCancel);
    }

    private string? ResolveForeignKeepPathLocked()
    {
        var currentToken = ReadExternalTokenSafe();
        if (string.IsNullOrEmpty(currentToken)) return null;
        var currentPath = ResolveBufferPathForToken(currentToken);
        return string.Equals(currentPath, _activeBufferPath, StringComparison.Ordinal) ? null : currentPath;
    }

    private string? ReadExternalTokenSafe()
    {
        if (_externalConsentToken == null) return null;
        try
        {
            return _externalConsentToken();
        }
        catch
        {
            return null;
        }
    }

    private string? ResolveBufferPathForToken(string? externalToken)
    {
        if (string.IsNullOrWhiteSpace(_bufferPath)) return null;
        if (_externalConsentToken == null || string.IsNullOrWhiteSpace(externalToken)) return _bufferPath;
        var rawGen = externalToken.StartsWith("enabled:", StringComparison.Ordinal)
            ? externalToken["enabled:".Length..]
            : "legacy";
        var safeGen = new string(rawGen.Where(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        if (string.IsNullOrEmpty(safeGen)) safeGen = "legacy";
        var dir = Path.GetDirectoryName(_bufferPath);
        var stem = Path.GetFileNameWithoutExtension(_bufferPath);
        var ext = Path.GetExtension(_bufferPath);
        var fileName = $"{stem}.{safeGen}{ext}";
        return string.IsNullOrEmpty(dir) ? fileName : Path.Combine(dir, fileName);
    }

    private bool TryDeleteForeignGenerationBuffers(string? keepPath)
    {
        if (string.IsNullOrWhiteSpace(_bufferPath)) return true;
        var dir = Path.GetDirectoryName(_bufferPath);
        var stem = Path.GetFileNameWithoutExtension(_bufferPath);
        var ext = Path.GetExtension(_bufferPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem)) return true;
        var keepName = string.IsNullOrEmpty(keepPath) ? null : Path.GetFileName(keepPath);
        var allDeleted = true;
        try
        {
            if (Directory.Exists(dir))
            {
                var baseName = stem + ext;
                foreach (var candidate in Directory.EnumerateFiles(dir, $"{stem}*{ext}"))
                {
                    var name = Path.GetFileName(candidate);
                    if (keepName != null && string.Equals(name, keepName, StringComparison.Ordinal)) continue;
                    if (!string.Equals(name, baseName, StringComparison.Ordinal) &&
                        !name.StartsWith(stem + ".", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    try
                    {
                        File.Delete(candidate);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        allDeleted = false;
                    }
                }
                foreach (var tmp in Directory.EnumerateFiles(dir, $".{stem}*.tmp"))
                {
                    var name = Path.GetFileName(tmp);
                    if (keepName != null && name.StartsWith($".{keepName}.", StringComparison.Ordinal)) continue;
                    try
                    {
                        File.Delete(tmp);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        allDeleted = false;
                    }
                }
            }
            return allDeleted;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDeleteSingleBufferAndTemps(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return true;
        var mainOk = TryDeleteSingleFile(targetPath);
        var dir = Path.GetDirectoryName(targetPath);
        var fileName = Path.GetFileName(targetPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(fileName)) return mainOk;
        try
        {
            var allDeleted = mainOk;
            foreach (var tmp in Directory.EnumerateFiles(dir, $".{fileName}.*.tmp"))
            {
                if (!TryDeleteSingleFile(tmp)) allDeleted = false;
            }
            return allDeleted;
        }
        catch (DirectoryNotFoundException)
        {
            return mainOk;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryDeleteBufferFallback(string? keepForeignGenerationPath = null)
    {
        if (string.IsNullOrWhiteSpace(_bufferPath)) return true;
        var activePath = _activeBufferPath ?? _bufferPath;
        var mainOk = keepForeignGenerationPath != null && string.Equals(activePath, keepForeignGenerationPath, StringComparison.Ordinal)
            ? true
            : TryDeleteSingleFile(activePath);
        if (!string.Equals(activePath, _bufferPath, StringComparison.Ordinal) &&
            (keepForeignGenerationPath == null || !string.Equals(_bufferPath, keepForeignGenerationPath, StringComparison.Ordinal)))
        {
            mainOk = TryDeleteSingleFile(_bufferPath) && mainOk;
        }
        var foreignOk = TryDeleteForeignGenerationBuffers(keepForeignGenerationPath);
        var tempsOk = TryDeleteSiblingTempFiles(keepForeignGenerationPath);
        return mainOk && foreignOk && tempsOk;
    }

    private static bool TryDeleteSingleFile(string path)
    {
        try
        {
            // Do NOT pre-check with File.Exists/Directory.Exists: both return false when directory
            // search permission is denied, which would falsely report purge success while data remains.
            File.Delete(path);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryDeleteSiblingTempFiles(string? keepForeignGenerationPath = null)
    {
        if (string.IsNullOrWhiteSpace(_bufferPath)) return true;
        var dir = Path.GetDirectoryName(_bufferPath);
        var stem = Path.GetFileNameWithoutExtension(_bufferPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem)) return true;
        var keepName = string.IsNullOrEmpty(keepForeignGenerationPath) ? null : Path.GetFileName(keepForeignGenerationPath);
        try
        {
            var pattern = $".{stem}*.tmp";
            var allDeleted = true;
            foreach (var tmp in Directory.EnumerateFiles(dir, pattern))
            {
                if (keepName != null && Path.GetFileName(tmp).StartsWith($".{keepName}.", StringComparison.Ordinal))
                {
                    continue;
                }
                try
                {
                    File.Delete(tmp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    allDeleted = false;
                }
            }
            return allDeleted;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CancelQuietly(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cts.Dispose();
        }
    }

    private static async Task WaitWithCancellationAsync(Task task, CancellationToken token)
    {
        if (task.IsCompleted)
        {
            await task.ConfigureAwait(false);
            return;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = token.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), tcs);
        var winner = await Task.WhenAny(task, tcs.Task).ConfigureAwait(false);
        if (winner == task)
        {
            await task.ConfigureAwait(false);
            return;
        }
        // Observe any fault on the abandoned native flush task so it never escalates to UnobservedTaskException.
        _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    internal static string? SerializeProperties(IReadOnlyDictionary<string, object>? properties)
    {
        if (properties == null || properties.Count == 0) return null;
        var flat = new Dictionary<string, object>(properties.Count, StringComparer.Ordinal);
        foreach (var kv in properties)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null) continue;
            switch (kv.Value)
            {
                case string s:
                    flat[kv.Key] = s;
                    break;
                case bool b:
                    flat[kv.Key] = b;
                    break;
                case int i:
                    flat[kv.Key] = i;
                    break;
                case long l:
                    flat[kv.Key] = l;
                    break;
                case double d when double.IsFinite(d):
                    flat[kv.Key] = d;
                    break;
                case float f when float.IsFinite(f):
                    flat[kv.Key] = (double)f;
                    break;
                default:
                    flat[kv.Key] = kv.Value.ToString() ?? "";
                    break;
            }
        }
        return flat.Count == 0 ? null : JsonSerializer.Serialize(flat);
    }
}
