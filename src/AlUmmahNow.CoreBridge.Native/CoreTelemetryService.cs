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
    private static readonly Regex FileUriRegex = new(@"file://[^\r\n]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WindowsPathRegex = new(@"(?:(?<![\p{L}\p{N}])[A-Za-z]:[\\/]|\\\\)[^\r\n]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex QuotedSpacedRootPathRegex = new(@"[""']/[ \t]+[^\r\n]+", RegexOptions.Compiled);
    private static readonly Regex UnixPathRegex = new(@"(?<![\p{L}\p{M}\p{N}._/-])(?:~/|(?<![A-Za-z][A-Za-z0-9+.-]{0,31}:)//+(?=[ \t]*[^\s/])|/(?=[ \t]*[^\s/]))[^\r\n]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly TelemetryClient _client;
    private readonly string? _bufferPath;
    private readonly Func<bool>? _isExternallyEnabled;
    private readonly object _gate = new();
    private TelemetryConsent _consent = TelemetryConsent.Off;
    private CancellationTokenSource _flushCts = new();
    private int _inFlightFlushes;
    private long _consentEpoch;
    private int _revokedFlushesRemaining;
    private bool _disposeRequested;
    private bool _clientDisposed;
    private bool _lastPurgeFailed;
    internal Func<Task>? FlushOverrideForTesting { get; set; }
    internal int ExternalPollIntervalMsForTesting { get; set; } = 50;
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
        Func<bool>? isExternallyEnabled = null)
    {
        _bufferPath = bufferPath;
        _isExternallyEnabled = isExternallyEnabled;
        var os = AunClient.CurrentOs();
        var app = new TelemetryAppInfo(
            platform,
            appVersion,
            string.IsNullOrEmpty(os.Version) ? os.Name : $"{os.Name} {os.Version}",
            "desktop",
            locale);

        var ingestUrl = ResolveIngestUrl(videosApiBaseUrl);
        var clientHeader = AunClient.FromUserAgent(userAgent, os);
        var config = new TelemetryClientConfig(
            app,
            ingestUrl,
            userAgent,
            clientHeader,
            bufferPath,
            null,
            null);

        _client = new TelemetryClient(config);
        SetConsent(initialEnabled ? TelemetryConsent.Full : TelemetryConsent.Off);
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
        Func<bool>? isExternallyEnabled = null)
    {
        if (demo) return NullTelemetryService.Instance;
        try
        {
            return new CoreTelemetryService(platform, appVersion, locale, videosApiBaseUrl, userAgent, bufferPath, initialEnabled, isExternallyEnabled);
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
        var scrubbed = FileUriRegex.Replace(text, "[redacted-path]");
        scrubbed = WindowsPathRegex.Replace(scrubbed, "[redacted-path]");
        scrubbed = QuotedSpacedRootPathRegex.Replace(scrubbed, "[redacted-path]");
        return UnixPathRegex.Replace(scrubbed, "[redacted-path]");
    }

    public TelemetryConsent Consent
    {
        get
        {
            lock (_gate) return _disposeRequested ? TelemetryConsent.Off : _consent;
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
        bool result;
        lock (_gate)
        {
            if (consent == TelemetryConsent.Off)
            {
                result = RevokeLocked(out toCancel, "telemetry disk purge failed on opt-out: ", ref purgeNotify, ref purgeNotifyValue);
            }
            else if (_disposeRequested || _clientDisposed)
            {
                return false;
            }
            else if (_revokedFlushesRemaining > 0 || (_lastPurgeFailed && !TryDeleteBufferFallback()))
            {
                return false;
            }
            else
            {
                if (_lastPurgeFailed)
                {
                    UpdatePurgeFailedLocked(false, ref purgeNotify, ref purgeNotifyValue);
                }
                try
                {
                    _client.SetConsent(state);
                    _consent = consent;
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
        }
        if (toCancel != null) CancelQuietly(toCancel);
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
            if (_isExternallyEnabled != null)
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
                    var (svc, cts, epoch) = ((CoreTelemetryService, CancellationTokenSource?, long))state!;
                    if (cts != null) CancelQuietly(cts);
                    svc.ReleaseInFlightFlush(epoch);
                },
                (this, monitorToStop, flushEpoch),
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
                ReleaseInFlightFlush(flushEpoch);
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
        if (_disposeRequested || _clientDisposed || _consent == TelemetryConsent.Off || _isExternallyEnabled == null)
        {
            return true;
        }
        bool stillEnabled;
        try
        {
            stillEnabled = _isExternallyEnabled();
        }
        catch
        {
            stillEnabled = false;
        }
        if (stillEnabled) return true;

        Action<bool>? purgeNotify = null;
        bool purgeNotifyValue = false;
        RevokeLocked(out toCancel, "telemetry disk purge failed after cross-instance opt-out: ", ref purgeNotify, ref purgeNotifyValue);
        if (purgeNotify != null)
        {
            var notify = purgeNotify;
            var value = purgeNotifyValue;
            _ = Task.Run(() => NotifyPurgeFailureQuietly(notify, value));
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
        ref bool purgeNotifyValue)
    {
        _consent = TelemetryConsent.Off;
        _consentEpoch++;
        _revokedFlushesRemaining = _inFlightFlushes;
        toCancel = _flushCts;
        _flushCts = new CancellationTokenSource();
        if (_clientDisposed)
        {
            UpdatePurgeFailedLocked(!TryDeleteBufferFallback(), ref purgeNotify, ref purgeNotifyValue);
            return !_lastPurgeFailed;
        }
        try
        {
            _client.SetConsent(TelemetryConsentState.Denied);
            UpdatePurgeFailedLocked(!TryDeleteSiblingTempFiles(), ref purgeNotify, ref purgeNotifyValue);
        }
        catch (AunException ex)
        {
            UpdatePurgeFailedLocked(!TryDeleteBufferFallback(), ref purgeNotify, ref purgeNotifyValue);
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

    private void ReleaseInFlightFlush(long flushEpoch)
    {
        Action<bool>? purgeNotify = null;
        bool purgeNotifyValue = false;
        lock (_gate)
        {
            _inFlightFlushes--;
            if (flushEpoch < _consentEpoch)
            {
                _revokedFlushesRemaining = Math.Max(0, _revokedFlushesRemaining - 1);
                if (!string.IsNullOrWhiteSpace(_bufferPath))
                {
                    // A flush already in flight when consent was revoked may finish its final
                    // persist rename after SetConsent(Denied) deleted the file; remove any remnant.
                    // Ordinary Dispose() without revocation does not advance _consentEpoch, so
                    // retryable buffers survive normal shutdown.
                    UpdatePurgeFailedLocked(!TryDeleteBufferFallback(), ref purgeNotify, ref purgeNotifyValue);
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

    private bool TryDeleteBufferFallback()
    {
        if (string.IsNullOrWhiteSpace(_bufferPath)) return true;
        var mainOk = false;
        try
        {
            // Do NOT pre-check with File.Exists/Directory.Exists: both return false when directory
            // search permission is denied, which would falsely report purge success while data remains.
            File.Delete(_bufferPath);
            mainOk = true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            mainOk = false;
        }
        var tempsOk = TryDeleteSiblingTempFiles();
        return mainOk && tempsOk;
    }

    private bool TryDeleteSiblingTempFiles()
    {
        if (string.IsNullOrWhiteSpace(_bufferPath)) return true;
        var dir = Path.GetDirectoryName(_bufferPath);
        var fileName = Path.GetFileName(_bufferPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(fileName)) return true;
        try
        {
            var pattern = $".{fileName}.*.tmp";
            var allDeleted = true;
            foreach (var tmp in Directory.EnumerateFiles(dir, pattern))
            {
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
