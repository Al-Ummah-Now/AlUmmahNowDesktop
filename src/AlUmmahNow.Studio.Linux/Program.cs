using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Services;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Native;
using AlUmmahNow.CoreBridge.Native.Studio;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Platform.Linux;
using AlUmmahNow.Studio.Demo;
using AlUmmahNow.Studio.Encode;
using AlUmmahNow.Studio.Linux;
using AlUmmahNow.Studio.Shell;
using AlUmmahNow.Studio.Upload;
using AlUmmahNow.Studio.Videos;
using AlUmmahNow.Studio.Analytics;

// Settings and logs follow the XDG base directories.
string XdgDir(string variable, string fallback)
{
    var value = Environment.GetEnvironmentVariable(variable);
    return Path.Combine(string.IsNullOrEmpty(value) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback) : value, "alummahnow-studio");
}

var configDir = XdgDir("XDG_CONFIG_HOME", ".config");
var stateDir = XdgDir("XDG_STATE_HOME", Path.Combine(".local", "state"));
Log.AddSink(TextWriterLogSink.StandardError());
Log.AddSink(new FileLogSink(Path.Combine(stateDir, "logs", "studio.log")));
var log = Log.For("app");

Adw.Module.Initialize();
Gst.Module.Initialize();
var gstArgs = Array.Empty<string>();
Gst.Functions.Init(ref gstArgs);

var localizer = new Localizer(Environment.GetEnvironmentVariable("AUN_LANG") is { Length: > 0 } lang ? lang : "ar");
var automation = Environment.GetEnvironmentVariable("AUN_AUTOMATION") ?? "";
var tabName = Environment.GetEnvironmentVariable("AUN_TAB") ?? "uploads";
var scene = Environment.GetEnvironmentVariable("AUN_SCENE") ?? "";
var exitAfter = int.TryParse(Environment.GetEnvironmentVariable("AUN_EXIT_AFTER"), out var s) ? s : 0;
// CI and screenshots only: canned accounts and data, nothing sent to a server.
var demo = Environment.GetEnvironmentVariable("AUN_DEMO") == "1";

