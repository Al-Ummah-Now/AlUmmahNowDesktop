using Path = System.IO.Path;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Services;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Native;
using AlUmmahNow.CoreBridge.Native.Studio;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.CoreBridge.Temporary;
using AlUmmahNow.Studio.Demo;
using AlUmmahNow.Studio.Encode;
using AlUmmahNow.Studio.Shell;
using AlUmmahNow.Studio.Upload;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AlUmmahNow.Studio.WindowsApp;

public sealed partial class App : Application
{
    // Held in fields: a DispatcherQueueTimer referenced only locally can be garbage collected.
    private DispatcherQueueTimer? _refresh;
    private DispatcherQueueTimer? _watchTimer;
    private DispatcherQueueTimer? _sceneTimer;
    private DispatcherQueueTimer? _endTimer;
    private StudioWindow? _window;

    public App()
    {
        InitializeComponent();
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
        var simulateUploads = demo || Environment.GetEnvironmentVariable("AUN_SIMULATE_UPLOADS") == "1";

        var environment = AppEnvironment.Resolve(Environment.GetEnvironmentVariable("AUN_ENV"), AppEnvironment.BuildDefault);
        // Each CI scenario starts clean.
        var dataRoot = simulateUploads
            ? Path.Combine(Path.GetTempPath(), "aun-studio-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlUmmahNowStudio");
        Log.AddSink(new FileLogSink(Path.Combine(dataRoot, "logs", "studio.log")));
        Log.AddSink(new DebugLogSink());
        var log = Log.For("app");
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        log.Info($"Al Ummah Now Studio {version} for Windows, environment {environment.Name}, core {CoreRuntime.VersionOrNone()}");

        var settingsStore = new SettingsStore(Path.Combine(dataRoot, "settings.json"));
        var settings = settingsStore.Load();
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AlUmmahNowStudioWindows/" + version);

        IStudioAccounts accounts;
        IApiTransport transport;
        IAuthSession? authSession = null;
        if (demo)
        {
            accounts = new DemoAccounts();
            transport = new DemoTransport();
        }
        else
        {
            var core = new CoreStudioAccounts(environment.AuthIssuer, environment.StudioClientId, new DpapiSecureStore(Path.Combine(dataRoot, "secure")), new ShellBrowserLauncher(), localizer["auth.browser.done"]);
            accounts = core;
            transport = new CoreApiTransport(core, environment.VideosApi, "AlUmmahNowStudioWindows/" + version, localizer.Language);
            authSession = new AccountsAuthSession(core);
        }

        var api = new StudioApiClient(transport);
        var shell = new StudioShellViewModel(accounts, api);
        IUploadEngine uploadEngine = simulateUploads || authSession == null
            ? new SimulatedUploadEngine(TimeSpan.FromMilliseconds(250))
            : new TusUploadEngine(http, environment.VideosApi, authSession, _ => Task.FromResult(shell.ActiveChannel?.Id ?? throw new InvalidOperationException("No channel selected")));
        var queue = new UploadQueue(uploadEngine, Path.Combine(dataRoot, "uploads.json"));
        var encode = new EncodeQueue(new MediaFoundationEncodeBackend());

        var banner = demo ? localizer["studio.shell.demo"] : environment.Name != "prod" ? localizer.Format("studio.shell.environment", environment.Name.ToUpperInvariant()) : null;
        _window = new StudioWindow(localizer, shell, banner);
        var window = _window;

        var watchDir = Environment.GetEnvironmentVariable("AUN_WATCH_DIR");
        if (string.IsNullOrEmpty(watchDir)) watchDir = settings.WatchFolder;
        var watch = string.IsNullOrEmpty(watchDir) ? null : new FolderWatch(watchDir);

        async Task PickWatchFolderAsync()
        {
            var picker = new global::Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window.Window));
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;
            settings.WatchFolder = folder.Path;
            settingsStore.Save(settings);
            watch = new FolderWatch(folder.Path);
        }

        var uploads = new UploadsPage(localizer, queue, PickWatchFolderAsync);
        var encodePage = new EncodePage(localizer, encode);
        var timeline = new TimelinePage(localizer);
        window.AddPage("uploads", localizer["studio.tab.uploads"], Symbol.Upload, uploads.Root);
        window.AddPage("encode", localizer["studio.tab.encode"], Symbol.Video, encodePage.Root);
        window.AddPage("timeline", localizer["studio.tab.timeline"], Symbol.Edit, timeline.Root);
        window.ShowPage(tabName);
        window.Window.Activate();
        shell.ChannelChanged += (_, _) => queue.Pump();

        _refresh = ui.CreateTimer();
        _refresh.Interval = TimeSpan.FromMilliseconds(300);
        _refresh.Tick += (_, _) =>
        {
            uploads.Tick();
            encodePage.Tick();
        };
        _refresh.Start();

        _watchTimer = ui.CreateTimer();
        _watchTimer.Interval = TimeSpan.FromSeconds(1);
        _watchTimer.Tick += (_, _) =>
        {
            if (watch == null || (!simulateUploads && shell.State != ShellState.Ready)) return;
            foreach (var (path, size) in watch.Scan()) queue.Enqueue(path, size);
        };
        _watchTimer.Start();

        _ = shell.InitializeAsync();

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
                var snapshot = queue.Snapshot();
                var uploadsDone = snapshot.Count > 0 && snapshot.All(i => i.Status == UploadStatus.Completed);
                var pass = job != null ? job.Status == EncodeStatus.Completed
                    : automation == "shell" ? shell.State == ShellState.Ready && shell.ActiveChannel != null
                    : uploadsDone || tabName != "uploads";
                File.WriteAllText(ResultPath, $"AUTOMATION {tabName} scene={scene} shell={shell.State} channel={shell.ActiveChannel?.Id} accounts={shell.AccountList.Count} uploads={snapshot.Count} uploadsDone={uploadsDone} encode={job?.Status} encoder={job?.UsedEncoder?.Name} error={job?.Error} RESULT {(pass ? "PASS" : "FAIL")}");
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
