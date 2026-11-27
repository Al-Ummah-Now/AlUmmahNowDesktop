using System.Text.Json;
using AlUmmahNow.Core;
using AlUmmahNow.CoreBridge.Logging;

namespace AlUmmahNow.CoreBridge.Native;

/// <summary>
/// Wraps <see cref="TelemetryClient"/> from <c>aun-core</c> behind <see cref="ITelemetryService"/>.
/// Consent defaults to off; revoking consent purges both memory and the disk buffer immediately.
/// </summary>
public sealed class CoreTelemetryService : ITelemetryService
{
    private readonly TelemetryClient _client;
    private readonly object _gate = new();
    private bool _disposed;

    public CoreTelemetryService(
        string platform,
        string appVersion,
        string locale,
        string? videosApiBaseUrl,
        string userAgent,
        string? bufferPath,
        bool initialEnabled = false)
    {
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
        bool demo = false)
    {
        if (demo) return NullTelemetryService.Instance;
        try
        {
            return new CoreTelemetryService(platform, appVersion, locale, videosApiBaseUrl, userAgent, bufferPath, initialEnabled);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or AunException)
        {
            Log.For("telemetry").Warn("telemetry unavailable, continuing with no-op service: " + ex.Message);
            return NullTelemetryService.Instance;
        }
    }

    /// <summary>
    /// Resolves <c>https://&lt;host&gt;/api/v1/telemetry/batches</c> when given an HTTPS API base URL;
    /// returns <c>null</c> (no-op sink) for empty or non-HTTPS URLs so local HTTP dev never throws.
    /// </summary>
    public static string? ResolveIngestUrl(string? videosApiBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(videosApiBaseUrl)) return null;
        var trimmed = videosApiBaseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return $"{trimmed}/api/v1/telemetry/batches";
    }

    public TelemetryConsent Consent
    {
        get
        {
            lock (_gate)
            {
                if (_disposed) return TelemetryConsent.Off;
                return _client.Consent() switch
                {
                    TelemetryConsentState.Analytics => TelemetryConsent.Full,
                    TelemetryConsentState.CrashOnly => TelemetryConsent.CrashOnly,
                    _ => TelemetryConsent.Off,
                };
            }
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _disposed ? 0 : (int)_client.PendingCount();
            }
        }
    }

    public void SetConsent(TelemetryConsent consent)
    {
        var state = consent switch
        {
            TelemetryConsent.Full => TelemetryConsentState.Analytics,
            TelemetryConsent.CrashOnly => TelemetryConsentState.CrashOnly,
            _ => TelemetryConsentState.Denied,
        };
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                _client.SetConsent(state);
            }
            catch (AunException ex)
            {
                Log.For("telemetry").Warn("failed to update telemetry consent: " + ex.Message);
            }
        }
    }

    public void Track(string name, IReadOnlyDictionary<string, object>? properties = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var json = SerializeProperties(properties);
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                _client.Track(name, json);
            }
            catch (AunException ex)
            {
                Log.For("telemetry").Warn("telemetry track skipped: " + ex.Message);
            }
        }
    }

    public void TrackError(string code, IReadOnlyDictionary<string, object>? properties = null)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        var json = SerializeProperties(properties);
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                _client.TrackError(code, json);
            }
            catch (AunException ex)
            {
                Log.For("telemetry").Warn("telemetry error skipped: " + ex.Message);
            }
        }
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
        lock (_gate)
        {
            if (_disposed) return;
            _client.AddBreadcrumb(category, message ?? "", mapped);
        }
    }

    public void ReportCrash(string kind, string message, string? stack = null, bool fromPreviousSession = false)
    {
        var report = new TelemetryCrashReport(
            string.IsNullOrWhiteSpace(kind) ? "UnhandledException" : kind,
            string.IsNullOrWhiteSpace(message) ? "Unhandled exception" : message,
            stack,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            fromPreviousSession,
            null);
        lock (_gate)
        {
            if (_disposed) return;
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
    }

    public void Persist()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                _client.Persist();
            }
            catch (AunException ex)
            {
                Log.For("telemetry").Warn("telemetry persist failed: " + ex.Message);
            }
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return;
        lock (_gate)
        {
            if (_disposed || _client.Consent() is TelemetryConsentState.Unknown or TelemetryConsentState.Denied) return;
        }
        try
        {
            await _client.Flush().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AunException or ObjectDisposedException or OperationCanceledException)
        {
            Log.For("telemetry").Warn("telemetry flush skipped: " + ex.Message);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _client.Dispose();
        }
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
