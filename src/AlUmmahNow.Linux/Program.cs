using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Media;
using AlUmmahNow.App.Navigation;
using AlUmmahNow.App.Services;
using AlUmmahNow.App.ViewModels;
using AlUmmahNow.CoreBridge.Native;
using AlUmmahNow.CoreBridge.Temporary;
using AlUmmahNow.Platform.Linux;
using AlUmmahNow.Linux;

Adw.Module.Initialize();
Gst.Module.Initialize();
var gstArgs = Array.Empty<string>();
Gst.Functions.Init(ref gstArgs);

const string Css = """
    .caption-overlay { background-color: rgba(9,48,65,0.82); color: #f7f1e8; padding: 6px 14px; border-radius: 6px; font-size: 20px; }
    @define-color accent_bg_color #ffa037;
    @define-color accent_color #ffa037;
    @define-color accent_fg_color #093041;
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
var catalog = new HttpCatalogClient(http, environment.VideosApi);
var localizer = new Localizer(settings.Language) { EasternNumerals = settings.EasternNumerals };
var navigator = new Navigator();
var authSession = new PkceAuthSession(http, environment.AuthIssuer, environment.ViewerClientId, new LibSecretStore("alummahnow"), new ShellBrowserLauncher(), localizer["auth.browser.done"]);
var auth = new AuthViewModel(authSession);
var coreVersion = CoreRuntime.VersionOrNone();
var app = Adw.Application.New("net.alummahnow.AlUmmahNow", Gio.ApplicationFlags.NonUnique);
var exitCode = 0;

app.OnActivate += async (_, _) =>
{
    var provider = Gtk.CssProvider.New();
    provider.LoadFromString(Css);
    Gtk.StyleContext.AddProviderForDisplay(Gdk.Display.GetDefault()!, provider, 600);

    var mpris = new MprisSession(SynchronizationContext.Current!);
    IMediaSession session = mpris;
    _ = mpris.StartAsync();

    var player = new GstMediaPlayer();
    var window = new MainWindow(app, catalog, localizer, navigator, player, session, http, settings, store, auth);
    try
    {
        auth.Restore();
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine("keyring: " + ex.Message);
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
                ? furthest > 2 && sawCaption && window.Watch?.PlaybackFailed == false
                : window.Home.Rails.Count > 0;
            Console.WriteLine($"AUTOMATION {automation} core={coreVersion} rails={window.Home.Rails.Count} furthest={furthest:F1} caption={sawCaption} RESULT {(pass ? "PASS" : "FAIL")}");
            exitCode = pass ? 0 : 3;
            app.Quit();
            return false;
        });
    }

    var started = window.StartAsync();
    if (automation.StartsWith("watch:", StringComparison.Ordinal)) navigator.Push(new Route.Watch(automation.Substring(6)));
    await started;
};

var rc = app.RunWithSynchronizationContext(null);
return rc != 0 ? rc : exitCode;
