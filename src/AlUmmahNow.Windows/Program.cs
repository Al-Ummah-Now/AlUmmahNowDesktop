using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Navigation;
using AlUmmahNow.App.Services;
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

        var environment = AppEnvironment.Resolve(Environment.GetEnvironmentVariable("AUN_ENV"), AppEnvironment.Qa);
        var store = new SettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlUmmahNow", "settings.json"));
        var settings = store.Load();
        if (Environment.GetEnvironmentVariable("AUN_LANG") is { Length: > 0 } lang) settings.Language = lang;
        if (Enum.TryParse<ThemePreference>(Environment.GetEnvironmentVariable("AUN_THEME"), true, out var theme)) settings.Theme = theme;
        var automation = Environment.GetEnvironmentVariable("AUN_AUTOMATION") ?? "";
        var exitAfter = int.TryParse(Environment.GetEnvironmentVariable("AUN_EXIT_AFTER"), out var s) ? s : 0;

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AlUmmahNowWindows/0.1");
        var catalog = new HttpCatalogClient(http, environment.VideosApi);
        var localizer = new Localizer(settings.Language) { EasternNumerals = settings.EasternNumerals };
        var navigator = new Navigator();
        var ui = DispatcherQueue.GetForCurrentThread();
        var player = new WindowsMediaPlayer(ui);
        var session = new SmtcSession(player.Player, ui);
        var shell = new Shell(catalog, localizer, navigator, player, session, settings, store);

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
                var pass = isWatch ? furthest > 2 && sawCaption : shell.Home.Rails.Count > 0;
                File.WriteAllText(Environment.GetEnvironmentVariable("AUN_RESULT") ?? "aun-result.txt",
                    $"AUTOMATION {automation} rails={shell.Home.Rails.Count} furthest={furthest:F1} caption={sawCaption} failed={shell.Watch?.PlaybackFailed} RESULT {(pass ? "PASS" : "FAIL")}");
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
