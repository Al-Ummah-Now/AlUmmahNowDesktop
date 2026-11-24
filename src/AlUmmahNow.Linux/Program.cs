using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Media;
using AlUmmahNow.App.Navigation;
using AlUmmahNow.App.Services;
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
    .caption-overlay { background-color: rgba(9,48,65,0.82); color: #f7f1e8; padding: 6px 14px; border-radius: 6px; font-size: 20px; }
    scale > trough > slider { background-color: @fanous_primary; }
    /* The toolkit dims with 55% opacity, under 4.5:1 in light mode: the theme's own second color instead. */
    .dim-label { opacity: 1; color: @fanous_text_secondary; }
    .aun-badge { border: 1px solid alpha(currentColor, 0.5); border-radius: 999px; padding: 0 10px; font-size: 12px; font-weight: 600; }
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
var authSession = new PkceAuthSession(http, environment.AuthIssuer, environment.ViewerClientId, new LibSecretStore("alummahnow"), new ShellBrowserLauncher(), localizer["auth.browser.done"]);
var auth = new AuthViewModel(authSession);
// AUN_DEMO_LIVE=1: fixture streams for review frames and the live scenarios (no network, never in a normal launch).
AlUmmahNow.CoreBridge.ILiveClient live = Environment.GetEnvironmentVariable("AUN_DEMO_LIVE") == "1"
    ? new AlUmmahNow.App.Demo.DemoLiveClient(Environment.GetEnvironmentVariable("AUN_FIXTURE_MEDIA"), settings.Language)
    : new HttpLiveClient(http, environment.VideosApi, HttpLiveClient.TokenOf(authSession));
var coreVersion = CoreRuntime.VersionOrNone();
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
    window = new MainWindow(app, catalog, localizer, navigator, player, session, http, settings, store, auth, live, environment.ShortLinkHost);
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
                : automation == "live" ? window.LiveList.State == LoadState.Content && window.LiveList.All.Count > 0
                : automation.StartsWith("live:", StringComparison.Ordinal) ? window.LiveWatch is { State: LoadState.Content, HasChat: true } l && (l.Watch == null || furthest > 2)
                : window.Home.Rails.Count > 0;
            Log.For("automation").Info($"AUTOMATION {automation} core={coreVersion} rails={window.Home.Rails.Count} furthest={furthest:F1} caption={sawCaption} RESULT {(pass ? "PASS" : "FAIL")}");
            exitCode = pass ? 0 : 3;
            app.Quit();
            return false;
        });
    }

    var started = window.StartAsync();
    if (automation.StartsWith("watch:", StringComparison.Ordinal)) navigator.Push(new Route.Watch(automation.Substring(6)));
    if (automation == "live") navigator.Push(new Route.LiveList());
    if (automation.StartsWith("live:", StringComparison.Ordinal)) navigator.Push(new Route.Live(automation.Substring(5)));
    await started;
};

var rc = app.RunWithSynchronizationContext(null);
return rc != 0 ? rc : exitCode;
