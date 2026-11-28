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
    private static readonly Regex ParenRelativeSlashPathRegex = new(@"(\()(?![ \t]*(?:HTTP|TLS|SSL)/\d+(?:\.\d+)*[.,;:!?\s]*\))(?![ \t]*\d+/\d+[.,;:!?\s]*\))[^\s/\\""'<>()\[\]{}:,;=\u001E\u001F]+/[^()\r\n\u001E]+(\))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WindowsPathRegex = new(@"(?:[A-Za-z]:[\\/]|\\\\)[^\S\r\n]*[^\r\n\u001E\s""](?:[^\r\n\u001E]*[^\r\n\u001E\s])?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RelativeBackslashPathRegex = new(@"(^|[\s(\[{""'=,:;<>)\u001F])[^\s\\/""'<>()\[{:,;=\u001E\u001F]+\\[^\r\n\u001E\u001F\s""](?:[^\r\n\u001E\u001F]*[^\r\n\u001E\u001F\s])?", RegexOptions.Compiled);
    private static readonly Regex QuotedSpacedRootPathRegex = new(@"[""']/[ \t]+[^\r\n\u001E\s""][^\r\n\u001E]*", RegexOptions.Compiled);
    private static readonly Regex UnixPathRegex = new(@"(?<![\p{L}\p{M}\p{N}._/-])(?:~/(?=[^\S\r\n]*[^\s""])|//+(?=[^\S\r\n]*[^\s/""])|/(?=[^\S\r\n]*[^\s/""]))[^\S\r\n]*[^\r\n\u001E\s](?:[^\r\n\u001E]*[^\r\n\u001E\s])?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RelativeSlashPathRegex = new(@"(^|[\s(\[{)\]}<>""'=,:;\u001F])(?!(?<!['""])(?:HTTP|TLS|SSL)/\d+(?:\.\d+)*(?:$|[\s(\[{)\]},;:!?]|\.(?![A-Za-z0-9])))(?!(?<!['""])\d+/\d+(?:$|[\s)\]},;:!?]|\.(?![A-Za-z0-9])))[^\s/\\""'<>()\[\]{}:,;=\u001E\u001F]+/[^\r\n\u001E\u001F\s""](?:[^\r\n\u001E\u001F]*[^\r\n\u001E\u001F\s])?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex KeywordQuotedPathRegex = new(@"\b(?:file|path|directory|folder|document)[^\S\r\n]+(?=[""'])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex KeywordFilenamePrefixRegex = new(@"\b(?:open|opening|read|reading|write|writing|delete|deleting|remove|removing|rename|stat|create|creating|load|loading|save|saving|access|accessing|unlink|file|path|directory|folder|document|filename|filepath)\b(?:[^\S\r\n]*[:=][^\S\r\n]*|[^\S\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> SourceLineExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "kt", "java", "swift", "cs", "cpp", "cc", "c", "h", "hpp", "rs", "py", "js", "ts", "go", "rb", "m", "mm"
    };
    private static readonly Regex KeywordAssignedPathRegex = new(@"(\b(?:file|path|filename|filepath)\s*[:=]\s*)(?!\u001E)[^\r\n\u001E\u001F]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BareFilenameRegex = new(@"(^|[\r\n:=;\u001F""])([ \t]*)(?=[^\r\n:=;\u001E\u001F"" \t])[^\r\n:=;\u001E\u001F""]*\.(?!ts:\d+)(?:mov|mp4|m4v|mkv|webm|avi|ts|m3u8|mpd|mp3|m4a|wav|aac|flac|ogg|opus|jpg|jpeg|png|gif|webp|heic|svg|pdf|txt|md|rtf|doc|docx|xls|xlsx|ppt|pptx|csv|json|xml|yaml|yml|toml|ini|cfg|conf|log|srt|vtt|ass|ssa|zip|tar|gz|tgz|bz2|xz|7z|rar|dmg|pkg|msi|exe|apk|aab|ipa|pem|key|crt|cer|p12|pfx|jks|keystore|db|sqlite|sqlite3|bin|dat|tmp|bak|proj|aun)\b(?!\()", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] KnownAuthSchemeList =
    {
        "Bearer", "Basic", "Digest", "Token", "Api-Key", "ApiKey",
        "OAuth", "Negotiate", "Mutual", "HOBA", "Vapid", "SCRAM-SHA-256", "AWS4-HMAC-SHA256"
    };
    private static readonly Regex BasicTokenRegex = new(@"\b(Basic)\s+(?:(""|'|\\""|\\u0022)([A-Za-z0-9+/_-]{2,}={0,2})(?![A-Za-z0-9+/_=-])(\2|$)|([A-Za-z0-9+/_-]{2,}={0,2})(?![A-Za-z0-9+/_=-]))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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
    private readonly string _instanceId = Guid.NewGuid().ToString("N")[..12];
    private TelemetryClient _client;
    private string? _activeBufferPath;
    private string? _activeGenerationPrefix;
    private FileStream? _instanceOwnerLock;
    private string? _instanceOwnerLockPath;
    private TelemetryConsent _consent = TelemetryConsent.Off;
    private string? _observedExternalToken;
    private bool _pendingInitialEnable;
    private bool _lastEnableBlockedByExternalLock;
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
        _activeGenerationPrefix = ResolveGenerationPrefix(initialToken);
        _activeBufferPath = ResolveBufferPathForToken(initialToken);
        EnsureBufferLockFileExists();
        _client = CreateClient(_activeBufferPath);
        if (initialEnabled)
        {
            if (!SetConsent(TelemetryConsent.Full) && _externalConsentToken != null)
            {
                lock (_gate)
                {
                    if (_lastEnableBlockedByExternalLock && !string.IsNullOrEmpty(ReadExternalTokenSafe()))
                    {
                        _pendingInitialEnable = true;
                    }
                }
            }
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
                    var keepGen = ResolveForeignKeepGenerationPrefixLocked();
                    _lastPurgeFailed = !(TryDeleteForeignGenerationBuffers(keepGen) && TryDeleteSiblingTempFiles(keepGen));
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
                    TryDeleteBufferFallback(ResolveForeignKeepGenerationPrefixLocked());
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
        scrubbed = RedactDoubleQuotedLocalPaths(scrubbed);
        scrubbed = ReplaceUnquotedParenPaths(scrubbed, ParenPathRegex);
        scrubbed = ReplaceUnquotedParenPaths(scrubbed, ParenRelativeSlashPathRegex);
        scrubbed = ReplacePreKeywordPathRules(scrubbed);
        scrubbed = RedactQuotedFilenames(scrubbed);
        scrubbed = ReplaceKeywordQuotedPaths(scrubbed);
        scrubbed = RedactKeywordUnquotedFilenamesRespectingQuotes(scrubbed);
        scrubbed = ReplaceKeywordAssignedPaths(scrubbed);
        scrubbed = RedactEmails(scrubbed);
        if (scrubbed.Contains('.') || scrubbed.Contains("\\u", StringComparison.Ordinal))
        {
            scrubbed = ReplaceBareFilenames(scrubbed);
        }
        scrubbed = RestoreVaultedUrls(scrubbed, VaultPrefix, vaultedUrls);
        if (vaultedUrls.Count > 0)
        {
            scrubbed = RedactEmails(scrubbed);
        }
        return hadSentinels ? UnescapeSentinels(scrubbed) : scrubbed;
    }

    private const string EscVaultPrefix = "\u001EAUNESC";

    private static string VaultNonFileUrls(string text, string prefix, out List<string> vaultedUrls)
    {
        vaultedUrls = new List<string>();
        var withEscaped = VaultJsonEscapedUrlsInBalancedQuotes(text, prefix, vaultedUrls);
        return VaultNonFileUrlsInto(withEscaped, prefix, vaultedUrls);
    }

    private static string VaultJsonEscapedUrlsInBalancedQuotes(string text, string prefix, List<string> vaultedUrls)
    {
        if (text.IndexOf('"') < 0 ||
            (!text.Contains("\\/", StringComparison.Ordinal) && !text.Contains("\\u", StringComparison.Ordinal)))
        {
            return text;
        }
        var spans = CollectBalancedDoubleQuoteSpans(text);
        if (spans.Count == 0) return text;
        StringBuilder? sb = null;
        var copied = 0;
        foreach (var (open, close) in spans)
        {
            var innerStart = open + 1;
            if (innerStart >= close || !ContainsSlashOrUnicodeEscape(text, innerStart, close)) continue;
            var norm = DecodeUrlEscapesForVault(text, innerStart, close);
            if (norm.IndexOf("://", StringComparison.Ordinal) < 0) continue;
            var innerVaulted = new List<string>();
            var wrapped = "\"" + norm + "\"";
            var innerResult = VaultNonFileUrlsInto(wrapped, EscVaultPrefix, innerVaulted);
            if (innerVaulted.Count == 0) continue;
            sb ??= new StringBuilder(text.Length);
            sb.Append(text, copied, innerStart - copied);
            if (innerVaulted.Count == 1 &&
                innerResult == "\"" + EscVaultPrefix + "0\u001F\"" &&
                string.Equals(innerVaulted[0], norm, StringComparison.Ordinal))
            {
                var idx = vaultedUrls.Count;
                vaultedUrls.Add(text.Substring(innerStart, close - innerStart));
                sb.Append(prefix).Append(idx).Append('\u001F');
            }
            else
            {
                AppendRemappedEscapedVault(sb, innerResult, 1, innerResult.Length - 1, prefix, vaultedUrls, innerVaulted);
            }
            copied = close;
        }
        if (sb == null) return text;
        return sb.Append(text, copied, text.Length - copied).ToString();
    }

    private static int ScanVaultIndexEnd(string text, int numStart, int limit)
    {
        var p = numStart;
        var maxEnd = Math.Min(limit, numStart + 10);
        while (p < maxEnd && text[p] is >= '0' and <= '9') p++;
        return p > numStart && p < limit && text[p] == '\u001F' ? p : -1;
    }

    private static void AppendRemappedEscapedVault(
        StringBuilder sb, string text, int start, int end, string prefix, List<string> vaultedUrls, List<string> innerVaulted)
    {
        var pos = start;
        while (pos < end)
        {
            var found = text.IndexOf(EscVaultPrefix, pos, end - pos, StringComparison.Ordinal);
            if (found < 0)
            {
                sb.Append(text, pos, end - pos);
                break;
            }
            sb.Append(text, pos, found - pos);
            var numStart = found + EscVaultPrefix.Length;
            var numEnd = ScanVaultIndexEnd(text, numStart, end);
            if (numEnd > numStart &&
                int.TryParse(text.AsSpan(numStart, numEnd - numStart), out var innerIdx) &&
                innerIdx >= 0 && innerIdx < innerVaulted.Count)
            {
                var globalIdx = vaultedUrls.Count;
                vaultedUrls.Add(innerVaulted[innerIdx]);
                sb.Append(prefix).Append(globalIdx).Append('\u001F');
                pos = numEnd + 1;
            }
            else
            {
                sb.Append(EscVaultPrefix);
                pos = numStart;
            }
        }
    }

    private static bool ContainsSlashOrUnicodeEscape(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (text[i] == '\\' && i + 1 < end)
            {
                var next = text[i + 1];
                if (next == '/' || (next == 'u' && i + 5 < end)) return true;
                i++;
            }
        }
        return false;
    }

    private static bool IsUrlStructuralEscape(char ch)
    {
        if (ch <= 0x20 || (ch >= 0x7F && ch <= 0x9F) || (ch >= 0xD800 && ch <= 0xDFFF) || char.IsWhiteSpace(ch))
        {
            return false;
        }
        return ch is not ('"' or '\\' or '<' or '>');
    }

    private static string DecodeUrlEscapesForVault(string text, int start, int end)
    {
        var sb = new StringBuilder(end - start);
        var pos = start;
        while (pos < end)
        {
            var ch = text[pos];
            if (ch == '\\' && pos + 1 < end)
            {
                var next = text[pos + 1];
                if (next == '/')
                {
                    sb.Append('/');
                    pos += 2;
                    continue;
                }
                if (next == 'u' && pos + 5 < end && TryParseHex4(text, pos + 2, out var code))
                {
                    if (code == '"')
                    {
                        sb.Append('\\').Append('"');
                        pos += 6;
                        continue;
                    }
                    var decoded = (char)code;
                    if (IsUrlStructuralEscape(decoded))
                    {
                        sb.Append(decoded);
                        pos += 6;
                        continue;
                    }
                }
                sb.Append(ch).Append(next);
                pos += 2;
                continue;
            }
            sb.Append(ch);
            pos++;
        }
        return sb.ToString();
    }

    private static string VaultNonFileUrlsInto(string text, string prefix, List<string> vaultedUrls)
    {
        if (text.IndexOf("://", StringComparison.Ordinal) < 0) return text;
        var tracker = new VaultLineTracker();
        StringBuilder? sb = null;
        var copied = 0;
        var searchFrom = 0;
        var quoteCursor = 0;
        var quoteState = 0;
        var failedContMax = 0;
        var failedSpacedContMax = 0;
        var unclosedAngleLineEnd = 0;
        var unclosedDoubleQuoteEnd = 0;
        var unclosedSingleQuoteEnd = 0;
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
            if (start > quoteCursor)
            {
                quoteState = AdvanceQuoteState(text, quoteCursor, start, quoteState);
                quoteCursor = start;
            }
            var inSingleQuote = (quoteState & 1) != 0;
            var inDoubleQuote = (quoteState & 2) != 0;
            var angleStopMask = ComputeQueryAngleStopMask(text, start, inDoubleQuote);
            if (!TryScanUrlAfterScheme(text, sep, angleStopMask, out var baseEnd, out var totalEnd, out var hasQf))
            {
                searchFrom = sep + 3;
                continue;
            }
            string url;
            string tail;
            if (hasQf)
            {
                var qf = TrimQueryOrFragmentDelimiters(
                    text.Substring(baseEnd, totalEnd - baseEnd), text, start, out tail, inSingleQuote);
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
                if ((inSingleQuote || (start > 0 && text[start - 1] == '\'')) && url.EndsWith('\''))
                {
                    url = url[..^1];
                    tail = "'" + tail;
                }
            }
            var rawUrl = text.Substring(start, baseEnd - start);
            var redactedUrl = RedactUrlPathSecrets(url);
            sb ??= new StringBuilder(text.Length);
            sb.Append(text, copied, start - copied);
            var finalTail = tail;
            if (!hasQf && totalEnd < text.Length &&
                ((IsUrlContinuationStarter(text, totalEnd, inDoubleQuote || (start > 0 && text[start - 1] == '"')) &&
                  TryExtendUrlContinuation(text, rawUrl, tail, start, quoteState, ref totalEnd, ref failedContMax,
                      ref unclosedDoubleQuoteEnd, ref unclosedSingleQuoteEnd, out var ext)) ||
                 TryExtendSpacedQuotedUrlQuery(text, rawUrl, tail, start, quoteState, ref totalEnd,
                     ref failedSpacedContMax, out ext)))
            {
                EmitUrlContinuation(sb, prefix, vaultedUrls, redactedUrl, tail, ext);
                finalTail = ext.Tail;
            }
            else
            {
                if (!hasQf && totalEnd < text.Length &&
                    TryResolveQuotedSecretUrlTail(text, start, totalEnd, inDoubleQuote, rawUrl, redactedUrl,
                        ref unclosedAngleLineEnd, out var secretRedacted, out var secretEnd))
                {
                    redactedUrl = secretRedacted;
                    totalEnd = secretEnd;
                    tail = string.Empty;
                    finalTail = string.Empty;
                }
                var idx = vaultedUrls.Count;
                vaultedUrls.Add(redactedUrl);
                sb.Append(prefix).Append(idx).Append('\u001F').Append(tail);
            }
            quoteState = AdvanceQuoteState(finalTail, 0, finalTail.Length, quoteState);
            var activeQuote = (quoteState & 2) != 0 ? '"' : ((quoteState & 1) != 0 ? '\'' : '\0');
            tracker.OnVaultedUrl(totalEnd, activeQuote);
            copied = totalEnd;
            quoteCursor = totalEnd;
            searchFrom = totalEnd;
        }
        if (sb == null) return text;
        sb.Append(text, copied, text.Length - copied);
        return sb.ToString();
    }

    private readonly record struct UrlContinuationResult(
        bool ReplaceBase, string BaseUrl, bool KeepBaseTail, string Middle, string QfToken, string Tail);

    private static void EmitUrlContinuation(
        StringBuilder sb, string prefix, List<string> vaultedUrls, string redactedUrl, string baseTail, UrlContinuationResult ext)
    {
        if (ext.ReplaceBase)
        {
            var idx = vaultedUrls.Count;
            vaultedUrls.Add(ext.BaseUrl);
            sb.Append(prefix).Append(idx).Append('\u001F').Append(ext.Tail);
            return;
        }
        var baseIdx = vaultedUrls.Count;
        vaultedUrls.Add(redactedUrl);
        sb.Append(prefix).Append(baseIdx).Append('\u001F');
        if (ext.KeepBaseTail) sb.Append(baseTail);
        sb.Append(ext.Middle.IndexOf("://", StringComparison.Ordinal) >= 0
            ? VaultNonFileUrlsInto(ext.Middle, prefix, vaultedUrls)
            : ext.Middle);
        if (ext.QfToken.Length > 0)
        {
            var qfIdx = vaultedUrls.Count;
            vaultedUrls.Add(ext.QfToken);
            sb.Append(prefix).Append(qfIdx).Append('\u001F');
        }
        sb.Append(ext.Tail);
    }

    private static bool IsUrlContinuationStarter(string text, int pos, bool inDoubleQuote)
    {
        var ch = text[pos];
        if (ch is ',' or '(' or '[' or ')' or ']' or '{' or '}') return true;
        return inDoubleQuote && ch == '\\' && pos + 1 < text.Length && text[pos + 1] is '"' or '\\' or '/';
    }

    private static bool TryExtendUrlContinuation(
        string text, string rawUrl, string baseTail, int urlStart, int quoteState, ref int totalEnd,
        ref int failedContMax, ref int unclosedDoubleQuoteEnd, ref int unclosedSingleQuoteEnd,
        out UrlContinuationResult result)
    {
        result = default;
        var inSingleQuote = (quoteState & 1) != 0;
        var inDoubleQuote = (quoteState & 2) != 0;
        var probeInput = rawUrl + "x";
        var probe = RedactUrlPathSecrets(probeInput);
        var webhookProbe = (probe != probeInput && probe.EndsWith("[redacted]", StringComparison.Ordinal)) ? probe : null;
        if (webhookProbe == null && totalEnd < failedContMax) return false;
        var outerQuote = (inDoubleQuote || (urlStart > 0 && text[urlStart - 1] == '"'))
            ? '"'
            : (urlStart > 0 && text[urlStart - 1] == '\'' ? '\'' : '\0');
        var allowDoubleQuotes = totalEnd >= unclosedDoubleQuoteEnd && outerQuote != '"';
        var allowEscapedDoubleQuotes = totalEnd >= unclosedDoubleQuoteEnd && outerQuote == '"';
        var allowSingleQuotes = totalEnd >= unclosedSingleQuoteEnd;
        var p = ScanUrlContinuation(text, totalEnd, allowDoubleQuotes, allowEscapedDoubleQuotes, allowSingleQuotes,
            outerQuote, stopAtSchemes: webhookProbe == null, out var sawInnerQf, out var unclosedQuote);
        var hitsQf = p > totalEnd && p < text.Length && text[p] is '?' or '#';
        if (unclosedQuote != '\0' && webhookProbe == null && !hitsQf)
        {
            if (unclosedQuote is '"' or '\u0001' && p > unclosedDoubleQuoteEnd) unclosedDoubleQuoteEnd = p;
            else if (unclosedQuote == '\'' && p > unclosedSingleQuoteEnd) unclosedSingleQuoteEnd = p;
            if (unclosedQuote == '"') allowDoubleQuotes = false;
            else if (unclosedQuote == '\u0001') allowEscapedDoubleQuotes = false;
            else allowSingleQuotes = false;
            p = ScanUrlContinuation(text, totalEnd, allowDoubleQuotes, allowEscapedDoubleQuotes, allowSingleQuotes,
                outerQuote, stopAtSchemes: true, out sawInnerQf, out unclosedQuote);
        }
        if (p > totalEnd && p < text.Length && text[p] is '?' or '#')
        {
            var qfStart = p;
            var angleStopMask = ComputeQueryAngleStopMask(text, urlStart, (quoteState & 2) != 0);
            p = ScanQueryOrFragment(text, qfStart, angleStopMask);
            var qf = TrimQueryOrFragmentDelimiters(
                text.Substring(qfStart, p - qfStart), text, urlStart, out var qfTail, inSingleQuote);
            var qfToken = qf[0] + "[redacted]";
            if (webhookProbe != null)
            {
                result = new UrlContinuationResult(true, webhookProbe + qfToken, false, string.Empty, string.Empty, qfTail);
            }
            else
            {
                var baseRedacted = RedactUrlPathSecrets(rawUrl);
                var prefixRedacted = RedactOuterContinuationPrefix(text, urlStart, totalEnd, qfStart);
                if (prefixRedacted == baseRedacted || prefixRedacted == baseRedacted + "[redacted]")
                {
                    result = new UrlContinuationResult(true, prefixRedacted + qfToken, false, string.Empty, string.Empty, qfTail);
                }
                else
                {
                    var middle = prefixRedacted.StartsWith(baseRedacted, StringComparison.Ordinal)
                        ? prefixRedacted.Substring(baseRedacted.Length)
                        : text.Substring(totalEnd, qfStart - totalEnd);
                    var keepTail = baseTail.Length > 0 && baseRedacted.EndsWith(baseTail, StringComparison.Ordinal);
                    result = new UrlContinuationResult(false, string.Empty, keepTail, middle, qfToken, qfTail);
                }
            }
            totalEnd = p;
            return true;
        }
        var end = TrimWebhookContinuationEnd(
            text, urlStart, totalEnd, p, allowDoubleQuotes && unclosedQuote != '"',
            allowEscapedDoubleQuotes && unclosedQuote != '\u0001',
            allowSingleQuotes && unclosedQuote != '\'', outerQuote == '"', inSingleQuote);
        if (end > totalEnd)
        {
            var extTail = text.Substring(end, p - end);
            if (webhookProbe != null)
            {
                result = new UrlContinuationResult(true, webhookProbe, false, string.Empty, string.Empty, extTail);
                totalEnd = p;
                return true;
            }
            var baseRedacted = RedactUrlPathSecrets(rawUrl);
            var extRedacted = RedactOuterContinuationPrefix(text, urlStart, totalEnd, end);
            if (extRedacted != baseRedacted + text.Substring(totalEnd, end - totalEnd))
            {
                if (extRedacted == baseRedacted || extRedacted == baseRedacted + "[redacted]")
                {
                    result = new UrlContinuationResult(true, extRedacted, false, string.Empty, string.Empty, extTail);
                }
                else
                {
                    var middle = extRedacted.StartsWith(baseRedacted, StringComparison.Ordinal)
                        ? extRedacted.Substring(baseRedacted.Length)
                        : text.Substring(totalEnd, end - totalEnd);
                    var keepTail = baseTail.Length > 0 && baseRedacted.EndsWith(baseTail, StringComparison.Ordinal);
                    result = new UrlContinuationResult(false, string.Empty, keepTail, middle, string.Empty, extTail);
                }
                totalEnd = p;
                return true;
            }
        }
        if (unclosedQuote is '"' or '\u0001' && p > unclosedDoubleQuoteEnd) unclosedDoubleQuoteEnd = p;
        else if (unclosedQuote == '\'' && p > unclosedSingleQuoteEnd) unclosedSingleQuoteEnd = p;
        if (!sawInnerQf && p > failedContMax) failedContMax = p;
        return false;
    }

    private static string RedactOuterContinuationPrefix(string text, int urlStart, int totalEnd, int sliceEnd)
    {
        var nestedStart = FindFirstNestedUrlStart(text, totalEnd, sliceEnd);
        if (nestedStart < 0) return RedactUrlPathSecrets(text.Substring(urlStart, sliceEnd - urlStart));
        var prefix = text.Substring(urlStart, nestedStart - urlStart);
        var probe = RedactUrlPathSecrets(prefix + "x");
        if (probe != prefix + "x" && probe.EndsWith("[redacted]", StringComparison.Ordinal))
        {
            return RedactUrlPathSecrets(text.Substring(urlStart, sliceEnd - urlStart));
        }
        return RedactUrlPathSecrets(prefix) + text.Substring(nestedStart, sliceEnd - nestedStart);
    }

    private static int FindFirstNestedUrlStart(string text, int from, int to)
    {
        var search = from;
        while (search < to)
        {
            var sep = text.IndexOf("://", search, to - search, StringComparison.Ordinal);
            if (sep < 0) return -1;
            if (TryFindNonFileSchemeStart(text, sep, from, out var start)) return start;
            search = sep + 3;
        }
        return -1;
    }

    private static int ScanUrlContinuation(
        string text, int start, bool allowDoubleQuotes, bool allowEscapedDoubleQuotes, bool allowSingleQuotes,
        char outerQuote, bool stopAtSchemes, out bool sawInnerQf, out char unclosedQuote)
    {
        var p = start;
        var groupStack = new List<char>();
        var quoteChar = '\0';
        sawInnerQf = false;
        while (p < text.Length)
        {
            var ch = text[p];
            if (quoteChar != '\0')
            {
                if (ch is '\r' or '\n') break;
                if (ch == '"' && outerQuote == '"') break;
                if (ch is '?' or '#') sawInnerQf = true;
                if (ch == '\\' && p + 1 < text.Length && text[p + 1] is not ('\r' or '\n'))
                {
                    if (quoteChar == '\u0001' && text[p + 1] == '"') quoteChar = '\0';
                    p += 2;
                    continue;
                }
                if (ch == quoteChar) quoteChar = '\0';
                p++;
                continue;
            }
            if (IsFileSchemeAt(text, p) || IsUrlWindowsDriveStopAt(text, p)) break;
            if (stopAtSchemes && p > start &&
                !IsUrlSchemeChar(text[p - 1]) && text[p - 1] is not ('/' or '\\') && IsUrlSchemeAt(text, p))
            {
                break;
            }
            if (ch == '\\' && outerQuote == '"' && p + 1 < text.Length)
            {
                var esc = text[p + 1];
                if (esc == '"')
                {
                    if (allowEscapedDoubleQuotes && groupStack.Count > 0) quoteChar = '\u0001';
                    p += 2;
                    continue;
                }
                if (!stopAtSchemes && esc is '\\' or '/')
                {
                    p += 2;
                    continue;
                }
                break;
            }
            if (char.IsWhiteSpace(ch) || ch is '<' or '>' or '\\' or '?' or '#' or '\r' or '\n')
            {
                break;
            }
            if (ch is '"' or '\'')
            {
                if (ch == '\'' && IsContractionApostrophe(text, p))
                {
                    p++;
                    continue;
                }
                var allowed = ch == '"' ? allowDoubleQuotes : allowSingleQuotes;
                var afterNonAlphaNum = p > start && !IsAsciiAlphaNum(text[p - 1]);
                var canOpen = allowed && groupStack.Count > 0 && (ch == '"' || afterNonAlphaNum);
                if (canOpen)
                {
                    quoteChar = ch;
                    p++;
                    continue;
                }
                if (ch == '"' || ch == outerQuote) break;
                p++;
                continue;
            }
            if (ch is '(' or '[' or '{')
            {
                groupStack.Add(ch);
            }
            else if (ch is ')' or ']' or '}')
            {
                var expected = ch == ')' ? '(' : (ch == ']' ? '[' : '{');
                if (groupStack.Count > 0 && groupStack[^1] == expected)
                {
                    groupStack.RemoveAt(groupStack.Count - 1);
                }
            }
            p++;
        }
        unclosedQuote = quoteChar;
        return p;
    }

    private static bool IsContractionApostrophe(string text, int pos) =>
        pos > 0 && pos + 1 < text.Length && IsAsciiAlphaNum(text[pos - 1]) && IsAsciiAlphaNum(text[pos + 1]);

    private static bool IsUrlSchemeAt(string text, int pos)
    {
        if (pos >= text.Length || !char.IsAsciiLetter(text[pos])) return false;
        var i = pos + 1;
        while (i < text.Length && IsUrlSchemeChar(text[i])) i++;
        return i - pos >= 2 && i + 2 < text.Length && text[i] == ':' && text[i + 1] == '/' && text[i + 2] == '/';
    }

    private static int TrimWebhookContinuationEnd(
        string text, int urlStart, int minEnd, int scanEnd, bool allowDoubleQuotes, bool allowEscapedDoubleQuotes,
        bool allowSingleQuotes, bool inDoubleQuote, bool inSingleQuote)
    {
        var excessParens = 0;
        var excessBrackets = 0;
        var excessBraces = 0;
        var quoteChar = '\0';
        var openGroups = 0;
        for (var i = urlStart; i < scanEnd; i++)
        {
            var c = text[i];
            if (quoteChar != '\0')
            {
                if (c == '\\' && i + 1 < scanEnd)
                {
                    if (quoteChar == '\u0001' && text[i + 1] == '"') quoteChar = '\0';
                    i++;
                    continue;
                }
                if (c == quoteChar) quoteChar = '\0';
                continue;
            }
            if (inDoubleQuote && c == '\\' && i + 1 < scanEnd && text[i + 1] is '"' or '\\' or '/')
            {
                if (openGroups > 0 && allowEscapedDoubleQuotes && text[i + 1] == '"') quoteChar = '\u0001';
                i++;
                continue;
            }
            var afterNonAlphaNum = i > urlStart && !IsAsciiAlphaNum(text[i - 1]);
            if (openGroups > 0 && ((c == '"' && allowDoubleQuotes) || (c == '\'' && allowSingleQuotes && afterNonAlphaNum)))
            {
                quoteChar = c;
                continue;
            }
            if (c == '(') { excessParens--; openGroups++; }
            else if (c == ')') { excessParens++; if (openGroups > 0) openGroups--; }
            else if (c == '[') { excessBrackets--; openGroups++; }
            else if (c == ']') { excessBrackets++; if (openGroups > 0) openGroups--; }
            else if (c == '{') { excessBraces--; openGroups++; }
            else if (c == '}') { excessBraces++; if (openGroups > 0) openGroups--; }
        }
        var precededByQuote = inSingleQuote || (urlStart > 0 && text[urlStart - 1] == '\'');
        var end = scanEnd;
        while (end > minEnd)
        {
            var c = text[end - 1];
            if (c is ',' or ';') { end--; continue; }
            if (c == '\'' && precededByQuote) { end--; continue; }
            if (c == ')' && excessParens > 0) { excessParens--; end--; continue; }
            if (c == ']' && excessBrackets > 0) { excessBrackets--; end--; continue; }
            if (c == '}' && excessBraces > 0) { excessBraces--; end--; continue; }
            break;
        }
        return end;
    }

    private static readonly Regex WebhookPathTokenRegex = new(
        @"((?:^|/|%2[fF])webhooks?(?:/|%2[fF]))[^\s?#]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SlackWebhookHostRegex = new(
        @"https?://hooks\.slack(?:-gov)?\.com\.?(?::\d+)?(?=(?:/|%2[fF]))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SlackWebhookRouteRegex = new(
        @"((?:/|%2[fF])(?:services|workflows|triggers)(?:/|%2[fF]))[^\s?#]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TelegramBotTokenUrlRegex = new(
        @"((?:/|%2[fF])bot\d+:)[^/\s?#]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string RedactUrlPathSecrets(string url)
    {
        var result = ReplaceUrlPathSecret(url, WebhookPathTokenRegex);
        result = RedactSlackWebhookUrl(result);
        result = ReplaceUrlPathSecret(result, TelegramBotTokenUrlRegex);
        return result;
    }

    private static string RedactSlackWebhookUrl(string url)
    {
        string decoded;
        int[]? origOffsets = null;
        if (url.IndexOf('%') >= 0)
        {
            (decoded, origOffsets) = DecodeUrlForPathSecretMatch(url);
        }
        else
        {
            decoded = url;
        }
        var hostMatch = SlackWebhookHostRegex.Match(decoded);
        if (!hostMatch.Success) return url;
        var hostEnd = hostMatch.Index + hostMatch.Length;
        var pathEnd = hostEnd;
        while (pathEnd < decoded.Length && !char.IsWhiteSpace(decoded[pathEnd]) && decoded[pathEnd] is not ('?' or '#'))
        {
            pathEnd++;
        }
        var routeMatch = SlackWebhookRouteRegex.Match(decoded, hostEnd, pathEnd - hostEnd);
        if (!routeMatch.Success) return url;
        var g1 = routeMatch.Groups[1];
        var rawStart = g1.Index + g1.Length;
        var rawEnd = routeMatch.Index + routeMatch.Length;
        var secretStart = origOffsets != null ? origOffsets[rawStart] : rawStart;
        var secretEnd = origOffsets != null ? origOffsets[rawEnd] : rawEnd;
        return string.Concat(url.AsSpan(0, secretStart), "[redacted]", url.AsSpan(secretEnd));
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
        var steppedJsonEscape = false;
        while (start < sep && start > minStart && text[start - 1] == '\\' && !IsPrecededByOddBackslashes(text, start - 1, minStart))
        {
            var esc = text[start];
            if (esc is 'n' or 'r' or 't' or 'b' or 'f')
            {
                start += 1;
                steppedJsonEscape = true;
                continue;
            }
            if (esc == 'u' && start + 5 <= sep && TryParseHex4(text, start + 1, out var code))
            {
                var decoded = (char)code;
                if (!IsUrlSchemeChar(decoded) && decoded is not ('/' or '\\'))
                {
                    start += 5;
                    steppedJsonEscape = true;
                    continue;
                }
            }
            break;
        }
        if (sep - start < 2 || !char.IsAsciiLetter(text[start])) return false;
        if (!steppedJsonEscape && start > 0 && (IsUrlSchemeChar(text[start - 1]) || text[start - 1] is '/' or '\\')) return false;
        var scheme = text.AsSpan(start, sep - start);
        return !scheme.Equals("file", StringComparison.OrdinalIgnoreCase) &&
            !scheme.Equals("content", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAngleWrappedBefore(string text, int urlStart)
    {
        if (urlStart > 0 && text[urlStart - 1] == '<') return true;
        return urlStart >= 6 &&
            text[urlStart - 6] == '\\' &&
            text[urlStart - 5] == 'u' &&
            text[urlStart - 4] == '0' &&
            text[urlStart - 3] == '0' &&
            text[urlStart - 2] == '3' &&
            text[urlStart - 1] is 'c' or 'C' &&
            !IsPrecededByOddBackslashes(text, urlStart - 6, 0);
    }

    private static bool IsEscapedGreaterAt(string text, int pos) =>
        pos + 5 < text.Length &&
        text[pos] == '\\' &&
        text[pos + 1] == 'u' &&
        text[pos + 2] == '0' &&
        text[pos + 3] == '0' &&
        text[pos + 4] == '3' &&
        text[pos + 5] is 'e' or 'E' &&
        !IsPrecededByOddBackslashes(text, pos, 0);

    private static bool TryResolveQuotedSecretUrlTail(
        string text, int urlStart, int totalEnd, bool inDoubleQuote, string rawUrl, string redactedUrl,
        ref int unclosedAngleLineEnd, out string secretRedacted, out int secretEnd)
    {
        secretRedacted = string.Empty;
        secretEnd = totalEnd;
        var wrappedInAngle = IsAngleWrappedBefore(text, urlStart);
        if (!inDoubleQuote && !wrappedInAngle) return false;
        if (!inDoubleQuote && totalEnd < unclosedAngleLineEnd) return false;
        var nextCh = text[totalEnd];
        if (nextCh is '\r' or '\n' ||
            (inDoubleQuote && nextCh == '"') ||
            (wrappedInAngle && (nextCh == '>' || IsEscapedGreaterAt(text, totalEnd))))
        {
            return false;
        }
        var resolved = ResolveQuotedSecretUrlRedaction(rawUrl, redactedUrl);
        if (resolved == null) return false;
        var p = totalEnd;
        var escaped = false;
        var stoppedAtAngle = false;
        while (p < text.Length)
        {
            var ch = text[p];
            if (ch is '\r' or '\n') break;
            if (inDoubleQuote && ch == '"' && !escaped) break;
            if (wrappedInAngle && !escaped && (ch == '>' || IsEscapedGreaterAt(text, p)))
            {
                stoppedAtAngle = true;
                break;
            }
            escaped = inDoubleQuote && ch == '\\' && !escaped;
            p++;
        }
        if (!inDoubleQuote && !stoppedAtAngle)
        {
            if (p > unclosedAngleLineEnd) unclosedAngleLineEnd = p;
            return false;
        }
        secretRedacted = resolved;
        secretEnd = p;
        return true;
    }

    private static string? ResolveQuotedSecretUrlRedaction(string rawUrl, string redactedUrl)
    {
        if (redactedUrl != rawUrl && redactedUrl.EndsWith("[redacted]", StringComparison.Ordinal)) return redactedUrl;
        var probeInput = rawUrl + "x";
        var probe = RedactUrlPathSecrets(probeInput);
        return probe != probeInput && probe.EndsWith("[redacted]", StringComparison.Ordinal) ? probe : null;
    }

    private static bool TryExtendSpacedQuotedUrlQuery(
        string text, string rawUrl, string baseTail, int urlStart, int quoteState, ref int totalEnd,
        ref int failedSpacedContMax, out UrlContinuationResult result)
    {
        result = default;
        var inDoubleQuote = (quoteState & 2) != 0;
        var wrappedInAngle = IsAngleWrappedBefore(text, urlStart);
        if ((!inDoubleQuote && !wrappedInAngle) || totalEnd >= text.Length) return false;
        var firstCh = text[totalEnd];
        if (firstCh is '\r' or '\n' ||
            (inDoubleQuote && firstCh == '"') ||
            (wrappedInAngle && (firstCh == '>' || IsEscapedGreaterAt(text, totalEnd))))
        {
            return false;
        }
        if (totalEnd < failedSpacedContMax) return false;
        var inSingleQuote = (quoteState & 1) != 0;
        var urlStartsSpan = wrappedInAngle || IsUrlAtDoubleQuoteStart(text, urlStart);
        var p = totalEnd;
        while (p < text.Length)
        {
            var ch = text[p];
            if (ch is '\r' or '\n') break;
            if (inDoubleQuote && ch == '"') break;
            if (wrappedInAngle && (ch == '>' || IsEscapedGreaterAt(text, p))) break;
            if (IsFileSchemeAt(text, p) || IsUrlWindowsDriveStopAt(text, p)) break;
            if (p > totalEnd &&
                !IsUrlSchemeChar(text[p - 1]) && text[p - 1] is not ('/' or '\\') && IsUrlSchemeAt(text, p))
            {
                break;
            }
            if (inDoubleQuote && ch == '\\' && p + 1 < text.Length)
            {
                var esc = text[p + 1];
                if (esc is 'r' or 'n') break;
                if (esc == 'u' && p + 5 < text.Length && TryParseHex4(text, p + 2, out var code))
                {
                    if (code is '\r' or '\n' || (wrappedInAngle && code == '>')) break;
                    p += 6;
                    continue;
                }
                p += 2;
                continue;
            }
            if (ch is '?' or '#' &&
                IsValidSpacedUrlQueryStart(text, p, totalEnd, inDoubleQuote, wrappedInAngle, urlStartsSpan))
            {
                var angleStopMask = ComputeQueryAngleStopMask(text, urlStart, inDoubleQuote);
                var qfEnd = ScanQueryOrFragment(text, p, angleStopMask);
                var qf = TrimQueryOrFragmentDelimiters(
                    text.Substring(p, qfEnd - p), text, urlStart, out var qfTail, inSingleQuote);
                var qfToken = qf[0] + "[redacted]";
                var secretPrefix = ResolveQuotedSecretUrlRedaction(rawUrl, RedactUrlPathSecrets(rawUrl));
                if (secretPrefix != null)
                {
                    result = new UrlContinuationResult(true, secretPrefix + qfToken, false, string.Empty, string.Empty, qfTail);
                }
                else
                {
                    result = new UrlContinuationResult(
                        false, string.Empty, baseTail.Length > 0, text.Substring(totalEnd, p - totalEnd), qfToken, qfTail);
                }
                totalEnd = qfEnd;
                return true;
            }
            p++;
        }
        if (p > failedSpacedContMax) failedSpacedContMax = p;
        return false;
    }

    private static bool IsValidSpacedUrlQueryStart(
        string text, int qfPos, int minPos, bool inDoubleQuote, bool wrappedInAngle, bool urlStartsSpan)
    {
        var nextPos = qfPos + 1;
        if (nextPos >= text.Length) return false;
        var nextCh = text[nextPos];
        if (nextCh is '\r' or '\n' ||
            (inDoubleQuote && nextCh == '"') ||
            (wrappedInAngle && (nextCh == '>' || IsEscapedGreaterAt(text, nextPos))))
        {
            return false;
        }
        var nextIsWs = char.IsWhiteSpace(nextCh) || (inDoubleQuote && IsJsonWhitespaceEscapeAt(text, nextPos));
        if (text[qfPos] == '?') return !nextIsWs || urlStartsSpan;
        return !nextIsWs && (urlStartsSpan || !IsPrecededBySpaceOrEscape(text, qfPos, minPos, inDoubleQuote));
    }

    private static bool IsUrlAtDoubleQuoteStart(string text, int urlStart)
    {
        var p = urlStart;
        while (p > 0)
        {
            var prev = text[p - 1];
            if (prev is ' ' or '\t' || (char.IsWhiteSpace(prev) && prev is not ('\r' or '\n')))
            {
                p--;
                continue;
            }
            if (p >= 2 && text[p - 2] == '\\' && prev == 't' && !IsPrecededByOddBackslashes(text, p - 2, 0))
            {
                p -= 2;
                continue;
            }
            if (p >= 6 && text[p - 6] == '\\' && text[p - 5] == 'u' &&
                !IsPrecededByOddBackslashes(text, p - 6, 0) &&
                TryParseHex4(text, p - 4, out var code) &&
                char.IsWhiteSpace((char)code) && code is not ('\r' or '\n'))
            {
                p -= 6;
                continue;
            }
            return prev == '"' && !IsPrecededByOddBackslashes(text, p - 1, 0);
        }
        return false;
    }

    private static bool IsJsonWhitespaceEscapeAt(string text, int pos)
    {
        if (text[pos] != '\\' || pos + 1 >= text.Length) return false;
        var esc = text[pos + 1];
        if (esc is 't' or 'r' or 'n') return true;
        return esc == 'u' && pos + 5 < text.Length && TryParseHex4(text, pos + 2, out var code) && char.IsWhiteSpace((char)code);
    }

    private static bool IsPrecededBySpaceOrEscape(string text, int pos, int minPos, bool inDoubleQuote)
    {
        if (pos <= minPos) return false;
        if (char.IsWhiteSpace(text[pos - 1])) return true;
        if (!inDoubleQuote) return false;
        if (pos - 2 >= minPos && text[pos - 2] == '\\' && text[pos - 1] == 't' &&
            !IsPrecededByOddBackslashes(text, pos - 2, minPos))
        {
            return true;
        }
        return pos - 6 >= minPos && text[pos - 6] == '\\' && text[pos - 5] == 'u' &&
            !IsPrecededByOddBackslashes(text, pos - 6, minPos) &&
            TryParseHex4(text, pos - 4, out var code) && char.IsWhiteSpace((char)code);
    }

    private static int ComputeQueryAngleStopMask(string text, int urlStart, bool inDoubleQuote)
    {
        var wrappedInAngle = IsAngleWrappedBefore(text, urlStart);
        var stopOnLess = !inDoubleQuote && !wrappedInAngle;
        var stopOnGreater = !inDoubleQuote || wrappedInAngle;
        var allowInlineSpace = inDoubleQuote;
        var stopOnEscapedGreater = wrappedInAngle;
        return (stopOnLess ? 1 : 0) | (stopOnGreater ? 2 : 0) | (allowInlineSpace ? 4 : 0) | (stopOnEscapedGreater ? 8 : 0);
    }

    private static bool TryScanUrlAfterScheme(
        string text, int sep, int angleStopMask, out int baseEnd, out int totalEnd, out bool hasQf)
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
            if (portEnd == portStart &&
                portEnd < text.Length &&
                text[portEnd] is not ('/' or '?' or '#') &&
                !IsUrlEndBoundary(text, portEnd))
            {
                return false;
            }
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
            p = ScanQueryOrFragment(text, p, angleStopMask);
        }
        totalEnd = p;
        return totalEnd == text.Length || IsUrlEndBoundary(text, totalEnd);
    }

    private static int ScanQueryOrFragment(string text, int start, int angleStopMask = 3)
    {
        var p = start + 1;
        while (p < text.Length)
        {
            var ch = text[p];
            if (ch is '\r' or '\n' ||
                ((angleStopMask & 4) == 0 && char.IsWhiteSpace(ch)) ||
                (ch == '<' && (angleStopMask & 1) != 0) ||
                (ch == '>' && (angleStopMask & 2) != 0) ||
                ((angleStopMask & 8) != 0 && ch == '\\' && IsEscapedGreaterAt(text, p)))
            {
                break;
            }
            if (ch == '"')
            {
                var bs = 0;
                while (p - 1 - bs > start && text[p - 1 - bs] == '\\') bs++;
                if ((bs & 1) == 0) break;
            }
            p++;
        }
        return p;
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
        var openBraceCount = 0;
        while (p < text.Length)
        {
            if (IsFileSchemeAt(text, p) || IsUrlWindowsDriveStopAt(text, p)) break;
            var ch = text[p];
            if (char.IsWhiteSpace(ch) || ch is '"' or '<' or '>' or '\\' or '?' or '#' or '\r' or '\n')
            {
                break;
            }
            if (ch == ',') break;
            if (ch is '(' or '[' or '{')
            {
                if (openBraceCount > 0 || StartsEmbeddedPathInsideGroup(text, p + 1)) break;
                openPositions.Add(p);
                openChars.Add(ch);
                if (ch == '{') openBraceCount++;
                p++;
                continue;
            }
            if (ch is ')' or ']' or '}')
            {
                var expected = ch == ')' ? '(' : (ch == ']' ? '[' : '{');
                if (openChars.Count == 0 || openChars[^1] != expected) break;
                openPositions.RemoveAt(openPositions.Count - 1);
                openChars.RemoveAt(openChars.Count - 1);
                if (ch == '}') openBraceCount--;
                p++;
                continue;
            }
            if (openBraceCount > 0 && !(IsAsciiAlphaNum(ch) || ch is '_' or '-')) break;
            if (openChars.Count > 0 && (ch == ':' || (ch == '\'' && (p == 0 || !IsAsciiAlphaNum(text[p - 1]))))) break;
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

    private static bool IsUrlWindowsDriveStopAt(string text, int pos)
    {
        if (!IsWindowsDriveAt(text, pos)) return false;
        if (pos == 0 || text[pos + 2] == '\\') return true;
        var prev = text[pos - 1];
        return !(IsAsciiAlphaNum(prev) || prev is '-' or '_' or '.' or '~');
    }

    private static bool IsUrlEndBoundary(string text, int pos)
    {
        var ch = text[pos];
        if (char.IsWhiteSpace(ch) || ch is '"' or '\'' or '<' or '>' or '\\' or '{' or '}' or ',' or ';' or '(' or ')' or '[' or ']')
        {
            return true;
        }
        return IsFileSchemeAt(text, pos) || IsUrlWindowsDriveStopAt(text, pos);
    }

    private static string RedactDoubleQuotedLocalPaths(string text)
    {
        if (text.IndexOf('"') < 0) return text;
        StringBuilder? sb = null;
        var copied = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '"' || IsPrecededByOddBackslashes(text, i, copied))
            {
                i++;
                continue;
            }
            var close = FindDoubleQuotedPathEnd(text, i + 1, out var hasVault, out var stopPos);
            if (close < 0)
            {
                i = stopPos + 1;
                continue;
            }
            if (!hasVault)
            {
                RedactQuotedLocalPathSegment(text, i + 1, close, beforeVault: false, ref sb, ref copied);
            }
            else
            {
                RedactQuotedNonVaultSegments(text, i + 1, close, ref sb, ref copied);
            }
            i = close + 1;
        }
        if (sb == null) return text;
        return sb.Append(text, copied, text.Length - copied).ToString();
    }

    private static void RedactQuotedLocalPathSegment(
        string text, int segStart, int segEnd, bool beforeVault, ref StringBuilder? sb, ref int copied)
    {
        var cur = segStart;
        while (cur < segEnd)
        {
            var pathPos = FindLocalPathStartInQuoted(text, cur, segEnd);
            if (pathPos < 0) break;
            if (pathPos > cur && IsPrecededByOddBackslashes(text, pathPos, cur)) pathPos--;
            if (IsPrecededByEscapedQuote(text, pathPos, cur) &&
                TryFindClosingEscapedQuote(text, pathPos, segEnd, out var quotePos, out var quoteLen))
            {
                sb ??= new StringBuilder(text.Length);
                sb.Append(text, copied, pathPos - copied).Append("[redacted-path]");
                copied = quotePos;
                cur = quotePos + quoteLen;
                continue;
            }
            var redactEnd = segEnd;
            if (beforeVault)
            {
                while (redactEnd > pathPos && text[redactEnd - 1] is ' ' or '\t') redactEnd--;
            }
            sb ??= new StringBuilder(text.Length);
            sb.Append(text, copied, pathPos - copied).Append("[redacted-path]");
            copied = redactEnd;
            break;
        }
    }

    private static bool IsPrecededByEscapedQuote(string text, int pos, int minPos)
    {
        if (pos - 2 >= minPos && text[pos - 2] == '\\' && text[pos - 1] == '"' &&
            !IsPrecededByOddBackslashes(text, pos - 2, minPos))
        {
            return true;
        }
        return pos - 6 >= minPos && text[pos - 6] == '\\' && text[pos - 5] == 'u' &&
            !IsPrecededByOddBackslashes(text, pos - 6, minPos) &&
            TryParseHex4(text, pos - 4, out var code) && code == '"';
    }

    private static bool TryFindClosingEscapedQuote(
        string text, int start, int end, out int quotePos, out int quoteLen)
    {
        var pos = start;
        var innerEscaped = false;
        while (pos < end)
        {
            var step = DecodeJsonCharAt(text, pos, end, out var decoded);
            if (innerEscaped)
            {
                innerEscaped = false;
                pos += step;
                continue;
            }
            if (decoded == '\\')
            {
                innerEscaped = true;
                pos += step;
                continue;
            }
            if (decoded == '"' && step > 1)
            {
                quotePos = pos;
                quoteLen = step;
                return true;
            }
            pos += step;
        }
        quotePos = -1;
        quoteLen = 0;
        return false;
    }

    private static void RedactQuotedNonVaultSegments(
        string text, int start, int close, ref StringBuilder? sb, ref int copied)
    {
        var segStart = start;
        while (segStart < close)
        {
            var vaultStart = text.IndexOf('\u001E', segStart, close - segStart);
            var segEnd = vaultStart >= 0 ? vaultStart : close;
            if (segStart < segEnd)
            {
                RedactQuotedLocalPathSegment(text, segStart, segEnd, beforeVault: vaultStart >= 0, ref sb, ref copied);
            }
            if (vaultStart < 0) break;
            var vaultEnd = text.IndexOf('\u001F', vaultStart + 1, close - vaultStart - 1);
            if (vaultEnd < 0) break;
            segStart = vaultEnd + 1;
        }
    }

    private static List<(int Open, int Close)> CollectBalancedDoubleQuoteSpans(string text)
    {
        var spans = new List<(int Open, int Close)>();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '"' || IsPrecededByOddBackslashes(text, i, 0))
            {
                i++;
                continue;
            }
            var close = FindDoubleQuotedPathEnd(text, i + 1, out _, out var stopPos);
            if (close < 0)
            {
                i = stopPos + 1;
                continue;
            }
            spans.Add((i, close));
            i = close + 1;
        }
        return spans;
    }

    private static string BuildMaskedDoubleQuotes(string text, List<(int Open, int Close)> spans)
    {
        var chars = text.ToCharArray();
        foreach (var (open, close) in spans)
        {
            for (var k = open + 1; k < close; k++)
            {
                if (chars[k] is not ('\u001E' or '\u001F')) chars[k] = 'a';
            }
        }
        return new string(chars);
    }

    private static string ReplacePreKeywordPathRules(string text)
    {
        var rules = new (Regex Regex, bool HasPrefix)[]
        {
            (WindowsPathRegex, false),
            (RelativeBackslashPathRegex, true),
            (QuotedSpacedRootPathRegex, false),
            (UnixPathRegex, false),
            (RelativeSlashPathRegex, true),
        };
        var current = text;
        string? masked = null;
        var hasMask = false;
        foreach (var (regex, hasPrefix) in rules)
        {
            if (current.IndexOf('"') < 0)
            {
                current = regex.Replace(current, hasPrefix ? "$1[redacted-path]" : "[redacted-path]");
                continue;
            }
            if (!hasMask)
            {
                var spans = CollectBalancedDoubleQuoteSpans(current);
                masked = spans.Count > 0 ? BuildMaskedDoubleQuotes(current, spans) : null;
                hasMask = true;
            }
            if (masked == null)
            {
                var next = regex.Replace(current, hasPrefix ? "$1[redacted-path]" : "[redacted-path]");
                if (!ReferenceEquals(next, current))
                {
                    current = next;
                    hasMask = false;
                }
                continue;
            }
            var replaced = ApplyMaskedPathRegex(current, masked, regex, hasPrefix);
            if (!ReferenceEquals(replaced, current))
            {
                current = replaced;
                hasMask = false;
            }
        }
        return current;
    }

    private static string ApplyMaskedPathRegex(string text, string masked, Regex regex, bool hasPrefix)
    {
        var match = regex.Match(masked);
        if (!match.Success) return text;
        var sb = new StringBuilder(text.Length);
        var copied = 0;
        while (match.Success)
        {
            var start = match.Index;
            var end = start + match.Length;
            sb.Append(text, copied, start - copied);
            if (hasPrefix)
            {
                var g1 = match.Groups[1];
                sb.Append(text, g1.Index, g1.Length);
            }
            sb.Append("[redacted-path]");
            copied = end;
            match = match.NextMatch();
        }
        sb.Append(text, copied, text.Length - copied);
        return sb.ToString();
    }

    private static string ReplaceKeywordAssignedPaths(string text)
    {
        if (text.IndexOf('"') < 0) return KeywordAssignedPathRegex.Replace(text, "$1[redacted-path]");
        var spans = CollectBalancedDoubleQuoteSpans(text);
        if (spans.Count == 0) return KeywordAssignedPathRegex.Replace(text, "$1[redacted-path]");
        var masked = BuildMaskedDoubleQuotes(text, spans);
        var current = ApplyMaskedPathRegex(text, masked, KeywordAssignedPathRegex, hasPrefix: true);
        if (!ReferenceEquals(current, text))
        {
            if (current.IndexOf('"') < 0) return current;
            spans = CollectBalancedDoubleQuoteSpans(current);
        }
        StringBuilder? sb = null;
        var copied = 0;
        foreach (var (open, close) in spans)
        {
            var innerStart = open + 1;
            var innerLen = close - innerStart;
            if (innerLen <= 0 || !ContainsAssignCandidate(current, innerStart, close)) continue;
            var (decoded, rawOffsets) = DecodeQuotedSpanWithOffsets(current, innerStart, close);
            var match = KeywordAssignedPathRegex.Match(decoded);
            if (!match.Success) continue;
            while (match.Success)
            {
                var g1 = match.Groups[1];
                var rawG1End = rawOffsets[g1.Index + g1.Length];
                var rawEnd = rawOffsets[match.Index + match.Length];
                sb ??= new StringBuilder(current.Length);
                sb.Append(current, copied, rawG1End - copied).Append("[redacted-path]");
                copied = rawEnd;
                match = match.NextMatch();
            }
        }
        if (sb == null) return current;
        return sb.Append(current, copied, current.Length - copied).ToString();
    }

    private static bool ContainsAssignCandidate(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            var ch = text[i];
            if (ch is ':' or '=' || (ch == '\\' && i + 5 < end && text[i + 1] == 'u')) return true;
        }
        return false;
    }

    private static (string Decoded, int[] RawOffsets) DecodeQuotedSpanWithOffsets(string text, int start, int end)
    {
        var len = end - start;
        var chars = new char[len];
        var offsets = new int[len + 1];
        var count = 0;
        var pos = start;
        while (pos < end)
        {
            offsets[count] = pos;
            var step = DecodeJsonCharAt(text, pos, end, out var decoded);
            if (step > 1 && decoded is '\u001E' or '\u001F') decoded = '_';
            chars[count++] = decoded;
            pos += step;
        }
        offsets[count] = end;
        return (new string(chars, 0, count), offsets);
    }

    private static bool ContainsDotOrUnicodeEscape(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            var ch = text[i];
            if (ch == '.') return true;
            if (ch == '\\' && i + 1 < end)
            {
                if (text[i + 1] == 'u' && i + 5 < end) return true;
                i++;
            }
        }
        return false;
    }

    private static bool ContainsQuoteOrQuoteEscape(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            var ch = text[i];
            if (ch == '\'') return true;
            if (ch == '\\' && i + 1 < end)
            {
                var next = text[i + 1];
                if (next == '"' || (next == 'u' && i + 5 < end)) return true;
                i++;
            }
        }
        return false;
    }

    private static bool ContainsSingleQuoteOrUnicodeEscape(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            var ch = text[i];
            if (ch == '\'') return true;
            if (ch == '\\' && i + 1 < end)
            {
                if (text[i + 1] == 'u' && i + 5 < end) return true;
                i++;
            }
        }
        return false;
    }

    private static string ReplaceKeywordQuotedPaths(string text)
    {
        if (text.IndexOf('"') < 0) return ReplaceKeywordQuotedOnSource(text, text);
        var spans = CollectBalancedDoubleQuoteSpans(text);
        if (spans.Count == 0) return ReplaceKeywordQuotedOnSource(text, text);
        var masked = BuildMaskedDoubleQuotes(text, spans);
        var current = ReplaceKeywordQuotedOnSource(text, masked);
        if (!ReferenceEquals(current, text))
        {
            if (current.IndexOf('"') < 0) return current;
            spans = CollectBalancedDoubleQuoteSpans(current);
        }
        StringBuilder? sb = null;
        var copied = 0;
        foreach (var (open, close) in spans)
        {
            var innerStart = open + 1;
            if (innerStart >= close || !ContainsQuoteOrQuoteEscape(current, innerStart, close)) continue;
            var (decoded, rawOffsets) = DecodeQuotedSpanWithOffsets(current, innerStart, close);
            if (!decoded.Contains('\'') && !decoded.Contains('"')) continue;
            ApplyKeywordQuotedDecoded(current, decoded, rawOffsets, ref sb, ref copied);
        }
        if (sb == null) return current;
        return sb.Append(current, copied, current.Length - copied).ToString();
    }

    private static string ReplaceKeywordQuotedOnSource(string text, string scanSource)
    {
        StringBuilder? sb = null;
        var copied = 0;
        var searchFrom = 0;
        var unclosedSingleUntil = -1;
        var unclosedDoubleUntil = -1;
        while (searchFrom < scanSource.Length)
        {
            var match = KeywordQuotedPathRegex.Match(scanSource, searchFrom);
            if (!match.Success) break;
            var kwEnd = match.Index + match.Length;
            var q = scanSource[kwEnd];
            var unclosedUntil = q == '\'' ? unclosedSingleUntil : unclosedDoubleUntil;
            if (kwEnd < unclosedUntil)
            {
                searchFrom = kwEnd + 1;
                continue;
            }
            var closeEnd = FindKeywordQuotedEnd(scanSource, kwEnd, out var stopPos);
            if (closeEnd > 0)
            {
                sb ??= new StringBuilder(text.Length);
                sb.Append(text, copied, kwEnd - copied).Append("[redacted-path]");
                copied = closeEnd;
                searchFrom = closeEnd;
            }
            else
            {
                if (stopPos > kwEnd)
                {
                    if (q == '\'') unclosedSingleUntil = Math.Max(unclosedSingleUntil, stopPos);
                    else unclosedDoubleUntil = Math.Max(unclosedDoubleUntil, stopPos);
                }
                searchFrom = kwEnd + 1;
            }
        }
        if (sb == null) return text;
        return sb.Append(text, copied, text.Length - copied).ToString();
    }

    private static void ApplyKeywordQuotedDecoded(
        string text, string decoded, int[] rawOffsets, ref StringBuilder? sb, ref int copied)
    {
        var searchFrom = 0;
        var unclosedSingleUntil = -1;
        var unclosedDoubleUntil = -1;
        while (searchFrom < decoded.Length)
        {
            var match = KeywordQuotedPathRegex.Match(decoded, searchFrom);
            if (!match.Success) break;
            var kwEnd = match.Index + match.Length;
            var q = decoded[kwEnd];
            var unclosedUntil = q == '\'' ? unclosedSingleUntil : unclosedDoubleUntil;
            if (kwEnd < unclosedUntil)
            {
                searchFrom = kwEnd + 1;
                continue;
            }
            var closeEnd = FindKeywordQuotedEnd(decoded, kwEnd, out var stopPos);
            if (closeEnd > 0)
            {
                var rawPrefixEnd = rawOffsets[kwEnd];
                var rawEnd = rawOffsets[closeEnd];
                sb ??= new StringBuilder(text.Length);
                sb.Append(text, copied, rawPrefixEnd - copied).Append("[redacted-path]");
                copied = rawEnd;
                searchFrom = closeEnd;
            }
            else
            {
                if (stopPos > kwEnd)
                {
                    if (q == '\'') unclosedSingleUntil = Math.Max(unclosedSingleUntil, stopPos);
                    else unclosedDoubleUntil = Math.Max(unclosedDoubleUntil, stopPos);
                }
                searchFrom = kwEnd + 1;
            }
        }
    }

    private static int FindKeywordQuotedEnd(string source, int quotePos, out int stopPos)
    {
        var q = source[quotePos];
        if (quotePos + 1 >= source.Length || source[quotePos + 1] == '\u001E')
        {
            stopPos = -1;
            return -1;
        }
        var j = quotePos + 1;
        while (j < source.Length)
        {
            var ch = source[j];
            if (ch is '\r' or '\n' or '\u001E' or '\u001F')
            {
                stopPos = j;
                return -1;
            }
            if (ch == q)
            {
                if (q == '\'' && j > quotePos + 1 && j + 1 < source.Length &&
                    char.IsLetterOrDigit(source[j - 1]) && char.IsLetter(source[j + 1]))
                {
                    j++;
                    continue;
                }
                stopPos = -1;
                return j > quotePos + 1 ? j + 1 : -1;
            }
            j++;
        }
        stopPos = source.Length;
        return -1;
    }

    private static string ReplaceBareFilenames(string text)
    {
        if (text.IndexOf('"') < 0) return BareFilenameRegex.Replace(text, "$1$2[redacted-path]");
        var spans = CollectBalancedDoubleQuoteSpans(text);
        if (spans.Count == 0) return BareFilenameRegex.Replace(text, "$1$2[redacted-path]");
        var masked = BuildMaskedDoubleQuotes(text, spans);
        var current = ApplyMaskedBareFilenameRegex(text, masked);
        if (!ReferenceEquals(current, text))
        {
            if (current.IndexOf('"') < 0) return current;
            spans = CollectBalancedDoubleQuoteSpans(current);
        }
        StringBuilder? sb = null;
        var copied = 0;
        foreach (var (open, close) in spans)
        {
            var innerStart = open + 1;
            if (innerStart >= close || !ContainsDotOrUnicodeEscape(current, innerStart, close)) continue;
            var (decoded, rawOffsets) = DecodeQuotedSpanWithOffsets(current, innerStart, close);
            if (!decoded.Contains('.')) continue;
            var hasVaultOrSep = decoded.IndexOfAny(new[] { '\u001E', '\u001F', '/', '\\', '\r', '\n', '"' }) >= 0;
            var followedByAt = close + 1 < current.Length && EmailAtLength(current, close + 1) > 0;
            if (!hasVaultOrSep && !followedByAt && EndsWithFilenameExt(decoded, 0, decoded.Length))
            {
                sb ??= new StringBuilder(current.Length);
                sb.Append(current, copied, innerStart - copied).Append("[redacted-path]");
                copied = close;
                continue;
            }
            var match = BareFilenameRegex.Match(decoded);
            while (match.Success)
            {
                var g2 = match.Groups[2];
                var rawPrefixEnd = rawOffsets[g2.Index + g2.Length];
                var rawEnd = rawOffsets[match.Index + match.Length];
                sb ??= new StringBuilder(current.Length);
                sb.Append(current, copied, rawPrefixEnd - copied).Append("[redacted-path]");
                copied = rawEnd;
                match = match.NextMatch();
            }
        }
        if (sb == null) return current;
        return sb.Append(current, copied, current.Length - copied).ToString();
    }

    private static string ApplyMaskedBareFilenameRegex(string text, string masked)
    {
        var match = BareFilenameRegex.Match(masked);
        if (!match.Success) return text;
        var sb = new StringBuilder(text.Length);
        var copied = 0;
        while (match.Success)
        {
            var g2 = match.Groups[2];
            var prefixEnd = g2.Index + g2.Length;
            var end = match.Index + match.Length;
            sb.Append(text, copied, prefixEnd - copied).Append("[redacted-path]");
            copied = end;
            match = match.NextMatch();
        }
        return sb.Append(text, copied, text.Length - copied).ToString();
    }

    private static int FindDoubleQuotedPathEnd(string text, int start, out bool hasVault, out int stopPos)
    {
        hasVault = false;
        var escaped = false;
        var j = start;
        while (j < text.Length)
        {
            var ch = text[j];
            if (ch is '\r' or '\n')
            {
                stopPos = j;
                return -1;
            }
            if (ch == '\u001E')
            {
                hasVault = true;
            }
            if (ch == '\u001F')
            {
                escaped = false;
                j++;
                continue;
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

    private static int DecodeJsonCharAt(string text, int pos, int end, out char decoded)
    {
        var ch = text[pos];
        if (ch == '\\' && pos + 1 < end)
        {
            var next = text[pos + 1];
            switch (next)
            {
                case '"': decoded = '"'; return 2;
                case '/': decoded = '/'; return 2;
                case '\\': decoded = '\\'; return 2;
                case 'b': decoded = '\b'; return 2;
                case 'f': decoded = '\f'; return 2;
                case 'n': decoded = '\n'; return 2;
                case 'r': decoded = '\r'; return 2;
                case 't': decoded = '\t'; return 2;
                case 'u':
                    if (pos + 5 < end && TryParseHex4(text, pos + 2, out var code))
                    {
                        decoded = (char)code;
                        return 6;
                    }
                    break;
            }
        }
        decoded = ch;
        return 1;
    }

    private static int FindLocalPathStartInQuoted(string text, int start, int end)
    {
        if (IsExactProtocolVersion(text, start, end)) return -1;
        var tokenStart = -1;
        var p = start;
        while (p < end)
        {
            var len0 = DecodeJsonCharAt(text, p, end, out var c0);
            if (tokenStart < 0 && ((c0 >= 'A' && c0 <= 'Z') || (c0 >= 'a' && c0 <= 'z')) && p + len0 < end)
            {
                var p1 = p + len0;
                var len1 = DecodeJsonCharAt(text, p1, end, out var c1);
                if (c1 == ':' && p1 + len1 < end)
                {
                    var p2 = p1 + len1;
                    var len2 = DecodeJsonCharAt(text, p2, end, out var c2);
                    if (c2 is '/' or '\\')
                    {
                        var next = NextNonHorizontalSpaceDecoded(text, p2 + len2, end, out var nextCh);
                        if (next < end && !IsDecodedWhitespace(nextCh)) return p;
                        tokenStart = -1;
                        p = next;
                        continue;
                    }
                }
                if (c0 is 'f' or 'F' or 'c' or 'C' && MatchDecodedFileScheme(text, p, end)) return p;
            }
            if (c0 == '/' || (c0 == '\\' && (tokenStart >= 0 || len0 > 1)))
            {
                if (tokenStart >= 0)
                {
                    var next = NextNonHorizontalSpaceDecoded(text, p + len0, end, out var nextCh);
                    if (next < end && !IsDecodedWhitespace(nextCh)) return tokenStart;
                    tokenStart = -1;
                    p = next;
                    continue;
                }
                var nextRoot = ScanCharRunEnd(text, p + len0, end, c0, out var rootCh);
                if (nextRoot < end && !IsDecodedWhitespace(rootCh)) return p;
                p = nextRoot;
                continue;
            }
            if (tokenStart < 0 && c0 == '~' && p + len0 < end)
            {
                var p1 = p + len0;
                var len1 = DecodeJsonCharAt(text, p1, end, out var c1);
                if (c1 == '/')
                {
                    var next = NextNonHorizontalSpaceDecoded(text, p1 + len1, end, out var nextCh);
                    if (next < end && !IsDecodedWhitespace(nextCh)) return p;
                    p = next;
                    continue;
                }
            }
            if (IsDecodedWhitespace(c0) || IsQuotedPathDelimiter(c0))
            {
                tokenStart = -1;
            }
            else if (tokenStart < 0)
            {
                tokenStart = p;
            }
            p += len0;
        }
        return -1;
    }

    private static bool IsExactProtocolVersion(string text, int start, int end)
    {
        var p = DecodeProtocolPrefix(text, start, end);
        if (p < 0 || p >= end) return false;
        var slashLen = DecodeJsonCharAt(text, p, end, out var slashCh);
        if (slashCh != '/') return false;
        p += slashLen;
        var hasDigit = false;
        var expectDigit = true;
        while (p < end)
        {
            var len = DecodeJsonCharAt(text, p, end, out var ch);
            if (ch >= '0' && ch <= '9')
            {
                hasDigit = true;
                expectDigit = false;
            }
            else if (ch == '.' && !expectDigit)
            {
                expectDigit = true;
            }
            else
            {
                return false;
            }
            p += len;
        }
        return hasDigit && !expectDigit;
    }

    private static int DecodeProtocolPrefix(string text, int start, int end)
    {
        var p = start;
        if (p >= end) return -1;
        p += DecodeJsonCharAt(text, p, end, out var c0);
        if (p >= end) return -1;
        p += DecodeJsonCharAt(text, p, end, out var c1);
        if (p >= end) return -1;
        p += DecodeJsonCharAt(text, p, end, out var c2);
        c0 = char.ToLowerInvariant(c0);
        c1 = char.ToLowerInvariant(c1);
        c2 = char.ToLowerInvariant(c2);
        if ((c0 == 't' && c1 == 'l' && c2 == 's') || (c0 == 's' && c1 == 's' && c2 == 'l')) return p;
        if (c0 == 'h' && c1 == 't' && c2 == 't' && p < end)
        {
            p += DecodeJsonCharAt(text, p, end, out var c3);
            if (char.ToLowerInvariant(c3) == 'p') return p;
        }
        return -1;
    }

    private static bool MatchDecodedFileScheme(string text, int pos, int end) =>
        MatchDecodedSchemePrefix(text, pos, end, "file:/") || MatchDecodedSchemePrefix(text, pos, end, "content://");

    private static bool MatchDecodedSchemePrefix(string text, int pos, int end, string target)
    {
        var p = pos;
        for (var i = 0; i < target.Length; i++)
        {
            if (p >= end) return false;
            var len = DecodeJsonCharAt(text, p, end, out var c);
            if (char.ToLowerInvariant(c) != target[i]) return false;
            p += len;
        }
        return true;
    }

    private static int ScanCharRunEnd(string text, int pos, int end, char runChar, out char stopChar)
    {
        var next = pos;
        stopChar = '\0';
        while (next < end)
        {
            var len = DecodeJsonCharAt(text, next, end, out var c);
            if (c == runChar || c is ' ' or '\t')
            {
                next += len;
            }
            else
            {
                stopChar = c;
                break;
            }
        }
        return next;
    }

    private static int NextNonHorizontalSpaceDecoded(string text, int pos, int end, out char stopChar)
    {
        var p = pos;
        stopChar = '\0';
        while (p < end)
        {
            var len = DecodeJsonCharAt(text, p, end, out var c);
            if (c is ' ' or '\t')
            {
                p += len;
            }
            else
            {
                stopChar = c;
                break;
            }
        }
        return p;
    }

    private static bool IsDecodedWhitespace(char ch) => char.IsWhiteSpace(ch) || ch == '\b';

    private static bool IsQuotedPathDelimiter(char ch) =>
        ch is '"' or '\'' or '<' or '>' or '(' or ')' or '[' or ']' or '{' or '}' or ':' or ',' or ';' or '=';

    private static string RedactQuotedFilenames(string text)
    {
        if (!text.Contains('.') && !text.Contains("\\u", StringComparison.Ordinal)) return text;
        var result = text.Contains('"') ? RedactDoubleQuotedFilenamesRespectingQuotes(text) : text;
        return result.Contains('\'') || result.Contains("\\u", StringComparison.Ordinal)
            ? RedactSingleQuotedFilenamesRespectingQuotes(result)
            : result;
    }

    private static string RedactDoubleQuotedFilenamesRespectingQuotes(string text)
    {
        var spans = CollectBalancedDoubleQuoteSpans(text);
        if (spans.Count == 0) return RedactDoubleQuotedFilenamesOnSource(text, text);
        var masked = BuildMaskedDoubleQuotes(text, spans);
        var current = RedactDoubleQuotedFilenamesOnSource(text, masked);
        if (!ReferenceEquals(current, text))
        {
            if (current.IndexOf('"') < 0) return current;
            spans = CollectBalancedDoubleQuoteSpans(current);
        }
        StringBuilder? sb = null;
        var copied = 0;
        foreach (var (open, close) in spans)
        {
            var innerStart = open + 1;
            if (innerStart >= close || !ContainsDotOrUnicodeEscape(current, innerStart, close)) continue;
            var (decoded, rawOffsets) = DecodeQuotedSpanWithOffsets(current, innerStart, close);
            if (!decoded.Contains('.')) continue;
            var hasSegStop = decoded.IndexOfAny(new[] { '\r', '\n', '/', '\\', '\u001E', '\u001F' }) >= 0;
            var followedByAt = close + 1 < current.Length && EmailAtLength(current, close + 1) > 0;
            if (!hasSegStop && !followedByAt && EndsWithFilenameExt(decoded, 0, decoded.Length))
            {
                sb ??= new StringBuilder(current.Length);
                sb.Append(current, copied, innerStart - copied).Append("[redacted-path]");
                copied = close;
                continue;
            }
            if (decoded.Contains('"'))
            {
                ApplyDoubleQuotedSpans(current, decoded, rawOffsets, ref sb, ref copied);
            }
        }
        if (sb == null) return current;
        return sb.Append(current, copied, current.Length - copied).ToString();
    }

    private static string RedactDoubleQuotedFilenamesOnSource(string text, string scanSource)
    {
        if (!scanSource.Contains('.')) return text;
        StringBuilder? sb = null;
        var copied = 0;
        var i = 0;
        while (i < scanSource.Length)
        {
            if (scanSource[i] != '"') { i++; continue; }
            var j = i + 1;
            while (j < scanSource.Length && scanSource[j] is not ('"' or '\r' or '\n' or '/' or '\\' or '\u001E' or '\u001F')) j++;
            if (j < scanSource.Length && scanSource[j] == '"' &&
                !(j + 1 < scanSource.Length && EmailAtLength(scanSource, j + 1) > 0) &&
                EndsWithFilenameExt(scanSource, i + 1, j))
            {
                sb ??= new StringBuilder(text.Length);
                sb.Append(text, copied, i - copied).Append("\"[redacted-path]\"");
                copied = j + 1;
                i = j + 1;
            }
            else
            {
                i = j < scanSource.Length && scanSource[j] == '"' ? j : j + 1;
            }
        }
        if (sb == null) return text;
        return sb.Append(text, copied, text.Length - copied).ToString();
    }

    private static void ApplyDoubleQuotedSpans(
        string text, string decoded, int[] rawOffsets, ref StringBuilder? sb, ref int copied)
    {
        var i = 0;
        while (i < decoded.Length)
        {
            if (decoded[i] != '"') { i++; continue; }
            var j = i + 1;
            while (j < decoded.Length && decoded[j] is not ('"' or '\r' or '\n' or '/' or '\\' or '\u001E' or '\u001F')) j++;
            if (j < decoded.Length && decoded[j] == '"' &&
                !(j + 1 < decoded.Length && EmailAtLength(decoded, j + 1) > 0) &&
                EndsWithFilenameExt(decoded, i + 1, j))
            {
                var rawInnerStart = rawOffsets[i + 1];
                var rawInnerEnd = rawOffsets[j];
                sb ??= new StringBuilder(text.Length);
                sb.Append(text, copied, rawInnerStart - copied).Append("[redacted-path]");
                copied = rawInnerEnd;
                i = j + 1;
            }
            else
            {
                i = j < decoded.Length && decoded[j] == '"' ? j : j + 1;
            }
        }
    }

    private static string RedactSingleQuotedFilenamesRespectingQuotes(string text)
    {
        if (text.IndexOf('"') < 0) return RedactSingleQuotedFilenamesOnSource(text, text);
        var spans = CollectBalancedDoubleQuoteSpans(text);
        if (spans.Count == 0) return RedactSingleQuotedFilenamesOnSource(text, text);
        var masked = BuildMaskedDoubleQuotes(text, spans);
        var current = RedactSingleQuotedFilenamesOnSource(text, masked);
        if (!ReferenceEquals(current, text))
        {
            if (current.IndexOf('"') < 0) return current;
            spans = CollectBalancedDoubleQuoteSpans(current);
        }
        StringBuilder? sb = null;
        var copied = 0;
        foreach (var (open, close) in spans)
        {
            var innerStart = open + 1;
            if (innerStart >= close || !ContainsSingleQuoteOrUnicodeEscape(current, innerStart, close) ||
                !ContainsDotOrUnicodeEscape(current, innerStart, close))
            {
                continue;
            }
            var (decoded, rawOffsets) = DecodeQuotedSpanWithOffsets(current, innerStart, close);
            if (!decoded.Contains('\'') || !decoded.Contains('.')) continue;
            ApplySingleQuotedSpans(current, decoded, rawOffsets, ref sb, ref copied);
        }
        if (sb == null) return current;
        return sb.Append(current, copied, current.Length - copied).ToString();
    }

    private static string RedactSingleQuotedFilenamesOnSource(string text, string scanSource)
    {
        var hasAt = HasEmailAt(scanSource);
        StringBuilder? sb = null;
        var copied = 0;
        var segStart = 0;
        while (segStart < scanSource.Length)
        {
            var segEnd = segStart;
            while (segEnd < scanSource.Length && scanSource[segEnd] is not ('\r' or '\n' or '/' or '\\' or '\u001E' or '\u001F')) segEnd++;
            var open = -1;
            var candidateClose = -1;
            for (var p = segStart; p < segEnd; p++)
            {
                if (p < copied || scanSource[p] != '\'') continue;
                var isOpener = p == segStart || !char.IsLetterOrDigit(scanSource[p - 1]);
                if (isOpener)
                {
                    if (open < 0 || candidateClose >= 0)
                    {
                        open = p;
                        candidateClose = -1;
                    }
                }
                else if (open >= 0 && (p + 1 == segEnd || !char.IsLetterOrDigit(scanSource[p + 1])))
                {
                    if (EndsWithFilenameExt(scanSource, open + 1, p))
                    {
                        var emailEnd = hasAt ? FindEmailTailEnd(scanSource, p + 1) : -1;
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

    private static void ApplySingleQuotedSpans(
        string text, string decoded, int[] rawOffsets, ref StringBuilder? sb, ref int copied)
    {
        var hasAt = HasEmailAt(decoded);
        var decCopied = 0;
        var segStart = 0;
        while (segStart < decoded.Length)
        {
            var segEnd = segStart;
            while (segEnd < decoded.Length && decoded[segEnd] is not ('\r' or '\n' or '/' or '\\' or '\u001E' or '\u001F')) segEnd++;
            var open = -1;
            var candidateClose = -1;
            for (var p = segStart; p < segEnd; p++)
            {
                if (p < decCopied || decoded[p] != '\'') continue;
                var isOpener = p == segStart || !char.IsLetterOrDigit(decoded[p - 1]);
                if (isOpener)
                {
                    if (open < 0 || candidateClose >= 0)
                    {
                        open = p;
                        candidateClose = -1;
                    }
                }
                else if (open >= 0 && (p + 1 == segEnd || !char.IsLetterOrDigit(decoded[p + 1])))
                {
                    if (EndsWithFilenameExt(decoded, open + 1, p))
                    {
                        var emailEnd = hasAt ? FindEmailTailEnd(decoded, p + 1) : -1;
                        var endPos = emailEnd > p + 1 ? emailEnd : p + 1;
                        sb ??= new StringBuilder(text.Length);
                        sb.Append(text, copied, rawOffsets[open] - copied).Append("[redacted-path]");
                        copied = rawOffsets[endPos];
                        decCopied = endPos;
                        open = -1;
                        candidateClose = -1;
                    }
                    else
                    {
                        candidateClose = p;
                    }
                }
            }
            segStart = Math.Max(segEnd + 1, decCopied);
        }
    }

    private static bool EndsWithFilenameExt(string text, int start, int end)
    {
        var extStart = end;
        while (extStart > start && end - extStart < 17 && IsAsciiAlphaNum(text[extStart - 1])) extStart--;
        var extLen = end - extStart;
        return extLen >= 1 && extLen <= 16 && extStart > start && text[extStart - 1] == '.';
    }

    private static string RedactKeywordUnquotedFilenamesRespectingQuotes(string text)
    {
        if (!text.Contains('.') && !text.Contains("\\u", StringComparison.Ordinal)) return text;
        if (text.IndexOf('"') < 0) return RedactKeywordUnquotedFilenamesOnSource(text, text);
        var spans = CollectBalancedDoubleQuoteSpans(text);
        if (spans.Count == 0) return RedactKeywordUnquotedFilenamesOnSource(text, text);
        var masked = BuildMaskedDoubleQuotes(text, spans);
        var current = RedactKeywordUnquotedFilenamesOnSource(text, masked, stopAtDoubleQuote: true);
        if (!ReferenceEquals(current, text))
        {
            if (current.IndexOf('"') < 0) return current;
            spans = CollectBalancedDoubleQuoteSpans(current);
        }
        StringBuilder? sb = null;
        var copied = 0;
        foreach (var (open, close) in spans)
        {
            var innerStart = open + 1;
            if (innerStart >= close || !ContainsDotOrUnicodeEscape(current, innerStart, close)) continue;
            var (decoded, rawOffsets) = DecodeQuotedSpanWithOffsets(current, innerStart, close);
            if (!decoded.Contains('.')) continue;
            ApplyKeywordUnquotedSpans(current, decoded, rawOffsets, ref sb, ref copied);
        }
        if (sb == null) return current;
        return sb.Append(current, copied, current.Length - copied).ToString();
    }

    private static string RedactKeywordUnquotedFilenamesOnSource(
        string text, string scanSource, bool stopAtDoubleQuote = false)
    {
        if (!scanSource.Contains('.')) return text;
        var hasAt = HasEmailAt(scanSource);
        StringBuilder? sb = null;
        var copied = 0;
        var segStart = 0;
        while (segStart < scanSource.Length)
        {
            var segEnd = segStart;
            while (segEnd < scanSource.Length &&
                   scanSource[segEnd] is not ('\r' or '\n' or '\u001E' or '\u001F') &&
                   (!stopAtDoubleQuote || scanSource[segEnd] != '"'))
            {
                segEnd++;
            }
            var (lastDot, lastExtEnd) = FindLastUnquotedFilenameExt(scanSource, segStart, segEnd);
            if (lastDot >= 0)
            {
                var kwMatch = KeywordFilenamePrefixRegex.Match(scanSource, segStart, lastDot - segStart);
                if (kwMatch.Success)
                {
                    var kwEnd = kwMatch.Index + kwMatch.Length;
                    var emailEnd = hasAt ? FindEmailTailEnd(scanSource, lastExtEnd) : -1;
                    if (emailEnd > 0 && !ContainsWhitespace(scanSource, kwEnd, lastDot))
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

    private static void ApplyKeywordUnquotedSpans(
        string text, string decoded, int[] rawOffsets, ref StringBuilder? sb, ref int copied)
    {
        var hasAt = HasEmailAt(decoded);
        var decCopied = 0;
        var segStart = 0;
        while (segStart < decoded.Length)
        {
            var segEnd = segStart;
            while (segEnd < decoded.Length && decoded[segEnd] is not ('\r' or '\n' or '\u001E' or '\u001F')) segEnd++;
            var (lastDot, lastExtEnd) = FindLastUnquotedFilenameExt(decoded, segStart, segEnd);
            if (lastDot >= 0)
            {
                var kwMatch = KeywordFilenamePrefixRegex.Match(decoded, segStart, lastDot - segStart);
                if (kwMatch.Success)
                {
                    var kwEnd = kwMatch.Index + kwMatch.Length;
                    var emailEnd = hasAt ? FindEmailTailEnd(decoded, lastExtEnd) : -1;
                    if (emailEnd > 0 && !ContainsWhitespace(decoded, kwEnd, lastDot))
                    {
                        segStart = segEnd + 1;
                        continue;
                    }
                    var endPos = emailEnd > lastExtEnd ? emailEnd : lastExtEnd;
                    sb ??= new StringBuilder(text.Length);
                    sb.Append(text, copied, rawOffsets[kwEnd] - copied).Append("[redacted-path]");
                    copied = rawOffsets[endPos];
                    decCopied = endPos;
                }
            }
            segStart = Math.Max(segEnd + 1, decCopied);
        }
    }

    private static string RedactAuthCredentials(string text)
    {
        var result = RedactAuthHeaders(text);
        result = RedactBearerTokens(result);
        result = RedactEscapedBearerInBalancedQuotes(result);
        result = RedactEscapedBasicInBalancedQuotes(result);
        return RedactBasicTokens(result);
    }

    private static string RedactBasicTokens(string text)
    {
        if (text.IndexOf("basic", StringComparison.OrdinalIgnoreCase) < 0) return text;
        StringBuilder? sb = null;
        var copied = 0;
        var pos = 0;
        while (pos < text.Length)
        {
            var match = BasicTokenRegex.Match(text, pos);
            if (!match.Success) break;
            var isQuoted = match.Groups[3].Success;
            var token = isQuoted ? match.Groups[3].Value : match.Groups[5].Value;
            if (!IsBasicCredentialToken(token))
            {
                pos = match.Index + 5;
                continue;
            }
            sb ??= new StringBuilder(text.Length);
            sb.Append(text, copied, match.Index - copied);
            if (isQuoted)
            {
                sb.Append(match.Groups[1].Value).Append(' ')
                    .Append(match.Groups[2].Value).Append("[redacted]").Append(match.Groups[4].Value);
            }
            else
            {
                sb.Append(match.Groups[1].Value).Append(" [redacted]");
            }
            copied = match.Index + match.Length;
            pos = copied;
        }
        return sb == null ? text : sb.Append(text, copied, text.Length - copied).ToString();
    }

    private static string RedactBearerTokens(string text)
    {
        if (text.IndexOf("bearer", StringComparison.OrdinalIgnoreCase) < 0) return text;
        var ranges = FindBearerTokenRanges(text, allowLiteralQuoted: true, requireClosingLiteralQuote: true);
        if (ranges.Count == 0) return text;
        var sb = new StringBuilder(text.Length);
        var copied = 0;
        foreach (var (start, end, redactedOnly) in ranges)
        {
            sb.Append(text, copied, start - copied);
            if (redactedOnly) sb.Append("[redacted]");
            else sb.Append(text, start, 6).Append(" [redacted]");
            copied = end;
        }
        return sb.Append(text, copied, text.Length - copied).ToString();
    }

    private static int EscapedDoubleQuoteLen(string text, int pos)
    {
        if (pos + 1 < text.Length && text[pos] == '\\' && text[pos + 1] == '"') return 2;
        if (pos + 5 < text.Length && text[pos] == '\\' && text[pos + 1] == 'u' &&
            TryParseHex4(text, pos + 2, out var code) && code == '"')
        {
            return 6;
        }
        return 0;
    }

    private static List<(int Start, int End, bool RedactedOnly)> FindBearerTokenRanges(
        string text, bool allowLiteralQuoted, bool requireClosingLiteralQuote)
    {
        var ranges = new List<(int Start, int End, bool RedactedOnly)>();
        var inEscapedQuote = false;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '\\' && i + 1 < text.Length)
            {
                var next = text[i + 1];
                if (next == '"')
                {
                    inEscapedQuote = !inEscapedQuote;
                    i += 2;
                    continue;
                }
                if (next == 'u' && i + 5 < text.Length && TryParseHex4(text, i + 2, out var code))
                {
                    if (code == '"') inEscapedQuote = !inEscapedQuote;
                    i += 6;
                    continue;
                }
                i += 2;
                continue;
            }
            if (ch is '"' or '\r' or '\n')
            {
                inEscapedQuote = false;
                i++;
                continue;
            }
            if (i + 6 >= text.Length ||
                (i > 0 && IsAsciiWordChar(text[i - 1])) ||
                !MatchesAsciiIgnoreCase(text, i, "bearer") ||
                !char.IsWhiteSpace(text[i + 6]))
            {
                i++;
                continue;
            }
            var wsEnd = i + 6;
            while (wsEnd < text.Length && char.IsWhiteSpace(text[wsEnd])) wsEnd++;
            if (wsEnd >= text.Length) break;
            if (MatchesBearerOrBasicSchemePrefix(text, wsEnd))
            {
                i = wsEnd;
                continue;
            }
            var wsCh = text[wsEnd];
            var openQuoteLen = !inEscapedQuote
                ? (allowLiteralQuoted && (wsCh is '"' or '\'') ? 1 : EscapedDoubleQuoteLen(text, wsEnd))
                : 0;
            if (openQuoteLen > 0)
            {
                var innerStart = wsEnd + openQuoteLen;
                var stopEsc = openQuoteLen > 1;
                if (IsRedactedBearerPlaceholderAt(text, innerStart, stopEsc))
                {
                    i = innerStart + 10;
                    continue;
                }
                var innerEnd = ScanBearerTokenEnd(text, innerStart, stopEsc);
                var closeQuoteLen = openQuoteLen == 1
                    ? (innerEnd < text.Length && text[innerEnd] == wsCh ? 1 : 0)
                    : EscapedDoubleQuoteLen(text, innerEnd);
                if (innerEnd > innerStart && (closeQuoteLen > 0 || (openQuoteLen == 1 && !requireClosingLiteralQuote)))
                {
                    ranges.Add((innerStart, innerEnd, true));
                    i = innerEnd + closeQuoteLen;
                    continue;
                }
                if (innerEnd == innerStart && closeQuoteLen > 0)
                {
                    i = innerEnd + closeQuoteLen;
                    continue;
                }
            }
            if (IsRedactedBearerPlaceholderAt(text, wsEnd, inEscapedQuote))
            {
                i = wsEnd + 10;
                continue;
            }
            var tokEnd = ScanBearerTokenEnd(text, wsEnd, inEscapedQuote);
            if (tokEnd > wsEnd)
            {
                ranges.Add((i, tokEnd, false));
                i = tokEnd;
            }
            else
            {
                i = wsEnd;
            }
        }
        return ranges;
    }

    private static bool MatchesBearerOrBasicSchemePrefix(string text, int pos) =>
        (pos + 6 < text.Length && MatchesAsciiIgnoreCase(text, pos, "bearer") && char.IsWhiteSpace(text[pos + 6])) ||
        (pos + 5 < text.Length && MatchesAsciiIgnoreCase(text, pos, "basic") && char.IsWhiteSpace(text[pos + 5]));

    private static bool IsBearerStopPunct(char ch) =>
        ch is '"' or '\'' or ',' or ';' or ')' or ']' or '}';

    private static bool IsBearerEscapeTerminator(string text, int pos, bool stopAtEscapedQuote)
    {
        if (text[pos] != '\\' || pos + 1 >= text.Length) return false;
        var esc = text[pos + 1];
        if (esc is 'n' or 'r' or 't' or 'b' or 'f') return true;
        if (stopAtEscapedQuote && esc == '"') return true;
        if (esc == 'u' && pos + 5 < text.Length && TryParseHex4(text, pos + 2, out var code))
        {
            var decoded = (char)code;
            return char.IsWhiteSpace(decoded) || (stopAtEscapedQuote && decoded == '"');
        }
        return false;
    }

    private static bool IsRedactedBearerPlaceholderAt(string text, int pos, bool stopAtEscapedQuote)
    {
        if (pos + 10 > text.Length || !text.AsSpan(pos, 10).Equals("[redacted]".AsSpan(), StringComparison.Ordinal))
        {
            return false;
        }
        var after = pos + 10;
        if (after >= text.Length) return true;
        var ch = text[after];
        if (char.IsWhiteSpace(ch) || IsBearerStopPunct(ch)) return true;
        return IsBearerEscapeTerminator(text, after, stopAtEscapedQuote);
    }

    private static int ScanBearerTokenEnd(string text, int start, bool stopAtEscapedQuote)
    {
        var p = start;
        while (p < text.Length)
        {
            var ch = text[p];
            if (ch == '\\')
            {
                if (p + 1 >= text.Length || char.IsWhiteSpace(text[p + 1]))
                {
                    p++;
                    break;
                }
                if (IsBearerEscapeTerminator(text, p, stopAtEscapedQuote)) break;
                var next = text[p + 1];
                p += next == 'u' && p + 5 < text.Length && TryParseHex4(text, p + 2, out _) ? 6 : 2;
                continue;
            }
            if (char.IsWhiteSpace(ch) || IsBearerStopPunct(ch)) break;
            p++;
        }
        return p;
    }

    private static string RedactEscapedBearerInBalancedQuotes(string text)
    {
        if (text.IndexOf('"') < 0 || text.IndexOf('\\') < 0) return text;
        var spans = CollectBalancedDoubleQuoteSpans(text);
        if (spans.Count == 0) return text;
        StringBuilder? sb = null;
        var copied = 0;
        foreach (var (open, close) in spans)
        {
            var innerStart = open + 1;
            if (innerStart >= close || text.IndexOf('\\', innerStart, close - innerStart) < 0) continue;
            var (decoded, rawOffsets) = DecodeQuotedSpanWithOffsets(text, innerStart, close);
            if (decoded.IndexOf("bearer", StringComparison.OrdinalIgnoreCase) < 0) continue;
            foreach (var (mStart, mEnd, redactedOnly) in FindBearerTokenRanges(
                decoded, allowLiteralQuoted: true, requireClosingLiteralQuote: false))
            {
                var rawStart = rawOffsets[mStart];
                var rawEnd = rawOffsets[mEnd];
                if (rawStart < copied) continue;
                sb ??= new StringBuilder(text.Length);
                sb.Append(text, copied, rawStart - copied);
                if (redactedOnly) sb.Append("[redacted]");
                else sb.Append(decoded, mStart, 6).Append(" [redacted]");
                copied = rawEnd;
            }
        }
        if (sb == null) return text;
        return sb.Append(text, copied, text.Length - copied).ToString();
    }

    private static string RedactEscapedBasicInBalancedQuotes(string text)
    {
        if (text.IndexOf('"') < 0 || text.IndexOf('\\') < 0) return text;
        var spans = CollectBalancedDoubleQuoteSpans(text);
        if (spans.Count == 0) return text;
        StringBuilder? sb = null;
        var copied = 0;
        foreach (var (open, close) in spans)
        {
            var innerStart = open + 1;
            if (innerStart >= close || text.IndexOf('\\', innerStart, close - innerStart) < 0) continue;
            var (decoded, rawOffsets) = DecodeQuotedSpanWithOffsets(text, innerStart, close);
            if (decoded.IndexOf("basic", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var pos = 0;
            while (pos < decoded.Length)
            {
                var match = BasicTokenRegex.Match(decoded, pos);
                if (!match.Success) break;
                var isQuoted = match.Groups[3].Success;
                var tokenGroup = isQuoted ? match.Groups[3] : match.Groups[5];
                if (!IsBasicCredentialToken(tokenGroup.Value))
                {
                    pos = match.Index + 5;
                    continue;
                }
                var targetIndex = isQuoted ? tokenGroup.Index : match.Index;
                var targetLength = isQuoted ? tokenGroup.Length : match.Length;
                var rawStart = rawOffsets[targetIndex];
                var rawEnd = rawOffsets[targetIndex + targetLength];
                if (rawStart >= copied)
                {
                    sb ??= new StringBuilder(text.Length);
                    sb.Append(text, copied, rawStart - copied);
                    if (isQuoted) sb.Append("[redacted]");
                    else sb.Append(match.Groups[1].Value).Append(" [redacted]");
                    copied = rawEnd;
                }
                pos = match.Index + match.Length;
            }
        }
        if (sb == null) return text;
        return sb.Append(text, copied, text.Length - copied).ToString();
    }

    private sealed class AuthHeaderWriter
    {
        private readonly string _text;
        private readonly List<(int Start, int End, string Replacement)>? _edits;
        private StringBuilder? _sb;
        public int Copied;

        public AuthHeaderWriter(string text, List<(int Start, int End, string Replacement)>? edits = null)
        {
            _text = text;
            _edits = edits;
        }

        public void AppendRedacted(int start, int end, string replacement)
        {
            if (_edits != null)
            {
                _edits.Add((start, end, replacement));
                Copied = end;
                return;
            }
            _sb ??= new StringBuilder(_text.Length);
            _sb.Append(_text, Copied, start - Copied).Append(replacement);
            Copied = end;
        }

        public string Finish()
        {
            if (_sb == null) return _text;
            _sb.Append(_text, Copied, _text.Length - Copied);
            return _sb.ToString();
        }
    }

    private static string EscapeJsonStringContent(string text)
    {
        StringBuilder? sb = null;
        var copied = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is '"' or '\\' || ch < 0x20)
            {
                sb ??= new StringBuilder(text.Length + 8);
                sb.Append(text, copied, i - copied);
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append($"\\u{(int)ch:x4}"); break;
                }
                copied = i + 1;
            }
        }
        return sb == null ? text : sb.Append(text, copied, text.Length - copied).ToString();
    }

    private static bool ContainsAuthHeaderCandidate(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            var ch = text[i];
            if (ch is ':' or '=' or '[') return true;
            if (ch == '\\' && i + 5 < end && text[i + 1] == 'u') return true;
        }
        return false;
    }

    private sealed class AuthJsonScanCache
    {
        public Dictionary<int, int>? ValidJsonValueEnd;
        public Dictionary<int, int>? CloseToOpenMap;
        public Dictionary<int, int>? MemberCommaOpen;
        public Dictionary<int, int>? ArrayCommaOpen;
        /// <summary>0 = valid top-level JSON container; -1 = malformed/unclosed; >0 = valid container end with trailing text on closing line.</summary>
        public Dictionary<int, int>? ContainerAuthState;
        public Dictionary<int, int>? EscapedContainerEnd;
        public int FailedContainerBack = -1;
    }

    private static bool MayContainAuthHeader(string text, int maxDepth) =>
        text.IndexOf(':') >= 0 ||
        text.IndexOf('=') >= 0 ||
        (text.IndexOf('[') >= 0 && text.IndexOf(',') >= 0) ||
        text.Contains("\\u", StringComparison.Ordinal);

    private static bool TryMatchAuthSeparator(string text, int pos, out int sepLen, out bool isColon)
    {
        sepLen = 0;
        isColon = false;
        if (pos >= text.Length) return false;
        var ch = text[pos];
        if (ch is ':' or '=')
        {
            sepLen = 1;
            isColon = ch == ':';
            return true;
        }
        if (ch == '\\' && pos + 5 < text.Length && text[pos + 1] == 'u' &&
            !IsPrecededByOddBackslashes(text, pos, 0) &&
            TryParseHex4(text, pos + 2, out var code) && code is ':' or '=')
        {
            sepLen = 6;
            isColon = code == ':';
            return true;
        }
        return false;
    }

    private static string RedactAuthHeaders(string text, int maxDepth = 4)
    {
        if (!MayContainAuthHeader(text, maxDepth)) return text;
        var writer = new AuthHeaderWriter(text);
        ScanAuthHeaders(text, maxDepth, writer);
        return writer.Finish();
    }

    private static void ScanAuthHeaders(string text, int maxDepth, AuthHeaderWriter writer)
    {
        if (!MayContainAuthHeader(text, maxDepth)) return;
        var cache = new AuthJsonScanCache();
        var allowTrailingProse = maxDepth < 4;
        var i = 0;
        while (i < text.Length)
        {
            if (!TryMatchAuthHeaderKey(
                text, i, allowTrailingProse, cache, out var keyEnd, out var isJsonCandidate, out var hasJsonPrefix, out var isTupleCandidate, out var failedTopEnd, out var normalizedKey))
            {
                if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '"' &&
                    !IsPrecededByOddBackslashes(text, i, writer.Copied))
                {
                    var escEnd = TryRedactEscapedQuotedAuthHeader(text, i, cache, writer);
                    if (escEnd > i)
                    {
                        i = escEnd;
                        continue;
                    }
                }
                if (maxDepth > 0 && text[i] == '"' && !IsPrecededByOddBackslashes(text, i, writer.Copied))
                {
                    var close = FindDoubleQuotedPathEnd(text, i + 1, out _, out _);
                    if (close > i)
                    {
                        var changed = RedactAuthHeadersInBalancedSpan(text, i + 1, close, maxDepth - 1, writer);
                        if (!changed && !IsJsonStringStartBoundary(text, i) && IsJsonStringStartBoundary(text, close))
                        {
                            i = close;
                            continue;
                        }
                        i = close + 1;
                        continue;
                    }
                }
                i++;
                continue;
            }
            var sep = SkipAuthWhitespace(text, keyEnd, isJsonCandidate || hasJsonPrefix || isTupleCandidate);
            if (!TryMatchAuthSeparator(text, sep, out var sepLen, out var isColon))
            {
                if (isTupleCandidate && sep < text.Length && text[sep] == ',' &&
                    TryResolveJsonTupleValueStart(text, sep + 1, cache, out var tupleValStart))
                {
                    if (normalizedKey != null)
                    {
                        writer.AppendRedacted(i, keyEnd, text[i] + normalizedKey + text[i]);
                    }
                    i = RedactAuthHeaderValue(text, tupleValStart, isJsonMember: true, allowTruncatedObject: false, allowTrailingProse, -1, cache, writer);
                    continue;
                }
                i = keyEnd;
                continue;
            }
            var isJsonMember = isJsonCandidate && isColon && sepLen == 1;
            var allowTruncatedObject = hasJsonPrefix && isColon && sepLen == 1;
            var containerTrailingEnd = isColon && sepLen == 1 ? failedTopEnd : -1;
            var valStart = SkipAuthWhitespace(text, sep + sepLen, isJsonMember || allowTruncatedObject);
            if (valStart >= text.Length || text[valStart] is '\r' or '\n')
            {
                i = sep + sepLen;
                continue;
            }
            if (normalizedKey != null)
            {
                writer.AppendRedacted(i, keyEnd, text[i] + normalizedKey + text[i]);
            }
            i = RedactAuthHeaderValue(text, valStart, isJsonMember, allowTruncatedObject, allowTrailingProse, containerTrailingEnd, cache, writer);
        }
    }

    private static bool TryResolveJsonTupleValueStart(string text, int afterComma, AuthJsonScanCache cache, out int valStart)
    {
        valStart = SkipAuthWhitespace(text, afterComma, allowNewlines: true);
        if (valStart >= text.Length) return false;
        var valEnd = TryParseJsonValue(text, valStart, ref cache.ValidJsonValueEnd);
        if (valEnd <= valStart) return false;
        var afterVal = SkipAuthWhitespace(text, valEnd, allowNewlines: true);
        return afterVal < text.Length && text[afterVal] == ']';
    }

    private static bool TryAppendTrimmedKeyChar(
        char ch, Span<char> core, ref int coreLen, ref bool sawTrailingPad, ref bool sawPad)
    {
        if (ch is ' ' or '\t')
        {
            sawPad = true;
            if (coreLen > 0) sawTrailingPad = true;
            return true;
        }
        if (sawTrailingPad || coreLen >= 19) return false;
        core[coreLen++] = ch;
        return true;
    }

    private static int SkipEscapedAuthWhitespace(string text, int start, bool allowNewlines)
    {
        var p = start;
        while (p < text.Length)
        {
            var ch = text[p];
            if (ch is ' ' or '\t' || (allowNewlines && ch is '\r' or '\n'))
            {
                p++;
                continue;
            }
            if (ch == '\\' && p + 1 < text.Length)
            {
                var next = text[p + 1];
                if (next == 't' || (allowNewlines && next is 'n' or 'r'))
                {
                    p += 2;
                    continue;
                }
            }
            break;
        }
        return p;
    }

    private static int SkipEscapedWhitespaceBackward(string text, int start)
    {
        var p = start;
        while (p >= 0)
        {
            var ch = text[p];
            if (ch is ' ' or '\t' or '\r' or '\n')
            {
                p--;
                continue;
            }
            if (p >= 1 && text[p - 1] == '\\' && ch is 't' or 'n' or 'r' &&
                !IsPrecededByOddBackslashes(text, p - 1, 0))
            {
                p -= 2;
                continue;
            }
            break;
        }
        return p;
    }

    private static bool IsEscapedWhitespaceTokenAt(string text, int pos) =>
        pos + 1 < text.Length && text[pos] == '\\' && text[pos + 1] is 't' or 'n' or 'r';

    private static int TryRedactEscapedQuotedAuthHeader(string text, int pos, AuthJsonScanCache cache, AuthHeaderWriter writer)
    {
        Span<char> core = stackalloc char[20];
        var coreLen = 0;
        var sawTrailingPad = false;
        var sawPad = false;
        var j = pos + 2;
        var keyEnd = -1;
        while (j < text.Length)
        {
            var c = text[j];
            if ((c < 0x20 && c != '\t') || c == '"') return -1;
            if (c == '\\' && j + 1 < text.Length)
            {
                var next = text[j + 1];
                if (next == '"')
                {
                    keyEnd = j + 2;
                    break;
                }
                if (next == 'u' && j + 5 < text.Length && TryParseHex4(text, j + 2, out var codeUnit))
                {
                    if (!TryAppendTrimmedKeyChar((char)codeUnit, core, ref coreLen, ref sawTrailingPad, ref sawPad)) return -1;
                    j += 6;
                    continue;
                }
                if (!TryAppendTrimmedKeyChar(DecodeSimpleJsonEscape(next), core, ref coreLen, ref sawTrailingPad, ref sawPad)) return -1;
                j += 2;
                continue;
            }
            if (!TryAppendTrimmedKeyChar(c, core, ref coreLen, ref sawTrailingPad, ref sawPad)) return -1;
            j++;
        }
        if (keyEnd < 0 || MatchCanonicalAuthHeaderName(core[..coreLen]) == null) return -1;
        var prevDelim = SkipEscapedWhitespaceBackward(text, pos - 1);
        var isEscapedJsonCtx = prevDelim >= 0 && text[prevDelim] is '{' or ',' or '[';
        var sep = SkipEscapedAuthWhitespace(text, keyEnd, allowNewlines: isEscapedJsonCtx);
        var isTuple = false;
        int sepLen;
        if (TryMatchAuthSeparator(text, sep, out sepLen, out _))
        {
            // matched ':' / '=' / '\u003a' / '\u003d'
        }
        else if (prevDelim >= 0 && text[prevDelim] == '[' && sep < text.Length && text[sep] == ',')
        {
            isTuple = true;
            sepLen = 1;
        }
        else
        {
            return -1;
        }
        var valStart = SkipEscapedAuthWhitespace(text, sep + sepLen, allowNewlines: isEscapedJsonCtx);
        if (valStart >= text.Length || text[valStart] is '\r' or '\n') return -1;
        if (valStart + 1 < text.Length && text[valStart] == '\\' && text[valStart + 1] == '"')
        {
            var closeVal = ScanEscapedQuotedAuthValueEnd(text, valStart + 2);
            var afterClose = closeVal + 1 < text.Length && text[closeVal] == '\\' && text[closeVal + 1] == '"'
                ? closeVal + 2
                : closeVal;
            if (isTuple)
            {
                var afterVal = SkipEscapedAuthWhitespace(text, afterClose, allowNewlines: true);
                if (afterVal >= text.Length || text[afterVal] != ']') return -1;
            }
            var redacted = RedactAuthValueBody(text, valStart + 2, closeVal);
            if (redacted != null)
            {
                writer.AppendRedacted(valStart + 2, closeVal, redacted);
            }
            return afterClose;
        }
        if (text[valStart] == '[')
        {
            if (isTuple)
            {
                var arrEnd = ScanEscapedJsonContainerEnd(text, valStart, cache);
                var afterArr = arrEnd > valStart ? SkipEscapedAuthWhitespace(text, arrEnd, allowNewlines: true) : -1;
                if (afterArr < 0 || afterArr >= text.Length || text[afterArr] != ']') return -1;
            }
            return RedactEscapedAuthArrayValue(text, valStart + 1, cache, writer);
        }
        if (text[valStart] == '{')
        {
            var objEnd = ScanEscapedJsonContainerEnd(text, valStart, cache);
            if (objEnd > valStart)
            {
                if (isTuple)
                {
                    var afterObj = SkipEscapedAuthWhitespace(text, objEnd, allowNewlines: true);
                    if (afterObj >= text.Length || text[afterObj] != ']') return -1;
                }
                writer.AppendRedacted(valStart, objEnd, "\\\"[redacted]\\\"");
                return objEnd;
            }
        }
        if (TryRedactEscapedJsonPrimitive(text, valStart, isTuple, writer, out var primEnd))
        {
            return primEnd;
        }
        if (!isTuple)
        {
            return RedactPlainAuthSpan(text, valStart, -1, writer);
        }
        return -1;
    }

    private static char DecodeSimpleJsonEscape(char next) => next switch
    {
        't' => '\t',
        'n' => '\n',
        'r' => '\r',
        'b' => '\b',
        'f' => '\f',
        _ => next,
    };

    private static bool TryRedactEscapedJsonPrimitive(
        string text, int valStart, bool isTuple, AuthHeaderWriter writer, out int endPos)
    {
        endPos = valStart;
        var tokEnd = valStart;
        while (tokEnd < text.Length &&
            text[tokEnd] is not (' ' or '\t' or '\r' or '\n' or ',' or '}' or ']' or '"') &&
            !IsEscapedWhitespaceTokenAt(text, tokEnd))
        {
            tokEnd++;
        }
        if (tokEnd == valStart) return false;
        var span = text.AsSpan(valStart, tokEnd - valStart);
        var isNull = span.Equals("null".AsSpan(), StringComparison.Ordinal);
        var isBool = span.Equals("true".AsSpan(), StringComparison.Ordinal) ||
            span.Equals("false".AsSpan(), StringComparison.Ordinal);
        if (!isNull && !isBool && !IsJsonNumberSpan(span)) return false;
        var after = SkipEscapedAuthWhitespace(text, tokEnd, allowNewlines: true);
        if (isTuple)
        {
            if (after >= text.Length || text[after] != ']') return false;
        }
        else if (after < text.Length && text[after] is not (',' or '}' or ']' or '\r' or '\n'))
        {
            return false;
        }
        if (!isNull) writer.AppendRedacted(valStart, tokEnd, "\\\"[redacted]\\\"");
        endPos = tokEnd;
        return true;
    }

    private static int ScanEscapedJsonContainerEnd(string text, int start, AuthJsonScanCache cache)
    {
        cache.EscapedContainerEnd ??= new Dictionary<int, int>();
        if (cache.EscapedContainerEnd.TryGetValue(start, out var cached)) return cached;
        var stack = new Stack<(int OpenPos, char ExpectedClose)>();
        var p = start;
        while (p < text.Length)
        {
            var ch = text[p];
            if (ch == '"') break;
            if (ch == '\\' && p + 1 < text.Length && text[p + 1] == '"')
            {
                var closeVal = ScanEscapedQuotedAuthValueEnd(text, p + 2);
                if (closeVal + 1 >= text.Length || text[closeVal] != '\\' || text[closeVal + 1] != '"') break;
                p = closeVal + 2;
                continue;
            }
            if (ch is '{' or '[')
            {
                if (cache.EscapedContainerEnd.TryGetValue(p, out var known))
                {
                    if (known <= p) break;
                    p = known;
                    if (stack.Count == 0) return p;
                    continue;
                }
                stack.Push((p, ch == '{' ? '}' : ']'));
            }
            else if (ch is '}' or ']')
            {
                if (stack.Count == 0 || stack.Peek().ExpectedClose != ch) break;
                var top = stack.Pop();
                cache.EscapedContainerEnd[top.OpenPos] = p + 1;
                if (stack.Count == 0) return p + 1;
            }
            p++;
        }
        while (stack.Count > 0) cache.EscapedContainerEnd[stack.Pop().OpenPos] = -1;
        cache.EscapedContainerEnd[start] = -1;
        return -1;
    }

    private static int ScanEscapedQuotedAuthValueEnd(string text, int start)
    {
        var p = start;
        var innerEscaped = false;
        while (p < text.Length)
        {
            var ch = text[p];
            if (ch is '\r' or '\n' or '"') return p;
            if (ch == '\\' && p + 1 < text.Length)
            {
                var next = text[p + 1];
                if (next == '"' && !innerEscaped) return p;
                innerEscaped = next == '\\' ? !innerEscaped : false;
                p += 2;
                continue;
            }
            innerEscaped = false;
            p++;
        }
        return p;
    }

    private static int RedactEscapedAuthArrayValue(string text, int start, AuthJsonScanCache cache, AuthHeaderWriter writer)
    {
        var p = start;
        var depth = 1;
        while (p < text.Length && depth > 0)
        {
            var ch = text[p];
            if (ch == '"') return p;
            if (ch is ' ' or '\t' or '\r' or '\n' or ',')
            {
                p++;
                continue;
            }
            if (IsEscapedWhitespaceTokenAt(text, p))
            {
                p += 2;
                continue;
            }
            if (ch == '[')
            {
                depth++;
                p++;
                continue;
            }
            if (ch == ']')
            {
                depth--;
                p++;
                continue;
            }
            if (ch == '\\' && p + 1 < text.Length && text[p + 1] == '"')
            {
                var closeVal = ScanEscapedQuotedAuthValueEnd(text, p + 2);
                var redacted = RedactAuthValueBody(text, p + 2, closeVal);
                if (redacted != null) writer.AppendRedacted(p + 2, closeVal, redacted);
                p = closeVal + 1 < text.Length && text[closeVal] == '\\' && text[closeVal + 1] == '"'
                    ? closeVal + 2
                    : closeVal;
                continue;
            }
            if (ch == '{')
            {
                var objEnd = ScanEscapedJsonContainerEnd(text, p, cache);
                if (objEnd > p)
                {
                    writer.AppendRedacted(p, objEnd, "\\\"[redacted]\\\"");
                    p = objEnd;
                    continue;
                }
            }
            var elemEnd = p;
            while (elemEnd < text.Length &&
                text[elemEnd] is not (' ' or '\t' or '\r' or '\n' or ',' or ']' or '[' or '}' or '"') &&
                !IsEscapedWhitespaceTokenAt(text, elemEnd))
            {
                elemEnd++;
            }
            if (elemEnd > p)
            {
                if (!text.AsSpan(p, elemEnd - p).Equals("null".AsSpan(), StringComparison.Ordinal))
                {
                    writer.AppendRedacted(p, elemEnd, "\\\"[redacted]\\\"");
                }
                p = elemEnd;
                continue;
            }
            p++;
        }
        return p;
    }

    private static string? MatchCanonicalAuthHeaderName(ReadOnlySpan<char> raw)
    {
        var trimmed = raw.Trim(" \t".AsSpan());
        if (IsKnownAuthHeaderName(trimmed))
        {
            return new string(trimmed);
        }
        return null;
    }

    private static bool IsKnownAuthHeaderName(ReadOnlySpan<char> trimmed) =>
        trimmed.Equals("authorization".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
        trimmed.Equals("proxy-authorization".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
        trimmed.Equals("cookie".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
        trimmed.Equals("set-cookie".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
        trimmed.Equals("x-api-key".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
        trimmed.Equals("api-key".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
        trimmed.Equals("x-auth-token".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
        trimmed.Equals("x-access-token".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
        trimmed.Equals("x-api-token".AsSpan(), StringComparison.OrdinalIgnoreCase);

    private static bool IsJsonStringStartBoundary(string text, int open)
    {
        var prev = SkipJsonWhitespaceBackward(text, open - 1);
        return prev < 0 || text[prev] is ':' or '=' or '[' or ',' or '{' or '(';
    }

    private static bool RedactAuthHeadersInBalancedSpan(
        string text, int innerStart, int close, int nextDepth, AuthHeaderWriter writer)
    {
        if (innerStart >= close || !ContainsAuthHeaderCandidate(text, innerStart, close)) return false;
        var hasBackslash = text.IndexOf('\\', innerStart, close - innerStart) >= 0;
        string decoded;
        int[]? rawOffsets = null;
        if (hasBackslash)
        {
            (decoded, rawOffsets) = DecodeQuotedSpanWithOffsets(text, innerStart, close);
        }
        else
        {
            decoded = text.Substring(innerStart, close - innerStart);
        }
        if (decoded.IndexOf(':') < 0 && decoded.IndexOf('=') < 0 &&
            !(decoded.IndexOf('[') >= 0 && decoded.IndexOf(',') >= 0))
        {
            return false;
        }
        var edits = new List<(int Start, int End, string Replacement)>();
        ScanAuthHeaders(decoded, nextDepth, new AuthHeaderWriter(decoded, edits));
        if (edits.Count == 0) return false;
        foreach (var (eStart, eEnd, eRepl) in edits)
        {
            var rawStart = rawOffsets != null ? rawOffsets[eStart] : innerStart + eStart;
            var rawEnd = rawOffsets != null ? rawOffsets[eEnd] : innerStart + eEnd;
            writer.AppendRedacted(rawStart, rawEnd, EscapeJsonStringContent(eRepl));
        }
        return true;
    }

    private static bool TryMatchAuthHeaderKey(
        string text, int pos, bool allowTrailingProse, AuthJsonScanCache cache,
        out int keyEnd, out bool isJsonCandidate, out bool hasJsonPrefix, out bool isTupleCandidate, out int failedTopEnd, out string? normalizedKey)
    {
        keyEnd = pos;
        isJsonCandidate = false;
        hasJsonPrefix = false;
        isTupleCandidate = false;
        failedTopEnd = -1;
        normalizedKey = null;
        var ch = text[pos];
        if (ch is '"' or '\'')
        {
            return TryMatchQuotedAuthKey(
                text, pos, ch, allowTrailingProse, cache, out keyEnd, out isJsonCandidate, out hasJsonPrefix, out isTupleCandidate, out failedTopEnd, out normalizedKey);
        }
        if (pos > 0 && IsAuthKeyWordChar(text[pos - 1])) return false;
        if (MatchesUnquotedAuthKey(text, pos, "proxy-authorization", out keyEnd) ||
            MatchesUnquotedAuthKey(text, pos, "authorization", out keyEnd) ||
            MatchesUnquotedAuthKey(text, pos, "set-cookie", out keyEnd) ||
            MatchesUnquotedAuthKey(text, pos, "cookie", out keyEnd) ||
            MatchesUnquotedAuthKey(text, pos, "x-api-key", out keyEnd) ||
            MatchesUnquotedAuthKey(text, pos, "api-key", out keyEnd) ||
            MatchesUnquotedAuthKey(text, pos, "x-auth-token", out keyEnd) ||
            MatchesUnquotedAuthKey(text, pos, "x-access-token", out keyEnd) ||
            MatchesUnquotedAuthKey(text, pos, "x-api-token", out keyEnd))
        {
            return true;
        }
        return false;
    }

    private static bool MatchesUnquotedAuthKey(string text, int pos, string literal, out int keyEnd)
    {
        var end = pos + literal.Length;
        if (MatchesAsciiIgnoreCase(text, pos, literal) && (end == text.Length || !IsAuthKeyWordChar(text[end])))
        {
            keyEnd = end;
            return true;
        }
        keyEnd = pos;
        return false;
    }

    private static bool TryMatchQuotedAuthKey(
        string text, int pos, char quote, bool allowTrailingProse, AuthJsonScanCache cache,
        out int keyEnd, out bool isJsonCandidate, out bool hasJsonPrefix, out bool isTupleCandidate, out int failedTopEnd, out string? normalizedKey)
    {
        keyEnd = pos;
        isJsonCandidate = false;
        hasJsonPrefix = false;
        isTupleCandidate = false;
        failedTopEnd = -1;
        normalizedKey = null;
        Span<char> core = stackalloc char[20];
        var coreLen = 0;
        var sawTrailingPad = false;
        var sawPad = false;
        var sawEscape = false;
        var hasInvalidJsonEscape = false;
        var j = pos + 1;
        while (j < text.Length)
        {
            var c = text[j];
            if (c < 0x20 && c != '\t') return false;
            if (c == quote)
            {
                var canonical = MatchCanonicalAuthHeaderName(core[..coreLen]);
                if (canonical == null) return false;
                keyEnd = j + 1;
                if (quote == '"')
                {
                    var prevDelim = SkipJsonWhitespaceBackward(text, pos - 1);
                    hasJsonPrefix = prevDelim >= 0 && text[prevDelim] is '{' or ',';
                    isTupleCandidate = prevDelim >= 0 && text[prevDelim] == '[' && !hasInvalidJsonEscape;
                    if (!hasInvalidJsonEscape)
                    {
                        var openBrace = FindEnclosingJsonObjectStart(text, pos, cache);
                        if (openBrace >= 0)
                        {
                            var state = ClassifyTopLevelJsonContainer(text, openBrace, allowTrailingProse, cache);
                            isJsonCandidate = state == 0;
                            failedTopEnd = state > 0 ? state : -1;
                        }
                    }
                }
                normalizedKey = sawEscape && !sawPad ? canonical : null;
                return true;
            }
            if (c == '\\')
            {
                sawEscape = true;
                if (j + 5 < text.Length && text[j + 1] == 'u' && TryParseHex4(text, j + 2, out var codeUnit))
                {
                    if (!TryAppendTrimmedKeyChar((char)codeUnit, core, ref coreLen, ref sawTrailingPad, ref sawPad)) return false;
                    j += 6;
                    continue;
                }
                if (j + 1 >= text.Length) return false;
                var esc = text[j + 1];
                char decodedChar;
                if (esc is 't' or 'n' or 'r' or 'b' or 'f' or '"' or '\\' or '/')
                {
                    decodedChar = DecodeSimpleJsonEscape(esc);
                }
                else
                {
                    hasInvalidJsonEscape = true;
                    decodedChar = esc;
                }
                if (!TryAppendTrimmedKeyChar(decodedChar, core, ref coreLen, ref sawTrailingPad, ref sawPad)) return false;
                j += 2;
                continue;
            }
            if (!TryAppendTrimmedKeyChar(c, core, ref coreLen, ref sawTrailingPad, ref sawPad)) return false;
            j++;
        }
        return false;
    }

    private static int RedactAuthHeaderValue(
        string text, int valStart, bool isJsonMember, bool allowTruncatedObject, bool allowTrailingProse, int failedTopEnd,
        AuthJsonScanCache cache, AuthHeaderWriter writer)
    {
        var ch = text[valStart];
        if (ch is '"' or '\'')
        {
            var afterQuote = RedactQuotedAuthString(text, valStart, ch, writer);
            var validJsonStr = isJsonMember && ch == '"' && ScanJsonStringEnd(text, valStart + 1) == afterQuote;
            return RedactTrailingPlainAuthContinuation(text, afterQuote, validJsonStr, allowTrailingProse, failedTopEnd, writer);
        }
        if (ch == '[')
        {
            var afterArray = RedactAuthArrayValue(text, valStart + 1, isJsonMember, allowTruncatedObject, cache, writer);
            var validJsonArr = isJsonMember && TryParseJsonValue(text, valStart, ref cache.ValidJsonValueEnd) == afterArray;
            return RedactTrailingPlainAuthContinuation(text, afterArray, validJsonArr, allowTrailingProse, failedTopEnd, writer);
        }
        if (ch == '{')
        {
            var objEnd = TryParseJsonValue(text, valStart, ref cache.ValidJsonValueEnd);
            if (objEnd > valStart)
            {
                writer.AppendRedacted(valStart, objEnd, isJsonMember ? "\"[redacted]\"" : "[redacted]");
                return RedactTrailingPlainAuthContinuation(text, objEnd, isJsonMember, allowTrailingProse, failedTopEnd, writer);
            }
            if (isJsonMember || allowTruncatedObject)
            {
                writer.AppendRedacted(valStart, text.Length, "\"[redacted]\"");
                return text.Length;
            }
        }
        if (isJsonMember && TryRedactJsonPrimitiveAuthValue(text, valStart, allowTrailingProse, writer, out var afterPrim))
        {
            return afterPrim;
        }
        return RedactPlainAuthSpan(text, valStart, failedTopEnd, writer);
    }

    private static bool TryRedactJsonPrimitiveAuthValue(
        string text, int valStart, bool allowTrailingProse, AuthHeaderWriter writer, out int endPos)
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
        if (next < text.Length && !IsJsonMemberContinuationDelimiter(text, next, allowTrailingProse)) return false;
        if (!isNull)
        {
            writer.AppendRedacted(valStart, tokenEnd, "\"[redacted]\"");
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

    private static int RedactPlainAuthSpan(string text, int valStart, int minEnd, AuthHeaderWriter writer)
    {
        var lineEnd = ScanPlainAuthValueEnd(text, valStart);
        if (minEnd > lineEnd) lineEnd = ScanPlainAuthValueEnd(text, minEnd);
        var redacted = RedactAuthValueBody(text, valStart, lineEnd);
        if (redacted != null)
        {
            writer.AppendRedacted(valStart, lineEnd, redacted);
        }
        return lineEnd;
    }

    private static int RedactTrailingPlainAuthContinuation(
        string text, int afterPos, bool allowNewlines, bool allowTrailingProse, int failedTopEnd, AuthHeaderWriter writer)
    {
        var tailStart = FindTrailingPlainAuthStart(text, afterPos, allowNewlines, allowTrailingProse);
        if (tailStart >= 0) return RedactPlainAuthSpan(text, tailStart, failedTopEnd, writer);
        if (!allowNewlines && failedTopEnd > afterPos)
        {
            var nextStart = SkipJsonWhitespaceForward(text, afterPos);
            if (nextStart < text.Length) return RedactPlainAuthSpan(text, nextStart, failedTopEnd, writer);
        }
        return afterPos;
    }

    private static int FindTrailingPlainAuthStart(string text, int afterPos, bool isJsonMember, bool allowTrailingProse)
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
        if (isJsonMember && IsJsonMemberContinuationDelimiter(text, p, allowTrailingProse)) return -1;
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

    private static int ClassifyTopLevelJsonContainer(
        string text, int openPos, bool allowTrailingProse, AuthJsonScanCache cache)
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
            var before = curOpen - 1;
            while (before >= 0 && text[before] is ' ' or '\t') before--;
            var hasSameLinePrefix = before >= 0 && text[before] is not ('\r' or '\n');
            state = allowTrailingProse || after >= text.Length || text[after] is '\r' or '\n' or ';' or ')' || hasSameLinePrefix
                ? 0
                : containerEnd;
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

    private static bool IsJsonMemberContinuationDelimiter(string text, int pos, bool allowTrailingProse)
    {
        if (text[pos] is '}' or ']')
        {
            return allowTrailingProse || pos + 1 >= text.Length || text[pos + 1] is ' ' or '\t' or '\r' or '\n' or ',' or '}' or ']' or ')' or ';';
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
        AuthJsonScanCache cache, AuthHeaderWriter writer)
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
                k = RedactQuotedAuthString(text, k, c, writer);
                continue;
            }
            if (c == '{')
            {
                var objEnd = TryParseJsonValue(text, k, ref cache.ValidJsonValueEnd);
                if (objEnd > k)
                {
                    writer.AppendRedacted(k, objEnd, isJsonMember ? "\"[redacted]\"" : "[redacted]");
                    k = objEnd;
                    continue;
                }
                if (isJsonMember || allowTruncatedObject)
                {
                    writer.AppendRedacted(k, text.Length, "\"[redacted]\"");
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
                    writer.AppendRedacted(k, elemEnd, replacement + text.Substring(trimEnd, elemEnd - trimEnd));
                }
            }
            k = elemEnd;
        }
        return k;
    }

    private static int RedactQuotedAuthString(
        string text, int quotePos, char quote, AuthHeaderWriter writer)
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
            writer.AppendRedacted(innerStart, j, redacted);
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
        text.IndexOf('@') >= 0 ||
        text.IndexOf("%40", StringComparison.Ordinal) >= 0 ||
        text.IndexOf("\\u0040", StringComparison.Ordinal) >= 0;

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
            var skipTo = SkipNumericDotRun(text, segStart, dot, extStart, extEnd);
            if (skipTo >= -1)
            {
                dot = skipTo + 1;
                continue;
            }
            return (dot, extEnd);
        }
        return (-1, -1);
    }

    private static int SkipNumericDotRun(string text, int segStart, int dot, int extStart, int extEnd)
    {
        for (var i = extStart; i < extEnd; i++)
        {
            if (!char.IsAsciiDigit(text[i])) return -2;
        }
        var p = dot - 1;
        var hasDigitBefore = false;
        while (p >= segStart && (char.IsAsciiDigit(text[p]) || text[p] == '.'))
        {
            if (char.IsAsciiDigit(text[p])) hasDigitBefore = true;
            p--;
        }
        return hasDigitBefore && (p < segStart || !IsAsciiWordChar(text[p])) ? p : -2;
    }

    private static bool IsAsciiAlphaNum(char ch) =>
        (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9');

    private static string RedactQuotedEmails(string text)
    {
        var current = RedactQuotedEmailsRaw(text);
        return RedactEmailsInEscapedDoubleQuotes(current, quotedOnly: true);
    }

    private static string RedactQuotedEmailsRaw(string text)
    {
        if (text.IndexOf('"') < 0 || !HasEmailAt(text))
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
        var current = RedactEmailsRaw(text);
        return RedactEmailsInEscapedDoubleQuotes(current, quotedOnly: false);
    }

    private static string RedactEmailsInEscapedDoubleQuotes(string text, bool quotedOnly)
    {
        if (text.IndexOf('"') < 0 || text.IndexOf('\\') < 0 ||
            (!HasEmailAt(text) && !text.Contains("\\u", StringComparison.Ordinal)))
        {
            return text;
        }
        var spans = CollectBalancedDoubleQuoteSpans(text);
        if (spans.Count == 0) return text;
        StringBuilder? sb = null;
        var copied = 0;
        foreach (var (open, close) in spans)
        {
            var innerStart = open + 1;
            if (innerStart >= close || text.IndexOf('\\', innerStart, close - innerStart) < 0) continue;
            var (decoded, rawOffsets) = DecodeQuotedSpanWithOffsets(text, innerStart, close);
            if (!HasEmailAt(decoded) || (quotedOnly && decoded.IndexOf('"') < 0)) continue;
            var matches = FindEmailMatchRanges(decoded, quotedOnly);
            foreach (var (mStart, mEnd) in matches)
            {
                var rawStart = rawOffsets[mStart];
                var rawEnd = rawOffsets[mEnd];
                if (rawStart < copied) continue;
                sb ??= new StringBuilder(text.Length);
                sb.Append(text, copied, rawStart - copied).Append("[redacted-email]");
                copied = rawEnd;
            }
        }
        if (sb == null) return text;
        return sb.Append(text, copied, text.Length - copied).ToString();
    }

    private static List<(int Start, int End)> FindEmailMatchRanges(string text, bool quotedOnly)
    {
        var ranges = new List<(int Start, int End)>();
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
                        ranges.Add((i, qDomEnd));
                        copied = qDomEnd;
                        i = qDomEnd;
                        quoteCacheUntil = -1;
                        continue;
                    }
                }
                i++;
                continue;
            }
            if (quotedOnly)
            {
                i++;
                continue;
            }
            var atLen = EmailAtLength(text, i);
            if (atLen == 0)
            {
                i++;
                continue;
            }
            var domEnd = FindEmailDomainEnd(text, i + atLen, requireDot: i > copied && text[i - 1] == '/');
            var localStart = domEnd > 0 ? FindEmailLocalStart(text, i, copied) : i;
            if (domEnd > 0 && localStart < i)
            {
                ranges.Add((localStart, domEnd));
                copied = domEnd;
                i = domEnd;
                quoteCacheUntil = -1;
            }
            else
            {
                i += atLen;
            }
        }
        return ranges;
    }

    private static string RedactEmailsRaw(string text)
    {
        if (!HasEmailAt(text))
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
            var domEnd = FindEmailDomainEnd(text, i + atLen, requireDot: i > copied && text[i - 1] == '/');
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

    private static int EmailAtLength(string text, int pos)
    {
        var ch = text[pos];
        if (ch == '@') return 1;
        if (ch == '%' && pos + 2 < text.Length && text[pos + 1] == '4' && text[pos + 2] == '0') return 3;
        if (ch == '\\' && pos + 5 < text.Length &&
            text.AsSpan(pos, 6).Equals("\\u0040", StringComparison.Ordinal) &&
            !IsPrecededByOddBackslashes(text, pos, 0))
        {
            return 6;
        }
        return 0;
    }

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
        while (true)
        {
            while (localStart > minPos && IsEmailLocalChar(text, localStart - 1) && !IsSchemeSlash(text, localStart - 1))
            {
                localStart--;
            }
            if (localStart < atPos && localStart > minPos && IsPrecededByOddBackslashes(text, localStart, minPos))
            {
                var esc = text[localStart];
                if (esc is 'n' or 'r' or 't' or 'b' or 'f' or '/')
                {
                    localStart++;
                    break;
                }
                if (esc == 'u' && localStart + 5 <= atPos && TryParseHex4(text, localStart + 1, out var code))
                {
                    var decoded = (char)code;
                    if (IsEmailLocalChar(decoded.ToString(), 0))
                    {
                        localStart--;
                        continue;
                    }
                    localStart += 5;
                    break;
                }
            }
            break;
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
        var maxClose = Math.Min(text.Length, domStart + 65);
        while (close < maxClose)
        {
            var ch = text[close];
            if (ch == ']') break;
            if (char.IsWhiteSpace(ch) || ch is '[' or '@' or '"' or '\'' or '<' or '>' or '\\' or '{' or '}' or '\r' or '\n')
            {
                return -1;
            }
            close++;
        }
        if (close >= maxClose || text[close] != ']' || close <= domStart + 1) return -1;
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

    private static DomainCharKind ClassifyUnicodeCategory(System.Globalization.UnicodeCategory cat) => cat switch
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

    private static DomainCharKind ClassifyDomainCharAt(string text, int pos, out int step)
    {
        step = 1;
        var ch = text[pos];
        if (ch == '\\' && pos + 5 < text.Length && text[pos + 1] == 'u' && TryParseHex4(text, pos + 2, out var code))
        {
            step = 6;
            ch = (char)code;
            if (ch < 0x80)
            {
                if (char.IsAsciiLetter(ch)) return DomainCharKind.Letter;
                if (char.IsAsciiDigit(ch)) return DomainCharKind.Digit;
                if (ch == '.') return DomainCharKind.Dot;
                return ch == '-' ? DomainCharKind.Hyphen : DomainCharKind.Other;
            }
            if (IsEmailDomainDot(ch)) return DomainCharKind.Dot;
            if (char.IsHighSurrogate(ch))
            {
                if (pos + 11 < text.Length && text[pos + 6] == '\\' && text[pos + 7] == 'u' &&
                    TryParseHex4(text, pos + 8, out var low) && char.IsLowSurrogate((char)low))
                {
                    step = 12;
                    return ClassifyUnicodeCategory(
                        System.Globalization.CharUnicodeInfo.GetUnicodeCategory(char.ConvertToUtf32(ch, (char)low)));
                }
                return DomainCharKind.Other;
            }
            return char.IsLowSurrogate(ch) ? DomainCharKind.Other : ClassifyUnicodeCategory(char.GetUnicodeCategory(ch));
        }
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
        return ClassifyUnicodeCategory(cat);
    }

    private static bool IsEmailBoundaryBlockedAt(string text, int pos)
    {
        var kind = ClassifyDomainCharAt(text, pos, out _);
        return kind is DomainCharKind.Letter or DomainCharKind.Digit or DomainCharKind.Hyphen || text[pos] == '_';
    }

    private static int FindEmailDomainEnd(string text, int domStart, bool requireDot = false)
    {
        if (domStart < text.Length && text[domStart] == '[')
        {
            return FindIpLiteralDomainEnd(text, domStart);
        }
        var k = domStart;
        var labelStart = domStart;
        var labelHasLetter = false;
        var hasDot = false;
        var lastKind = DomainCharKind.Other;
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
                if (k == labelStart || lastKind == DomainCharKind.Hyphen) break;
                labelStart = k + step;
                labelHasLetter = false;
                hasDot = true;
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
            lastKind = kind;
            k += step;
            var atBoundary = k == text.Length || !IsEmailBoundaryBlockedAt(text, k);
            var continuesDomain = k < text.Length &&
                ClassifyDomainCharAt(text, k, out var dotStep) == DomainCharKind.Dot &&
                k + dotStep < text.Length &&
                ClassifyDomainCharAt(text, k + dotStep, out _) is DomainCharKind.Letter or DomainCharKind.Digit;
            if ((!requireDot || hasDot) && k > labelStart && labelHasLetter && lastKind != DomainCharKind.Hyphen && atBoundary && !continuesDomain)
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

        public void OnVaultedUrl(int matchEnd, char activeQuote = '\0')
        {
            _cursor = matchEnd;
            ResetLineState();
            _openQuote = activeQuote;
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
                TrackTokenChar(text, ch);
                _inWrapperRun = false;
                _wsBeforeRun = false;
            }
            _escapedBackslash = ch == '\\' ? !_escapedBackslash : false;
        }

        private void TrackTokenChar(string text, char ch)
        {
            if (ch == '\\')
            {
                var isNonPathJsonEscape = false;
                if (_openQuote == '"' && !_escapedBackslash && _cursor + 1 < text.Length)
                {
                    var next = text[_cursor + 1];
                    isNonPathJsonEscape = next is '"' or 'n' or 'r' or 't' or 'b' or 'f' ||
                        (next == 'u' && _cursor + 5 < text.Length && TryParseHex4(text, _cursor + 2, out var code) &&
                         code is not ('\\' or '/'));
                }
                if (!isNonPathJsonEscape) _tokenHasBackslash = true;
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

    private static bool IsFileSchemeAt(string text, int pos) => LocalUriSchemeLengthAt(text, pos) > 0;

    private static int LocalUriSchemeLengthAt(string text, int pos)
    {
        if (pos > 0 && IsUrlSchemeChar(text[pos - 1])) return 0;
        if (pos + 7 <= text.Length && text.AsSpan(pos, 7).Equals("file://", StringComparison.OrdinalIgnoreCase))
        {
            return 7;
        }
        if (pos + 6 <= text.Length && text.AsSpan(pos, 6).Equals("file:/", StringComparison.OrdinalIgnoreCase))
        {
            return 6;
        }
        if (pos + 10 <= text.Length && text.AsSpan(pos, 10).Equals("content://", StringComparison.OrdinalIgnoreCase))
        {
            return 10;
        }
        return 0;
    }

    private static string RedactFileUris(string text, string vaultPrefix)
    {
        if (text.Length < 6) return text;
        var checkedQuotes = false;
        List<(int Open, int Close)>? quoteSpans = null;
        var spanIdx = 0;
        StringBuilder? sb = null;
        var copyStart = 0;
        var pos = 0;
        while (pos < text.Length)
        {
            var schemeLen = LocalUriSchemeLengthAt(text, pos);
            if (schemeLen > 0)
            {
                // Double-quoted file:// or content:// URIs terminate at the next unescaped '"'. Unquoted and
                // single-quoted URIs fail closed to the next vaulted URL or line break so
                // unescaped apostrophes inside filenames (e.g. "O' Connor") can never leak suffixes.
                if (!checkedQuotes)
                {
                    checkedQuotes = true;
                    if (text.IndexOf('"') >= 0) quoteSpans = CollectBalancedDoubleQuoteSpans(text);
                }
                if (quoteSpans != null)
                {
                    while (spanIdx < quoteSpans.Count && quoteSpans[spanIdx].Close <= pos) spanIdx++;
                }
                var inBalancedQuote = quoteSpans != null && spanIdx < quoteSpans.Count &&
                    quoteSpans[spanIdx].Open < pos && pos < quoteSpans[spanIdx].Close;
                var isDoubleQuoted = inBalancedQuote || (pos > 0 && text[pos - 1] == '"');
                var scanLimit = text.Length;
                if (inBalancedQuote &&
                    IsPrecededByEscapedQuote(text, pos, quoteSpans![spanIdx].Open + 1) &&
                    TryFindClosingEscapedQuote(text, pos, quoteSpans[spanIdx].Close, out var escClosePos, out _))
                {
                    scanLimit = escClosePos;
                }
                var schemeEnd = pos + schemeLen;
                var rawEnd = schemeEnd;
                var stoppedAtVault = false;
                var escapedBackslash = false;
                while (rawEnd < scanLimit)
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
            var numEnd = ScanVaultIndexEnd(text, numStart, text.Length);
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

    private static int AdvanceQuoteState(string text, int from, int to, int quoteState)
    {
        var inSingle = (quoteState & 1) != 0;
        var inDouble = (quoteState & 2) != 0;
        for (var i = from; i < to; i++)
        {
            var ch = text[i];
            if (ch is '\r' or '\n') { inSingle = false; inDouble = false; continue; }
            if (ch == '\\' && (inSingle || inDouble) && i + 1 < to && text[i + 1] is not ('\r' or '\n')) { i++; continue; }
            if (ch == '"' && !inSingle) { inDouble = !inDouble; continue; }
            if (ch == '\'' && !inDouble && !IsContractionApostrophe(text, i)) inSingle = !inSingle;
        }
        return (inSingle ? 1 : 0) | (inDouble ? 2 : 0);
    }

    private static string TrimQueryOrFragmentDelimiters(
        string qf, string fullText, int matchStart, out string tail, bool inSingleQuote = false)
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
        var precededBySingleQuote = inSingleQuote || (matchStart > 0 && fullText[matchStart - 1] == '\'');
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
            TryCompletePendingInitialEnable();
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

    private void TryCompletePendingInitialEnable()
    {
        lock (_gate)
        {
            if (!_pendingInitialEnable || _disposeRequested || _clientDisposed || _consent != TelemetryConsent.Off)
            {
                return;
            }
            if (string.IsNullOrEmpty(ReadExternalTokenSafe()))
            {
                _pendingInitialEnable = false;
                return;
            }
        }
        if (SetConsent(TelemetryConsent.Full))
        {
            lock (_gate) _pendingInitialEnable = false;
        }
        else
        {
            lock (_gate)
            {
                if (!_lastEnableBlockedByExternalLock || string.IsNullOrEmpty(ReadExternalTokenSafe()))
                {
                    _pendingInitialEnable = false;
                }
            }
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
        if (consent == TelemetryConsent.Off)
        {
            lock (_gate)
            {
                _pendingInitialEnable = false;
                _lastEnableBlockedByExternalLock = false;
            }
        }
        else
        {
            lock (_gate) _lastEnableBlockedByExternalLock = false;
        }
        if (consent != TelemetryConsent.Off && _acquireExternalConsentLock != null)
        {
            externalConsentLock = TryAcquireExternalConsentLease();
            if (externalConsentLock == null)
            {
                lock (_gate) _lastEnableBlockedByExternalLock = true;
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
                            keepForeignGenerationPrefix: null,
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
                            ResolveForeignKeepGenerationPrefixLocked());
                    }
                }
                else if (_disposeRequested || _clientDisposed)
                {
                    return false;
                }
                else if (_revokedFlushesRemaining > 0 || (_lastPurgeFailed && !TryDeleteBufferFallback(ResolveForeignKeepGenerationPrefixLocked())))
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
                        var targetGenerationPrefix = ResolveGenerationPrefix(externalToken);
                        var targetBufferPath = ResolveBufferPathForToken(externalToken);
                        if (!TryDeleteForeignGenerationBuffers(targetGenerationPrefix))
                        {
                            return false;
                        }
                        if (targetGenerationPrefix != null && !string.IsNullOrWhiteSpace(targetBufferPath) &&
                            !TryAcquireInstanceOwnerLockLocked(targetBufferPath))
                        {
                            return false;
                        }
                        var switchedClient = false;
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
                                _activeGenerationPrefix = targetGenerationPrefix;
                                nextClient = null;
                                switchedClient = true;
                            }
                            catch (AunException ex)
                            {
                                nextClient?.Dispose();
                                Log.For("telemetry").Warn("failed to bind generation telemetry buffer: " + ex.Message);
                                return false;
                            }
                        }
                        else
                        {
                            _activeGenerationPrefix = targetGenerationPrefix;
                        }
                        if (targetGenerationPrefix != null && !string.IsNullOrWhiteSpace(targetBufferPath) &&
                            (_consent == TelemetryConsent.Off || switchedClient))
                        {
                            AdoptOrphanedGenerationBuffersLocked(targetBufferPath, targetGenerationPrefix);
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
                        _observedExternalToken = NormalizeConsentGenerationToken(externalToken);
                        _pendingInitialEnable = false;
                        UpdatePurgeFailedLocked(false, ref purgeNotify, ref purgeNotifyValue);
                        result = true;
                    }
                    catch (AunException ex)
                    {
                        // Fail closed in BOTH managed and native state so an earlier native consent update
                        // (before an I/O failure) cannot leave native telemetry enabled for a queued flush.
                        RevokeLocked(
                            out toCancel,
                            "telemetry rollback purge failed: ",
                            ref purgeNotify,
                            ref purgeNotifyValue,
                            ResolveForeignKeepGenerationPrefixLocked());
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
        TryCompletePendingInitialEnable();
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
        TryCompletePendingInitialEnable();
        lock (_gate)
        {
            if (_disposeRequested || _clientDisposed || _consent != TelemetryConsent.Full) return;
        }
        var safeName = SanitizeLocalPaths(name);
        if (string.IsNullOrWhiteSpace(safeName)) return;
        var json = SerializeProperties(properties);
        WithPersistingConsent(requiresFull: true, () =>
        {
            try
            {
                _client.Track(safeName, json);
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
        TryCompletePendingInitialEnable();
        lock (_gate)
        {
            if (_disposeRequested || _clientDisposed || _consent != TelemetryConsent.Full) return;
        }
        var safeCode = SanitizeLocalPaths(code);
        if (string.IsNullOrWhiteSpace(safeCode)) return;
        var json = SerializeProperties(properties);
        WithPersistingConsent(requiresFull: true, () =>
        {
            try
            {
                _client.TrackError(safeCode, json);
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
        TryCompletePendingInitialEnable();
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
        TryCompletePendingInitialEnable();
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
        TryCompletePendingInitialEnable();

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
        if (!string.IsNullOrEmpty(currentToken) &&
            string.Equals(NormalizeConsentGenerationToken(currentToken), _observedExternalToken, StringComparison.Ordinal))
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
            keepForeignGenerationPrefix: null,
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
        string? keepForeignGenerationPrefix = null,
        bool ownBufferOnly = false)
    {
        var ownGenerationPrefix = _activeGenerationPrefix;
        _consent = TelemetryConsent.Off;
        _observedExternalToken = null;
        _consentEpoch++;
        _revokedFlushesRemaining = _inFlightFlushes;
        toCancel = _flushCts;
        _flushCts = new CancellationTokenSource();
        if (_inFlightFlushes == 0)
        {
            ReleaseInstanceOwnerLockLocked();
        }
        if (_clientDisposed)
        {
            var fallbackOk = ownBufferOnly
                ? TryDeleteOwnBufferAndGenerationTemps(_activeBufferPath, ownGenerationPrefix)
                : TryDeleteBufferFallback(keepForeignGenerationPrefix);
            UpdatePurgeFailedLocked(!fallbackOk, ref purgeNotify, ref purgeNotifyValue);
            return !_lastPurgeFailed;
        }
        try
        {
            _client.SetConsent(TelemetryConsentState.Denied);
            var ownOk = ownBufferOnly
                ? TryDeleteOwnBufferAndGenerationTemps(_activeBufferPath, ownGenerationPrefix)
                : (!string.IsNullOrWhiteSpace(_activeBufferPath) &&
                   !string.Equals(_activeBufferPath, _bufferPath, StringComparison.Ordinal)
                    ? TryDeleteSingleBufferAndTemps(_activeBufferPath)
                    : true);
            var cleanOk = ownBufferOnly
                ? ownOk
                : ownOk &&
                  TryDeleteForeignGenerationBuffers(keepForeignGenerationPrefix) &&
                  TryDeleteSiblingTempFiles(keepForeignGenerationPrefix);
            UpdatePurgeFailedLocked(!cleanOk, ref purgeNotify, ref purgeNotifyValue);
        }
        catch (AunException ex)
        {
            _ = ownBufferOnly
                ? TryDeleteOwnBufferAndGenerationTemps(_activeBufferPath, ownGenerationPrefix)
                : TryDeleteBufferFallback(keepForeignGenerationPrefix);
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
            if (_inFlightFlushes == 0 && (_disposeRequested || _consent == TelemetryConsent.Off))
            {
                ReleaseInstanceOwnerLockLocked();
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
            _pendingInitialEnable = false;
            _consent = TelemetryConsent.Off;
            toCancel = _flushCts;
            if (_inFlightFlushes == 0)
            {
                ReleaseInstanceOwnerLockLocked();
                if (!_clientDisposed)
                {
                    _clientDisposed = true;
                    _client.Dispose();
                }
            }
        }
        CancelQuietly(toCancel);
    }

    private string? ResolveForeignKeepGenerationPrefixLocked()
    {
        var currentToken = ReadExternalTokenSafe();
        if (string.IsNullOrEmpty(currentToken)) return null;
        var currentGenPrefix = ResolveGenerationPrefix(currentToken);
        return string.IsNullOrEmpty(currentGenPrefix) ? null : currentGenPrefix;
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

    private static string? NormalizeConsentGenerationToken(string? externalToken)
    {
        if (string.IsNullOrEmpty(externalToken)) return null;
        if (!externalToken.StartsWith("enabled:", StringComparison.Ordinal)) return externalToken;
        var secondColon = externalToken.IndexOf(':', "enabled:".Length);
        return secondColon >= 0 ? externalToken.Substring(0, secondColon) : externalToken;
    }

    private string? ResolveGenerationPrefix(string? externalToken)
    {
        if (string.IsNullOrWhiteSpace(_bufferPath) || _externalConsentToken == null || string.IsNullOrWhiteSpace(externalToken))
        {
            return null;
        }
        var stem = Path.GetFileNameWithoutExtension(_bufferPath);
        if (string.IsNullOrEmpty(stem)) return null;
        var normalized = NormalizeConsentGenerationToken(externalToken) ?? string.Empty;
        var rawGen = normalized.StartsWith("enabled:", StringComparison.Ordinal)
            ? normalized["enabled:".Length..]
            : "legacy";
        var safeGen = new string(rawGen.Where(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        if (string.IsNullOrEmpty(safeGen)) safeGen = "legacy";
        return $"{stem}.{safeGen}";
    }

    private string? ResolveBufferPathForToken(string? externalToken)
    {
        if (string.IsNullOrWhiteSpace(_bufferPath)) return null;
        var genPrefix = ResolveGenerationPrefix(externalToken);
        if (genPrefix == null) return _bufferPath;
        var dir = Path.GetDirectoryName(_bufferPath);
        var ext = Path.GetExtension(_bufferPath);
        var fileName = $"{genPrefix}.{_instanceId}{ext}";
        return string.IsNullOrEmpty(dir) ? fileName : Path.Combine(dir, fileName);
    }

    private static bool IsBufferFileInGeneration(string fileName, string generationPrefix, string ext) =>
        string.Equals(fileName, generationPrefix + ext, StringComparison.Ordinal) ||
        (fileName.StartsWith(generationPrefix + ".", StringComparison.Ordinal) &&
         fileName.EndsWith(ext, StringComparison.Ordinal));

    private static bool IsTempFileInGeneration(string fileName, string generationPrefix, string ext) =>
        fileName.StartsWith($".{generationPrefix}{ext}.", StringComparison.Ordinal) ||
        fileName.StartsWith($".{generationPrefix}.", StringComparison.Ordinal);

    private static string GetInstanceOwnerLockPath(string bufferPath)
    {
        var dir = Path.GetDirectoryName(bufferPath);
        var fileName = Path.GetFileName(bufferPath);
        return string.IsNullOrEmpty(dir) ? $".{fileName}.owner" : Path.Combine(dir, $".{fileName}.owner");
    }

    private bool TryAcquireInstanceOwnerLockLocked(string targetBufferPath)
    {
        var ownerPath = GetInstanceOwnerLockPath(targetBufferPath);
        if (_instanceOwnerLock != null && string.Equals(_instanceOwnerLockPath, ownerPath, StringComparison.Ordinal))
        {
            return true;
        }
        try
        {
            var dir = Path.GetDirectoryName(ownerPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var nextLock = new FileStream(ownerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            ReleaseInstanceOwnerLockLocked();
            _instanceOwnerLock = nextLock;
            _instanceOwnerLockPath = ownerPath;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void ReleaseInstanceOwnerLockLocked()
    {
        var held = _instanceOwnerLock;
        var path = _instanceOwnerLockPath;
        _instanceOwnerLock = null;
        _instanceOwnerLockPath = null;
        if (held != null)
        {
            try
            {
                held.Dispose();
            }
            catch (IOException)
            {
            }
        }
        if (!string.IsNullOrEmpty(path))
        {
            TryDeleteSingleFile(path);
        }
    }

    private void AdoptOrphanedGenerationBuffersLocked(string targetBufferPath, string generationPrefix)
    {
        var dir = Path.GetDirectoryName(targetBufferPath);
        var ext = Path.GetExtension(targetBufferPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        var orphans = new List<(string BufferPath, FileStream OwnerLock, string OwnerPath, DateTime LastWriteUtc)>();
        try
        {
            foreach (var candidate in Directory.EnumerateFiles(dir, $"{generationPrefix}*{ext}"))
            {
                if (string.Equals(candidate, targetBufferPath, StringComparison.Ordinal)) continue;
                var name = Path.GetFileName(candidate);
                if (!IsBufferFileInGeneration(name, generationPrefix, ext)) continue;
                var ownerPath = GetInstanceOwnerLockPath(candidate);
                FileStream? ownerLock = null;
                try
                {
                    ownerLock = new FileStream(ownerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    var mtime = File.GetLastWriteTimeUtc(candidate);
                    orphans.Add((candidate, ownerLock, ownerPath, mtime));
                    ownerLock = null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ownerLock?.Dispose();
                }
            }
            if (orphans.Count == 0) return;

            orphans.Sort(static (a, b) => a.LastWriteUtc.CompareTo(b.LastWriteUtc));
            var mergedEvents = new List<AdoptedEvent>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var latestBreadcrumbs = new List<AdoptedBreadcrumb>();

            if (File.Exists(targetBufferPath) &&
                !ReadPersistedBufferInto(targetBufferPath, mergedEvents, seenIds, latestBreadcrumbs))
            {
                return;
            }
            var adoptedPaths = new List<string>(orphans.Count);
            foreach (var (orphanPath, _, _, _) in orphans)
            {
                if (ReadPersistedBufferInto(orphanPath, mergedEvents, seenIds, latestBreadcrumbs))
                {
                    adoptedPaths.Add(orphanPath);
                }
            }
            if (adoptedPaths.Count == 0) return;

            if (mergedEvents.Count > 0 || latestBreadcrumbs.Count > 0)
            {
                var tmpPath = Path.Combine(dir, $".{Path.GetFileName(targetBufferPath)}.{Environment.ProcessId}.adopt.tmp");
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new Utf8JsonWriter(fs))
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("events");
                    writer.WriteStartArray();
                    foreach (var ev in mergedEvents) WriteAdoptedEvent(writer, ev);
                    writer.WriteEndArray();
                    writer.WritePropertyName("breadcrumbs");
                    writer.WriteStartArray();
                    foreach (var crumb in latestBreadcrumbs) WriteAdoptedBreadcrumb(writer, crumb);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                File.Move(tmpPath, targetBufferPath, overwrite: true);
            }

            var adoptedNames = new HashSet<string>(adoptedPaths.Count, StringComparer.Ordinal);
            foreach (var orphanPath in adoptedPaths)
            {
                TryDeleteSingleFile(orphanPath);
                adoptedNames.Add(Path.GetFileName(orphanPath));
            }
            TryDeleteAdoptedOrphanTempFiles(dir, generationPrefix, ext, adoptedNames);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Log.For("telemetry").Warn("telemetry orphan buffer adoption skipped: " + ex.Message);
        }
        finally
        {
            foreach (var (_, ownerLock, ownerPath, _) in orphans)
            {
                try
                {
                    ownerLock.Dispose();
                }
                catch (IOException)
                {
                }
                TryDeleteSingleFile(ownerPath);
            }
        }
    }

    private static void TryDeleteAdoptedOrphanTempFiles(
        string dir, string generationPrefix, string ext, HashSet<string> adoptedNames)
    {
        if (adoptedNames.Count == 0) return;
        try
        {
            var extDot = ext + ".";
            var minExtIdx = 1 + generationPrefix.Length;
            foreach (var tmp in Directory.EnumerateFiles(dir, $".{generationPrefix}*{ext}.*.tmp"))
            {
                var name = Path.GetFileName(tmp);
                if (name.Length <= minExtIdx || name[0] != '.') continue;
                var extIdx = name.IndexOf(extDot, minExtIdx, StringComparison.Ordinal);
                if (extIdx < minExtIdx) continue;
                var ownerName = name.Substring(1, extIdx + ext.Length - 1);
                if (adoptedNames.Contains(ownerName))
                {
                    TryDeleteSingleFile(tmp);
                }
            }
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private readonly struct AdoptedPropValue
    {
        public readonly JsonValueKind Kind;
        public readonly bool BoolValue;
        public readonly long IntValue;
        public readonly double FloatValue;
        public readonly string? TextValue;
        public readonly bool IsInt;

        private AdoptedPropValue(JsonValueKind kind, bool b = false, long i = 0, double f = 0, string? s = null, bool isInt = false)
        {
            Kind = kind;
            BoolValue = b;
            IntValue = i;
            FloatValue = f;
            TextValue = s;
            IsInt = isInt;
        }

        public static AdoptedPropValue FromBool(bool v) => new(v ? JsonValueKind.True : JsonValueKind.False, b: v);
        public static AdoptedPropValue FromInt(long v) => new(JsonValueKind.Number, i: v, isInt: true);
        public static AdoptedPropValue FromFloat(double v) => new(JsonValueKind.Number, f: v);
        public static AdoptedPropValue FromText(string v) => new(JsonValueKind.String, s: v);
    }

    private sealed class AdoptedBreadcrumb
    {
        public long TimestampMs;
        public string Category = string.Empty;
        public string Message = string.Empty;
        public string? Level;
    }

    private sealed class AdoptedEvent
    {
        public string Id = string.Empty;
        public string Kind = string.Empty;
        public string Name = string.Empty;
        public long TimestampMs;
        public string SessionId = string.Empty;
        public Dictionary<string, AdoptedPropValue>? Properties;
        public List<AdoptedBreadcrumb>? Breadcrumbs;
    }

    private static void WriteAdoptedEvent(Utf8JsonWriter writer, AdoptedEvent ev)
    {
        writer.WriteStartObject();
        writer.WriteString("id", ev.Id);
        writer.WriteString("kind", ev.Kind);
        writer.WriteString("name", ev.Name);
        writer.WriteNumber("timestamp_ms", ev.TimestampMs);
        writer.WriteString("session_id", ev.SessionId);
        if (ev.Properties != null)
        {
            writer.WritePropertyName("properties");
            writer.WriteStartObject();
            foreach (var kv in ev.Properties)
            {
                writer.WritePropertyName(kv.Key);
                switch (kv.Value.Kind)
                {
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        writer.WriteBooleanValue(kv.Value.BoolValue);
                        break;
                    case JsonValueKind.Number:
                        if (kv.Value.IsInt) writer.WriteNumberValue(kv.Value.IntValue);
                        else writer.WriteNumberValue(kv.Value.FloatValue);
                        break;
                    case JsonValueKind.String:
                        writer.WriteStringValue(kv.Value.TextValue);
                        break;
                }
            }
            writer.WriteEndObject();
        }
        if (ev.Breadcrumbs is { Count: > 0 })
        {
            writer.WritePropertyName("breadcrumbs");
            writer.WriteStartArray();
            foreach (var crumb in ev.Breadcrumbs) WriteAdoptedBreadcrumb(writer, crumb);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private static void WriteAdoptedBreadcrumb(Utf8JsonWriter writer, AdoptedBreadcrumb crumb)
    {
        writer.WriteStartObject();
        writer.WriteNumber("timestamp_ms", crumb.TimestampMs);
        writer.WriteString("category", crumb.Category);
        writer.WriteString("message", crumb.Message);
        if (crumb.Level != null) writer.WriteString("level", crumb.Level);
        writer.WriteEndObject();
    }

    private static bool ReadPersistedBufferInto(
        string path, List<AdoptedEvent> mergedEvents, HashSet<string> seenIds, List<AdoptedBreadcrumb> latestBreadcrumbs)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return true;
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return true;
            var seenRootKeys = new HashSet<string>(StringComparer.Ordinal);
            JsonElement? eventsEl = null;
            JsonElement? crumbsEl = null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!seenRootKeys.Add(prop.Name)) return true;
                if (prop.Name == "events") eventsEl = prop.Value;
                else if (prop.Name == "breadcrumbs") crumbsEl = prop.Value;
            }
            if (eventsEl is { ValueKind: JsonValueKind.Array } evArray)
            {
                foreach (var ev in evArray.EnumerateArray())
                {
                    if (!TryParsePersistedEvent(ev, out var parsed)) continue;
                    if (seenIds.Add(parsed.Id))
                    {
                        mergedEvents.Add(parsed);
                    }
                }
            }
            if (crumbsEl is { ValueKind: JsonValueKind.Array } crArray && crArray.GetArrayLength() > 0)
            {
                var validCrumbs = new List<AdoptedBreadcrumb>();
                foreach (var crumb in crArray.EnumerateArray())
                {
                    if (TryParsePersistedBreadcrumb(crumb, out var parsedCrumb)) validCrumbs.Add(parsedCrumb);
                }
                if (validCrumbs.Count > 0)
                {
                    latestBreadcrumbs.Clear();
                    latestBreadcrumbs.AddRange(validCrumbs);
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryParsePersistedEvent(JsonElement ev, out AdoptedEvent parsed)
    {
        parsed = null!;
        if (ev.ValueKind != JsonValueKind.Object) return false;
        try
        {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? id = null;
        string? kind = null;
        string? name = null;
        long timestampMs = 0;
        var hasTimestamp = false;
        string? sessionId = null;
        Dictionary<string, AdoptedPropValue>? props = null;
        List<AdoptedBreadcrumb>? crumbs = null;
        foreach (var field in ev.EnumerateObject())
        {
            if (!seen.Add(field.Name)) return false;
            switch (field.Name)
            {
                case "id":
                    if (field.Value.ValueKind != JsonValueKind.String) return false;
                    id = field.Value.GetString() ?? string.Empty;
                    break;
                case "kind":
                    if (field.Value.ValueKind != JsonValueKind.String) return false;
                    var k = field.Value.GetString();
                    if (k is not ("analytics" or "qoe" or "error" or "crash")) return false;
                    kind = k;
                    break;
                case "name":
                    if (field.Value.ValueKind != JsonValueKind.String) return false;
                    name = field.Value.GetString() ?? string.Empty;
                    break;
                case "timestamp_ms":
                    if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt64(out timestampMs)) return false;
                    hasTimestamp = true;
                    break;
                case "session_id":
                    if (field.Value.ValueKind != JsonValueKind.String) return false;
                    sessionId = field.Value.GetString() ?? string.Empty;
                    break;
                case "properties":
                    if (field.Value.ValueKind != JsonValueKind.Object) return false;
                    props = new Dictionary<string, AdoptedPropValue>(StringComparer.Ordinal);
                    foreach (var prop in field.Value.EnumerateObject())
                    {
                        if (!TryParsePersistedPropValue(prop.Value, out var propVal) || !props.TryAdd(prop.Name, propVal))
                        {
                            return false;
                        }
                    }
                    break;
                case "breadcrumbs":
                    if (field.Value.ValueKind != JsonValueKind.Array) return false;
                    crumbs = new List<AdoptedBreadcrumb>();
                    foreach (var crumb in field.Value.EnumerateArray())
                    {
                        if (!TryParsePersistedBreadcrumb(crumb, out var parsedCrumb)) return false;
                        crumbs.Add(parsedCrumb);
                    }
                    break;
            }
        }
        if (id == null || kind == null || name == null || !hasTimestamp || sessionId == null) return false;
        parsed = new AdoptedEvent
        {
            Id = id,
            Kind = kind,
            Name = name,
            TimestampMs = timestampMs,
            SessionId = sessionId,
            Properties = props,
            Breadcrumbs = crumbs,
        };
        return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static bool TryParsePersistedPropValue(JsonElement value, out AdoptedPropValue parsed)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                parsed = AdoptedPropValue.FromBool(true);
                return true;
            case JsonValueKind.False:
                parsed = AdoptedPropValue.FromBool(false);
                return true;
            case JsonValueKind.String:
                parsed = AdoptedPropValue.FromText(value.GetString() ?? string.Empty);
                return true;
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var i))
                {
                    parsed = AdoptedPropValue.FromInt(i);
                    return true;
                }
                if (value.TryGetDouble(out var d) && double.IsFinite(d))
                {
                    parsed = AdoptedPropValue.FromFloat(d);
                    return true;
                }
                parsed = default;
                return false;
            default:
                parsed = default;
                return false;
        }
    }

    private static bool TryParsePersistedBreadcrumb(JsonElement crumb, out AdoptedBreadcrumb parsed)
    {
        parsed = null!;
        if (crumb.ValueKind != JsonValueKind.Object) return false;
        try
        {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long timestampMs = 0;
        var hasTimestamp = false;
        string? category = null;
        string? message = null;
        string? level = null;
        foreach (var field in crumb.EnumerateObject())
        {
            if (!seen.Add(field.Name)) return false;
            switch (field.Name)
            {
                case "timestamp_ms":
                    if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt64(out timestampMs)) return false;
                    hasTimestamp = true;
                    break;
                case "category":
                    if (field.Value.ValueKind != JsonValueKind.String) return false;
                    category = field.Value.GetString() ?? string.Empty;
                    break;
                case "message":
                    if (field.Value.ValueKind != JsonValueKind.String) return false;
                    message = field.Value.GetString() ?? string.Empty;
                    break;
                case "level":
                    if (field.Value.ValueKind != JsonValueKind.String) return false;
                    var lvl = field.Value.GetString();
                    if (lvl is not ("debug" or "info" or "warning" or "error")) return false;
                    level = lvl;
                    break;
            }
        }
        if (!hasTimestamp || category == null || message == null) return false;
        parsed = new AdoptedBreadcrumb
        {
            TimestampMs = timestampMs,
            Category = category,
            Message = message,
            Level = level,
        };
        return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private bool TryDeleteOwnBufferAndGenerationTemps(string? targetPath, string? ownGenerationPrefix)
    {
        var ok = TryDeleteSingleBufferAndTemps(targetPath);
        if (string.IsNullOrWhiteSpace(_bufferPath) || string.IsNullOrEmpty(ownGenerationPrefix)) return ok;
        var dir = Path.GetDirectoryName(_bufferPath);
        var ext = Path.GetExtension(_bufferPath);
        if (string.IsNullOrEmpty(dir)) return ok;
        try
        {
            foreach (var tmp in Directory.EnumerateFiles(dir, $".{ownGenerationPrefix}{ext}.*.tmp"))
            {
                if (!TryDeleteSingleFile(tmp)) ok = false;
            }
            return ok;
        }
        catch (DirectoryNotFoundException)
        {
            return ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryDeleteForeignGenerationBuffers(string? keepGenerationPrefix)
    {
        if (string.IsNullOrWhiteSpace(_bufferPath)) return true;
        var dir = Path.GetDirectoryName(_bufferPath);
        var stem = Path.GetFileNameWithoutExtension(_bufferPath);
        var ext = Path.GetExtension(_bufferPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem)) return true;
        var allDeleted = true;
        try
        {
            if (Directory.Exists(dir))
            {
                var baseName = stem + ext;
                foreach (var candidate in Directory.EnumerateFiles(dir, $"{stem}*{ext}"))
                {
                    var name = Path.GetFileName(candidate);
                    if (keepGenerationPrefix != null && IsBufferFileInGeneration(name, keepGenerationPrefix, ext)) continue;
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
                    if (keepGenerationPrefix != null && IsTempFileInGeneration(name, keepGenerationPrefix, ext)) continue;
                    try
                    {
                        File.Delete(tmp);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        allDeleted = false;
                    }
                }
                foreach (var owner in Directory.EnumerateFiles(dir, $".{stem}*.owner"))
                {
                    var name = Path.GetFileName(owner);
                    if (keepGenerationPrefix != null && IsTempFileInGeneration(name, keepGenerationPrefix, ext)) continue;
                    TryDeleteSingleFile(owner);
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

    private bool TryDeleteBufferFallback(string? keepForeignGenerationPrefix = null)
    {
        if (string.IsNullOrWhiteSpace(_bufferPath)) return true;
        var activePath = _activeBufferPath ?? _bufferPath;
        var ext = Path.GetExtension(_bufferPath);
        var isPerInstanceActive = !string.Equals(activePath, _bufferPath, StringComparison.Ordinal);
        var activeKept = !isPerInstanceActive && keepForeignGenerationPrefix != null &&
            IsBufferFileInGeneration(Path.GetFileName(activePath), keepForeignGenerationPrefix, ext);
        var mainOk = activeKept ? true : (isPerInstanceActive ? TryDeleteSingleBufferAndTemps(activePath) : TryDeleteSingleFile(activePath));
        if (isPerInstanceActive &&
            (keepForeignGenerationPrefix == null ||
             !IsBufferFileInGeneration(Path.GetFileName(_bufferPath), keepForeignGenerationPrefix, ext)))
        {
            mainOk = TryDeleteSingleFile(_bufferPath) && mainOk;
        }
        var foreignOk = TryDeleteForeignGenerationBuffers(keepForeignGenerationPrefix);
        var tempsOk = TryDeleteSiblingTempFiles(keepForeignGenerationPrefix);
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

    private bool TryDeleteSiblingTempFiles(string? keepForeignGenerationPrefix = null)
    {
        if (string.IsNullOrWhiteSpace(_bufferPath)) return true;
        var dir = Path.GetDirectoryName(_bufferPath);
        var stem = Path.GetFileNameWithoutExtension(_bufferPath);
        var ext = Path.GetExtension(_bufferPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem)) return true;
        try
        {
            var pattern = $".{stem}*.tmp";
            var allDeleted = true;
            foreach (var tmp in Directory.EnumerateFiles(dir, pattern))
            {
                if (keepForeignGenerationPrefix != null &&
                    IsTempFileInGeneration(Path.GetFileName(tmp), keepForeignGenerationPrefix, ext))
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

    private static bool IsAuthHeaderPropertyKey(string key) => IsKnownAuthHeaderName(key.AsSpan().Trim());

    private static string ResolveUniquePropertyKey(
        Dictionary<string, object> flat, string safeKey, Dictionary<string, int> nextSuffixByBase)
    {
        if (!flat.ContainsKey(safeKey)) return safeKey;
        var suffix = nextSuffixByBase.TryGetValue(safeKey, out var next) ? next : 2;
        while (true)
        {
            var candidate = $"{safeKey}#{suffix}";
            suffix++;
            if (!flat.ContainsKey(candidate))
            {
                nextSuffixByBase[safeKey] = suffix;
                return candidate;
            }
        }
    }

    internal static string? SerializeProperties(IReadOnlyDictionary<string, object>? properties)
    {
        if (properties == null || properties.Count == 0) return null;
        var flat = new Dictionary<string, object>(properties.Count, StringComparer.Ordinal);
        var nextSuffixByBase = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in properties)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null) continue;
            var isAuthKey = IsAuthHeaderPropertyKey(kv.Key);
            var safeKey = isAuthKey ? kv.Key : SanitizeLocalPaths(kv.Key);
            if (string.IsNullOrWhiteSpace(safeKey)) continue;
            var uniqueKey = ResolveUniquePropertyKey(flat, safeKey, nextSuffixByBase);
            if (isAuthKey)
            {
                flat[uniqueKey] = "[redacted]";
                continue;
            }
            switch (kv.Value)
            {
                case string s:
                    flat[uniqueKey] = SanitizeLocalPaths(s);
                    break;
                case bool b:
                    flat[uniqueKey] = b;
                    break;
                case int i:
                    flat[uniqueKey] = i;
                    break;
                case long l:
                    flat[uniqueKey] = l;
                    break;
                case double d when double.IsFinite(d):
                    flat[uniqueKey] = d;
                    break;
                case float f when float.IsFinite(f):
                    flat[uniqueKey] = (double)f;
                    break;
                default:
                    flat[uniqueKey] = SanitizeLocalPaths(kv.Value.ToString() ?? "");
                    break;
            }
        }
        return flat.Count == 0 ? null : JsonSerializer.Serialize(flat);
    }
}
