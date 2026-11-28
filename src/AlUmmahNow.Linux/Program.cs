using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Media;
using AlUmmahNow.App.Navigation;
using AlUmmahNow.App.Demo;
using AlUmmahNow.App.Services;
using AlUmmahNow.App.Updates;
using AlUmmahNow.App.ViewModels;
using AlUmmahNow.CoreBridge.Native;
using AlUmmahNow.CoreBridge.Temporary;
using AlUmmahNow.Platform.Linux;
using AlUmmahNow.Linux;

Log.AddSink(TextWriterLogSink.StandardError());
Adw.Module.Initialize();
Gst.Module.Initialize();
var gstArgs = Array.Empty<string>();
Gst.Functions.Init(ref gstArgs);

const string Css = """
    .caption-credit { color: #f7f1e8; background: #093041; font-size: 14px; text-decoration: underline; padding: 1px 4px; min-height: 0; }
    .caption-overlay { background-color: rgba(9,48,65,0.82); color: #f7f1e8; padding: 6px 14px; border-radius: 6px; font-size: 20px; }
    /* Not gold in light mode, where it is under 3:1 on the page: the registry's accent is darker there. */
    scale > trough { background-color: alpha(currentColor, 0.55); }
    scale > trough > highlight, scale > trough > slider { background-color: @fanous_accent; }
    /* The toolkit dims with 55% opacity, under 4.5:1 in light mode: the theme's own second color instead. */
    .dim-label { opacity: 1; color: @fanous_text_secondary; }
    /* The header is dark in both modes. */
    headerbar .dim-label { color: @headerbar_fg_color; }
    .aun-badge { border: 1px solid alpha(currentColor, 0.5); border-radius: 999px; padding: 2px 10px; min-height: 18px; font-size: 12px; font-weight: 600; }
    .aun-badge.on-air { background-color: @fanous_primary; color: @fanous_on_primary; border-color: transparent; }
    .aun-pinned { background-color: alpha(currentColor, 0.06); padding: 10px 12px; }
    """;

var environment = AppEnvironment.Resolve(Environment.GetEnvironmentVariable("AUN_ENV"), AppEnvironment.BuildDefault);
var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
if (string.IsNullOrEmpty(configHome)) configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
var store = new SettingsStore(Path.Combine(configHome, "alummahnow", "settings.json"));
var settings = store.Load();
if (Environment.GetEnvironmentVariable("AUN_LANG") is { Length: > 0 } lang) settings.Language = lang;
if (Enum.TryParse<ThemePreference>(Environment.GetEnvironmentVariable("AUN_THEME"), true, out var theme)) settings.Theme = theme;

var automation = Environment.GetEnvironmentVariable("AUN_AUTOMATION") ?? "";
var exitAfter = int.TryParse(Environment.GetEnvironmentVariable("AUN_EXIT_AFTER"), out var s) ? s : 0;

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("AlUmmahNowLinux/0.1");
// AUN_DEMO=1: the fixture catalog for review frames and store screenshots (no network, never in a normal launch).
AlUmmahNow.CoreBridge.ICatalogClient catalog = Environment.GetEnvironmentVariable("AUN_DEMO") == "1"
    ? new AlUmmahNow.App.Demo.DemoCatalogClient(Environment.GetEnvironmentVariable("AUN_DEMO_THUMBS"), settings.Language)
    : new HttpCatalogClient(http, environment.VideosApi);
// CI's watch scenario plays a local fixture (AUN_FIXTURE_MEDIA), never a remote stream.
if (Environment.GetEnvironmentVariable("AUN_FIXTURE_MEDIA") is { Length: > 0 } fixtureMedia) catalog = new AlUmmahNow.App.Demo.FixtureMediaCatalog(catalog, fixtureMedia, settings.Language);
var localizer = new Localizer(settings.Language);
var navigator = new Navigator();
AlUmmahNow.CoreBridge.IAuthSession authSession = Environment.GetEnvironmentVariable("AUN_DEMO_AUTH") is { Length: > 0 } demoAuth
    ? new AlUmmahNow.App.Demo.DemoAuthSession(demoAuth == "waiting")
    : new PkceAuthSession(http, environment.AuthIssuer, environment.ViewerClientId, new LibSecretStore("alummahnow"), new ShellBrowserLauncher(), localizer["auth.browser.done"]);
