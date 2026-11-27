using Path = System.IO.Path;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Demo;
using AlUmmahNow.App.Services;
using AlUmmahNow.App.Updates;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.Contrast;
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
using AlUmmahNow.Updates.Views;
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
    private DispatcherQueueTimer? _updateTimer;
    private StudioWindow? _window;
    private ITelemetryService _telemetry = NullTelemetryService.Instance;

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
            _telemetry.ReportCrash(e.Exception.GetType().Name, e.Exception.Message, e.Exception.StackTrace);
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
        var version = UpdateText.VersionOf(typeof(App).Assembly);
        log.Info($"Al Ummah Now Studio {version} for Windows, environment {environment.Name}, core {CoreRuntime.VersionOrNone()}");

        var settingsStore = new SettingsStore(Path.Combine(dataRoot, "settings.json"));
        var settings = settingsStore.Load();
        // A forced theme is the one Settings shows as chosen.
        if (Environment.GetEnvironmentVariable("AUN_THEME") is "dark" or "light") settings.Theme = RequestedTheme == ApplicationTheme.Light ? ThemePreference.Light : ThemePreference.Dark;
        // The saved language (Settings), Arabic by default; CI and screenshots force one with AUN_LANG.
        var localizer = new Localizer(Environment.GetEnvironmentVariable("AUN_LANG") is { Length: > 0 } l ? l : settings.Language);
        var userAgent = "AlUmmahNowStudioWindows/" + version;
        _telemetry = CoreTelemetryService.CreateOrNull(
            platform: "windows",
            appVersion: version,
            locale: localizer.Language,
            videosApiBaseUrl: environment.VideosApi.AbsoluteUri,
            userAgent: userAgent,
            bufferPath: Path.Combine(dataRoot, "telemetry-buffer.json"),
            initialEnabled: settings.TelemetryEnabled,
            demo: demo);

        // The passkey sheet is modal to the Studio window; its handle is filled in once it exists.
        var hwnd = IntPtr.Zero;
        IStudioAccounts accounts;
        IApiTransport transport;
        Func<string, IUploadService> uploadServiceFor;
        if (demo)
        {
            var canned = new DemoTransport(DemoTransport.FeaturesOff(Environment.GetEnvironmentVariable("AUN_DEMO_FEATURES_OFF")), Environment.GetEnvironmentVariable("AUN_DEMO_THUMBS"), localizer.Language, withDeleted: scene.StartsWith("deleted", StringComparison.Ordinal), withAvatars: scene == "avatar");
            transport = canned;
            accounts = new DemoAccounts(canned.Avatar);
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
        // The check for a new version: none in a demo, or in a build that is no release.
        bool IsBusy() => uploadsVm.IsWorking || encode.IsBusy;
        var updateScene = UpdateScene.FromEnvironment(localizer, UpdateInstall.Installer, IsBusy);
        var updateStore = new UpdateStore(Path.Combine(dataRoot, "updates.json"));
        var updates = updateScene?.Service ?? WindowsInstall.Service("studio", environment.VideosApi, version, userAgent, dataRoot, updateStore, demo, IsBusy, WindowsEnd.Close);
        var updateViews = new UpdateViews(updates, new UpdateText(localizer, UpdateApp.Studio, updates), UpdateText.Icon(UpdateApp.Studio), WindowsEnd.Close);
        window.UseUpdates(updateViews);
        var settingsPage = new SettingsPage(localizer, settings, settingsStore, shell, uploadsVm, PickFolderAsync, window.SignOut, window.AddPasskeyAsync, updates.InstalledVersion, updateViews, () =>
        {
            updates.AskRestart();
            WindowsEnd.Close();
        }, _telemetry);
        window.AddPage(StudioPages.Settings, localizer["studio.tab.settings"], Symbol.Setting, settingsPage.Root);
        window.UsePages(pages);
        window.SearchRequested += (_, q) => videos.SearchFor(q);
        window.Window.Activate();
        // Features are re-read when Studio comes back to the foreground (at most once a minute).
        window.Window.Activated += (sender, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) _ = shell.RefreshFeaturesAsync();
        };
        var closing = new CancellationTokenSource();
        _ = updates.RunAsync(closing.Token);
        _ = _telemetry.FlushAsync(closing.Token);
        WindowsEnd.Attach(window.Window, updates, new Action[] { closing.Cancel, _telemetry.Persist },
            // Uploads (and the details of finished ones) and encodes, at once and within the end's limit
            stop => Task.WhenAll(uploadsVm.StopAsync(stop), encode.StopAsync(stop), _telemetry.FlushAsync(stop)), allowRestart: updateScene == null);

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

        var captureUpdate = demo && updateScene != null && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AUN_END_FILE"));
        if (updateScene != null)
        {
            _updateTimer = ui.CreateTimer();
            _updateTimer.Interval = TimeSpan.FromSeconds(6);
            _updateTimer.IsRepeating = false;
            _updateTimer.Tick += (_, _) =>
            {
                if (updateScene.IsInSettings) window.ShowPage(StudioPages.Settings);
                // The page's controls are there a moment later
                ui.TryEnqueue(DispatcherQueuePriority.Low, () => _ = updateScene.ActAsync(updateViews));
            };
            _updateTimer.Start();
        }

        // "highcontrast-delete": the scene "delete", in high contrast
        var act = scene.StartsWith("highcontrast-", StringComparison.Ordinal) ? scene["highcontrast-".Length..] : scene;
        if (scene.Length > 0)
        {
            _sceneTimer = ui.CreateTimer();
            _sceneTimer.Interval = TimeSpan.FromSeconds(5);
            _sceneTimer.IsRepeating = false;
            _sceneTimer.Tick += (_, _) =>
            {
                if (act == "accounts") window.OpenAccountMenu();
                else if (act == "channels") window.OpenChannelPicker();
                else if (act == "signout") window.AskSignOut();
                else if (act == "searchfocus") window.FocusSearch();
                else if (act == "addpasskey") _ = window.AddPasskeyAsync();
                else if (act is "scheduled" or "schedulepick" or "schedulesoon" or "schedulesoonpick") uploads.RunScene(act);
                else if (act == "active") AlUmmahNow.Studio.Demo.DemoEncodeBackend.Seed(encode, Path.GetTempPath());
                // The demo's second channel has no videos: the Videos empty state.
                else if (act == "emptychannel") shell.SelectChannel(shell.Channels.ElementAtOrDefault(1));
                else videos.RunScene(act);
            };
            _sceneTimer.Start();
        }

        if (exitAfter > 0)
        {
            _endTimer = ui.CreateTimer();
            _endTimer.Interval = TimeSpan.FromSeconds(exitAfter);
            _endTimer.IsRepeating = false;
            var told = AlUmmahNow.App.Demo.AutomationEnd.FromEnvironment();
            var resultWritten = false;
            var triedUpdateScroll = false;
            _endTimer.Tick += (_, _) =>
            {
                if ((!captureUpdate || resultWritten) && told.Waits())
                {
                    _endTimer.Interval = TimeSpan.FromMilliseconds(500);
                    _endTimer.Start();
                    return;
                }

                if (resultWritten)
                {
                    Exit();
                    return;
                }

                if (captureUpdate && updateScene!.IsInSettings && updateViews.IsSectionShown
                    && !settingsPage.IsUpdateCardInView && !triedUpdateScroll)
                {
                    triedUpdateScroll = true;
                    settingsPage.BringUpdatesIntoView();
                    _endTimer.Interval = TimeSpan.FromMilliseconds(500);
                    _endTimer.Start();
                    return;
                }

                var snapshot = uploadsVm.Items;
                var uploadsDone = snapshot.Count > 0 && snapshot.All(i => i.Phase == UploadPhase.Done);
                var pass = job != null ? job.Status == EncodeStatus.Completed
                    : automation == "shell" ? shell.State == ShellState.Ready && shell.ActiveChannel != null
                    : uploadsDone || tabName != "uploads";
                var look = LanternGlass.Look(window.Window.Content);
                var cutOff = pages.Current == StudioPages.Uploads ? LanternGlass.CutOff(uploads.Root) : "";
                var (looked, strays) = SystemBrushes.Strays(window.Window.Content);
                pass = pass && strays.Length == 0 && cutOff.Length == 0 && videos.CardsAreWhole && LanternGlass.Missing == 0 && LanternGlass.IsPainted && LanternGlass.UnreadableDialogs == 0
                    && (act is not ("edit" or "bulk" or "delete") || LanternGlass.CheckedDialogs > 0)
                    && (!scene.StartsWith("deleted", StringComparison.Ordinal) || videos.DeletedIsShown)
                    && (scene != "searchfocus" || window.SearchHasFocus)
                    && (scene != "searchhover" || window.SearchIsHovered)
                    && (pages.Current != StudioPages.Analytics || !demo || (analytics.PickedIsReadable == true && analytics.ChartIsSeen))
                    && (scene != "cardhover" || analytics.PickedIsHovered)
                    && (scene != "avatar" || look.AvatarPictures >= 3)
                    && look.Mirrored == 0 && look.Unreadable == 0
                    && (automation != "shell" || updateViews.IsForced || look.Avatars > 0) && (demo || look.Pictures > 0)
                    && (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AUN_DEMO_THUMBS")) || pages.Current is not (StudioPages.Videos or StudioPages.Analytics) || look.Pictures > 0)
                    && (!scene.StartsWith("highcontrast", StringComparison.Ordinal) || (LanternGlass.IsHighContrast && looked > 0))
                    && uploads.ScheduleIs(act)
                    && (act is not ("edit" or "bulk" or "delete" or "channels" or "schedulepick" or "schedulesoonpick") || !LanternGlass.IsHighContrast || window.HasPopup);
                var updateWrong = updateScene?.Wrong(updateViews);
                if (updateWrong == null && updateScene != null && !updateViews.IsPainted) updateWrong = "not the system's colors";
                if (updateWrong == null && captureUpdate && updateScene!.IsInSettings && updateViews.IsSectionShown && !settingsPage.IsUpdateCardInView)
                    updateWrong = "the Settings card is cut off";
                pass = pass && updateWrong == null;
                var updateLine = updateScene == null ? "" : $" update={updateScene.Name}:{updateWrong ?? "ok"}";
                var captureLine = captureUpdate ? $" pid={Environment.ProcessId} hwnd={hwnd.ToInt64()} lang={localizer.Language} updatesScrolled={triedUpdateScroll}" : "";
                var result = $"AUTOMATION {tabName} page={pages.Current} upload={pages.IsVisible(StudioPages.Uploads)} analytics={pages.IsVisible(StudioPages.Analytics)} bulkSchedule={uploadsVm.BulkScheduleOn} scene={scene}{updateLine} shell={shell.State} channel={shell.ActiveChannel?.Id} accounts={shell.AccountList.Count} uploads={snapshot.Count} uploadsDone={uploadsDone} encode={job?.Status} encoder={job?.UsedEncoder?.Name} error={job?.Error} searchFocused={window.SearchHasFocus} fieldFocused={window.FieldHasFocus} cardsWhole={videos.CardsAreWhole} searchHovered={window.SearchIsHovered} pickedReadable={analytics.PickedIsReadable} pickedHovered={analytics.PickedIsHovered} chartSeen={analytics.ChartIsSeen} schedule={uploads.ScheduleIs(act)} cutOff='{cutOff}' pictures={look.Pictures} mirrored={look.Mirrored} avatars={look.Avatars} avatarPictures={look.AvatarPictures} unreadableInitials={look.Unreadable} looked={looked} strays='{strays}' highContrast={LanternGlass.IsHighContrast} painted={LanternGlass.IsPainted} missing={LanternGlass.Missing} unreadableDialogs={LanternGlass.UnreadableDialogs} checkedDialogs={LanternGlass.CheckedDialogs} deletedShown={videos.DeletedIsShown}{captureLine} RESULT {(pass ? "PASS" : "FAIL")}";
                if (captureUpdate)
                {
                    // Publish the completed validation before letting the capture script release the window.
                    File.WriteAllText(ResultPath + ".pending", result);
                    File.Move(ResultPath + ".pending", ResultPath, overwrite: true);
                    resultWritten = true;
                    _endTimer.Interval = TimeSpan.FromMilliseconds(500);
                    _endTimer.Start();
                    return;
                }

                File.WriteAllText(ResultPath, result);
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
