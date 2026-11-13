using System.Reflection;
using AlUmmahNow.Admin.Auth;
using AlUmmahNow.Admin.Demo;
using AlUmmahNow.Admin.Media;
using AlUmmahNow.Admin.Push;
using AlUmmahNow.Admin.Review;
using AlUmmahNow.Admin.Security;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Services;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Native;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Path = System.IO.Path;

namespace AlUmmahNow.Admin.WindowsApp;

public sealed partial class App : Application
{
    private readonly AppInstance _instance;
    private DispatcherQueueTimer? _sceneTimer;
    private DispatcherQueueTimer? _endTimer;
    private AdminWindow? _window;
    private IAdminAuth? _auth;
    private WindowsPush? _push;
    private string _sceneResult = "";

    public App(AppInstance instance)
    {
        _instance = instance;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log.For("app").Error("unhandled exception", e.Exception);
            if (ResultPath != null) File.AppendAllText(ResultPath + ".crash.txt", LogScrubber.Scrub(e.Exception.ToString()) + "\n");
        };
    }

    private static string? ResultPath => Environment.GetEnvironmentVariable("AUN_RESULT");

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var demo = Environment.GetEnvironmentVariable("AUN_DEMO") == "1";
        var environment = AppEnvironment.Resolve(Environment.GetEnvironmentVariable("AUN_ENV"), AppEnvironment.BuildDefault);
        var dataRoot = demo
            ? Path.Combine(Path.GetTempPath(), "aun-admin-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlUmmahNowAdmin");
        Log.AddSink(new FileLogSink(Path.Combine(dataRoot, "logs", "admin.log")));
        var log = Log.For("app");
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        log.Info($"Al Ummah Now Admin {version} for Windows, environment {environment.Name}, core {CoreRuntime.VersionOrNone()}");

        var settingsPath = Path.Combine(dataRoot, "settings.json");
        var settings = AdminSettings.Load(settingsPath);
        if (Environment.GetEnvironmentVariable("AUN_LANG") is { Length: > 0 } lang) settings.Language = lang == "ar" ? "ar" : "en";
        if (Environment.GetEnvironmentVariable("AUN_THEME") is { Length: > 0 } theme) settings.Theme = theme == "dark" ? "dark" : "light";
        settings.Save(settingsPath);
        var localizer = new Localizer(settings.Language);
        var userAgent = "AlUmmahNowAdminWindows/" + version;

        IReviewService service;
        IPushRegistry registry;
        if (demo)
        {
            var stream = Environment.GetEnvironmentVariable("AUN_DEMO_STREAM") is { Length: > 0 } s ? new Uri(s) : null;
            var demoService = new DemoReviewService(senior: Environment.GetEnvironmentVariable("AUN_DEMO_ROLE") != "junior", stream: stream);
            service = demoService;
            registry = demoService;
            var demoAuth = new DemoAdminAuth();
            _auth = demoAuth;
            if (Environment.GetEnvironmentVariable("AUN_SCENE") == "notstaff") service = new NotStaffService(demoService);
        }
        else
        {
            var auth = new CoreAdminAuth(environment.AuthIssuer, environment.AdminClientId, new DpapiSecureStore(Path.Combine(dataRoot, "secure")), new ShellBrowserLauncher());
            _auth = auth;
            var api = new AdminApiReviewService(environment.AdminApi, environment.VideosApi, auth.AccessTokenAsync, userAgent,
                fallbackAccount: () => auth.UserId == null ? null : new StaffProfile(auth.UserId, auth.DisplayName, Array.Empty<string>()));
            service = api;
            registry = api;
            ProtocolRegistration.EnsureRegistered();
        }

        var identity = typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "AdminPackageIdentity")?.Value;
        var pushConfig = new PushConfig(identity, WindowsPush.IsPackaged());
        var context = new AdminContext
        {
            L = localizer,
            Settings = settings,
            SaveSettings = () => settings.Save(settingsPath),
            Auth = _auth,
            Service = service,
            Lock = new LockPolicy(),
            Reveal = new RevealPolicy(),
            InstallId = settings.InstallId,
            Demo = demo,
            Banner = demo ? localizer["admin.demo"] : environment.Name != "prod" ? localizer.Format("admin.environment", environment.Name.ToUpperInvariant()) : null,
            AfterSignIn = async () =>
            {
                if (_push != null) await _push.StartAsync(version);
                if (_window != null) _window.ShowPushState(_push?.Message);
            },
            BeforeSignOut = async () =>
            {
                // Unregister while the token still works; if the server can't be told, retire
                // this install id so the stale registration never reaches the next account.
                if (_push != null && !await _push.StopAsync())
                {
                    settings.InstallId = "win-" + Guid.NewGuid().ToString("N");
                    settings.Save(settingsPath);
                }
            },
        };
        context.PushMessage = pushConfig.CanRegister ? null : localizer["admin.push.off"];
        _window = new AdminWindow(context);
        _push = new WindowsPush(pushConfig, registry, () => settings.InstallId, localizer, a => _window.ActivateReviewAsync(a.ReviewId));
        _push.ListenForToasts();
        SessionWatch.Start(_window);

        _instance.Activated += (_, e) => _window.Window.DispatcherQueue.TryEnqueue(() => HandleActivation(e));
        HandleActivation(_instance.GetActivatedEventArgs());

        _window.Window.Activate();
        _ = _window.StartAsync();
        StartAutomation(demo, settings);
    }

    /// <summary>A second launch redirected here: the sign-in callback, or a toast.</summary>
    private void HandleActivation(AppActivationArguments e)
    {
        if (e.Kind == ExtendedActivationKind.Protocol && e.Data is IProtocolActivatedEventArgs p)
        {
            _auth?.HandleRedirect(p.Uri.AbsoluteUri);
            _window?.Window.Activate();
        }
        else if (e.Kind == ExtendedActivationKind.Launch && e.Data is ILaunchActivatedEventArgs l && PushActivation.Parse(l.Arguments) is { } a)
        {
            _ = _window?.ActivateReviewAsync(a.ReviewId);
        }
    }

    // ---------------- CI scenarios (AUN_DEMO=1 only) ----------------

    private void StartAutomation(bool demo, AdminSettings settings)
    {
        var scene = Environment.GetEnvironmentVariable("AUN_SCENE") ?? "";
        var exitAfter = int.TryParse(Environment.GetEnvironmentVariable("AUN_EXIT_AFTER"), out var s) ? s : 0;
        var ui = DispatcherQueue.GetForCurrentThread();
        if (!demo || _window == null) return;
        var window = _window;
        if (scene.Length > 0)
        {
            _sceneTimer = ui.CreateTimer();
            _sceneTimer.Interval = TimeSpan.FromSeconds(4);
            _sceneTimer.IsRepeating = false;
            _sceneTimer.Tick += async (_, _) =>
            {
                try
                {
                    _sceneResult = await Scenes.RunAsync(window, scene);
                }
                catch (Exception ex)
                {
                    _sceneResult = "scene error " + ex.GetType().Name;
                }
            };
            _sceneTimer.Start();
        }

        if (exitAfter > 0 && ResultPath is { } result)
        {
            _endTimer = ui.CreateTimer();
            _endTimer.Interval = TimeSpan.FromSeconds(exitAfter);
            _endTimer.IsRepeating = false;
            _endTimer.Tick += (_, _) =>
            {
                var affinity = Protection.Affinity(window.Hwnd);
                var pass = Scenes.Passed(window, scene, _sceneResult) && affinity != 0;
                File.WriteAllText(result, $"AUTOMATION scene={scene} lang={settings.Language} theme={settings.Theme} state={window.State} rows={window.Queue.Items.Count} open={window.Session.Detail?.Item.Id} affinity={affinity} toast=\"{window.LastToast}\" detail={_sceneResult} RESULT {(pass ? "PASS" : "FAIL")}");
                Exit();
            };
            _endTimer.Start();
        }
    }

    /// <summary>The "staff only" screen: the profile check refuses this account.</summary>
    private sealed class NotStaffService : IReviewService
    {
        private readonly IReviewService _inner;

        public NotStaffService(IReviewService inner) => _inner = inner;

        public Task<StaffProfile> ProfileAsync(CancellationToken ct = default) => throw new NotStaffException();
        public Task<ReviewPage> QueueAsync(ReviewTab tab, string? query, string? cursor, CancellationToken ct = default) => throw new NotStaffException();
        public Task<ReviewDetail> DetailAsync(string id, CancellationToken ct = default) => throw new NotStaffException();
        public Task ClaimAsync(string id, CancellationToken ct = default) => throw new NotStaffException();
        public Task RevealAsync(string id, string target, string installId, CancellationToken ct = default) => throw new NotStaffException();
        public Task<ReviewItem> DecideAsync(string id, ReviewDecision decision, string? reasonCode, string? note, int? version, CancellationToken ct = default) => throw new NotStaffException();
        public Task<byte[]> MediaAsync(string id, string key, CancellationToken ct = default) => throw new NotStaffException();
        public Task RecordCaptureAsync(CaptureEvent captureEvent, string? reviewId, string installId, CancellationToken ct = default) => _inner.RecordCaptureAsync(captureEvent, reviewId, installId, ct);
    }
}