var auth = new AuthViewModel(authSession);
// AUN_DEMO_LIVE=1: fixture streams for review frames and the live scenarios (no network, never in a normal launch).
AlUmmahNow.CoreBridge.ILiveClient live = Environment.GetEnvironmentVariable("AUN_DEMO_LIVE") is "1" or "off"
    ? new AlUmmahNow.App.Demo.DemoLiveClient(Environment.GetEnvironmentVariable("AUN_FIXTURE_MEDIA"), settings.Language,
        Environment.GetEnvironmentVariable("AUN_DEMO_LIVE") == "off", Environment.GetEnvironmentVariable("AUN_DEMO_REMIND") == "fail")
    : new HttpLiveClient(http, environment.VideosApi, HttpLiveClient.TokenOf(authSession));
var coreVersion = CoreRuntime.VersionOrNone();
// The check for a new version: none in a demo, or in a build that is no release.
var isDemo = Environment.GetEnvironmentVariable("AUN_DEMO") == "1";
var updateInstall = isDemo ? UpdateInstall.Development : UpdateInstalls.OnLinux(UpdateInstalls.IsReleaseBuild, File.Exists("/.flatpak-info"), AppContext.BaseDirectory, UpdateApp.Viewer);
MainWindow? viewer = null;
bool IsBusy() => viewer?.IsLivePlaying == true;
var updateScene = UpdateScene.FromEnvironment(localizer, UpdateInstall.Flatpak, IsBusy);
var version = UpdateText.VersionOf(typeof(MainWindow).Assembly);
var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
if (string.IsNullOrEmpty(stateHome)) stateHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
var initialConsentToken = isDemo ? null : store.LoadTelemetryConsentToken();
var initialTelemetryEnabled = initialConsentToken != null;
settings.TelemetryEnabled = initialTelemetryEnabled;
var telemetry = CoreTelemetryService.CreateOrNull(
    platform: "linux",
    appVersion: version,
    locale: settings.Language,
    videosApiBaseUrl: environment.VideosApi.AbsoluteUri,
    userAgent: "AlUmmahNowLinux/" + version,
    bufferPath: Path.Combine(stateHome, "alummahnow", "telemetry-buffer.json"),
    initialEnabled: initialTelemetryEnabled,
    demo: isDemo,
    externalConsentToken: () => store.LoadTelemetryConsentToken(),
    acquireExternalConsentLock: () => store.TryAcquireTelemetryConsentLock(),
    externalConsentTokenForClaim: claim => store.LoadTelemetryConsentTokenForClaim(claim),
    acquireActiveFlushGuard: () => store.TryAcquireActiveFlushGuard());
if (!isDemo &&
    initialTelemetryEnabled &&
    telemetry.Consent == AlUmmahNow.CoreBridge.TelemetryConsent.Off &&
    CoreTelemetryService.ShouldClearStaleStartupConsent(telemetry) &&
    store.ClearTelemetryConsentIfTokenMatches(initialConsentToken))
{
    settings.TelemetryEnabled = false;
    telemetry.SetConsent(AlUmmahNow.CoreBridge.TelemetryConsent.Off);
}
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is Exception ex) telemetry.ReportCrash(ex.GetType().FullName ?? ex.GetType().Name, ex.GetType().Name, ex.StackTrace);
};
var updateStore = new UpdateStore(Path.Combine(configHome, "alummahnow", "updates.json"));
var app = Adw.Application.New("net.alummahnow.AlUmmahNow", Gio.ApplicationFlags.NonUnique);
var exitCode = 0;