var environment = AppEnvironment.Resolve(Environment.GetEnvironmentVariable("AUN_ENV"), AppEnvironment.BuildDefault);
var version = typeof(StudioWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
log.Info($"Al Ummah Now Studio {version} for Linux, environment {environment.Name}, core {CoreRuntime.VersionOrNone()}");

var settingsStore = new SettingsStore(Path.Combine(configDir, "settings.json"));
var settings = settingsStore.Load();

var userAgent = "AlUmmahNowStudioLinux/" + version;
IStudioAccounts accounts;
IApiTransport transport;
Func<string, IUploadService> uploadServiceFor;
if (demo)
{
    accounts = new DemoAccounts();
    transport = new DemoTransport();
    uploadServiceFor = account => new SimulatedUploadService(account, TimeSpan.FromMilliseconds(250));
}
else
{
    var core = new CoreStudioAccounts(environment.AuthIssuer, environment.StudioClientId, new LibSecretStore("alummahnow-studio"), new ShellBrowserLauncher(), localizer["auth.browser.done"]);
    accounts = core;
    transport = new CoreApiTransport(core, environment.VideosApi, userAgent, localizer.Language);
    uploadServiceFor = account => new CoreUploadService(core, environment.VideosApi, userAgent, Path.Combine(stateDir, "uploads"), account);
}

var api = new StudioApiClient(transport);
var encode = new EncodeQueue(new GStreamerEncodeBackend());

var app = Adw.Application.New("net.alummahnow.Studio", Gio.ApplicationFlags.NonUnique);
var exitCode = 0;

app.OnActivate += (_, _) =>
{
    Gtk.Widget.SetDefaultDirection(localizer.IsRtl ? Gtk.TextDirection.Rtl : Gtk.TextDirection.Ltr);
    // The view-model posts its updates to the GTK main loop's synchronization context.
    var shell = new StudioShellViewModel(accounts, api);
    var uploadsVm = new UploadsViewModel(shell, api, uploadServiceFor, Path.Combine(stateDir, demo ? "demo" : "studio"));
    var banner = demo ? localizer["studio.shell.demo"] : environment.Name != "prod" ? localizer.Format("studio.shell.environment", environment.Name.ToUpperInvariant()) : null;
    var window = new StudioWindow(app, localizer, shell, banner);

    uploadsVm.Watch(Environment.GetEnvironmentVariable("AUN_WATCH_DIR") is { Length: > 0 } dir ? dir : settings.WatchFolder);
    var uploads = new UploadPage(localizer, uploadsVm, shell, window.Window, path =>
    {
        settings.WatchFolder = path;
        settingsStore.Save(settings);
    });
    var videosVm = new VideosViewModel(shell, api,
        account => new RecentlyDeleted(Path.Combine(stateDir, demo ? "demo" : "studio", "deleted-" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(account)) + ".json")),
        () => settings.RecentSearches ?? new List<string>(),
        recents =>
        {
            settings.RecentSearches = recents.ToList();
            settingsStore.Save(settings);
        });
    var analyticsVm = new AnalyticsViewModel(shell, api);
    var videos = new VideosPage(localizer, videosVm, uploadsVm, window.Window);
    var analytics = new AnalyticsPage(localizer, analyticsVm);
    var encodePage = new EncodePage(localizer, encode);
    var timeline = new TimelinePage(localizer);
    window.AddPage("uploads", localizer["studio.tab.upload"], "document-send-symbolic", uploads.Root);
    window.AddPage("videos", localizer["studio.tab.videos"], "view-list-symbolic", videos.Root);
    window.AddPage("analytics", localizer["studio.tab.analytics"], "power-profile-performance-symbolic", analytics.Root);
    window.AddPage("encode", localizer["studio.tab.encode"], "media-record-symbolic", encodePage.Root);
    window.AddPage("timeline", localizer["studio.tab.timeline"], "video-x-generic-symbolic", timeline.Root);
    window.ShowPage(tabName);
    window.Window.Present();

    GLib.Functions.TimeoutAdd(0, 300, () =>
    {
        uploadsVm.Tick();
        encodePage.Tick();
        return true;
    });
    app.OnShutdown += (_, _) => uploadsVm.Dispose();

    _ = shell.InitializeAsync();
    // CI screenshots: stage a folder on the Upload page.
    if (Environment.GetEnvironmentVariable("AUN_STAGE_DIR") is { Length: > 0 } stageDir) uploadsVm.AddFolder(stageDir, recursive: false);

    EncodeJob? automationJob = null;
    if (automation.StartsWith("encode:", StringComparison.Ordinal))
    {
        var source = automation.Substring(7);
        automationJob = encode.Add(new EncodeRequest(source, source + ".out.mp4", EncodePreset.Lecture));
    }

    if (scene.Length > 0)
    {
        GLib.Functions.TimeoutAdd(0, 4000, () =>
        {
            if (scene == "accounts") window.OpenAccountMenu();
            else if (scene == "signout") window.AskSignOut();
            else videos.RunScene(scene);
            return false;
        });
    }

    if (exitAfter > 0)
    {
        GLib.Functions.TimeoutAdd(0, (uint)exitAfter * 1000, () =>
        {
            var items = uploadsVm.Items;
            var uploadsDone = items.Count > 0 && items.All(i => i.Phase == UploadPhase.Done);
            var encoded = automationJob == null || automationJob.Status == EncodeStatus.Completed;
            var pass = automation.StartsWith("encode:", StringComparison.Ordinal) ? encoded
                : automation == "shell" ? shell.State == ShellState.Ready && shell.ActiveChannel != null
                : uploadsDone || tabName != "uploads";
            Log.For("automation").Info($"AUTOMATION {tabName} scene={scene} shell={shell.State} channel={shell.ActiveChannel?.Id} accounts={shell.AccountList.Count} uploads={items.Count} uploadsDone={uploadsDone} encode={automationJob?.Status} encoder={automationJob?.UsedEncoder?.Name} error={automationJob?.Error} RESULT {(pass ? "PASS" : "FAIL")}");
            exitCode = pass ? 0 : 3;
            app.Quit();
            return false;
        });
    }
};

var rc = app.RunWithSynchronizationContext(null);
return rc != 0 ? rc : exitCode;
