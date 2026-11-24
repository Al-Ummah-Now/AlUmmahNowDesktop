using Path = System.IO.Path;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Services;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Native;
using AlUmmahNow.CoreBridge.Native.Studio;
using AlUmmahNow.CoreBridge.Passkeys;
using AlUmmahNow.Platform.Windows;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Demo;
using AlUmmahNow.Studio.Encode;
using AlUmmahNow.Studio.Shell;
using AlUmmahNow.Studio.Upload;
using AlUmmahNow.Studio.Videos;
using AlUmmahNow.Studio.Analytics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AlUmmahNow.Studio.WindowsApp;

public sealed partial class App : Application
{
    // Held in fields: a DispatcherQueueTimer referenced only locally can be garbage collected.
    private DispatcherQueueTimer? _refresh;
    private UploadsViewModel? _uploads;
    private DispatcherQueueTimer? _sceneTimer;
    private DispatcherQueueTimer? _endTimer;
    private StudioWindow? _window;

    public App()
    {
        InitializeComponent();
        // Light or Dark (no "System"): the saved choice, Dark by default; CI and screenshots force one with
        // AUN_THEME=dark|light. Only settable here, before any window.
        var theme = Environment.GetEnvironmentVariable("AUN_THEME");
        if (theme is not ("dark" or "light"))
        {
            var saved = new SettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlUmmahNowStudio", "settings.json")).Load();
            theme = saved.Theme == ThemePreference.Light ? "light" : "dark";
        }

        RequestedTheme = theme == "dark" ? ApplicationTheme.Dark : ApplicationTheme.Light;
        UnhandledException += (_, e) =>
        {
            Log.For("app").Error("unhandled exception", e.Exception);
            File.AppendAllText(ResultPath + ".crash.txt", LogScrubber.Scrub(e.Exception.ToString()) + "\n");
        };

    }

    private static string ResultPath => Environment.GetEnvironmentVariable("AUN_RESULT") ?? "aun-studio-result.txt";

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Application.Resources only exists from here on (not in App()).
        try
        {
            LanternGlass.Apply(this);
        }
        catch (Exception ex)
        {
            // Never a dead Studio over styling: report it, keep the stock look.
            File.AppendAllText(ResultPath + ".crash.txt", "theme: " + LogScrubber.Scrub(ex.ToString()) + "\n");
        }

        var automation = Environment.GetEnvironmentVariable("AUN_AUTOMATION") ?? "";
        var tabName = Environment.GetEnvironmentVariable("AUN_TAB") ?? "uploads";
        var scene = Environment.GetEnvironmentVariable("AUN_SCENE") ?? "";
        var exitAfter = int.TryParse(Environment.GetEnvironmentVariable("AUN_EXIT_AFTER"), out var s) ? s : 0;
        var ui = DispatcherQueue.GetForCurrentThread();
        // CI and screenshots only: canned accounts and data, nothing sent to a server.
        var demo = Environment.GetEnvironmentVariable("AUN_DEMO") == "1";

        var environment = AppEnvironment.Resolve(Environment.GetEnvironmentVariable("AUN_ENV"), AppEnvironment.BuildDefault);
        // Each CI scenario starts clean.
        var dataRoot = demo || Environment.GetEnvironmentVariable("AUN_SIMULATE_UPLOADS") == "1"
            ? Path.Combine(Path.GetTempPath(), "aun-studio-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlUmmahNowStudio");
        Log.AddSink(new FileLogSink(Path.Combine(dataRoot, "logs", "studio.log")));
        Log.AddSink(new DebugLogSink());
        var log = Log.For("app");
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        log.Info($"Al Ummah Now Studio {version} for Windows, environment {environment.Name}, core {CoreRuntime.VersionOrNone()}");

        var settingsStore = new SettingsStore(Path.Combine(dataRoot, "settings.json"));
        var settings = settingsStore.Load();
        // The saved language (Settings), Arabic by default; CI and screenshots force one with AUN_LANG.
        var localizer = new Localizer(Environment.GetEnvironmentVariable("AUN_LANG") is { Length: > 0 } l ? l : settings.Language);
        var userAgent = "AlUmmahNowStudioWindows/" + version;

