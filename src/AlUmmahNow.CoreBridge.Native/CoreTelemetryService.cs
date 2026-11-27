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
    private static readonly string[] KnownAuthSchemeList =
    {
        "Bearer", "Basic", "Digest", "Token", "Api-Key", "ApiKey",
        "OAuth", "Negotiate", "Mutual", "HOBA", "Vapid", "SCRAM-SHA-256", "AWS4-HMAC-SHA256"
    };
    private static readonly Regex BearerTokenRegex = new(@"\b(Bearer)\s+(?!\[redacted\](?=$|[\s""',;)\]}]))[^\s""',;)\]}]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BasicTokenRegex = new(@"\b(Basic)\s+([A-Za-z0-9+/_-]{2,}={0,2})(?![A-Za-z0-9+/_=-])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly TelemetryAppInfo _app;
    private readonly string? _ingestUrl;
    private readonly string _userAgent;
    private readonly string _clientHeader;
    private readonly string? _bufferPath;
    private readonly Func<string?>? _externalConsentToken;
    private readonly Func<IDisposable?>? _acquireExternalConsentLock;
    private readonly object _externalLockGate = new();
    private IDisposable? _heldExternalLock;
    private int _externalLockRefs;
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
        var scrubbed = RedactQuotedEmails(escaped);
        scrubbed = RedactAuthCredentials(scrubbed);
        scrubbed = VaultNonFileUrls(scrubbed, VaultPrefix, out var vaultedUrls);
        scrubbed = RedactFileUris(scrubbed, VaultPrefix);
        scrubbed = ReplaceUnquotedParenPaths(scrubbed, ParenPathRegex);
        scrubbed = ReplaceUnquotedParenPaths(scrubbed, ParenRelativeSlashPathRegex);
        scrubbed = WindowsPathRegex.Replace(scrubbed, "[redacted-path]");
        scrubbed = RelativeBackslashPathRegex.Replace(scrubbed, "$1[redacted-path]");
        scrubbed = QuotedSpacedRootPathRegex.Replace(scrubbed, "[redacted-path]");
        scrubbed = UnixPathRegex.Replace(scrubbed, "[redacted-path]");
        scrubbed = RelativeSlashPathRegex.Replace(scrubbed, "$1[redacted-path]");
        scrubbed = RedactQuotedFilenames(scrubbed);
        scrubbed = KeywordQuotedPathRegex.Replace(scrubbed, "$1[redacted-path]");
        scrubbed = RedactKeywordUnquotedFilenames(scrubbed);
        scrubbed = KeywordAssignedPathRegex.Replace(scrubbed, "$1[redacted-path]");
        scrubbed = RedactEmails(scrubbed);
        if (scrubbed.Contains('.'))
        {
            scrubbed = BareFilenameRegex.Replace(scrubbed, "$1$2[redacted-path]");
        }
        scrubbed = RestoreVaultedUrls(scrubbed, VaultPrefix, vaultedUrls);
        if (vaultedUrls.Count > 0)
        {
            scrubbed = RedactEmails(scrubbed);
        }
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
        var failedContMax = 0;
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
            var rawUrl = text.Substring(start, baseEnd - start);
            var redactedUrl = RedactUrlPathSecrets(url);
            if (!hasQf && totalEnd >= failedContMax && totalEnd < text.Length &&
                text[totalEnd] is ',' or '(' or '[' or ')' or ']')
            {
                if (TryExtendUrlContinuation(text, rawUrl, start, ref totalEnd, ref failedContMax, out var extUrl, out var extTail))
                {
                    redactedUrl = extUrl;
                    tail = extTail;
                }
            }
            sb ??= new StringBuilder(text.Length);
            sb.Append(text, copied, start - copied);
            var idx = vaultedUrls.Count;
            vaultedUrls.Add(redactedUrl);
            tracker.OnVaultedUrl(totalEnd);
            sb.Append(prefix).Append(idx).Append('\u001F').Append(tail);
            copied = totalEnd;
            searchFrom = totalEnd;
        }
        if (sb == null) return text;
        sb.Append(text, copied, text.Length - copied);
        return sb.ToString();
    }

    private static bool TryExtendUrlContinuation(
        string text, string rawUrl, int urlStart, ref int totalEnd, ref int failedContMax, out string resultUrl, out string tail)
    {
        resultUrl = string.Empty;
        tail = string.Empty;
        var p = ScanUrlContinuation(text, totalEnd);
        if (p > totalEnd && p < text.Length && text[p] is '?' or '#')
        {
            var qfStart = p;
            p++;
            while (p < text.Length && !char.IsWhiteSpace(text[p]) && text[p] is not ('"' or '<' or '>' or '\r' or '\n'))
            {
                p++;
            }
            var qf = TrimQueryOrFragmentDelimiters(text.Substring(qfStart, p - qfStart), text, urlStart, out tail);
            resultUrl = RedactUrlPathSecrets(text.Substring(urlStart, qfStart - urlStart)) + qf[0] + "[redacted]";
            totalEnd = p;
            return true;
        }
        var end = TrimWebhookContinuationEnd(text, urlStart, totalEnd, p);
        if (end > totalEnd)
        {
            var baseRedacted = RedactUrlPathSecrets(rawUrl);
            var extCandidate = text.Substring(urlStart, end - urlStart);
            var extRedacted = RedactUrlPathSecrets(extCandidate);
            if (extRedacted != baseRedacted + text.Substring(totalEnd, end - totalEnd))
            {
                resultUrl = extRedacted;
                tail = text.Substring(end, p - end);
                totalEnd = p;
                return true;
            }
        }
        if (p > failedContMax) failedContMax = p;
        return false;
    }

    private static int ScanUrlContinuation(string text, int start)
    {
        var p = start;
        while (p < text.Length)
        {
            if (IsFileSchemeAt(text, p) || IsWindowsDriveAt(text, p)) break;
            if (p > start && text[p - 1] is ',' or ';' or '(' or '[' or ')' or ']' or '\'' && IsWebUrlSchemeAt(text, p))
            {
                break;
            }
            var ch = text[p];
            if (char.IsWhiteSpace(ch) || ch is '"' or '<' or '>' or '\\' or '{' or '}' or '?' or '#' or '\r' or '\n')
            {
                break;
            }
            p++;
        }
        return p;
    }

    private static bool IsWebUrlSchemeAt(string text, int pos)
    {
        var span = text.AsSpan(pos);
        return span.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            span.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            span.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) ||
            span.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
            span.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);
    }

    private static int TrimWebhookContinuationEnd(string text, int urlStart, int minEnd, int scanEnd)
    {
        var excessParens = 0;
        var excessBrackets = 0;
        for (var i = urlStart; i < scanEnd; i++)
        {
            if (text[i] == '(') excessParens--;
            else if (text[i] == ')') excessParens++;
            else if (text[i] == '[') excessBrackets--;
            else if (text[i] == ']') excessBrackets++;
        }
        var precededByQuote = urlStart > 0 && text[urlStart - 1] == '\'';
        var end = scanEnd;
        while (end > minEnd)
        {
            var c = text[end - 1];
            if (c is ',' or ';') { end--; continue; }
            if (c == '\'' && precededByQuote) { end--; continue; }
            if (c == ')' && excessParens > 0) { excessParens--; end--; continue; }
            if (c == ']' && excessBrackets > 0) { excessBrackets--; end--; continue; }
            break;
        }
        return end;
    }

    private static readonly Regex WebhookPathTokenRegex = new(
        @"((?:^|/|%2[fF])webhooks?(?:/|%2[fF]))[^\s?#]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SlackWebhookUrlRegex = new(
        @"(https?://hooks\.slack(?:-gov)?\.com\.?(?::\d+)?(?:/|%2[fF])(?:services|workflows|triggers)(?:/|%2[fF]))[^\s?#]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TelegramBotTokenUrlRegex = new(
        @"((?:/|%2[fF])bot\d+:)[^/\s?#]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string RedactUrlPathSecrets(string url)
    {
        var result = ReplaceUrlPathSecret(url, WebhookPathTokenRegex);
        result = ReplaceUrlPathSecret(result, SlackWebhookUrlRegex);
        result = ReplaceUrlPathSecret(result, TelegramBotTokenUrlRegex);
        return result;
    }

    private static string ReplaceUrlPathSecret(string url, Regex regex)
    {
        if (url.IndexOf('%') < 0) return regex.Replace(url, "$1[redacted]");
        var (decoded, origOffsets) = DecodeUrlForPathSecretMatch(url);
        var matches = regex.Matches(decoded);
        if (matches.Count == 0) return url;
        var sb = new StringBuilder(url.Length);
        var copied = 0;
        foreach (Match m in matches)
        {
            var g1 = m.Groups[1];
            var secretStart = origOffsets[g1.Index + g1.Length];
            if (secretStart < copied) continue;
            var secretEnd = origOffsets[m.Index + m.Length];
            sb.Append(url, copied, secretStart - copied).Append("[redacted]");
            copied = secretEnd;
        }
        if (copied == 0) return url;
        sb.Append(url, copied, url.Length - copied);
        return sb.ToString();
    }

    private static (string Decoded, int[] OrigOffsets) DecodeUrlForPathSecretMatch(string url)
    {
        var decoded = new StringBuilder(url.Length);
        var offsets = new int[url.Length + 1];
        var outLen = 0;
        var i = 0;
        while (i < url.Length)
        {
            offsets[outLen++] = i;
            if (url[i] == '%' && i + 2 < url.Length && TryParseHexByte(url[i + 1], url[i + 2], out var b) &&
                b >= 0x21 && b <= 0x7E && b is not ((byte)'/' or (byte)'?' or (byte)'#'))
            {
                decoded.Append((char)b);
                i += 3;
            }
            else
            {
                decoded.Append(url[i]);
                i++;
            }
        }
        offsets[outLen] = url.Length;
        return (decoded.ToString(), offsets);
    }

    private static bool TryParseHexByte(char hi, char lo, out byte value)
    {
        var h = HexVal(hi);
        var l = HexVal(lo);
        if (h < 0 || l < 0)
        {
            value = 0;
            return false;
        }
        value = (byte)((h << 4) | l);
        return true;
    }

    private static int HexVal(char ch) => ch switch
    {
        >= '0' and <= '9' => ch - '0',
        >= 'a' and <= 'f' => ch - 'a' + 10,
        >= 'A' and <= 'F' => ch - 'A' + 10,
        _ => -1,
    };

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
            if (char.IsWhiteSpace(ch) || ch is '"' or '<' or '>' or '\\' or '{' or '}' or '?' or '#' or '\r' or '\n')
            {
                break;
            }
            if (ch == ',') break;
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

    private static bool StartsEmbeddedFilesystemPath(string text, int nextPos)
    {
        if (nextPos >= text.Length) return true;
        var ch = text[nextPos];
        if (ch is '/' or '\\') return true;
        if (ch == '~' && nextPos + 1 < text.Length && text[nextPos + 1] == '/') return true;
        return IsFileSchemeAt(text, nextPos) || IsWindowsDriveAt(text, nextPos);
    }

    private static bool StartsEmbeddedPathInsideGroup(string text, int nextPos)
    {
        if (nextPos >= text.Length) return true;
        if (text[nextPos] == ':') return true;
        return StartsEmbeddedFilesystemPath(text, nextPos);
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
            if (j < text.Length && text[j] == '"' &&
                !(j + 1 < text.Length && EmailAtLength(text, j + 1) > 0) &&
                EndsWithFilenameExt(text, i + 1, j))
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
        var hasAt = HasEmailAt(text);
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
                if (p < copied || text[p] != '\'') continue;
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
                        var emailEnd = hasAt ? FindEmailTailEnd(text, p + 1) : -1;
                        sb ??= new StringBuilder(text.Length);
                        sb.Append(text, copied, open - copied).Append("[redacted-path]");
                        copied = emailEnd > p + 1 ? emailEnd : p + 1;
                        open = -1;
                        candidateClose = -1;
                    }
                    else
                    {
                        candidateClose = p;
                    }
                }
            }
            segStart = Math.Max(segEnd + 1, copied);
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
        var hasAt = HasEmailAt(text);
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
                    var emailEnd = hasAt ? FindEmailTailEnd(text, lastExtEnd) : -1;
                    if (emailEnd > 0 && !ContainsWhitespace(text, kwEnd, lastDot))
                    {
                        segStart = segEnd + 1;
                        continue;
                    }
                    sb ??= new StringBuilder(text.Length);
                    sb.Append(text, copied, kwEnd - copied);
                    sb.Append("[redacted-path]");
                    copied = emailEnd > lastExtEnd ? emailEnd : lastExtEnd;
                }
            }
            segStart = Math.Max(segEnd + 1, copied);
        }
        if (sb == null) return text;
        sb.Append(text, copied, text.Length - copied);
        return sb.ToString();
    }

    private static string RedactAuthCredentials(string text)
    {
        var result = RedactAuthHeaders(text);
        result = BearerTokenRegex.Replace(result, "$1 [redacted]");
        return BasicTokenRegex.Replace(result, match =>
            IsBasicCredentialToken(match.Groups[2].Value)
                ? $"{match.Groups[1].Value} [redacted]"
                : match.Value);
    }

    private sealed class AuthJsonScanCache
    {
        public Dictionary<int, int>? ValidJsonValueEnd;
        public Dictionary<int, int>? CloseToOpenMap;
        public Dictionary<int, int>? MemberCommaOpen;
        public Dictionary<int, int>? ArrayCommaOpen;
        /// <summary>0 = valid top-level JSON container; -1 = malformed/unclosed; >0 = valid container end with trailing text on closing line.</summary>
        public Dictionary<int, int>? ContainerAuthState;
        public int FailedContainerBack = -1;
    }

    private static string RedactAuthHeaders(string text)
    {
        if (text.IndexOf(':') < 0 && text.IndexOf('=') < 0) return text;
        StringBuilder? sb = null;
        var copied = 0;
        var cache = new AuthJsonScanCache();
        var i = 0;
        while (i < text.Length)
        {
            if (!TryMatchAuthHeaderKey(
                text, i, cache, out var keyEnd, out var isJsonCandidate, out var hasJsonPrefix, out var failedTopEnd, out var normalizedKey))
            {
                i++;
                continue;
            }
            var sep = SkipAuthWhitespace(text, keyEnd, isJsonCandidate || hasJsonPrefix);
            if (sep >= text.Length || text[sep] is not (':' or '='))
            {
                i = keyEnd;
                continue;
            }
            var isJsonMember = isJsonCandidate && text[sep] == ':';
            var allowTruncatedObject = hasJsonPrefix && text[sep] == ':';
            var containerTrailingEnd = text[sep] == ':' ? failedTopEnd : -1;
            var valStart = SkipAuthWhitespace(text, sep + 1, isJsonMember || allowTruncatedObject);
            if (valStart >= text.Length || text[valStart] is '\r' or '\n')
            {
                i = sep + 1;
                continue;
            }
            if (normalizedKey != null)
            {
                sb ??= new StringBuilder(text.Length);
                sb.Append(text, copied, i - copied).Append(text[i]).Append(normalizedKey).Append(text[i]);
                copied = keyEnd;
            }
            i = RedactAuthHeaderValue(text, valStart, isJsonMember, allowTruncatedObject, containerTrailingEnd, cache, ref sb, ref copied);
        }
        if (sb == null) return text;
        sb.Append(text, copied, text.Length - copied);
        return sb.ToString();
    }

    private static bool TryMatchAuthHeaderKey(
        string text, int pos, AuthJsonScanCache cache,
        out int keyEnd, out bool isJsonCandidate, out bool hasJsonPrefix, out int failedTopEnd, out string? normalizedKey)
    {
        keyEnd = pos;
        isJsonCandidate = false;
        hasJsonPrefix = false;
        failedTopEnd = -1;
        normalizedKey = null;
        var ch = text[pos];
        if (ch is '"' or '\'')
        {
            return TryMatchQuotedAuthKey(
                text, pos, ch, cache, out keyEnd, out isJsonCandidate, out hasJsonPrefix, out failedTopEnd, out normalizedKey);
        }
        if (pos > 0 && IsAuthKeyWordChar(text[pos - 1])) return false;
        if (MatchesAsciiIgnoreCase(text, pos, "proxy-authorization") &&
            (pos + 19 == text.Length || !IsAuthKeyWordChar(text[pos + 19])))
        {
            keyEnd = pos + 19;
            return true;
        }
        if (MatchesAsciiIgnoreCase(text, pos, "authorization") &&
            (pos + 13 == text.Length || !IsAuthKeyWordChar(text[pos + 13])))
        {
            keyEnd = pos + 13;
            return true;
        }
        return false;
    }

    private static bool TryMatchQuotedAuthKey(
        string text, int pos, char quote, AuthJsonScanCache cache,
        out int keyEnd, out bool isJsonCandidate, out bool hasJsonPrefix, out int failedTopEnd, out string? normalizedKey)
    {
        keyEnd = pos;
        isJsonCandidate = false;
        hasJsonPrefix = false;
        failedTopEnd = -1;
        normalizedKey = null;
        var limit = Math.Min(text.Length, pos + 129);
        Span<char> decoded = stackalloc char[20];
        var len = 0;
        var sawEscape = false;
        var hasInvalidJsonEscape = false;
        var j = pos + 1;
        while (j < limit)
        {
            var c = text[j];
            if (c < 0x20) return false;
            if (c == quote)
            {
                ReadOnlySpan<char> span = decoded[..len];
                string? canonical = null;
                if (span.Equals("authorization".AsSpan(), StringComparison.OrdinalIgnoreCase)) canonical = "Authorization";
                else if (span.Equals("proxy-authorization".AsSpan(), StringComparison.OrdinalIgnoreCase)) canonical = "Proxy-Authorization";
                if (canonical == null) return false;
                keyEnd = j + 1;
                if (quote == '"')
                {
                    var prevDelim = SkipJsonWhitespaceBackward(text, pos - 1);
                    hasJsonPrefix = prevDelim >= 0 && text[prevDelim] is '{' or ',';
                    if (!hasInvalidJsonEscape)
                    {
                        var openBrace = FindEnclosingJsonObjectStart(text, pos, cache);
                        if (openBrace >= 0)
                        {
                            var state = ClassifyTopLevelJsonContainer(text, openBrace, cache);
                            isJsonCandidate = state == 0;
                            failedTopEnd = state > 0 ? state : -1;
                        }
                    }
                }
                normalizedKey = sawEscape ? canonical : null;
                return true;
            }
            if (len >= 19) return false;
            if (c == '\\')
            {
                sawEscape = true;
                if (j + 5 < text.Length && text[j + 1] == 'u' && TryParseHex4(text, j + 2, out var codeUnit))
                {
                    decoded[len++] = (char)codeUnit;
                    j += 6;
                    continue;
                }
                if (j + 1 >= text.Length) return false;
                hasInvalidJsonEscape = true;
                decoded[len++] = text[j + 1];
                j += 2;
                continue;
            }
            decoded[len++] = c;
            j++;
        }
        return false;
    }

    private static int RedactAuthHeaderValue(
        string text, int valStart, bool isJsonMember, bool allowTruncatedObject, int failedTopEnd,
        AuthJsonScanCache cache, ref StringBuilder? sb, ref int copied)
    {
        var ch = text[valStart];
        if (ch is '"' or '\'')
        {
            var afterQuote = RedactQuotedAuthString(text, valStart, ch, ref sb, ref copied);
            var validJsonStr = isJsonMember && ch == '"' && ScanJsonStringEnd(text, valStart + 1) == afterQuote;
            return RedactTrailingPlainAuthContinuation(text, afterQuote, validJsonStr, failedTopEnd, ref sb, ref copied);
        }
        if (ch == '[')
        {
            var afterArray = RedactAuthArrayValue(text, valStart + 1, isJsonMember, allowTruncatedObject, cache, ref sb, ref copied);
            var validJsonArr = isJsonMember && TryParseJsonValue(text, valStart, ref cache.ValidJsonValueEnd) == afterArray;
            return RedactTrailingPlainAuthContinuation(text, afterArray, validJsonArr, failedTopEnd, ref sb, ref copied);
        }
        if (ch == '{')
        {
            var objEnd = TryParseJsonValue(text, valStart, ref cache.ValidJsonValueEnd);
            if (objEnd > valStart)
            {
                sb ??= new StringBuilder(text.Length);
                sb.Append(text, copied, valStart - copied).Append(isJsonMember ? "\"[redacted]\"" : "[redacted]");
                copied = objEnd;
                return RedactTrailingPlainAuthContinuation(text, objEnd, isJsonMember, failedTopEnd, ref sb, ref copied);
            }
            if (isJsonMember || allowTruncatedObject)
            {
                sb ??= new StringBuilder(text.Length);
                sb.Append(text, copied, valStart - copied).Append("\"[redacted]\"");
                copied = text.Length;
                return text.Length;
            }
        }
        if (isJsonMember && TryRedactJsonPrimitiveAuthValue(text, valStart, ref sb, ref copied, out var afterPrim))
        {
            return afterPrim;
        }
        return RedactPlainAuthSpan(text, valStart, failedTopEnd, ref sb, ref copied);
    }

    private static bool TryRedactJsonPrimitiveAuthValue(
        string text, int valStart, ref StringBuilder? sb, ref int copied, out int endPos)
    {
        endPos = valStart;
        var tokenEnd = valStart;
        while (tokenEnd < text.Length && text[tokenEnd] is not (' ' or '\t' or '\r' or '\n' or ',' or '}' or ']'))
        {
            tokenEnd++;
        }
        var len = tokenEnd - valStart;
        if (len == 0) return false;
        var span = text.AsSpan(valStart, len);
        var isNull = span.Equals("null".AsSpan(), StringComparison.Ordinal);
        var isBool = span.Equals("true".AsSpan(), StringComparison.Ordinal) ||
            span.Equals("false".AsSpan(), StringComparison.Ordinal);
        var isNumber = !isNull && !isBool && IsJsonNumberSpan(span);
        if (!isNull && !isBool && !isNumber) return false;
        var next = SkipAuthWhitespace(text, tokenEnd, allowNewlines: true);
        if (next < text.Length && !IsJsonMemberContinuationDelimiter(text, next)) return false;
        if (!isNull)
        {
            sb ??= new StringBuilder(text.Length);
            sb.Append(text, copied, valStart - copied).Append("\"[redacted]\"");
            copied = tokenEnd;
        }
        endPos = tokenEnd;
        return true;
    }

    private static bool IsJsonNumberSpan(ReadOnlySpan<char> span)
    {
        var i = 0;
        if (span[i] == '-') i++;
        if (i >= span.Length || span[i] < '0' || span[i] > '9') return false;
        if (span[i] == '0') i++;
        else while (i < span.Length && span[i] >= '0' && span[i] <= '9') i++;
        if (i < span.Length && span[i] == '.')
        {
            i++;
            if (i >= span.Length || span[i] < '0' || span[i] > '9') return false;
            while (i < span.Length && span[i] >= '0' && span[i] <= '9') i++;
        }
        if (i < span.Length && span[i] is 'e' or 'E')
        {
            i++;
            if (i < span.Length && span[i] is '+' or '-') i++;
            if (i >= span.Length || span[i] < '0' || span[i] > '9') return false;
            while (i < span.Length && span[i] >= '0' && span[i] <= '9') i++;
        }
        return i == span.Length;
    }

    private static int RedactPlainAuthSpan(string text, int valStart, int minEnd, ref StringBuilder? sb, ref int copied)
    {
        var lineEnd = ScanPlainAuthValueEnd(text, valStart);
        if (minEnd > lineEnd) lineEnd = ScanPlainAuthValueEnd(text, minEnd);
        var redacted = RedactAuthValueBody(text, valStart, lineEnd);
        if (redacted != null)
        {
            sb ??= new StringBuilder(text.Length);
            sb.Append(text, copied, valStart - copied).Append(redacted);
            copied = lineEnd;
        }
        return lineEnd;
    }

    private static int RedactTrailingPlainAuthContinuation(
        string text, int afterPos, bool allowNewlines, int failedTopEnd, ref StringBuilder? sb, ref int copied)
    {
        var tailStart = FindTrailingPlainAuthStart(text, afterPos, allowNewlines);
        if (tailStart >= 0) return RedactPlainAuthSpan(text, tailStart, failedTopEnd, ref sb, ref copied);
        if (!allowNewlines && failedTopEnd > afterPos)
        {
            var nextStart = SkipJsonWhitespaceForward(text, afterPos);
            if (nextStart < text.Length) return RedactPlainAuthSpan(text, nextStart, failedTopEnd, ref sb, ref copied);
        }
        return afterPos;
    }

    private static int FindTrailingPlainAuthStart(string text, int afterPos, bool isJsonMember)
    {
        var p = afterPos;
        while (p < text.Length)
        {
            while (p < text.Length && text[p] is ' ' or '\t') p++;
            var fold = AdvanceObsFold(text, p);
            if (fold > p)
            {
                p = fold;
                continue;
            }
            break;
        }
        if (p >= text.Length || text[p] is '\r' or '\n') return -1;
        if (isJsonMember && IsJsonMemberContinuationDelimiter(text, p)) return -1;
        return p;
    }

    private static int FindEnclosingJsonObjectStart(string text, int pos, AuthJsonScanCache cache)
    {
        var p = SkipJsonWhitespaceBackward(text, pos - 1);
        if (p < 0) return -1;
        if (text[p] == '{') return p;
        if (text[p] != ',') return -1;
        return FindEnclosingJsonObjectStartFromComma(text, p, cache);
    }

    private static int FindEnclosingJsonObjectStartFromComma(string text, int commaPos, AuthJsonScanCache cache)
    {
        cache.MemberCommaOpen ??= new Dictionary<int, int>();
        if (cache.MemberCommaOpen.TryGetValue(commaPos, out var cached)) return cached;
        var visited = new List<int>();
        var p = commaPos;
        var openBrace = -1;
        while (p >= 0 && text[p] == ',')
        {
            if (cache.MemberCommaOpen.TryGetValue(p, out var known))
            {
                openBrace = known;
                break;
            }
            visited.Add(p);
            var beforeKey = StepBackOverJsonMember(text, p, cache);
            if (beforeKey < 0) break;
            if (text[beforeKey] == '{')
            {
                openBrace = beforeKey;
                break;
            }
            p = beforeKey;
        }
        foreach (var cPos in visited) cache.MemberCommaOpen[cPos] = openBrace;
        return openBrace;
    }

    private static int FindEnclosingJsonArrayStartFromComma(string text, int commaPos, AuthJsonScanCache cache)
    {
        cache.ArrayCommaOpen ??= new Dictionary<int, int>();
        if (cache.ArrayCommaOpen.TryGetValue(commaPos, out var cached)) return cached;
        var visited = new List<int>();
        var p = commaPos;
        var openBracket = -1;
        while (p >= 0 && text[p] == ',')
        {
            if (cache.ArrayCommaOpen.TryGetValue(p, out var known))
            {
                openBracket = known;
                break;
            }
            visited.Add(p);
            var prev = SkipJsonWhitespaceBackward(text, p - 1);
            if (prev < 0) break;
            var valStart = FindJsonValueStartBackward(text, prev, cache);
            if (valStart <= 0) break;
            var beforeVal = SkipJsonWhitespaceBackward(text, valStart - 1);
            if (beforeVal < 0) break;
            if (text[beforeVal] == '[')
            {
                openBracket = beforeVal;
                break;
            }
            if (text[beforeVal] != ',') break;
            p = beforeVal;
        }
        foreach (var cPos in visited) cache.ArrayCommaOpen[cPos] = openBracket;
        return openBracket;
    }

    private static int ClassifyTopLevelJsonContainer(string text, int openPos, AuthJsonScanCache cache)
    {
        cache.ContainerAuthState ??= new Dictionary<int, int>();
        if (cache.ContainerAuthState.TryGetValue(openPos, out var cached)) return cached;
        var visited = new List<int>();
        var curOpen = openPos;
        var state = -1;
        while (curOpen >= 0)
        {
            if (cache.ContainerAuthState.TryGetValue(curOpen, out var known))
            {
                state = known;
                break;
            }
            visited.Add(curOpen);
            var containerEnd = TryParseJsonValue(text, curOpen, ref cache.ValidJsonValueEnd);
            if (containerEnd <= curOpen) break;
            var parentOpen = FindParentJsonContainerOpen(text, curOpen, cache);
            if (parentOpen >= 0)
            {
                curOpen = parentOpen;
                continue;
            }
            var after = containerEnd;
            while (after < text.Length && text[after] is ' ' or '\t') after++;
            state = after >= text.Length || text[after] is '\r' or '\n' ? 0 : containerEnd;
            break;
        }
        foreach (var pos in visited) cache.ContainerAuthState[pos] = state;
        return state;
    }

    private static int FindParentJsonContainerOpen(string text, int valueStart, AuthJsonScanCache cache)
    {
        var prev = SkipJsonWhitespaceBackward(text, valueStart - 1);
        if (prev < 0) return -1;
        if (text[prev] == ':')
        {
            var beforeKey = FindDelimiterBeforeJsonMemberKeyColon(text, valueStart);
            if (beforeKey < 0) return -1;
            return text[beforeKey] == '{' ? beforeKey : FindEnclosingJsonObjectStartFromComma(text, beforeKey, cache);
        }
        if (text[prev] == '[') return prev;
        if (text[prev] == ',') return FindEnclosingJsonArrayStartFromComma(text, prev, cache);
        return -1;
    }

    private static int StepBackOverJsonMember(string text, int commaPos, AuthJsonScanCache cache)
    {
        var prev = SkipJsonWhitespaceBackward(text, commaPos - 1);
        if (prev < 0) return -1;
        var valueStart = FindJsonValueStartBackward(text, prev, cache);
        if (valueStart <= 0) return -1;
        return FindDelimiterBeforeJsonMemberKeyColon(text, valueStart);
    }

    private static int FindJsonValueStartBackward(string text, int prev, AuthJsonScanCache cache)
    {
        var ch = text[prev];
        if (ch == '"') return ScanJsonStringStartBackward(text, prev);
        if (ch is '}' or ']')
        {
            return ScanBalancedJsonContainerStartBackward(
                text, prev, ref cache.ValidJsonValueEnd, ref cache.CloseToOpenMap, ref cache.FailedContainerBack);
        }
        if (ch >= '0' && ch <= '9')
        {
            var s = prev;
            while (s >= 0 && text[s] is not (' ' or '\t' or '\r' or '\n' or ':' or ',' or '[')) s--;
            return IsJsonNumberSpan(text.AsSpan(s + 1, prev - s)) ? s + 1 : -1;
        }
        return FindJsonKeywordStartEndingAt(text, prev);
    }

    private static int FindJsonKeywordStartEndingAt(string text, int endIdx)
    {
        if (MatchesKeywordSpan(text, endIdx, "true")) return endIdx - 3;
        if (MatchesKeywordSpan(text, endIdx, "false")) return endIdx - 4;
        if (MatchesKeywordSpan(text, endIdx, "null")) return endIdx - 3;
        return -1;
    }

    private static bool MatchesKeywordSpan(string text, int endIdx, string keyword)
    {
        var start = endIdx - keyword.Length + 1;
        return start > 0 && text.AsSpan(start, keyword.Length).Equals(keyword.AsSpan(), StringComparison.Ordinal);
    }

    private static int SkipJsonWhitespaceBackward(string text, int start)
    {
        var p = start;
        while (p >= 0 && text[p] is ' ' or '\t' or '\r' or '\n') p--;
        return p;
    }

    private static int SkipJsonWhitespaceForward(string text, int start)
    {
        var p = start;
        while (p < text.Length && text[p] is ' ' or '\t' or '\r' or '\n') p++;
        return p;
    }

    private static int FindDelimiterBeforeJsonMemberKeyColon(string text, int valueStart)
    {
        var colonPos = SkipJsonWhitespaceBackward(text, valueStart - 1);
        if (colonPos <= 0 || text[colonPos] != ':') return -1;
        var keyEnd = SkipJsonWhitespaceBackward(text, colonPos - 1);
        if (keyEnd <= 0 || text[keyEnd] != '"') return -1;
        var keyStart = ScanJsonStringStartBackward(text, keyEnd);
        if (keyStart <= 0) return -1;
        var beforeKey = SkipJsonWhitespaceBackward(text, keyStart - 1);
        return beforeKey >= 0 && text[beforeKey] is '{' or ',' ? beforeKey : -1;
    }

    private static bool IsEscapedQuote(string text, int quotePos)
    {
        var backslashes = 0;
        for (var i = quotePos - 1; i >= 0 && text[i] == '\\'; i--) backslashes++;
        return (backslashes & 1) == 1;
    }

    private static int ScanJsonStringStartBackward(string text, int closeQuotePos)
    {
        var open = -1;
        for (var i = closeQuotePos - 1; i >= 0; i--)
        {
            var c = text[i];
            if (c < 0x20) return -1;
            if (c == '"' && !IsEscapedQuote(text, i))
            {
                open = i;
                break;
            }
        }
        if (open < 0) return -1;
        return ScanJsonStringEnd(text, open + 1) == closeQuotePos + 1 ? open : -1;
    }

    private static int ScanBalancedJsonContainerStartBackward(
        string text, int closePos, ref Dictionary<int, int>? validJsonValueEnd,
        ref Dictionary<int, int>? closeToOpenMap, ref int failedFloor)
    {
        closeToOpenMap ??= new Dictionary<int, int>();
        if (closeToOpenMap.TryGetValue(closePos, out var cached)) return cached;
        if (closePos <= failedFloor) return -1;
        var stack = new Stack<(int CloseIdx, char ExpectedOpen)>();
        var i = closePos;
        while (i > failedFloor)
        {
            var c = text[i];
            if (c == '"')
            {
                if (IsEscapedQuote(text, i)) break;
                var strStart = ScanJsonStringStartBackward(text, i);
                if (strStart < 0) break;
                i = strStart - 1;
                continue;
            }
            if (c is '}' or ']')
            {
                if (closeToOpenMap.TryGetValue(i, out var innerOpen))
                {
                    if (innerOpen < 0) break;
                    i = innerOpen - 1;
                    continue;
                }
                stack.Push((i, c == '}' ? '{' : '['));
            }
            else if (c is '{' or '[')
            {
                if (stack.Count == 0 || stack.Peek().ExpectedOpen != c) break;
                var matchedClose = stack.Pop().CloseIdx;
                if (TryParseJsonValue(text, i, ref validJsonValueEnd) != matchedClose + 1)
                {
                    closeToOpenMap[matchedClose] = -1;
                    break;
                }
                closeToOpenMap[matchedClose] = i;
                if (stack.Count == 0) return i;
            }
            i--;
        }
        while (stack.Count > 0) closeToOpenMap[stack.Pop().CloseIdx] = -1;
        closeToOpenMap[closePos] = -1;
        failedFloor = Math.Max(failedFloor, closePos);
        return -1;
    }

    private static bool IsJsonMemberContinuationDelimiter(string text, int pos)
    {
        if (text[pos] == '}')
        {
            return pos + 1 >= text.Length || text[pos + 1] is ' ' or '\t' or '\r' or '\n' or ',' or '}' or ']' or ')' or ';';
        }
        if (text[pos] != ',') return false;
        var next = SkipAuthWhitespace(text, pos + 1, allowNewlines: true);
        if (next >= text.Length || text[next] != '"') return false;
        var strEnd = ScanJsonStringEnd(text, next + 1);
        if (strEnd <= 0) return false;
        var afterKey = SkipAuthWhitespace(text, strEnd, allowNewlines: true);
        return afterKey < text.Length && text[afterKey] == ':';
    }

    private static int TryParseJsonValue(string text, int startPos, ref Dictionary<int, int>? cache)
    {
        if (startPos >= text.Length) return -1;
        cache ??= new Dictionary<int, int>();
        if (cache.TryGetValue(startPos, out var cached)) return cached;
        var stack = new Stack<(int OpenPos, bool IsObject)>();
        var pos = startPos;
        var expectingValue = true;
        while (true)
        {
            pos = SkipJsonWhitespaceForward(text, pos);
            if (expectingValue)
            {
                if (pos >= text.Length) break;
                var c = text[pos];
                if (c is '{' or '[')
                {
                    if (cache.TryGetValue(pos, out var known))
                    {
                        if (known <= pos) break;
                        pos = known;
                        expectingValue = false;
                        if (stack.Count == 0) return pos;
                        continue;
                    }
                    var openPos = pos;
                    var isObj = c == '{';
                    pos = SkipJsonWhitespaceForward(text, pos + 1);
                    if (pos < text.Length && text[pos] == (isObj ? '}' : ']'))
                    {
                        cache[openPos] = pos + 1;
                        pos++;
                        expectingValue = false;
                        if (stack.Count == 0) return pos;
                        continue;
                    }
                    if (isObj && !TryAdvanceOverJsonObjectKeyColon(text, ref pos)) break;
                    stack.Push((openPos, isObj));
                    continue;
                }
                var scalarEnd = ScanJsonScalarEnd(text, pos);
                if (scalarEnd <= pos) break;
                pos = scalarEnd;
                expectingValue = false;
                if (stack.Count == 0) return pos;
            }
            else
            {
                if (stack.Count == 0) return pos;
                if (pos >= text.Length) break;
                var top = stack.Peek();
                var c = text[pos];
                if (top.IsObject)
                {
                    if (c == '}')
                    {
                        stack.Pop();
                        cache[top.OpenPos] = pos + 1;
                        pos++;
                        if (stack.Count == 0) return pos;
                        continue;
                    }
                    if (c != ',') break;
                    pos = SkipJsonWhitespaceForward(text, pos + 1);
                    if (!TryAdvanceOverJsonObjectKeyColon(text, ref pos)) break;
                    expectingValue = true;
                }
                else
                {
                    if (c == ']')
                    {
                        stack.Pop();
                        cache[top.OpenPos] = pos + 1;
                        pos++;
                        if (stack.Count == 0) return pos;
                        continue;
                    }
                    if (c != ',') break;
                    pos++;
                    expectingValue = true;
                }
            }
        }
        while (stack.Count > 0) cache[stack.Pop().OpenPos] = -1;
        cache[startPos] = -1;
        return -1;
    }

    private static bool TryAdvanceOverJsonObjectKeyColon(string text, ref int pos)
    {
        if (pos >= text.Length || text[pos] != '"') return false;
        var keyEnd = ScanJsonStringEnd(text, pos + 1);
        if (keyEnd <= 0) return false;
        var colon = SkipJsonWhitespaceForward(text, keyEnd);
        if (colon >= text.Length || text[colon] != ':') return false;
        pos = colon + 1;
        return true;
    }

    private static int ScanJsonScalarEnd(string text, int pos)
    {
        var c = text[pos];
        if (c == '"') return ScanJsonStringEnd(text, pos + 1);
        if (c == 't') return pos + 4 <= text.Length && text.AsSpan(pos, 4).Equals("true".AsSpan(), StringComparison.Ordinal) ? pos + 4 : -1;
        if (c == 'f') return pos + 5 <= text.Length && text.AsSpan(pos, 5).Equals("false".AsSpan(), StringComparison.Ordinal) ? pos + 5 : -1;
        if (c == 'n') return pos + 4 <= text.Length && text.AsSpan(pos, 4).Equals("null".AsSpan(), StringComparison.Ordinal) ? pos + 4 : -1;
        return ScanJsonNumberEnd(text, pos);
    }

    private static int ScanJsonNumberEnd(string text, int pos)
    {
        var i = pos;
        if (i < text.Length && text[i] == '-') i++;
        if (i >= text.Length || text[i] < '0' || text[i] > '9') return -1;
        if (text[i] == '0') i++;
        else while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++;
        if (i < text.Length && text[i] == '.')
        {
            i++;
            if (i >= text.Length || text[i] < '0' || text[i] > '9') return -1;
            while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++;
        }
        if (i < text.Length && text[i] is 'e' or 'E')
        {
            i++;
            if (i < text.Length && text[i] is '+' or '-') i++;
            if (i >= text.Length || text[i] < '0' || text[i] > '9') return -1;
            while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++;
        }
        return i;
    }

    private static int ScanJsonStringEnd(string text, int start)
    {
        var i = start;
        while (i < text.Length)
        {
            var c = text[i];
            if (c < 0x20) return -(i + 1);
            if (c == '"') return i + 1;
            if (c == '\\')
            {
                if (i + 1 >= text.Length) return -(text.Length + 1);
                var esc = text[i + 1];
                if (esc is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')
                {
                    i += 2;
                    continue;
                }
                if (esc == 'u' && i + 5 < text.Length && TryParseHex4(text, i + 2, out _))
                {
                    i += 6;
                    continue;
                }
                return -(i + 2);
            }
            i++;
        }
        return -(text.Length + 1);
    }

    private static int ScanPlainAuthValueEnd(string text, int start)
    {
        var end = start;
        while (end < text.Length)
        {
            var c = text[end];
            if (c is '\r' or '\n')
            {
                var fold = AdvanceObsFold(text, end);
                if (fold > end)
                {
                    end = fold;
                    continue;
                }
                break;
            }
            end++;
        }
        return end;
    }

    private static int RedactAuthArrayValue(
        string text, int start, bool isJsonMember, bool allowTruncatedObject,
        AuthJsonScanCache cache, ref StringBuilder? sb, ref int copied)
    {
        var depth = 1;
        var k = start;
        while (k < text.Length && depth > 0)
        {
            var c = text[k];
            if (c == '[') { depth++; k++; continue; }
            if (c == ']') { depth--; k++; continue; }
            if (c is '"' or '\'')
            {
                k = RedactQuotedAuthString(text, k, c, ref sb, ref copied);
                continue;
            }
            if (c == '{')
            {
                var objEnd = TryParseJsonValue(text, k, ref cache.ValidJsonValueEnd);
                if (objEnd > k)
                {
                    sb ??= new StringBuilder(text.Length);
                    sb.Append(text, copied, k - copied).Append(isJsonMember ? "\"[redacted]\"" : "[redacted]");
                    copied = objEnd;
                    k = objEnd;
                    continue;
                }
                if (isJsonMember || allowTruncatedObject)
                {
                    sb ??= new StringBuilder(text.Length);
                    sb.Append(text, copied, k - copied).Append("\"[redacted]\"");
                    copied = text.Length;
                    return text.Length;
                }
            }
            if (c is ' ' or '\t' or ',') { k++; continue; }
            if (c is '\r' or '\n')
            {
                if (isJsonMember || allowTruncatedObject) { k++; continue; }
                var fold = AdvanceObsFold(text, k);
                if (fold > k) { k = fold; continue; }
                return k;
            }
            var elemEnd = k;
            while (elemEnd < text.Length && text[elemEnd] is not (',' or '[' or ']' or '\r' or '\n')) elemEnd++;
            var trimEnd = elemEnd;
            while (trimEnd > k && text[trimEnd - 1] is ' ' or '\t') trimEnd--;
            if (trimEnd > k && !text.AsSpan(k, trimEnd - k).Equals("null".AsSpan(), StringComparison.Ordinal))
            {
                var redacted = RedactAuthValueBody(text, k, trimEnd);
                if (redacted != null)
                {
                    var replacement = isJsonMember ? "\"" + redacted + "\"" : redacted;
                    sb ??= new StringBuilder(text.Length);
                    sb.Append(text, copied, k - copied).Append(replacement).Append(text, trimEnd, elemEnd - trimEnd);
                    copied = elemEnd;
                }
            }
            k = elemEnd;
        }
        return k;
    }

    private static int RedactQuotedAuthString(
        string text, int quotePos, char quote, ref StringBuilder? sb, ref int copied)
    {
        var j = quotePos + 1;
        var escaped = false;
        while (j < text.Length)
        {
            var c = text[j];
            if (c is '\r' or '\n') break;
            if (c == quote && !escaped) break;
            escaped = c == '\\' && !escaped;
            j++;
        }
        var innerStart = quotePos + 1;
        var redacted = RedactAuthValueBody(text, innerStart, j);
        if (redacted != null)
        {
            sb ??= new StringBuilder(text.Length);
            sb.Append(text, copied, innerStart - copied).Append(redacted);
            copied = j;
        }
        return j < text.Length && text[j] == quote ? j + 1 : j;
    }

    private static string? RedactAuthValueBody(string text, int start, int end)
    {
        var s = start;
        while (s < end && text[s] is ' ' or '\t') s++;
        if (s >= end) return null;
        foreach (var scheme in KnownAuthSchemeList)
        {
            if (s + scheme.Length < end && MatchesAsciiIgnoreCase(text, s, scheme))
            {
                var after = s + scheme.Length;
                if (text[after] is ' ' or '\t')
                {
                    while (after < end && text[after] is ' ' or '\t') after++;
                    if (after < end)
                    {
                        return text.Substring(start, after - start) + "[redacted]";
                    }
                }
            }
        }
        return text.Substring(start, s - start) + "[redacted]";
    }

    private static int SkipAuthWhitespace(string text, int pos, bool allowNewlines)
    {
        var p = pos;
        while (p < text.Length)
        {
            var c = text[p];
            if (c is ' ' or '\t' || (allowNewlines && c is '\r' or '\n'))
            {
                p++;
            }
            else if (!allowNewlines && c is '\r' or '\n')
            {
                var fold = AdvanceObsFold(text, p);
                if (fold > p) p = fold;
                else break;
            }
            else
            {
                break;
            }
        }
        return p;
    }

    private static int AdvanceObsFold(string text, int nlPos)
    {
        if (nlPos >= text.Length || text[nlPos] is not ('\r' or '\n')) return -1;
        var afterNl = text[nlPos] == '\r' && nlPos + 1 < text.Length && text[nlPos + 1] == '\n'
            ? nlPos + 2
            : nlPos + 1;
        if (afterNl >= text.Length || text[afterNl] is not (' ' or '\t')) return -1;
        var p = afterNl;
        while (p < text.Length && text[p] is ' ' or '\t') p++;
        return p;
    }

    private static bool IsAuthKeyWordChar(char ch) => IsAsciiWordChar(ch) || ch == '-';

    private static bool MatchesAsciiIgnoreCase(string text, int pos, string literal) =>
        pos + literal.Length <= text.Length &&
        text.AsSpan(pos, literal.Length).Equals(literal.AsSpan(), StringComparison.OrdinalIgnoreCase);

    private static bool TryParseHex4(string text, int pos, out int value)
    {
        value = 0;
        for (var k = 0; k < 4; k++)
        {
            var c = text[pos + k];
            int digit;
            if (c >= '0' && c <= '9') digit = c - '0';
            else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
            else if (c >= 'A' && c <= 'F') digit = c - 'A' + 10;
            else return false;
            value = (value << 4) | digit;
        }
        return true;
    }

    private static int Base64CharValue(char ch)
    {
        if (ch >= 'A' && ch <= 'Z') return ch - 'A';
        if (ch >= 'a' && ch <= 'z') return ch - 'a' + 26;
        if (ch >= '0' && ch <= '9') return ch - '0' + 52;
        if (ch is '+' or '-') return 62;
        if (ch is '/' or '_') return 63;
        return -1;
    }

    private static bool IsBasicCredentialToken(string token)
    {
        var unpadded = token.TrimEnd('=');
        var pad = token.Length - unpadded.Length;
        if (pad > 2 || (pad > 0 && (token.Length & 3) != 0)) return false;
        var rem = unpadded.Length & 3;
        if (rem == 1 || (pad > 0 && rem + pad != 4)) return false;
        if (rem == 2 && (Base64CharValue(unpadded[^1]) & 0x0F) != 0) return false;
        if (rem == 3 && (Base64CharValue(unpadded[^1]) & 0x03) != 0) return false;
        var normalized = unpadded.Replace('-', '+').Replace('_', '/');
        if (rem == 2) normalized += "==";
        else if (rem == 3) normalized += "=";
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(normalized);
        }
        catch (FormatException)
        {
            return false;
        }
        if (Array.IndexOf(bytes, (byte)':') < 0) return false;
        if (IsPrintableStrictUtf8(bytes)) return true;
        return IsPrintableIso88591(bytes);
    }

    private static bool IsPrintableStrictUtf8(byte[] bytes)
    {
        try
        {
            var decoded = StrictUtf8.GetString(bytes);
            foreach (var ch in decoded)
            {
                if (ch < 0x20 || ch == 0x7F || (ch >= 0x80 && ch <= 0x9F)) return false;
            }
            return true;
        }
        catch (System.Text.DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsPrintableIso88591(byte[] bytes)
    {
        foreach (var b in bytes)
        {
            if (b < 0x20 || (b >= 0x7F && b <= 0x9F)) return false;
        }
        return true;
    }

    private static bool HasEmailAt(string text) =>
        text.IndexOf('@') >= 0 || text.IndexOf("%40", StringComparison.Ordinal) >= 0;

    private static bool ContainsWhitespace(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (char.IsWhiteSpace(text[i])) return true;
        }
        return false;
    }

    private static int FindEmailTailEnd(string text, int pos)
    {
        var p = pos;
        while (p < text.Length && IsEmailLocalChar(text, p)) p++;
        if (p >= text.Length) return -1;
        var atLen = EmailAtLength(text, p);
        return atLen > 0 ? FindEmailDomainEnd(text, p + atLen) : -1;
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

    private static string RedactQuotedEmails(string text)
    {
        if (text.IndexOf('"') < 0 || (text.IndexOf('@') < 0 && text.IndexOf("%40", StringComparison.Ordinal) < 0))
        {
            return text;
        }
        StringBuilder? sb = null;
        var copied = 0;
        var i = 0;
        var quoteCacheUntil = -1;
        var cachedNextQuote = -1;
        while (i < text.Length)
        {
            if (text[i] == '"' && !IsPrecededByOddBackslashes(text, i, copied))
            {
                int closeQuote;
                if (i < quoteCacheUntil)
                {
                    closeQuote = cachedNextQuote;
                }
                else
                {
                    closeQuote = FindNextEmailClosingQuote(text, i + 1, out var stopPos);
                    cachedNextQuote = closeQuote;
                    quoteCacheUntil = closeQuote >= 0 ? closeQuote : stopPos;
                }
                if (closeQuote > i)
                {
                    var qAtPos = closeQuote + 1;
                    var qAtLen = qAtPos < text.Length ? EmailAtLength(text, qAtPos) : 0;
                    var qDomEnd = qAtLen > 0 ? FindEmailDomainEnd(text, qAtPos + qAtLen) : -1;
                    if (qDomEnd > 0)
                    {
                        sb ??= new StringBuilder(text.Length);
                        sb.Append(text, copied, i - copied);
                        sb.Append("[redacted-email]");
                        copied = qDomEnd;
                        i = qDomEnd;
                        quoteCacheUntil = -1;
                        continue;
                    }
                }
            }
            i++;
        }
        if (sb == null) return text;
        sb.Append(text, copied, text.Length - copied);
        return sb.ToString();
    }

    private static string RedactEmails(string text)
    {
        if (text.IndexOf('@') < 0 && text.IndexOf("%40", StringComparison.Ordinal) < 0)
        {
            return text;
        }
        StringBuilder? sb = null;
        var copied = 0;
        var i = 0;
        var quoteCacheUntil = -1;
        var cachedNextQuote = -1;
        while (i < text.Length)
        {
            if (text[i] == '"' && !IsPrecededByOddBackslashes(text, i, copied))
            {
                int closeQuote;
                if (i < quoteCacheUntil)
                {
                    closeQuote = cachedNextQuote;
                }
                else
                {
                    closeQuote = FindNextEmailClosingQuote(text, i + 1, out var stopPos);
                    cachedNextQuote = closeQuote;
                    quoteCacheUntil = closeQuote >= 0 ? closeQuote : stopPos;
                }
                if (closeQuote > i)
                {
                    var qAtPos = closeQuote + 1;
                    var qAtLen = qAtPos < text.Length ? EmailAtLength(text, qAtPos) : 0;
                    var qDomEnd = qAtLen > 0 ? FindEmailDomainEnd(text, qAtPos + qAtLen) : -1;
                    if (qDomEnd > 0)
                    {
                        sb ??= new StringBuilder(text.Length);
                        sb.Append(text, copied, i - copied);
                        sb.Append("[redacted-email]");
                        copied = qDomEnd;
                        i = qDomEnd;
                        quoteCacheUntil = -1;
                        continue;
                    }
                }
                i++;
                continue;
            }
            var atLen = EmailAtLength(text, i);
            if (atLen == 0)
            {
                i++;
                continue;
            }
            var domEnd = FindEmailDomainEnd(text, i + atLen);
            var localStart = domEnd > 0 ? FindEmailLocalStart(text, i, copied) : i;
            if (domEnd > 0 && localStart < i)
            {
                sb ??= new StringBuilder(text.Length);
                sb.Append(text, copied, localStart - copied);
                sb.Append("[redacted-email]");
                copied = domEnd;
                i = domEnd;
                quoteCacheUntil = -1;
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

    private static int EmailAtLength(string text, int pos) =>
        text[pos] == '@'
            ? 1
            : (text[pos] == '%' && pos + 2 < text.Length && text[pos + 1] == '4' && text[pos + 2] == '0' ? 3 : 0);

    private static int FindNextEmailClosingQuote(string text, int start, out int stopPos)
    {
        var j = start;
        var escaped = false;
        while (j < text.Length)
        {
            var ch = text[j];
            if (ch is '\r' or '\n')
            {
                // RFC 5322 folding whitespace (FWS): CRLF/LF/CR immediately followed by SP or HTAB.
                var afterNewline = ch == '\r' && j + 1 < text.Length && text[j + 1] == '\n' ? j + 2 : j + 1;
                if (afterNewline < text.Length && text[afterNewline] is ' ' or '\t')
                {
                    escaped = false;
                    j = afterNewline + 1;
                    continue;
                }
                stopPos = j;
                return -1;
            }
            if (ch == '"' && !escaped)
            {
                stopPos = j;
                return j;
            }
            escaped = ch == '\\' && !escaped;
            j++;
        }
        stopPos = text.Length;
        return -1;
    }

    private static string ReplaceUnquotedParenPaths(string text, Regex regex)
    {
        if (text.IndexOf('(') < 0) return text;
        var cursor = 0;
        var openQuote = '\0';
        var escapedBackslash = false;
        return regex.Replace(text, match =>
        {
            while (cursor < match.Index)
            {
                var ch = text[cursor];
                if (ch is '\r' or '\n')
                {
                    openQuote = '\0';
                    escapedBackslash = false;
                }
                else if (!escapedBackslash && openQuote != '\0' && ch == openQuote &&
                    !(ch == '\'' && cursor > 0 && cursor + 1 < text.Length &&
                      char.IsLetterOrDigit(text[cursor - 1]) && char.IsLetter(text[cursor + 1])))
                {
                    openQuote = '\0';
                }
                else if (!escapedBackslash && openQuote == '\0' &&
                    (ch == '"' || (ch == '\'' && (cursor == 0 || !char.IsLetterOrDigit(text[cursor - 1])))))
                {
                    openQuote = ch;
                }
                escapedBackslash = ch == '\\' && !escapedBackslash;
                cursor++;
            }
            return openQuote != '\0' ? match.Value : "([redacted-path])";
        });
    }

    private static bool IsEmailLeadingWrapper(char ch)
    {
        if (ch is '\'' or '{' or '}') return true;
        if (ch < 0x80 || char.IsSurrogate(ch)) return false;
        return char.GetUnicodeCategory(ch) is
            System.Globalization.UnicodeCategory.OpenPunctuation or
            System.Globalization.UnicodeCategory.InitialQuotePunctuation;
    }

    private static bool IsNonAsciiEmailLocalChar(char ch)
    {
        if (ch < 0x80) return false;
        if (char.IsSurrogate(ch)) return true;
        return !char.IsWhiteSpace(ch) && !char.IsControl(ch);
    }

    private static bool IsEmailLocalChar(string text, int pos)
    {
        var ch = text[pos];
        if (IsAsciiAlphaNum(ch) ||
            ch is '.' or '_' or '+' or '-' or '/' or '!' or '#' or '$' or '&' or '\'' or '*' or '=' or '?' or '^' or '`' or '{' or '|' or '}' or '~' ||
            IsNonAsciiEmailLocalChar(ch))
        {
            return true;
        }
        return ch == '%' && !(pos + 2 < text.Length && text[pos + 1] == '4' && text[pos + 2] == '0');
    }

    private static bool IsSchemeSlash(string text, int pos) =>
        text[pos] == '/' && pos >= 2 && text[pos - 1] == '/' && text[pos - 2] == ':';

    private static int FindEmailLocalStart(string text, int atPos, int minPos)
    {
        if (atPos > minPos && text[atPos - 1] == '"' && !IsPrecededByOddBackslashes(text, atPos - 1, minPos))
        {
            var q = atPos - 2;
            while (q >= minPos)
            {
                var ch = text[q];
                if (ch is '\r' or '\n') break;
                if (ch == '"' && !IsPrecededByOddBackslashes(text, q, minPos))
                {
                    return q;
                }
                q--;
            }
            return atPos;
        }
        var localStart = atPos;
        while (localStart > minPos && IsEmailLocalChar(text, localStart - 1) && !IsSchemeSlash(text, localStart - 1))
        {
            localStart--;
        }
        var trimmedStart = localStart;
        while (trimmedStart < atPos && IsEmailLeadingWrapper(text[trimmedStart]))
        {
            trimmedStart++;
        }
        return trimmedStart < atPos ? trimmedStart : localStart;
    }

    private static bool IsPrecededByOddBackslashes(string text, int pos, int minPos)
    {
        var count = 0;
        var p = pos - 1;
        while (p >= minPos && text[p] == '\\')
        {
            count++;
            p--;
        }
        return (count & 1) == 1;
    }

    private static int FindIpLiteralDomainEnd(string text, int domStart)
    {
        var close = domStart + 1;
        while (close < text.Length)
        {
            var ch = text[close];
            if (ch == ']') break;
            if (char.IsWhiteSpace(ch) || ch is '[' or '"' or '\'' or '<' or '>' or '\\' or '{' or '}' or '\r' or '\n')
            {
                return -1;
            }
            close++;
        }
        if (close >= text.Length || text[close] != ']' || close <= domStart + 1) return -1;
        var inner = text.Substring(domStart + 1, close - domStart - 1);
        if (inner.StartsWith("IPv6:", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.TryParse(inner[5..], out var ip6) && ip6.AddressFamily == AddressFamily.InterNetworkV6
                ? close + 1
                : -1;
        }
        return IsStrictIpv4(inner) ? close + 1 : -1;
    }

    private static bool IsStrictIpv4(string text)
    {
        var parts = text.Split('.');
        if (parts.Length != 4) return false;
        foreach (var part in parts)
        {
            if (part.Length is < 1 or > 3) return false;
            var val = 0;
            foreach (var ch in part)
            {
                if (!char.IsAsciiDigit(ch)) return false;
                val = val * 10 + (ch - '0');
            }
            if (val > 255) return false;
        }
        return true;
    }

    private enum DomainCharKind
    {
        Other,
        Letter,
        Digit,
        Dot,
        Hyphen
    }

    private static bool IsEmailDomainDot(char ch) =>
        ch is '.' or '\u3002' or '\uFF0E' or '\uFF61';

    private static DomainCharKind ClassifyDomainCharAt(string text, int pos, out int step)
    {
        step = 1;
        var ch = text[pos];
        if (ch < 0x80)
        {
            if (char.IsAsciiLetter(ch)) return DomainCharKind.Letter;
            if (char.IsAsciiDigit(ch)) return DomainCharKind.Digit;
            if (ch == '.') return DomainCharKind.Dot;
            if (ch == '-') return DomainCharKind.Hyphen;
            return DomainCharKind.Other;
        }
        if (IsEmailDomainDot(ch)) return DomainCharKind.Dot;
        System.Globalization.UnicodeCategory cat;
        if (char.IsHighSurrogate(ch))
        {
            if (pos + 1 >= text.Length || !char.IsLowSurrogate(text[pos + 1])) return DomainCharKind.Other;
            step = 2;
            cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(text, pos);
        }
        else if (char.IsLowSurrogate(ch))
        {
            return DomainCharKind.Other;
        }
        else
        {
            cat = char.GetUnicodeCategory(ch);
        }
        return cat switch
        {
            System.Globalization.UnicodeCategory.UppercaseLetter
                or System.Globalization.UnicodeCategory.LowercaseLetter
                or System.Globalization.UnicodeCategory.TitlecaseLetter
                or System.Globalization.UnicodeCategory.ModifierLetter
                or System.Globalization.UnicodeCategory.OtherLetter
                or System.Globalization.UnicodeCategory.NonSpacingMark
                or System.Globalization.UnicodeCategory.SpacingCombiningMark
                or System.Globalization.UnicodeCategory.EnclosingMark => DomainCharKind.Letter,
            System.Globalization.UnicodeCategory.DecimalDigitNumber => DomainCharKind.Digit,
            _ => DomainCharKind.Other
        };
    }

    private static bool IsEmailBoundaryBlockedAt(string text, int pos)
    {
        var kind = ClassifyDomainCharAt(text, pos, out _);
        return kind is DomainCharKind.Letter or DomainCharKind.Digit or DomainCharKind.Hyphen || text[pos] == '_';
    }

    private static int FindEmailDomainEnd(string text, int domStart)
    {
        if (domStart < text.Length && text[domStart] == '[')
        {
            return FindIpLiteralDomainEnd(text, domStart);
        }
        var k = domStart;
        var labelStart = domStart;
        var labelHasLetter = false;
        var bestEnd = -1;
        while (k < text.Length)
        {
            var kind = ClassifyDomainCharAt(text, k, out var step);
            if (kind == DomainCharKind.Letter)
            {
                labelHasLetter = true;
            }
            else if (kind == DomainCharKind.Dot)
            {
                if (k == labelStart || text[k - 1] == '-') break;
                labelStart = k + 1;
                labelHasLetter = false;
            }
            else if (kind == DomainCharKind.Digit)
            {
                // Digits are allowed inside domain labels (e.g. 3com, 123.example.com, xn--p1ai).
            }
            else if (kind == DomainCharKind.Hyphen)
            {
                if (k == labelStart) break;
            }
            else
            {
                break;
            }
            k += step;
            var atBoundary = k == text.Length || !IsEmailBoundaryBlockedAt(text, k);
            var continuesDomain = k + 1 < text.Length && IsEmailDomainDot(text[k]) &&
                ClassifyDomainCharAt(text, k + 1, out _) is DomainCharKind.Letter or DomainCharKind.Digit;
            if (k > labelStart && labelHasLetter && text[k - 1] != '-' && atBoundary && !continuesDomain)
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
                PromoteTokenPath(text);
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
            return pos == 0 || !char.IsLetterOrDigit(text[pos - 1]) ||
                (pos + 1 < text.Length && (IsFileSchemeAt(text, pos + 1) || StartsLocalPathAt(text, pos + 1, atTokenStart: true)));
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
            externalConsentLock = TryAcquireExternalConsentLease();
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
            externalConsentLock = TryAcquireExternalConsentLease();
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
        lock (_gate)
        {
            if (_disposeRequested || _clientDisposed || _consent == TelemetryConsent.Off) return;
        }
        var mapped = level switch
        {
            TelemetryBreadcrumbSeverity.Debug => TelemetryBreadcrumbLevel.Debug,
            TelemetryBreadcrumbSeverity.Warning => TelemetryBreadcrumbLevel.Warning,
            TelemetryBreadcrumbSeverity.Error => TelemetryBreadcrumbLevel.Error,
            _ => TelemetryBreadcrumbLevel.Info,
        };
        var safeCategory = SanitizeLocalPaths(category);
        var safeMessage = SanitizeLocalPaths(message ?? "");
        CancellationTokenSource? toCancel = null;
        lock (_gate)
        {
            if (!EnsureExternalConsentLocked(out toCancel)) goto CancelAndReturn;
            if (_disposeRequested || _clientDisposed || _consent == TelemetryConsent.Off) return;
            _client.AddBreadcrumb(safeCategory, safeMessage, mapped);
        }
        return;
    CancelAndReturn:
        if (toCancel != null) CancelQuietly(toCancel);
    }

    public void ReportCrash(string kind, string message, string? stack = null, bool fromPreviousSession = false)
    {
        lock (_gate)
        {
            if (_disposeRequested || _clientDisposed || _consent == TelemetryConsent.Off) return;
        }
        var safeMessage = string.IsNullOrWhiteSpace(message) ? "Unhandled exception" : SanitizeLocalPaths(message);
        var safeStack = stack == null ? null : SanitizeLocalPaths(stack);
        var safeKind = string.IsNullOrWhiteSpace(kind) ? "UnhandledException" : SanitizeLocalPaths(kind);
        var report = new TelemetryCrashReport(
            safeKind,
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
            externalConsentLock = TryAcquireExternalConsentLease();
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
        var lockTransferred = false;
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
                    // Dispatch under the cross-process consent lease and hold the lease until the
                    // entire native flush completes so another process's opt-out serializes cleanly
                    // with multi-batch flushing and no batch can start after opt-out finishes.
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
            if (nativeFlush != null)
            {
                var monitorToStop = monitorCts;
                var heldLease = externalConsentLock;
                lockTransferred = true;
                // Defer decrementing `_inFlightFlushes`, any pending native `_client.Dispose()`, and
                // releasing the cross-process consent lease until `nativeFlush` actually finishes,
                // even if `WaitWithCancellationAsync` returns early on cancel/revoke.
                _ = nativeFlush.ContinueWith(
                    static (t, state) =>
                    {
                        _ = t.Exception;
                        var (svc, cts, lease, epoch, bufPath) =
                            ((CoreTelemetryService, CancellationTokenSource?, IDisposable?, long, string?))state!;
                        try
                        {
                            if (cts != null) CancelQuietly(cts);
                            svc.ReleaseInFlightFlush(epoch, bufPath);
                        }
                        finally
                        {
                            lease?.Dispose();
                        }
                    },
                    (this, monitorToStop, heldLease, flushEpoch, flushBufferPath),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            if (!lockTransferred)
            {
                externalConsentLock?.Dispose();
            }
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

    private IDisposable? TryAcquireExternalConsentLease()
    {
        if (_acquireExternalConsentLock == null) return null;
        lock (_externalLockGate)
        {
            if (_externalLockRefs == 0)
            {
                var acquired = _acquireExternalConsentLock();
                if (acquired == null) return null;
                _heldExternalLock = acquired;
            }
            _externalLockRefs++;
            return new ExternalConsentLease(this);
        }
    }

    private void ReleaseExternalConsentLease()
    {
        IDisposable? toDispose = null;
        lock (_externalLockGate)
        {
            if (_externalLockRefs <= 0) return;
            _externalLockRefs--;
            if (_externalLockRefs == 0)
            {
                toDispose = _heldExternalLock;
                _heldExternalLock = null;
            }
        }
        toDispose?.Dispose();
    }

    private sealed class ExternalConsentLease : IDisposable
    {
        private CoreTelemetryService? _owner;

        public ExternalConsentLease(CoreTelemetryService owner) => _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseExternalConsentLease();
        }
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
