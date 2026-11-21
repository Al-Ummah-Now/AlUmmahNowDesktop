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
// The saved language (Settings), Arabic by default; CI and screenshots force one with AUN_LANG.
var localizer = new Localizer(Environment.GetEnvironmentVariable("AUN_LANG") is { Length: > 0 } lang ? lang : settings.Language);

var userAgent = "AlUmmahNowStudioLinux/" + version;
IStudioAccounts accounts;
IApiTransport transport;
Func<string, IUploadService> uploadServiceFor;
if (demo)
{
    accounts = new DemoAccounts();
    transport = new DemoTransport(DemoTransport.FeaturesOff(Environment.GetEnvironmentVariable("AUN_DEMO_FEATURES_OFF")), Environment.GetEnvironmentVariable("AUN_DEMO_THUMBS"), localizer.Language);
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
// Review frames, scene "active": a demo queue with one finished and one running job.
var encode = demo && scene == "active"
    ? new EncodeQueue(new AlUmmahNow.Studio.Demo.DemoEncodeBackend("vaapih264enc"), AlUmmahNow.Studio.Demo.DemoEncodeBackend.Clock())
    : new EncodeQueue(new GStreamerEncodeBackend());

var app = Adw.Application.New("net.alummahnow.Studio", Gio.ApplicationFlags.NonUnique);
var exitCode = 0;

app.OnActivate += (_, _) =>
{
    Gtk.Widget.SetDefaultDirection(localizer.IsRtl ? Gtk.TextDirection.Rtl : Gtk.TextDirection.Ltr);
    // Light or Dark (no "System"): the saved choice, Dark by default; CI and screenshots force one with AUN_THEME=dark|light.
    var theme = Environment.GetEnvironmentVariable("AUN_THEME");
    if (theme is not ("dark" or "light")) theme = settings.Theme == ThemePreference.Light ? "light" : "dark";
    Adw.StyleManager.GetDefault().SetColorScheme(theme == "dark" ? Adw.ColorScheme.ForceDark : Adw.ColorScheme.ForceLight);
    // The view-model posts its updates to the GTK main loop's synchronization context.
    var shell = new StudioShellViewModel(accounts, api, new FeaturesClient(transport),
            profiles: new ProfileClient(transport), language: localizer.Language,
            knownNames: settings.AccountNames, saveNames: names =>
            {
                settings.AccountNames = names.ToDictionary(kv => kv.Key, kv => kv.Value);
                settingsStore.Save(settings);
            });
    var uploadsVm = new UploadsViewModel(shell, api, uploadServiceFor, Path.Combine(stateDir, demo ? "demo" : "studio"));
    var banner = demo ? localizer["studio.shell.demo"] : environment.Name != "prod" ? localizer.Format("studio.shell.environment", environment.Name.ToUpperInvariant()) : null;
    // Review and store screenshots (AUN_STORE_SHOT=1, as on Windows) show the demo data without the notice:
    // real users never see it.
    if (demo && Environment.GetEnvironmentVariable("AUN_STORE_SHOT") == "1") banner = null;
    StudioStyle.Apply();
    var window = new StudioWindow(app, localizer, shell, banner);

    uploadsVm.Watch(Environment.GetEnvironmentVariable("AUN_WATCH_DIR") is { Length: > 0 } dir ? dir : settings.WatchFolder);
    var uploads = new UploadPage(localizer, uploadsVm, shell, window.Window, path =>
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
    var analyticsVm = new AnalyticsViewModel(shell, api);
    var videos = new VideosPage(localizer, videosVm, uploadsVm, window.Window);
    var analytics = new AnalyticsPage(localizer, analyticsVm);
    // Upload (creator tier) and Analytics (feature_off) are left out of the navigation when off.
    var pages = new StudioPages(shell, analyticsVm, tabName);
    var encodePage = new EncodePage(localizer, encode);
    var timeline = new TimelinePage(localizer);
    window.AddPage(StudioPages.Uploads, localizer["studio.tab.upload"], "document-send-symbolic", uploads.Root);
    window.AddPage(StudioPages.Videos, localizer["studio.tab.videos"], "view-list-symbolic", videos.Root);
    window.AddPage(StudioPages.Analytics, localizer["studio.tab.analytics"], "power-profile-performance-symbolic", analytics.Root);
    window.AddPage(StudioPages.Encode, localizer["studio.tab.encode"], "video-x-generic-symbolic", encodePage.Root);
    window.AddPage(StudioPages.Timeline, localizer["studio.tab.timeline"], "edit-cut-symbolic", timeline.Root);
    async Task<string?> ChooseFolderAsync()
    {
        try
        {
            var dialog = Gtk.FileDialog.New();
            dialog.SetTitle(localizer["studio.upload.pickFolder"]);
            var folder = await dialog.SelectFolderAsync(window.Window);
            return folder?.GetPath();
        }
        catch (GLib.GException)
        {
            return null;
        }
    }

    var settingsPage = new SettingsPage(localizer, settings, settingsStore, shell, uploadsVm, ChooseFolderAsync, window.SignOut, version);
    window.AddPage(StudioPages.Settings, localizer["studio.tab.settings"], "emblem-system-symbolic", settingsPage.Root);
    window.UsePages(pages);
    window.SearchRequested += (_, q) => videos.SearchFor(q);
    window.Window.Present();
    // Features are re-read when Studio comes back to the foreground (at most once a minute).
    window.Window.OnNotify += (sender, e) =>
    {
        if (e.Pspec.GetName() == "is-active" && window.Window.IsActive) _ = shell.RefreshFeaturesAsync();
    };

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
            else if (scene == "channels") window.OpenChannelPicker();
            else if (scene == "signout") window.AskSignOut();
            else if (scene == "scheduled") uploads.RunScene(scene);
            else if (scene == "active") AlUmmahNow.Studio.Demo.DemoEncodeBackend.Seed(encode, Path.GetTempPath());
            // The demo's second channel has no videos: the Videos empty state.
            else if (scene == "emptychannel") shell.SelectChannel(shell.Channels.ElementAtOrDefault(1));
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
            Log.For("automation").Info($"AUTOMATION {tabName} page={pages.Current} upload={pages.IsVisible(StudioPages.Uploads)} analytics={pages.IsVisible(StudioPages.Analytics)} bulkSchedule={uploadsVm.BulkScheduleOn} scene={scene} shell={shell.State} channel={shell.ActiveChannel?.Id} accounts={shell.AccountList.Count} uploads={items.Count} uploadsDone={uploadsDone} encode={automationJob?.Status} encoder={automationJob?.UsedEncoder?.Name} error={automationJob?.Error} everyText={uploads.ScheduleEveryText} searchFocused={window.SearchHasFocus} fieldFocused={window.FieldHasFocus} RESULT {(pass ? "PASS" : "FAIL")}");
            exitCode = pass ? 0 : 3;
            app.Quit();
            return false;
        });
    }
};

var rc = app.RunWithSynchronizationContext(null);
return rc != 0 ? rc : exitCode;
