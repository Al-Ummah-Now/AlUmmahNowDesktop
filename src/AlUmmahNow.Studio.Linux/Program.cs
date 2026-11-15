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
using AlUmmahNow.Studio.Captions;
using AlUmmahNow.Studio.Institution;
using AlUmmahNow.Studio.Engines;
using AlUmmahNow.Studio.Timeline;

// Settings and logs follow the XDG base directories.
string XdgDir(string variable, string fallback)
{
    var value = Environment.GetEnvironmentVariable(variable);
    return Path.Combine(string.IsNullOrEmpty(value) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback) : value, "alummahnow-studio");
}

var configDir = XdgDir("XDG_CONFIG_HOME", ".config");
var stateDir = XdgDir("XDG_STATE_HOME", Path.Combine(".local", "state"));
var dataDir = XdgDir("XDG_DATA_HOME", Path.Combine(".local", "share"));
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
ICaptionsApi captionsApi;
IInstitutionApi institutionApi;
IInsightsApi insightsApi;
IOnDeviceTranscriber transcriber;
IRenditionsApi renditionsApi;
// Media work always runs on Core and this machine's GStreamer: probing, the HLS ladder and packaging (D11), whisper
// models (D10) and the edit list (19.5). Demo mode only swaps the server for canned data.
var probe = new CoreMediaProbe();
var modelsDir = Environment.GetEnvironmentVariable("AUN_MODELS_DIR") is { Length: > 0 } md ? md : Path.Combine(dataDir, "models");
// AUN_REAL_MEDIA=1 runs a demo pre-encode on the real encoder and packaging (CI); otherwise demo media is simulated.
var realMedia = !demo || Environment.GetEnvironmentVariable("AUN_REAL_MEDIA") == "1";
if (demo)
{
    accounts = new DemoAccounts();
    transport = new DemoTransport();
    uploadServiceFor = account => new SimulatedUploadService(account, TimeSpan.FromMilliseconds(250));
    var demoServices = new DemoStudioServices();
    captionsApi = demoServices;
    institutionApi = demoServices;
    insightsApi = demoServices;
    renditionsApi = demoServices;
    // Screenshots show an installed model without downloading one; AUN_MODELS_DIR uses real models.
    transcriber = Environment.GetEnvironmentVariable("AUN_MODELS_DIR") is { Length: > 0 } ? new CoreOnDeviceTranscriber(modelsDir) : new DemoTranscriber();
}
else
{
    var core = new CoreStudioAccounts(environment.AuthIssuer, environment.StudioClientId, new LibSecretStore("alummahnow-studio"), new ShellBrowserLauncher(), localizer["auth.browser.done"]);
    accounts = core;
    transport = new CoreApiTransport(core, environment.VideosApi, userAgent, localizer.Language);
    uploadServiceFor = account => new CoreUploadService(core, environment.VideosApi, userAgent, Path.Combine(stateDir, "uploads"), account);
    var session = new AccountsAuthSession(core);
    var services = new StudioServicesClient(new StudioHttpClient(environment.VideosApi, session.GetAccessTokenAsync, userAgent, localizer.Language));
    captionsApi = services;
    institutionApi = services;
    insightsApi = services;
    renditionsApi = services;
    transcriber = new CoreOnDeviceTranscriber(modelsDir);
}

PreEncodeCoordinator preEncoder;
if (realMedia)
{
    var packaging = new CoreRenditionPackaging();
    var rungEncoder = new GStreamerRungEncoder();
    preEncoder = new PreEncodeCoordinator(() => new LocalRenditionJob(probe, rungEncoder, packaging, renditionsApi) { EncoderName = $"Al Ummah Now Studio {version} (Linux)" }, Path.Combine(stateDir, "renditions"));
}
else
{
    var demoMedia = new DemoRenditionPackaging();
    preEncoder = new PreEncodeCoordinator(() => new LocalRenditionJob(demoMedia, demoMedia, demoMedia, renditionsApi), Path.Combine(stateDir, "demo-renditions"));
}

var api = new StudioApiClient(transport);
var encode = new EncodeQueue(new GStreamerEncodeBackend());

var app = Adw.Application.New("net.alummahnow.Studio", Gio.ApplicationFlags.NonUnique);
var exitCode = 0;