        // The passkey sheet is modal to the Studio window; its handle is filled in once it exists.
        var hwnd = IntPtr.Zero;
        IStudioAccounts accounts;
        IApiTransport transport;
        Func<string, IUploadService> uploadServiceFor;
        if (demo)
        {
            accounts = new DemoAccounts();
            transport = new DemoTransport(DemoTransport.FeaturesOff(Environment.GetEnvironmentVariable("AUN_DEMO_FEATURES_OFF")), Environment.GetEnvironmentVariable("AUN_DEMO_THUMBS"), localizer.Language, withDeleted: scene.StartsWith("deleted", StringComparison.Ordinal));
            uploadServiceFor = account => new SimulatedUploadService(account, TimeSpan.FromMilliseconds(250));
        }
        else
        {
            var passkeys = new PasskeyFlows(new NativePasskeyClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, environment.AuthIssuer, userAgent), new WindowsWebAuthn(() => hwnd));
            var core = new CoreStudioAccounts(environment.AuthIssuer, environment.StudioClientId, new DpapiSecureStore(Path.Combine(dataRoot, "secure")), new ShellBrowserLauncher(), localizer["auth.browser.done"], passkeys: passkeys);
            accounts = core;
            transport = new CoreApiTransport(core, environment.VideosApi, userAgent, localizer.Language);
            uploadServiceFor = account => new CoreUploadService(core, environment.VideosApi, userAgent, Path.Combine(dataRoot, "uploads"), account);
        }

        var api = new StudioApiClient(transport);
        var shell = new StudioShellViewModel(accounts, api, new FeaturesClient(transport),
            profiles: new ProfileClient(transport), language: localizer.Language,
            knownNames: settings.AccountNames, saveNames: names =>
            {
                settings.AccountNames = names.ToDictionary(kv => kv.Key, kv => kv.Value);
                settingsStore.Save(settings);
            });
        _uploads = new UploadsViewModel(shell, api, uploadServiceFor, Path.Combine(dataRoot, demo ? "demo" : "studio"),
            demo ? () => AlUmmahNow.App.Demo.DemoClock.Now : null);
        var uploadsVm = _uploads;
        // Review frames, scene "active": a demo queue with one finished and one running job.
        var encode = demo && scene == "active"
            ? new EncodeQueue(new AlUmmahNow.Studio.Demo.DemoEncodeBackend("Media Foundation H.264"), AlUmmahNow.Studio.Demo.DemoEncodeBackend.Clock())
            : new EncodeQueue(new MediaFoundationEncodeBackend());

        var banner = demo ? localizer["studio.shell.demo"] : environment.Name != "prod" ? localizer.Format("studio.shell.environment", environment.Name.ToUpperInvariant()) : null;
        // Store screenshots (.github/workflows/store-screenshots.yml) show the demo data without the notice.
        if (demo && Environment.GetEnvironmentVariable("AUN_STORE_SHOT") == "1") banner = null;
        _window = new StudioWindow(localizer, shell, banner);
        var window = _window;
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window.Window);

        var watchDir = Environment.GetEnvironmentVariable("AUN_WATCH_DIR");
        uploadsVm.Watch(string.IsNullOrEmpty(watchDir) ? settings.WatchFolder : watchDir);

        async Task<string?> PickFolderAsync()
        {
            var picker = new global::Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window.Window));
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }

        async Task<IReadOnlyList<string>> PickFilesAsync()
        {
            var picker = new global::Windows.Storage.Pickers.FileOpenPicker();
            foreach (var ext in UploadPlanner.VideoExtensions) picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window.Window));
            var files = await picker.PickMultipleFilesAsync();
            return files.Select(f => f.Path).ToList();
        }

        var uploads = new UploadPage(localizer, uploadsVm, shell, () => window.Window.Content?.XamlRoot, PickFilesAsync, PickFolderAsync, path =>
        {
            settings.WatchFolder = path;
            settingsStore.Save(settings);
        });
        var videosVm = new VideosViewModel(shell, api,
            () => settings.RecentSearches ?? new List<string>(),
            recents =>
            {
                settings.RecentSearches = recents.ToList();
                settingsStore.Save(settings);
            },
            language: () => localizer.Language);
        var analyticsVm = new AnalyticsViewModel(shell, api, demo ? () => AlUmmahNow.App.Demo.DemoClock.Now : null);
        var videos = new VideosPage(localizer, videosVm, uploadsVm, () => window.Window.Content?.XamlRoot);
        var analytics = new AnalyticsPage(localizer, analyticsVm);
        // Upload (creator tier) and Analytics (feature_off) are left out of the navigation when off.
        var pages = new StudioPages(shell, analyticsVm, tabName);
        var encodePage = new EncodePage(localizer, encode);
        var timeline = new TimelinePage(localizer);
        window.AddPage(StudioPages.Uploads, localizer["studio.tab.upload"], Symbol.Upload, uploads.Root);
        window.AddPage(StudioPages.Videos, localizer["studio.tab.videos"], Symbol.List, videos.Root);
        window.AddPage(StudioPages.Analytics, localizer["studio.tab.analytics"], Symbol.Calculator, analytics.Root);
        window.AddPage(StudioPages.Encode, localizer["studio.tab.encode"], Symbol.Video, encodePage.Root);
        window.AddPage(StudioPages.Timeline, localizer["studio.tab.timeline"], Symbol.Edit, timeline.Root);
        var settingsPage = new SettingsPage(localizer, settings, settingsStore, shell, uploadsVm, PickFolderAsync, window.SignOut, window.AddPasskeyAsync, version);
        window.AddPage(StudioPages.Settings, localizer["studio.tab.settings"], Symbol.Setting, settingsPage.Root);
        window.UsePages(pages);
        window.SearchRequested += (_, q) => videos.SearchFor(q);
        window.Window.Activate();
        // Features are re-read when Studio comes back to the foreground (at most once a minute).
        window.Window.Activated += (sender, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) _ = shell.RefreshFeaturesAsync();
        };
        window.Window.Closed += (_, _) => uploadsVm.Dispose();

        _refresh = ui.CreateTimer();
        _refresh.Interval = TimeSpan.FromMilliseconds(300);
        _refresh.Tick += (_, _) =>
        {
            uploadsVm.Tick();
            encodePage.Tick();
        };
        _refresh.Start();

        _ = shell.InitializeAsync();
        // CI screenshots: stage a folder on the Upload page.
        if (Environment.GetEnvironmentVariable("AUN_STAGE_DIR") is { Length: > 0 } stageDir) uploadsVm.AddFolder(stageDir, recursive: false);

        EncodeJob? job = null;
        if (automation.StartsWith("encode:", StringComparison.Ordinal))
        {
            var source = automation.Substring(7);
            job = encode.Add(new EncodeRequest(source, source + ".out.mp4", EncodePreset.Lecture));
        }

        if (scene.Length > 0)
        {
            _sceneTimer = ui.CreateTimer();
            _sceneTimer.Interval = TimeSpan.FromSeconds(5);
            _sceneTimer.IsRepeating = false;
            _sceneTimer.Tick += (_, _) =>
            {
                if (scene == "accounts") window.OpenAccountMenu();
                else if (scene == "channels") window.OpenChannelPicker();
                else if (scene == "signout") window.AskSignOut();
                else if (scene == "searchfocus") window.FocusSearch();
                else if (scene == "addpasskey") _ = window.AddPasskeyAsync();
                else if (scene == "scheduled") uploads.RunScene(scene);
                else if (scene == "active") AlUmmahNow.Studio.Demo.DemoEncodeBackend.Seed(encode, Path.GetTempPath());
                // The demo's second channel has no videos: the Videos empty state.
                else if (scene == "emptychannel") shell.SelectChannel(shell.Channels.ElementAtOrDefault(1));
                else videos.RunScene(scene);
            };
            _sceneTimer.Start();
        }

        if (exitAfter > 0)
        {
            _endTimer = ui.CreateTimer();
            _endTimer.Interval = TimeSpan.FromSeconds(exitAfter);
            _endTimer.IsRepeating = false;
            _endTimer.Tick += (_, _) =>
            {
                var snapshot = uploadsVm.Items;
                var uploadsDone = snapshot.Count > 0 && snapshot.All(i => i.Phase == UploadPhase.Done);
                var pass = job != null ? job.Status == EncodeStatus.Completed
                    : automation == "shell" ? shell.State == ShellState.Ready && shell.ActiveChannel != null
                    : uploadsDone || tabName != "uploads";
                pass = pass && videos.CardsAreWhole && LanternGlass.Missing == 0 && LanternGlass.IsPainted
                    && (scene != "searchfocus" || window.SearchHasFocus)
                    && (scene != "searchhover" || window.SearchIsHovered)
                    && (!scene.StartsWith("highcontrast", StringComparison.Ordinal) || LanternGlass.IsHighContrast);
                File.WriteAllText(ResultPath, $"AUTOMATION {tabName} page={pages.Current} upload={pages.IsVisible(StudioPages.Uploads)} analytics={pages.IsVisible(StudioPages.Analytics)} bulkSchedule={uploadsVm.BulkScheduleOn} scene={scene} shell={shell.State} channel={shell.ActiveChannel?.Id} accounts={shell.AccountList.Count} uploads={snapshot.Count} uploadsDone={uploadsDone} encode={job?.Status} encoder={job?.UsedEncoder?.Name} error={job?.Error} searchFocused={window.SearchHasFocus} fieldFocused={window.FieldHasFocus} cardsWhole={videos.CardsAreWhole} searchHovered={window.SearchIsHovered} highContrast={LanternGlass.IsHighContrast} painted={LanternGlass.IsPainted} missing={LanternGlass.Missing} RESULT {(pass ? "PASS" : "FAIL")}");
                Exit();
            };
            _endTimer.Start();
        }
    }
}

/// <summary>Debugger output (DebugView, Visual Studio).</summary>
internal sealed class DebugLogSink : ILogSink
{
    public void Write(LogEntry entry) => System.Diagnostics.Debug.WriteLine(entry.ToString());
}
