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
using AlUmmahNow.Studio.Captions;
using AlUmmahNow.Studio.Institution;
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
        // AUN_THEME=dark|light forces a theme (CI screenshots); otherwise Windows' app mode applies. Only settable here.
        var theme = Environment.GetEnvironmentVariable("AUN_THEME");
        if (theme == "dark") RequestedTheme = ApplicationTheme.Dark;
        else if (theme == "light") RequestedTheme = ApplicationTheme.Light;
        UnhandledException += (_, e) =>
        {
            Log.For("app").Error("unhandled exception", e.Exception);
            File.AppendAllText(ResultPath + ".crash.txt", LogScrubber.Scrub(e.Exception.ToString()) + "\n");
        };
    }

    private static string ResultPath => Environment.GetEnvironmentVariable("AUN_RESULT") ?? "aun-studio-result.txt";

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var localizer = new Localizer(Environment.GetEnvironmentVariable("AUN_LANG") is { Length: > 0 } l ? l : "ar");
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
        var userAgent = "AlUmmahNowStudioWindows/" + version;

        // The passkey sheet is modal to the Studio window; its handle is filled in once it exists.
        var hwnd = IntPtr.Zero;
        IStudioAccounts accounts;
        IApiTransport transport;
        Func<string, IUploadService> uploadServiceFor;
        ICaptionsApi captionsApi;
        IInstitutionApi institutionApi;
        IInsightsApi insightsApi;
        // On-device transcription and pre-encoding need Core's whisper and HLS packaging (v0.4); until then they stay hidden.
        IOnDeviceTranscriber? transcriber = null;
        PreEncodeCoordinator? preEncoder = null;
        if (demo)
        {
            accounts = new DemoAccounts();
            transport = new DemoTransport();
            uploadServiceFor = account => new SimulatedUploadService(account, TimeSpan.FromMilliseconds(250));
            var demoServices = new DemoStudioServices();
            captionsApi = demoServices;
            institutionApi = demoServices;
            insightsApi = demoServices;
            transcriber = new DemoTranscriber();
            var demoMedia = new DemoRenditionPackaging();
            preEncoder = new PreEncodeCoordinator(() => new LocalRenditionJob(demoMedia, demoMedia, demoMedia, demoServices), Path.Combine(dataRoot, "demo-renditions"));
        }
        else
        {
            var passkeys = new PasskeyFlows(new NativePasskeyClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, environment.AuthIssuer, userAgent), new WindowsWebAuthn(() => hwnd));
            var core = new CoreStudioAccounts(environment.AuthIssuer, environment.StudioClientId, new DpapiSecureStore(Path.Combine(dataRoot, "secure")), new ShellBrowserLauncher(), localizer["auth.browser.done"], passkeys: passkeys);
            accounts = core;
            transport = new CoreApiTransport(core, environment.VideosApi, userAgent, localizer.Language);
            uploadServiceFor = account => new CoreUploadService(core, environment.VideosApi, userAgent, Path.Combine(dataRoot, "uploads"), account);
            var session = new AccountsAuthSession(core);
            var services = new StudioServicesClient(new StudioHttpClient(environment.VideosApi, session.GetAccessTokenAsync, userAgent, localizer.Language));
            captionsApi = services;
            institutionApi = services;
            insightsApi = services;
        }

        var api = new StudioApiClient(transport);
        var shell = new StudioShellViewModel(accounts, api);
        _uploads = new UploadsViewModel(shell, api, uploadServiceFor, Path.Combine(dataRoot, demo ? "demo" : "studio")) { PreEncoder = preEncoder };
        var uploadsVm = _uploads;
        var encode = new EncodeQueue(new MediaFoundationEncodeBackend());

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

        async Task<string?> PickFileAsync(IEnumerable<string> extensions)
        {
            var picker = new global::Windows.Storage.Pickers.FileOpenPicker();
            foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window.Window));
            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }

        async Task<string?> PickSaveAsync(string suggestedName)
        {
            var picker = new global::Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName) };
            picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window.Window));
            var file = await picker.PickSaveFileAsync();
            return file?.Path;
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
            });
        var analyticsVm = new AnalyticsViewModel(shell, api);
        var videos = new VideosPage(localizer, videosVm, uploadsVm, () => window.Window.Content?.XamlRoot);
        var analytics = new AnalyticsPage(localizer, analyticsVm);
        var encodePage = new EncodePage(localizer, encode);
        var timeline = new TimelinePage(localizer);
        var captionsVm = new CaptionsViewModel(shell, api, captionsApi, transcriber);
        var captions = new CaptionsPage(localizer, captionsVm, () => window.Window.Content?.XamlRoot, () => PickFileAsync(UploadPlanner.VideoExtensions));
        var insightsVm = new InsightsViewModel(shell, api, insightsApi);
        var insights = new InsightsPage(localizer, insightsVm, PickSaveAsync);
        var institutionVm = new InstitutionViewModel(shell, institutionApi);
        var brand = new BrandPage(localizer, institutionVm, () => window.Window.Content?.XamlRoot,
            () => PickFileAsync(new[] { ".png", ".jpg", ".jpeg", ".webp", ".mp4", ".mov", ".mp3", ".m4a", ".wav" }));
        window.AddPage("uploads", localizer["studio.tab.upload"], Symbol.Upload, uploads.Root);
        window.AddPage("videos", localizer["studio.tab.videos"], Symbol.List, videos.Root);
        window.AddPage("captions", localizer["studio.tab.captions"], Symbol.ClosedCaption, captions.Root);
        window.AddPage("analytics", localizer["studio.tab.analytics"], Symbol.Calculator, analytics.Root);
        window.AddPage("reports", localizer["studio.tab.reports"], Symbol.ViewAll, insights.Root);
        window.AddPage("brand", localizer["studio.tab.brand"], Symbol.People, brand.Root);
        window.AddPage("encode", localizer["studio.tab.encode"], Symbol.Video, encodePage.Root);
        window.AddPage("timeline", localizer["studio.tab.timeline"], Symbol.Edit, timeline.Root);
        window.ShowPage(tabName);

        // Studio screens (E19): the scene's data actually rendered, not only the shell.
        bool SceneOk() => tabName switch
        {
            "captions" => scene == "captions-editor" ? captionsVm.Editor != null : captionsVm.Drafts != null,
            "reports" => scene == "reports-video" ? insightsVm.Retention != null : insightsVm.Summary != null,
            "brand" => scene == "brand-team" ? institutionVm.State != null && !institutionVm.CanEdit : institutionVm.State is { CanEdit: true },
            _ => scene != "preencode" || uploadsVm.Items.Any(i => preEncoder?.StatusOf(i.Id) != null),
        };
        window.Window.Activate();
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
                else if (scene == "signout") window.AskSignOut();
                else if (scene == "addpasskey") _ = window.AddPasskeyAsync();
                else if (scene.StartsWith("captions", StringComparison.Ordinal)) captions.RunScene(scene);
                else if (scene == "reports-video" && insightsVm.VideoStats.FirstOrDefault() is { } top) insightsVm.VideoId = top.VideoId;
                else if (scene == "brand-team" && shell.Channels.FirstOrDefault(c => c.Role != ChannelRole.Owner) is { } managed) institutionVm.Switch(managed);
                else if (scene == "preencode") uploadsVm.Start();
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
                var sceneOk = SceneOk();
                pass = pass && sceneOk;
                File.WriteAllText(ResultPath, $"AUTOMATION {tabName} scene={scene} sceneOk={sceneOk} shell={shell.State} channel={shell.ActiveChannel?.Id} accounts={shell.AccountList.Count} uploads={snapshot.Count} uploadsDone={uploadsDone} encode={job?.Status} encoder={job?.UsedEncoder?.Name} error={job?.Error} RESULT {(pass ? "PASS" : "FAIL")}");
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