app.OnActivate += (_, _) =>
{
    Gtk.Widget.SetDefaultDirection(localizer.IsRtl ? Gtk.TextDirection.Rtl : Gtk.TextDirection.Ltr);
    // AUN_THEME=dark|light forces a scheme (CI screenshots); otherwise the desktop's preference applies.
    var theme = Environment.GetEnvironmentVariable("AUN_THEME");
    Adw.StyleManager.GetDefault().SetColorScheme(theme == "dark" ? Adw.ColorScheme.ForceDark : theme == "light" ? Adw.ColorScheme.ForceLight : Adw.ColorScheme.Default);
    var css = Gtk.CssProvider.New();
    css.LoadFromString(".aun-pad { padding: 12px 14px; }");
    Gtk.StyleContext.AddProviderForDisplay(Gdk.Display.GetDefault()!, css, 600);
    // The view-model posts its updates to the GTK main loop's synchronization context.
    var shell = new StudioShellViewModel(accounts, api);
    var uploadsVm = new UploadsViewModel(shell, api, uploadServiceFor, Path.Combine(stateDir, demo ? "demo" : "studio")) { PreEncoder = preEncoder };
    var banner = demo ? localizer["studio.shell.demo"] : environment.Name != "prod" ? localizer.Format("studio.shell.environment", environment.Name.ToUpperInvariant()) : null;
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
        });
    var videos = new VideosPage(localizer, videosVm, uploadsVm, window.Window);
    var encodePage = new EncodePage(localizer, encode);
    var editorVm = new EditorViewModel(new CoreEdlEditing(), probe, new GesTimelineRenderer());
    var timeline = new TimelinePage(localizer, editorVm, uploadsVm, window.Window);
    var captionsVm = new CaptionsViewModel(shell, api, captionsApi, transcriber);
    var captions = new CaptionsPage(localizer, captionsVm, window.Window);
    var insightsVm = new InsightsViewModel(shell, api, insightsApi);
    var insights = new InsightsPage(localizer, insightsVm, window.Window);
    var institutionVm = new InstitutionViewModel(shell, institutionApi);
    var brand = new BrandPage(localizer, institutionVm, window.Window);
    window.AddPage("uploads", localizer["studio.tab.upload"], "document-send-symbolic", uploads.Root);
    window.AddPage("videos", localizer["studio.tab.videos"], "view-list-symbolic", videos.Root);
    window.AddPage("captions", localizer["studio.tab.captions"], "media-view-subtitles-symbolic", captions.Root);
    window.AddPage("analytics", localizer["studio.tab.analytics"], "x-office-spreadsheet-symbolic", insights.Root);
    window.AddPage("brand", localizer["studio.tab.brand"], "applications-graphics-symbolic", brand.Root);
    window.AddPage("encode", localizer["studio.tab.encode"], "media-record-symbolic", encodePage.Root);
    window.AddPage("timeline", localizer["studio.tab.timeline"], "video-x-generic-symbolic", timeline.Root);
    window.ShowPage(tabName);

    // Studio screens (E19): the scene's data actually rendered, not only the shell.
    bool SceneOk() => tabName switch
    {
        "captions" => scene == "captions-editor" ? captionsVm.Editor != null : captionsVm.Drafts != null,
        "analytics" => scene == "analytics-video" ? insightsVm.Retention != null : insightsVm.Summary != null,
"timeline" => scene != "editor" || editorVm.Clips.Count >= 3,
        "brand" => scene == "brand-team" ? institutionVm.State != null && !institutionVm.CanEdit : institutionVm.State is { CanEdit: true },
        _ => scene != "preencode" || uploadsVm.Items.Any(i => preEncoder?.StatusOf(i.Id) != null),
    };
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
            else if (scene.StartsWith("captions", StringComparison.Ordinal)) captions.RunScene(scene);
            else if (scene == "analytics-video" && insightsVm.VideoStats.FirstOrDefault() is { } top) insightsVm.VideoId = top.VideoId;
            else if (scene == "brand-team" && shell.Channels.FirstOrDefault(c => c.Role != ChannelRole.Owner) is { } managed) institutionVm.Switch(managed);
            else if (scene == "preencode")
            {
                uploadsVm.Start();
                uploads.ShowQueue();
            }
            else if (scene == "signout") window.AskSignOut();
            else if (scene == "editor") _ = RunEditorSceneAsync();
            else videos.RunScene(scene);
            return false;
        });
    }

    // CI: the editor scene, then (AUN_EXPORT_TO) a real export of the cut timeline through GES.
    async Task RunEditorSceneAsync()
    {
        await timeline.RunSceneAsync((Environment.GetEnvironmentVariable("AUN_EDITOR_FILES") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries));
        if (Environment.GetEnvironmentVariable("AUN_EXPORT_TO") is { Length: > 0 } output) await editorVm.ExportAsync(output);
    }

    if (exitAfter > 0)
    {
        GLib.Functions.TimeoutAdd(0, (uint)exitAfter * 1000, () =>
        {
            var items = uploadsVm.Items;
            var uploadsDone = items.Count > 0 && items.All(i => i.Phase == UploadPhase.Done);
            var encoded = automationJob == null || automationJob.Status == EncodeStatus.Completed;
            var preEncoded = items.Select(i => preEncoder.StatusOf(i.Id)?.Outcome).FirstOrDefault(o => o != null);
            var pass = automation.StartsWith("encode:", StringComparison.Ordinal) ? encoded
                : automation == "shell" ? shell.State == ShellState.Ready && shell.ActiveChannel != null
                : automation == "export" ? editorVm.ExportedPath is { } exported && new FileInfo(exported).Length > 0
                : automation == "preencode" ? preEncoded == RenditionOutcomeKind.Ready
                : uploadsDone || tabName != "uploads";
            var sceneOk = SceneOk();
            pass = pass && sceneOk;
            Log.For("automation").Info($"AUTOMATION {tabName} scene={scene} sceneOk={sceneOk} shell={shell.State} channel={shell.ActiveChannel?.Id} accounts={shell.AccountList.Count} uploads={items.Count} uploadsDone={uploadsDone} encode={automationJob?.Status} encoder={automationJob?.UsedEncoder?.Name} error={automationJob?.Error} clips={editorVm.Clips.Count} exported={editorVm.ExportedPath} editorError={editorVm.ErrorKey}:{editorVm.ErrorDetail} preEncode={preEncoded} RESULT {(pass ? "PASS" : "FAIL")}");
            exitCode = pass ? 0 : 3;
            app.Quit();
            return false;
        });
    }
};

var rc = app.RunWithSynchronizationContext(null);
return rc != 0 ? rc : exitCode;
