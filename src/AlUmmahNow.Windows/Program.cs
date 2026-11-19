using AlUmmahNow.App.Demo;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Navigation;
using AlUmmahNow.App.Services;
using AlUmmahNow.App.ViewModels;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Native;
using AlUmmahNow.CoreBridge.Temporary;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace AlUmmahNow.Windows;

public sealed partial class App : Application
{
    private Window? _window;

    // Held in fields: a DispatcherQueueTimer referenced only locally can be garbage collected.
    private DispatcherQueueTimer? _sampleTimer;
    private DispatcherQueueTimer? _endTimer;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => ReportCrash("unhandled: " + e.Message + "\n" + e.Exception);
        try
        {
            ViewerTheme.Apply(this);
        }
        catch (Exception ex)
        {
            // Never a dead viewer over styling: report it, keep the stock accent.
            ReportCrash("theme: " + ex);
        }
    }

    private static void ReportCrash(string text)
    {
        var path = Environment.GetEnvironmentVariable("AUN_RESULT") ?? "aun-result.txt";
        File.AppendAllText(path + ".crash.txt", text + "\n");
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            await LaunchAsync();
        }
        catch (Exception ex)
        {
            ReportCrash("launch: " + ex);
            throw;
        }
    }

    private async Task LaunchAsync()
    {

        var environment = AppEnvironment.Resolve(Environment.GetEnvironmentVariable("AUN_ENV"), AppEnvironment.BuildDefault);
        var store = new SettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlUmmahNow", "settings.json"));
        var settings = store.Load();
        if (Environment.GetEnvironmentVariable("AUN_LANG") is { Length: > 0 } lang) settings.Language = lang;
        if (Enum.TryParse<ThemePreference>(Environment.GetEnvironmentVariable("AUN_THEME"), true, out var theme)) settings.Theme = theme;
        var automation = Environment.GetEnvironmentVariable("AUN_AUTOMATION") ?? "";
        var exitAfter = int.TryParse(Environment.GetEnvironmentVariable("AUN_EXIT_AFTER"), out var s) ? s : 0;

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AlUmmahNowWindows/0.1");
        // AUN_DEMO=1: fixture catalog for the store screenshots (no network, never in a normal launch).
        ICatalogClient catalog = Environment.GetEnvironmentVariable("AUN_DEMO") == "1"
            ? new DemoCatalogClient(Environment.GetEnvironmentVariable("AUN_DEMO_THUMBS"))
            : new HttpCatalogClient(http, environment.VideosApi);
        // CI's watch scenario plays a local fixture (AUN_FIXTURE_MEDIA), never a remote stream.
        if (Environment.GetEnvironmentVariable("AUN_FIXTURE_MEDIA") is { Length: > 0 } fixtureMedia) catalog = new FixtureMediaCatalog(catalog, fixtureMedia);
        var localizer = new Localizer(settings.Language);
        var navigator = new Navigator();
        var secureStore = new DpapiSecureStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlUmmahNow", "secure"));
        var authSession = new PkceAuthSession(http, environment.AuthIssuer, environment.ViewerClientId, secureStore, new ShellBrowserLauncher(), localizer["auth.browser.done"]);
        var auth = new AuthViewModel(authSession);
        auth.Restore();
        var coreVersion = CoreRuntime.VersionOrNone();
        var ui = DispatcherQueue.GetForCurrentThread();
        var player = new WindowsMediaPlayer(ui);
        var session = new SmtcSession(player.Player, ui);
        var shell = new Shell(catalog, localizer, navigator, player, session, settings, store, auth);

        _window = new Window { Title = localizer["app.name"], Content = shell.Root };
        _window.AppWindow.Resize(new global::Windows.Graphics.SizeInt32(1280, 800));
        if (_window.AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        _window.Closed += (_, _) => player.Stop();
        _window.Activate();

        var furthest = 0d;
        var sawCaption = false;
        if (exitAfter > 0)
        {
            var sample = _sampleTimer = ui.CreateTimer();
            sample.Interval = TimeSpan.FromMilliseconds(200);
            sample.Tick += (_, _) =>
            {
                if (shell.Watch is { } w)
                {
                    furthest = Math.Max(furthest, w.Position);
                    sawCaption |= w.Caption.Length > 0;
                }
            };
            sample.Start();
            var end = _endTimer = ui.CreateTimer();
            end.Interval = TimeSpan.FromSeconds(exitAfter);
            end.IsRepeating = false;
            end.Tick += (_, _) =>
            {
                var isWatch = automation.StartsWith("watch:", StringComparison.Ordinal);
                var pass = isWatch ? furthest > 2 && (sawCaption || Environment.GetEnvironmentVariable("AUN_EXPECT_CAPTION") == "0") : shell.Home.Rails.Count > 0;
                File.WriteAllText(Environment.GetEnvironmentVariable("AUN_RESULT") ?? "aun-result.txt",
                    $"AUTOMATION {automation} core={coreVersion} rails={shell.Home.Rails.Count} furthest={furthest:F1} caption={sawCaption} failed={shell.Watch?.PlaybackFailed} RESULT {(pass ? "PASS" : "FAIL")}");
                player.Stop();
                Exit();
            };
            end.Start();
        }

        var started = shell.StartAsync();
        if (automation.StartsWith("watch:", StringComparison.Ordinal)) navigator.Push(new Route.Watch(automation.Substring(6)));
        await started;
    }
}
