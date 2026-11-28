using AlUmmahNow.Core;

namespace AlUmmahNow.CoreBridge.Native;

/// <summary>
/// <see cref="ITelemetryService"/> on aun-core's <see cref="TelemetryClient"/>. The core scrubs every string
/// (e-mails, tokens, URLs' queries, long secrets), keeps the buffer file, purges it when consent is withdrawn and
/// posts batches to the videos API's <c>/api/v1/telemetry/batches</c>. This class only maps the app's on/off to
/// consent, hands it crashes and asks it to flush.
/// </summary>
public sealed class CoreTelemetryService : ITelemetryService, IDisposable
{
    private readonly TelemetryClient _client;

    /// <param name="videosApi">The videos API; reports go only to an https one (none in local dev).</param>
    /// <param name="userAgent">The app's User-Agent, e.g. <c>AlUmmahNowStudioWindows/1.4.0</c>.</param>
    /// <param name="appVersion">The app's version.</param>
    /// <param name="locale">The UI language, <c>ar</c> or <c>en</c>.</param>
    /// <param name="bufferPath">The buffer file, in the app's own data folder.</param>
    /// <param name="enabled">The saved choice; off unless the person turned it on.</param>
    public CoreTelemetryService(Uri videosApi, string userAgent, string appVersion, string locale, string bufferPath, bool enabled)
    {
        var os = AunClient.CurrentOs();
        _client = new TelemetryClient(new TelemetryClientConfig(
            new TelemetryAppInfo(Platform(os.Name), appVersion, os.Version, "desktop", locale),
            IngestUrl: IngestUrl(videosApi),
            UserAgent: userAgent,
            ClientHeader: AunClient.FromUserAgent(userAgent),
            BufferPath: bufferPath));
        SetEnabled(enabled);
    }

    /// <summary>
    /// The app's telemetry: installs the crash hooks, sends what an earlier run left and then sends every 15
    /// minutes. Falls back to doing nothing where the core library cannot load.
    /// </summary>
    public static ITelemetryService Start(Uri videosApi, string userAgent, string appVersion, string locale, string bufferPath, bool enabled)
    {
        ITelemetryService telemetry;
        try
        {
            telemetry = new CoreTelemetryService(videosApi, userAgent, appVersion, locale, bufferPath, enabled);
        }
        catch (Exception)
        {
            telemetry = NullTelemetryService.Instance;
        }
        CrashHooks.Install(telemetry);
        if (telemetry is CoreTelemetryService core)
            core._timer = new Timer(_ => _ = telemetry.FlushAsync(), null, TimeSpan.Zero, TimeSpan.FromMinutes(15));
        return telemetry;
    }

    private Timer? _timer;

    public bool Enabled => Safe(() => _client.Consent() == TelemetryConsentState.Analytics, false);

    public void SetEnabled(bool enabled) =>
        Safe(() => _client.SetConsent(enabled ? TelemetryConsentState.Analytics : TelemetryConsentState.Denied));

    public void ReportCrash(Exception exception) => Safe(() =>
    {
        // ToString() carries the type, message, inner exceptions and stack; the core scrubs and truncates it.
        _client.ReportCrash(new TelemetryCrashReport(
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message,
            exception.ToString(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        _client.Persist();
    });

    public async Task FlushAsync()
    {
        try
        {
            await _client.Flush().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Offline or the server said no: the core keeps the batch for the next flush.
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _client.Dispose();
    }

    /// <summary>The batches endpoint on an https videos API; none otherwise, so local dev never sends.</summary>
    internal static string? IngestUrl(Uri videosApi) =>
        videosApi.Scheme == Uri.UriSchemeHttps ? videosApi.AbsoluteUri.TrimEnd('/') + "/api/v1/telemetry/batches" : null;

    /// <summary>The platform names the server maps to an app (unknown names count as desktop).</summary>
    internal static string Platform(string osName) => osName switch
    {
        "Windows" => "windows",
        "macOS" => "macos",
        "Linux" => "linux",
        _ => osName.ToLowerInvariant(),
    };

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // Reporting must never cause a failure of its own.
        }
    }

    private static T Safe<T>(Func<T> f, T fallback)
    {
        try
        {
            return f();
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
