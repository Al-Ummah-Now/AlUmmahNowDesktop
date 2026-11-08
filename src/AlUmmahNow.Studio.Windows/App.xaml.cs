using Path = System.IO.Path;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Services;
using AlUmmahNow.App.ViewModels;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Native;
using AlUmmahNow.CoreBridge.Temporary;
using AlUmmahNow.Studio.Encode;
using AlUmmahNow.Studio.Timeline;
using AlUmmahNow.Studio.Upload;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AlUmmahNow.Studio.WindowsApp;

public sealed partial class App : Application
{
    private Window? _window;

    // Held in fields: a DispatcherQueueTimer referenced only locally can be garbage collected.
    private DispatcherQueueTimer? _refresh;
    private DispatcherQueueTimer? _watchTimer;
    private DispatcherQueueTimer? _endTimer;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => File.AppendAllText(ResultPath + ".crash.txt", e.Exception + "\n");
    }

    private static string ResultPath => Environment.GetEnvironmentVariable("AUN_RESULT") ?? "aun-studio-result.txt";

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var localizer = new Localizer(Environment.GetEnvironmentVariable("AUN_LANG") is { Length: > 0 } l ? l : "ar");
        var automation = Environment.GetEnvironmentVariable("AUN_AUTOMATION") ?? "";
        var tabName = Environment.GetEnvironmentVariable("AUN_TAB") ?? "uploads";
        var exitAfter = int.TryParse(Environment.GetEnvironmentVariable("AUN_EXIT_AFTER"), out var s) ? s : 0;
        var ui = DispatcherQueue.GetForCurrentThread();

        var environment = AppEnvironment.Resolve(Environment.GetEnvironmentVariable("AUN_ENV"), AppEnvironment.BuildDefault);
        // CI only: there is no account to sign in with on a runner, and each scenario starts clean.
        var simulateUploads = Environment.GetEnvironmentVariable("AUN_SIMULATE_UPLOADS") == "1";
        var dataRoot = simulateUploads
            ? Path.Combine(Path.GetTempPath(), "aun-studio-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlUmmahNowStudio");
        var settingsStore = new SettingsStore(Path.Combine(dataRoot, "settings.json"));
        var settings = settingsStore.Load();
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AlUmmahNowStudioWindows/0.1");
        var authSession = new PkceAuthSession(http, environment.AuthIssuer, environment.StudioClientId, new DpapiSecureStore(Path.Combine(dataRoot, "secure")), new ShellBrowserLauncher(), localizer["auth.browser.done"]);
        var auth = new AuthViewModel(authSession);
        var uploadContext = new UploadContext(new HttpChannelClient(http, environment.VideosApi, authSession));
        IUploadEngine uploadEngine = simulateUploads
            ? new SimulatedUploadEngine(TimeSpan.FromMilliseconds(250))
            : new TusUploadEngine(http, environment.VideosApi, authSession, uploadContext.ChannelIdAsync);
        var uploads = new UploadQueue(uploadEngine, Path.Combine(dataRoot, "uploads.json"));
        var encode = new EncodeQueue(new MediaFoundationEncodeBackend());

        var project = new TimelineProject();
        var video = new Track { Kind = TrackKind.Video };
        video.Clips.Add(new Clip { Source = "intro.mp4", Start = 0, Duration = 12 });
        video.Clips.Add(new Clip { Source = "lecture.mp4", Start = 12, Duration = 40 });
        var audio = new Track { Kind = TrackKind.Audio };
        audio.Clips.Add(new Clip { Source = "lecture.m4a", Start = 0, Duration = 52 });
        project.Tracks.Add(video);
        project.Tracks.Add(audio);

        var uploadRows = new StackPanel { Spacing = 8 };
        var encodeRows = new StackPanel { Spacing = 8 };

        var pivot = new Pivot { FlowDirection = localizer.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight };
        var uploadsPage = new StackPanel { Spacing = 10, Padding = new Thickness(12) };
        var notice = new TextBlock { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        var signButton = new Button { Content = localizer["auth.signIn"] };
        var watchButton = new Button { Content = localizer["studio.upload.watch"] };
        var accountRow = new Grid { ColumnSpacing = 8 };
        accountRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        accountRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        accountRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        accountRow.Children.Add(notice);
        Grid.SetColumn(watchButton, 1);
        accountRow.Children.Add(watchButton);
        Grid.SetColumn(signButton, 2);
        accountRow.Children.Add(signButton);
        uploadsPage.Children.Add(accountRow);

        void RenderAccount()
        {
            signButton.Content = localizer[auth.IsBusy ? "auth.signingIn" : auth.IsSignedIn ? "auth.signOut" : "auth.signIn"];
            signButton.IsEnabled = !auth.IsBusy;
            notice.Text = simulateUploads ? localizer["studio.upload.simulated"]
                : auth.Failed ? localizer["auth.failed"]
                : !auth.IsSignedIn ? localizer["studio.upload.signIn"]
                : uploadContext.Channel is { } c ? localizer.Format("studio.upload.channel", Localizer.Isolate(c.Name))
                : Localizer.Isolate(auth.AccountName);
        }

        async Task RefreshChannelAsync()
        {
            uploadContext.Reset();
            if (auth.IsSignedIn && !simulateUploads)
            {
                try
                {
                    if (await uploadContext.LoadAsync() == null) notice.Text = localizer["studio.upload.noChannel"];
                }
                catch (Exception ex) when (ex is CatalogException || ex is AuthRequiredException || ex is HttpRequestException)
                {
                    File.AppendAllText(ResultPath + ".crash.txt", "channels: " + ex.Message + "\n");
                }

                uploads.Pump();
            }

            RenderAccount();
        }

        auth.PropertyChanged += (_, e) =>
        {
            RenderAccount();
            if (e.PropertyName == nameof(AuthViewModel.IsSignedIn)) _ = RefreshChannelAsync();
        };
        signButton.Click += async (_, _) =>
        {
            if (auth.IsSignedIn) auth.SignOut();
            else await auth.SignInAsync();
        };
        uploadsPage.Children.Add(uploadRows);
        pivot.Items.Add(new PivotItem { Header = localizer["studio.tab.uploads"], Content = uploadsPage, Tag = "uploads" });
        var encodePage = new StackPanel { Padding = new Thickness(12) };
        encodePage.Children.Add(encodeRows);
        pivot.Items.Add(new PivotItem { Header = localizer["studio.tab.encode"], Content = encodePage, Tag = "encode" });

        double playhead = 20;
        // The canvas computes mirrored coordinates itself, so it must not be mirrored again.
        var canvas = new Canvas { Height = 220, FlowDirection = FlowDirection.LeftToRight, Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 9, 48, 65)) };

        void DrawTimeline()
        {
            canvas.Children.Clear();
            var width = canvas.ActualWidth > 0 ? canvas.ActualWidth : 1200;
            var viewport = new TimelineViewport(width / Math.Max(1, project.Duration), 0, width, localizer.IsRtl);
            for (var t = 0; t < project.Tracks.Count; t++)
            {
                foreach (var clip in project.Tracks[t].Clips)
                {
                    var a = viewport.ToX(clip.Start);
                    var b = viewport.ToX(clip.End);
                    var color = project.Tracks[t].Kind == TrackKind.Video
                        ? global::Windows.UI.Color.FromArgb(255, 7, 66, 90)
                        : global::Windows.UI.Color.FromArgb(255, 142, 108, 83);
                    var rect = new Rectangle { Width = Math.Max(1, Math.Abs(b - a) - 2), Height = 56, Fill = new SolidColorBrush(color) };
                    Canvas.SetLeft(rect, Math.Min(a, b) + 1);
                    Canvas.SetTop(rect, 10 + (t * 64));
                    canvas.Children.Add(rect);
                }
            }

            var head = new Rectangle { Width = 2, Height = 220, Fill = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 160, 55)) };
            Canvas.SetLeft(head, viewport.ToX(playhead) - 1);
            canvas.Children.Add(head);
        }

        void Scrub(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var viewport = new TimelineViewport(canvas.ActualWidth / Math.Max(1, project.Duration), 0, canvas.ActualWidth, localizer.IsRtl);
            playhead = Math.Clamp(viewport.ToTime(e.GetCurrentPoint(canvas).Position.X), 0, project.Duration);
            DrawTimeline();
        }

        canvas.SizeChanged += (_, _) => DrawTimeline();
        canvas.PointerPressed += (_, e) => { canvas.CapturePointer(e.Pointer); Scrub(e); };
        canvas.PointerMoved += (_, e) => { if (e.Pointer.IsInContact) Scrub(e); };
        canvas.PointerReleased += (_, e) => canvas.ReleasePointerCapture(e.Pointer);

        var split = new Button { Content = localizer["studio.timeline.split"] };
        split.Click += (_, _) => { foreach (var t in project.Tracks) project.Split(t, playhead); DrawTimeline(); };
        var ripple = new Button { Content = localizer["studio.timeline.delete"] };
        ripple.Click += (_, _) =>
        {
            foreach (var t in project.Tracks)
            {
                if (t.Clips.FirstOrDefault(c => playhead >= c.Start && playhead < c.End) is { } clip) project.RippleDelete(t, clip);
            }

            playhead = Math.Min(playhead, project.Duration);
            DrawTimeline();
        };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tools.Children.Add(split);
        tools.Children.Add(ripple);
        tools.Children.Add(new TextBlock { Text = localizer["studio.timeline.hint"], VerticalAlignment = VerticalAlignment.Center });
        var timelinePage = new StackPanel { Spacing = 10, Padding = new Thickness(12) };
        timelinePage.Children.Add(tools);
        timelinePage.Children.Add(canvas);
        pivot.Items.Add(new PivotItem { Header = localizer["studio.tab.timeline"], Content = timelinePage, Tag = "timeline" });
        pivot.SelectedItem = pivot.Items.Cast<PivotItem>().First(p => (string)p.Tag == tabName);

        _window = new Window { Title = localizer["studio.name"], Content = pivot };
        _window.Activate();

        _refresh = ui.CreateTimer();
        _refresh.Interval = TimeSpan.FromMilliseconds(300);
        _refresh.Tick += (_, _) =>
        {
            uploadRows.Children.Clear();
            foreach (var item in uploads.Snapshot())
            {
                uploadRows.Children.Add(Row(Path.GetFileName(item.FilePath), item.Fraction, localizer["studio.status." + item.Status.ToString().ToLowerInvariant()]));
            }

            encodeRows.Children.Clear();
            foreach (var current in encode.Jobs.ToList())
            {
                var status = current.Status switch
                {
                    EncodeStatus.Running => localizer["studio.encode.running"],
                    EncodeStatus.Completed => localizer["studio.status.completed"],
                    EncodeStatus.Failed => localizer["studio.status.failed"],
                    EncodeStatus.Canceled => localizer["studio.encode.canceled"],
                    _ => localizer["studio.status.queued"],
                };
                var name = Path.GetFileName(current.Request.Source) + " · " + localizer["studio.preset." + current.Request.Preset.Key];
                var encoder = current.UsedEncoder is { } e ? " " + localizer[e.Kind == EncoderKind.Hardware ? "studio.encode.hardware" : "studio.encode.software"] : "";
                encodeRows.Children.Add(Row(name, current.Progress, status + encoder));
            }
        };
        _refresh.Start();

        var dir = Environment.GetEnvironmentVariable("AUN_WATCH_DIR");
        if (string.IsNullOrEmpty(dir)) dir = settings.WatchFolder;
        var watch = string.IsNullOrEmpty(dir) ? null : new FolderWatch(dir);
        _watchTimer = ui.CreateTimer();
        _watchTimer.Interval = TimeSpan.FromSeconds(1);
        _watchTimer.Tick += (_, _) =>
        {
            if (watch == null || (!simulateUploads && !auth.IsSignedIn)) return;
            foreach (var (path, size) in watch.Scan()) uploads.Enqueue(path, size);
        };
        _watchTimer.Start();

        watchButton.Click += async (_, _) =>
        {
            var picker = new global::Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_window));
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;
            settings.WatchFolder = folder.Path;
            settingsStore.Save(settings);
            watch = new FolderWatch(folder.Path);
        };

        auth.Restore();
        _ = RefreshChannelAsync();

        EncodeJob? job = null;
        if (automation.StartsWith("encode:", StringComparison.Ordinal))
        {
            var source = automation.Substring(7);
            job = encode.Add(new EncodeRequest(source, source + ".out.mp4", EncodePreset.Lecture));
        }

        if (exitAfter > 0)
        {
            _endTimer = ui.CreateTimer();
            _endTimer.Interval = TimeSpan.FromSeconds(exitAfter);
            _endTimer.IsRepeating = false;
            _endTimer.Tick += (_, _) =>
            {
                var snapshot = uploads.Snapshot();
                var uploadsDone = snapshot.Count > 0 && snapshot.All(i => i.Status == UploadStatus.Completed);
                var pass = job != null ? job.Status == EncodeStatus.Completed : uploadsDone || tabName == "timeline";
                File.WriteAllText(ResultPath, $"AUTOMATION {tabName} uploads={snapshot.Count} uploadsDone={uploadsDone} encode={job?.Status} encoder={job?.UsedEncoder?.Name} error={job?.Error} RESULT {(pass ? "PASS" : "FAIL")}");
                Exit();
            };
            _endTimer.Start();
        }
    }

    private static FrameworkElement Row(string name, double fraction, string status)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = fraction, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(bar, 1);
        grid.Children.Add(bar);
        var text = new TextBlock { Text = status };
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        return grid;
    }
}
