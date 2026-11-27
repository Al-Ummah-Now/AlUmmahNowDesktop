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
/// Consent defaults to off; opting out immediately blocks collection in the wrapper, cancels in-flight
/// flushes, and purges both memory and the disk buffer. Disposal defers releasing the native handle
/// until any in-flight flush completes without deleting a retryable buffer unless consent was revoked.
/// </summary>
public sealed class CoreTelemetryService : ITelemetryService
{
    private static readonly Regex UrlVaultRegex = new(@"(?<![A-Za-z0-9+.-/\\])([A-Za-z][A-Za-z0-9+.-]+://(?:\[([^\]\s""'<>\\{}\r\n]+)\]|(?:[A-Za-z0-9._~!$&'*+;=-]|%[0-9A-Fa-f]{2})+)(?::\d+)?(?:/(?:(?!(?:file://|[A-Za-z]:[\\/]))(?:[^\s""<>\\{},()\[\]?#\r\n]|\([^\s""<>\\{},()\[\]/:\\?#]+\)|\[[^\s""<>\\{},()\[\]/:\\?#]+\]))*)?)([?#][^\s""<>\r\n]*)?(?=$|[\s""'<>\\{},;()\[\]]|file://|[A-Za-z]:[\\/])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FileUriRegex = new(@"(?<![A-Za-z0-9+.-])file://[^\s\r\n\u001E]+(?:(?>[ \t]+)(?![\(\[\{""']{0,2}\u001E)[^\s\r\n\u001E]+)*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ParenPathRegex = new(@"(\()(?:/|~/|[A-Za-z]:[\\/]|\\\\)[^()\r\n\u001E]+(\))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ParenRelativeSlashPathRegex = new(@"(\()(?!HTTP/\d)(?!TLS/\d)(?!SSL/\d)[^\s/\\""'<>()\[\]{}:,;\u001E\u001F]+/[^()\r\n\u001E]+(\))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WindowsPathRegex = new(@"(?:[A-Za-z]:[\\/]|\\\\)(?:(?!\s+\u001E)[^\r\n\u001E])+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RelativeBackslashPathRegex = new(@"(^|[\s(\[{""'=,:;\u001F])[^\s\\/""'<>(),;\u001E\u001F]+\\(?:(?!\s+\u001E)[^\r\n\u001E\u001F])+", RegexOptions.Compiled);
    private static readonly Regex QuotedSpacedRootPathRegex = new(@"[""']/[ \t]+[^\r\n\u001E]+", RegexOptions.Compiled);
    private static readonly Regex UnixPathRegex = new(@"(?<![\p{L}\p{M}\p{N}._/-])(?:~/|//+(?=[ \t]*[^\s/])|/(?=[ \t]*[^\s/]))(?:(?!\s+\u001E)[^\r\n\u001E])+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RelativeSlashPathRegex = new(@"(^|[\s(\[{""'=,:;\u001F])(?!HTTP/\d)(?!TLS/\d)(?!SSL/\d)[^\s/\\""'<>()\[\]{}:,;\u001E\u001F]+/(?:(?!\s+\u001E)[^\r\n\u001E\u001F])+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DoubleQuotedFilenameRegex = new(@"""[^""\r\n/\\]*\.[A-Za-z0-9]{1,16}""", RegexOptions.Compiled);
    private static readonly Regex SingleQuotedFilenameRegex = new(@"'(?:[^'\r\n/\\]|'(?=\p{L}))*\.[A-Za-z0-9]{1,16}'", RegexOptions.Compiled);
    private static readonly Regex KeywordQuotedPathRegex = new(@"(\b(?:file|path|directory|folder|document)\s+)([""'])(?!\u001E)[^'""\r\n]+\2", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex KeywordUnquotedFilenameRegex = new(@"(\b(?:open|opening|read|reading|write|writing|delete|deleting|remove|removing|rename|stat|create|creating|load|loading|save|saving|access|accessing|unlink|file|path|directory|folder|document|filename|filepath)\b(?:\s*[:=]\s*|\s+))(?!\u001E)[^\r\n\u001E\u001F]*\.(?!(?:kt|java|swift|cs|cpp|cc|c|h|hpp|rs|py|js|ts|go|rb|m|mm):\d+)[A-Za-z0-9]{1,16}\b(?!\()", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex KeywordAssignedPathRegex = new(@"(\b(?:file|path|filename|filepath)\s*[:=]\s*)(?!\u001E)[^\r\n\u001E\u001F]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BareFilenameRegex = new(@"(^|[\r\n:=;\u001F])([ \t]*)[^\r\n:=;\u001E\u001F]*\.(?!ts:\d+)(?:mov|mp4|m4v|mkv|webm|avi|ts|m3u8|mpd|mp3|m4a|wav|aac|flac|ogg|opus|jpg|jpeg|png|gif|webp|heic|svg|pdf|txt|md|rtf|doc|docx|xls|xlsx|ppt|pptx|csv|json|xml|yaml|yml|toml|ini|cfg|conf|log|srt|vtt|ass|ssa|zip|tar|gz|tgz|bz2|xz|7z|rar|dmg|pkg|msi|exe|apk|aab|ipa|pem|key|crt|cer|p12|pfx|jks|keystore|db|sqlite|sqlite3|bin|dat|tmp|bak|proj|aun)\b(?!\()", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmailRegex = new(@"(?:[A-Za-z0-9._+-]|%(?!40))+(?:@|%40)[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BearerTokenRegex = new(@"\bBearer\s+\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly TelemetryAppInfo _app;
    private readonly string? _ingestUrl;
    private readonly string _userAgent;
    private readonly string _clientHeader;
    private readonly string? _bufferPath;
    private readonly Func<string?>? _externalConsentToken;
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
        Func<string?>? externalConsentToken = null)
    {
        _bufferPath = bufferPath;
        _externalConsentToken = externalConsentToken;
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
        Func<string?>? externalConsentToken = null)
    {
        if (demo) return NullTelemetryService.Instance;
        try
        {
            return new CoreTelemetryService(platform, appVersion, locale, videosApiBaseUrl, userAgent, bufferPath, initialEnabled, externalConsentToken);
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
    internal static string SanitizeLocalPaths(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var prefix = "\u001EAUNURL";
        while (text.Contains(prefix, StringComparison.Ordinal))
        {
            prefix += "X";
        }
        var vaultedUrls = new List<string>();
        var scanCursor = 0;
        var hasFileUriOnLine = false;
        var fileUriQuote = '\0';
        var lastNonWsOrWrapperChar = '\0';
        var inWrapperRun = false;
        var wsBeforeRun = false;
        var scrubbed = UrlVaultRegex.Replace(text, match =>
        {
            while (scanCursor < match.Index)
            {
                var ch = text[scanCursor];
                if (ch is '\r' or '\n')
                {
                    hasFileUriOnLine = false;
                    fileUriQuote = '\0';
                    lastNonWsOrWrapperChar = '\0';
                    inWrapperRun = false;
                    wsBeforeRun = false;
                }
                else
                {
                    var closedFileQuote = fileUriQuote != '\0' && ch == fileUriQuote;
                    if (closedFileQuote)
                    {
                        fileUriQuote = '\0';
                    }
                    else if ((ch == 'f' || ch == 'F') &&
                        scanCursor + 7 <= text.Length &&
                        text.AsSpan(scanCursor, 7).Equals("file://", StringComparison.OrdinalIgnoreCase) &&
                        (scanCursor == 0 || !IsUrlSchemeChar(text[scanCursor - 1])))
                    {
                        hasFileUriOnLine = true;
                        if (scanCursor > 0 && text[scanCursor - 1] is '"' or '\'')
                        {
                            fileUriQuote = text[scanCursor - 1];
                        }
                    }
                    if (closedFileQuote)
                    {
                        lastNonWsOrWrapperChar = ch;
                        inWrapperRun = false;
                        wsBeforeRun = false;
                    }
                    else if (char.IsWhiteSpace(ch))
                    {
                        if (!inWrapperRun)
                        {
                            wsBeforeRun = true;
                        }
                    }
                    else if (ch is '(' or '[' or '{' or '"' or '\'')
                    {
                        inWrapperRun = true;
                    }
                    else
                    {
                        lastNonWsOrWrapperChar = ch;
                        inWrapperRun = false;
                        wsBeforeRun = false;
                    }
                }
                scanCursor++;
            }

            var raw = match.Value;
            var afterPathSep = lastNonWsOrWrapperChar is '/' or '\\';
            var embeddedInPath = fileUriQuote != '\0' || (!wsBeforeRun && (hasFileUriOnLine || afterPathSep));
            if (raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase) || embeddedInPath)
            {
                return raw;
            }
            if (match.Groups[2].Success &&
                (!IPAddress.TryParse(match.Groups[2].Value, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6))
            {
                return raw;
            }
            var tail = string.Empty;
            var hasQueryOrFragment = match.Groups[3].Success && match.Groups[3].Length > 0;
            string url;
            if (hasQueryOrFragment)
            {
                var qf = TrimQueryOrFragmentDelimiters(match.Groups[3].Value, text, match.Index, out tail);
                url = match.Groups[1].Value + qf[0] + "[redacted]";
            }
            else
            {
                url = match.Groups[1].Value;
                if (url.EndsWith(';'))
                {
                    url = url[..^1];
                    tail = ";";
                }
                if (match.Index > 0 && text[match.Index - 1] == '\'' && url.EndsWith('\''))
                {
                    url = url[..^1];
                    tail = "'" + tail;
                }
            }
            var idx = vaultedUrls.Count;
            vaultedUrls.Add(url);
            return $"{prefix}{idx}\u001F{tail}";
        });
        scrubbed = FileUriRegex.Replace(scrubbed, "[redacted-path]");
        scrubbed = ParenPathRegex.Replace(scrubbed, "$1[redacted-path]$2");
        scrubbed = ParenRelativeSlashPathRegex.Replace(scrubbed, "$1[redacted-path]$2");
        scrubbed = WindowsPathRegex.Replace(scrubbed, "[redacted-path]");
        scrubbed = RelativeBackslashPathRegex.Replace(scrubbed, "$1[redacted-path]");
        scrubbed = QuotedSpacedRootPathRegex.Replace(scrubbed, "[redacted-path]");
        scrubbed = UnixPathRegex.Replace(scrubbed, "[redacted-path]");
        scrubbed = RelativeSlashPathRegex.Replace(scrubbed, "$1[redacted-path]");
        scrubbed = DoubleQuotedFilenameRegex.Replace(scrubbed, "[redacted-path]");
        scrubbed = SingleQuotedFilenameRegex.Replace(scrubbed, "[redacted-path]");
        scrubbed = KeywordQuotedPathRegex.Replace(scrubbed, "$1[redacted-path]");
        scrubbed = KeywordUnquotedFilenameRegex.Replace(scrubbed, "$1[redacted-path]");
        scrubbed = KeywordAssignedPathRegex.Replace(scrubbed, "$1[redacted-path]");
        scrubbed = BareFilenameRegex.Replace(scrubbed, "$1$2[redacted-path]");
        scrubbed = RestoreVaultedUrls(scrubbed, prefix, vaultedUrls);
        scrubbed = EmailRegex.Replace(scrubbed, "[redacted-email]");
        scrubbed = BearerTokenRegex.Replace(scrubbed, "Bearer [redacted]");
        return scrubbed;
    }

    private static bool IsUrlSchemeChar(char ch) =>
        (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch is '+' or '.' or '-';

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
        if (toCancel != null) CancelQuietly(toCancel);
        NotifyConsentChangedQuietly(consentNotify, consentNotifyValue);
        NotifyPurgeFailureQuietly(purgeNotify, purgeNotifyValue);
        return result;
    }

    public void Track(string name, IReadOnlyDictionary<string, object>? properties = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var json = SerializeProperties(properties);
        CancellationTokenSource? toCancel = null;
        lock (_gate)
        {
            if (!EnsureExternalConsentLocked(out toCancel)) goto CancelAndReturn;
            if (_disposeRequested || _clientDisposed || _consent != TelemetryConsent.Full) return;
            try
            {
                _client.Track(name, json);
            }
            catch (AunException ex)
            {
                Log.For("telemetry").Warn("telemetry track skipped: " + ex.Message);
            }
        }
        return;
    CancelAndReturn:
        if (toCancel != null) CancelQuietly(toCancel);
    }

    public void TrackError(string code, IReadOnlyDictionary<string, object>? properties = null)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        var json = SerializeProperties(properties);
        CancellationTokenSource? toCancel = null;
        lock (_gate)
        {
            if (!EnsureExternalConsentLocked(out toCancel)) goto CancelAndReturn;
            if (_disposeRequested || _clientDisposed || _consent != TelemetryConsent.Full) return;
            try
            {
                _client.TrackError(code, json);
            }
            catch (AunException ex)
            {
                Log.For("telemetry").Warn("telemetry error skipped: " + ex.Message);
            }
        }
        return;
    CancelAndReturn:
        if (toCancel != null) CancelQuietly(toCancel);
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
        CancellationTokenSource? toCancel = null;
        lock (_gate)
        {
            if (!EnsureExternalConsentLocked(out toCancel)) goto CancelAndReturn;
            if (_disposeRequested || _clientDisposed || _consent == TelemetryConsent.Off) return;
            try
            {
                _client.ReportCrash(report);
                _client.Persist();
            }
            catch (AunException ex)
            {
                Log.For("telemetry").Warn("telemetry crash record skipped: " + ex.Message);
            }
        }
        return;
    CancelAndReturn:
        if (toCancel != null) CancelQuietly(toCancel);
    }

    public void Persist()
    {
        CancellationTokenSource? toCancel = null;
        lock (_gate)
        {
            if (!EnsureExternalConsentLocked(out toCancel)) goto CancelAndReturn;
            if (_disposeRequested || _clientDisposed || _consent == TelemetryConsent.Off) return;
            try
            {
                _client.Persist();
            }
            catch (AunException ex)
            {
                Log.For("telemetry").Warn("telemetry persist failed: " + ex.Message);
            }
        }
        return;
    CancelAndReturn:
        if (toCancel != null) CancelQuietly(toCancel);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return;

        CancellationToken revokeToken;
        long flushEpoch;
        string? flushBufferPath;
        CancellationTokenSource? toCancel = null;
        lock (_gate)
        {
            if (!EnsureExternalConsentLocked(out toCancel))
            {
                if (toCancel != null) goto CancelAndReturn;
                return;
            }
            if (_disposeRequested || _clientDisposed || _consent == TelemetryConsent.Off) return;
            revokeToken = _flushCts.Token;
            flushEpoch = _consentEpoch;
            flushBufferPath = _activeBufferPath;
            _inFlightFlushes++;
        }

        Task? nativeFlush = null;
        CancellationTokenSource? monitorCts = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, revokeToken);
            if (linked.IsCancellationRequested) return;

            var flushOverride = FlushOverrideForTesting;
            nativeFlush = flushOverride != null ? flushOverride() : _client.Flush();
            if (_externalConsentToken != null)
            {
                monitorCts = new CancellationTokenSource();
                _ = MonitorExternalConsentWhileFlushingAsync(monitorCts.Token);
            }
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
        finally
        {
            if (nativeFlush == null)
            {
                if (monitorCts != null) CancelQuietly(monitorCts);
                ReleaseInFlightFlush(flushEpoch, flushBufferPath);
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