app.OnActivate += async (_, _) =>
{
    var provider = Gtk.CssProvider.New();
    provider.LoadFromString(Css);
    Gtk.StyleContext.AddProviderForDisplay(Gdk.Display.GetDefault()!, provider, 600);
    // The Fawanees tokens from the shared registry (light or dark stylesheet, following Light/Dark).
    var tokens = Gtk.CssProvider.New();
    var styles = Adw.StyleManager.GetDefault();
    void LoadTokens() => tokens.LoadFromString(AlUmmahNow.App.Theme.Fanous.File(styles.GetDark() ? "native-fawanees-dark.css" : "native-fawanees.css"));
    LoadTokens();
    styles.OnNotify += (_, e) =>
    {
        if (e.Pspec.GetName() == "dark") LoadTokens();
    };
    Gtk.StyleContext.AddProviderForDisplay(Gdk.Display.GetDefault()!, tokens, 599);

    var mpris = new MprisSession(SynchronizationContext.Current!);
    IMediaSession session = mpris;
    _ = mpris.StartAsync();

    var player = new GstMediaPlayer();
    MainWindow? window = null;
    app.OnShutdown += (_, _) => window?.Close();
    var updates = updateScene?.Service ?? new UpdateService(
        new CoreUpdateChecker(environment.VideosApi, "viewer", "linux", version, "AlUmmahNowLinux/" + version, updateStore.Load().Core),
        updateStore, updateInstall, version, IsBusy, new DownloadPageDelivery(new ShellBrowserLauncher()));
    var closing = new CancellationTokenSource();
    app.OnShutdown += (_, _) =>
    {
        closing.Cancel();
        telemetry.Persist();
        using var exitFlushCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            Task.Run(() => telemetry.FlushAsync(exitFlushCts.Token)).Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
    };
    window = new MainWindow(app, catalog, localizer, navigator, player, session, http, settings, store, auth, live, environment.ShortLinkHost, updates, telemetry);
    viewer = window;
    try
    {
        auth.Restore();
    }
    catch (IOException ex)
    {
        Log.For("app").Warn("keyring unavailable", ex);
    }


    var sawCaption = false;
    double furthest = 0;
    if (exitAfter > 0)
    {
        GLib.Functions.TimeoutAdd(0, 200, () =>
        {
            if (window.Watch is { } w)
            {
                if (w.Caption.Length > 0) sawCaption = true;
                furthest = Math.Max(furthest, w.Position);
            }

            return true;
        });
        GLib.Functions.TimeoutAdd(0, (uint)exitAfter * 1000, () =>
        {
            var pass = automation.StartsWith("watch:", StringComparison.Ordinal)
                ? furthest > 2 && (sawCaption || Environment.GetEnvironmentVariable("AUN_EXPECT_CAPTION") == "0") && window.Watch?.PlaybackFailed == false
                : AlUmmahNow.App.Demo.LiveAutomation.Passed(automation, window.LiveList, window.LiveWatch, auth, furthest) is { } asAsked
                ? asAsked
                : window.Home.Rails.Count > 0;
            pass = pass && window.LiveTitlesAreWhole;
            var updateWrong = updateScene?.Wrong(window.Updates);
            pass = pass && updateWrong == null;
            if (updateScene != null) Log.For("automation").Info($"UPDATE {updateScene.Name} {updateWrong ?? "ok"}");
            Log.For("automation").Info($"AUTOMATION {automation} core={coreVersion} rails={window.Home.Rails.Count} furthest={furthest:F1} caption={sawCaption} titlesWhole={window.LiveTitlesAreWhole} RESULT {(pass ? "PASS" : "FAIL")}");
            exitCode = pass ? 0 : 3;
            app.Quit();
            return false;
        });
    }

    var started = window.StartAsync();
    _ = updates.RunAsync(closing.Token);
    _ = telemetry.FlushAsync(closing.Token);
    if (updateScene != null)
    {
        GLib.Functions.TimeoutAdd(0, 6000, () =>
        {
            if (updateScene.IsInSettings && updates.IsSectionVisible) navigator.Push(new Route.Settings());
            _ = updateScene.ActAsync(window.Updates);
            return false;
        });
    }

    if (automation.StartsWith("watch:", StringComparison.Ordinal)) navigator.Push(new Route.Watch(automation.Substring(6)));
    if (AlUmmahNow.App.Demo.LiveAutomation.RouteOf(automation) is { } route)
    {
        navigator.Push(route);
        _ = AlUmmahNow.App.Demo.LiveAutomation.ActAsync(automation, () => window.LiveWatch, auth);
    }

    await started;
};

var rc = app.RunWithSynchronizationContext(null);
return rc != 0 ? rc : exitCode;
