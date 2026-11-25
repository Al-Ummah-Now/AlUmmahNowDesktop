using System.Reflection;
using AlUmmahNow.App.Demo;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Updates;
using AlUmmahNow.CoreBridge;
using Xunit;

namespace AlUmmahNow.App.Tests;

/// <summary>The check for a new version (wiki AUN-APP-UPDATES §4.1.1, §5.5): it tells, once and at the right time, and does no more.</summary>
public sealed class UpdateTests
{
    private sealed class Checker : IUpdateChecker
    {
        public Func<Task<UpdateAnswer>> Next = () => Task.FromResult(new UpdateAnswer { Enabled = true });
        public UpdatePromptKind Known;
        public int Checks;

        public Task<UpdateAnswer> CheckAsync(CancellationToken cancellationToken = default)
        {
            Checks++;
            cancellationToken.ThrowIfCancellationRequested();
            return Next();
        }

        public UpdatePromptKind LastKnown() => Known;
        public string State() => "core-state";
        public void Answers(UpdatePromptKind kind, string? version = "1.5.0", int hours = 6, bool enabled = true) =>
            Next = () => Task.FromResult(new UpdateAnswer { Enabled = enabled, Kind = kind, Version = version, MinimumVersion = "1.4.0", NotesEn = "n", NotesAr = "ن", CheckHours = hours });
        public void Fails(Exception? ex = null) => Next = () => Task.FromException<UpdateAnswer>(ex ?? new UpdateCheckFailedException("offline"));
    }

    /// <summary>Time that only passes when it is told to: every wait of the service ends when its moment is there.</summary>
    private sealed class Time
    {
        private readonly List<(DateTimeOffset At, TaskCompletionSource Done)> _waits = new();
        public DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        public TimeSpan Longest;

        /// <summary>How far the wall clock is off: a wait does not know it.</summary>
        public TimeSpan Off;

        public Task Delay(TimeSpan wait, CancellationToken stop)
        {
            if (wait > Longest) Longest = wait;
            var done = new TaskCompletionSource();
            stop.Register(() => done.TrySetCanceled(stop));
            _waits.Add((Now + wait, done));
            return done.Task;
        }

        public void Pass(TimeSpan time)
        {
            var end = Now + time;
            while (_waits.Where(w => w.At <= end && !w.Done.Task.IsCompleted).OrderBy(w => w.At).FirstOrDefault() is { Done: not null } next)
            {
                if (next.At > Now) Now = next.At;
                _waits.Remove(next);
                next.Done.TrySetResult();
            }

            Now = end;
        }
    }

    private sealed class Delivery : IUpdateDelivery
    {
        public int Begun;
        public void Begin() => Begun++;
    }

    private sealed class Browser : IBrowserLauncher
    {
        public List<Uri> Opened = new();
        public Exception? Breaks;

        public void Open(Uri uri)
        {
            if (Breaks != null) throw Breaks;
            Opened.Add(uri);
        }
    }

    private readonly Checker _checker = new();
    private readonly Time _time = new();
    private readonly Delivery _delivery = new();
    private readonly string _file = Path.Combine(Path.GetTempPath(), "aun-updates-" + Guid.NewGuid().ToString("N"), "updates.json");
    private bool _busy;

    private UpdateService Service(UpdateInstall install = UpdateInstall.Installer) =>
        new(_checker, new UpdateStore(_file), install, "1.4.2", () => _busy, _delivery, () => _time.Now + _time.Off, _time.Delay);

    private static TimeSpan Seconds(double s) => TimeSpan.FromSeconds(s);
    private static TimeSpan Hours(double h) => TimeSpan.FromHours(h);

    [Fact]
    public void The_first_check_is_thirty_seconds_after_launch_then_every_check_hours()
    {
        _checker.Answers(UpdatePromptKind.None, hours: 3);
        _ = Service().RunAsync();
        _time.Pass(Seconds(29));
        Assert.Equal(0, _checker.Checks);
        _time.Pass(Seconds(1));
        Assert.Equal(1, _checker.Checks);
        _time.Pass(Hours(3) - Seconds(1));
        Assert.Equal(1, _checker.Checks);
        _time.Pass(Seconds(1));
        Assert.Equal(2, _checker.Checks);
        // A computer that slept is asked for soon after it wakes: no wait is the whole interval
        Assert.True(_time.Longest <= UpdateService.LongestWait);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1_000, 48)]
    public void The_interval_keeps_its_bounds(int answered, int hours)
    {
        _checker.Answers(UpdatePromptKind.None, hours: answered);
        _ = Service().RunAsync();
        _time.Pass(Seconds(30));
        _time.Pass(Hours(hours) - Seconds(1));
        Assert.Equal(1, _checker.Checks);
        _time.Pass(Seconds(1));
        Assert.Equal(2, _checker.Checks);
    }

    [Fact]
    public async Task Check_now_puts_the_next_check_off_and_never_runs_twice_at_once()
    {
        var service = Service();
        _ = service.RunAsync();
        _time.Pass(Seconds(30));
        _time.Pass(Hours(5));
        var gate = new TaskCompletionSource<UpdateAnswer>();
        _checker.Next = () => gate.Task;
        var first = service.CheckAsync(isAsked: true);
        var second = service.CheckAsync(isAsked: true);
        Assert.Same(first, second);
        Assert.Equal(UpdateResult.Checking, service.Result);
        gate.SetResult(new UpdateAnswer { Enabled = true });
        Assert.True(await first);
        Assert.Equal(2, _checker.Checks);
        _checker.Answers(UpdatePromptKind.None);
        _time.Pass(Hours(5));
        Assert.Equal(2, _checker.Checks);
        _time.Pass(Hours(1));
        Assert.Equal(3, _checker.Checks);
    }

    [Fact]
    public void Offline_is_silent_and_the_schedule_goes_on()
    {
        _checker.Fails();
        var service = Service();
        var run = service.RunAsync();
        _time.Pass(Seconds(30));
        Assert.Equal(UpdateStage.Hidden, service.View.Stage);
        Assert.Equal("", Text(service, "en").Result);
        _checker.Answers(UpdatePromptKind.Notice);
        _time.Pass(Hours(6));
        Assert.Equal(2, _checker.Checks);
        Assert.Equal(UpdateStage.Available, service.View.Stage);
        Assert.False(run.IsCompleted);
    }

    [Fact]
    public void Nothing_a_check_throws_ends_the_schedule()
    {
        _checker.Fails(new NullReferenceException());
        var service = Service();
        _ = service.RunAsync();
        _time.Pass(Seconds(30));
        _checker.Next = () => throw new TimeoutException();
        _time.Pass(Hours(6));
        _time.Pass(Hours(6));
        Assert.Equal(3, _checker.Checks);
        Assert.Equal(UpdateResult.Failed, service.Result);
    }

    [Fact]
    public async Task Only_check_now_tells_that_it_came_to_nothing()
    {
        _checker.Fails();
        var service = Service();
        Assert.False(await service.CheckAsync());
        Assert.Equal("", Text(service, "en").Result);
        Assert.False(await service.CheckAsync(isAsked: true));
        Assert.Equal("Couldn't check for updates. Try again later.", Text(service, "en").Result);
    }

    [Fact]
    public async Task Offline_keeps_what_the_last_answer_asked_for()
    {
        _checker.Answers(UpdatePromptKind.Notice);
        var service = Service();
        await service.CheckAsync();
        _checker.Fails();
        await service.CheckAsync();
        Assert.Equal(UpdateStage.Available, service.View.Stage);
    }

    [Fact]
    public void Offline_at_launch_is_a_bar_at_most()
    {
        _checker.Answers(UpdatePromptKind.Forced);
        _ = Service().CheckAsync();
        _checker.Fails();
        foreach (var known in new[] { UpdatePromptKind.Forced, UpdatePromptKind.RequiredBar })
        {
            _checker.Known = known;
            var again = Service();
            _ = again.RunAsync();
            Assert.Equal(UpdateStage.Required, again.View.Stage);
            Assert.False(again.View.CanClose);
            Assert.False(again.View.CanDownload);
            _time.Pass(Seconds(30));
            Assert.Equal(UpdateStage.Required, again.View.Stage);
        }

        _checker.Known = UpdatePromptKind.Notice;
        var notice = Service();
        _ = notice.RunAsync();
        Assert.Equal(UpdateStage.Hidden, notice.View.Stage);
    }

    [Fact]
    public async Task A_closed_bar_stays_closed_for_its_version_and_across_runs()
    {
        _checker.Answers(UpdatePromptKind.Notice);
        var service = Service();
        await service.CheckAsync();
        Assert.True(service.View.CanClose);
        service.Dismiss();
        Assert.Equal(UpdateStage.Hidden, service.View.Stage);
        await service.CheckAsync();
        Assert.Equal(UpdateStage.Hidden, service.View.Stage);
        // Settings still tells of it
        Assert.Equal(UpdateResult.Found, service.Result);
        Assert.Equal("1.5.0", service.Found.Version);

        var next = Service();
        await next.CheckAsync();
        Assert.Equal(UpdateStage.Hidden, next.View.Stage);
        _checker.Answers(UpdatePromptKind.Notice, "1.5.1");
        await next.CheckAsync();
        Assert.Equal(UpdateStage.Available, next.View.Stage);
    }

    [Fact]
    public async Task Only_so_many_closed_versions_are_kept()
    {
        var service = Service();
        for (var i = 0; i < 15; i++)
        {
            _checker.Answers(UpdatePromptKind.Notice, "1.5." + i);
            await service.CheckAsync();
            service.Dismiss();
        }

        var kept = new UpdateStore(_file).Load().Dismissed!;
        Assert.Equal(UpdateService.MaxDismissed, kept.Count);
        Assert.Equal("1.5.14", kept[^1]);
    }

    [Theory]
    [InlineData(UpdatePromptKind.RequiredBar, UpdateStage.Required)]
    [InlineData(UpdatePromptKind.Forced, UpdateStage.Forced)]
    public async Task What_is_required_cannot_be_closed(UpdatePromptKind kind, UpdateStage stage)
    {
        _checker.Answers(kind);
        var service = Service();
        await service.CheckAsync();
        service.Dismiss();
        Assert.Equal(stage, service.View.Stage);
        Assert.Null(new UpdateStore(_file).Load().Dismissed);
    }

    /// <summary>Too old with nothing to update to: Settings names no version and no notes, and does not say "latest".</summary>
    [Theory]
    [InlineData(UpdatePromptKind.RequiredBar, UpdateResult.Required, "This version will stop working soon. Update when a new version is offered.")]
    [InlineData(UpdatePromptKind.None, UpdateResult.UpToDate, "You have the latest version.")]
    public async Task Settings_tells_of_a_version_only_when_there_is_one_to_update_to(UpdatePromptKind kind, UpdateResult result, string told)
    {
        _checker.Answers(kind);
        var service = Service();
        await service.CheckAsync(isAsked: true);
        Assert.Equal(result, service.Result);
        Assert.Equal(told, Text(service, "en").Result);
        foreach (var view in new[] { service.View, service.Found })
        {
            Assert.Null(view.Version);
            Assert.Equal("", view.NotesEn + view.NotesAr + view.MessageEn + view.MessageAr);
        }
    }

    [Fact]
    public async Task The_screen_waits_for_running_work_and_comes_when_it_is_done()
    {
        _busy = true;
        _checker.Answers(UpdatePromptKind.Forced);
        var service = Service();
        await service.CheckAsync();
        Assert.Equal(UpdateStage.Waiting, service.View.Stage);
        _time.Pass(TimeSpan.FromMinutes(5));
        Assert.Equal(UpdateStage.Waiting, service.View.Stage);
        _busy = false;
        _time.Pass(UpdateService.BusyPoll);
        Assert.Equal(UpdateStage.Forced, service.View.Stage);
        // Work that is started behind it does not take it away
        _busy = true;
        await service.CheckAsync();
        Assert.Equal(UpdateStage.Forced, service.View.Stage);
    }

    [Fact]
    public async Task Waiting_ends_when_the_server_asks_for_no_more()
    {
        _busy = true;
        _checker.Answers(UpdatePromptKind.Forced);
        var service = Service();
        await service.CheckAsync();
        _checker.Answers(UpdatePromptKind.None);
        await service.CheckAsync();
        Assert.Equal(UpdateStage.Hidden, service.View.Stage);
        _busy = false;
        _time.Pass(TimeSpan.FromMinutes(1));
        Assert.Equal(UpdateStage.Hidden, service.View.Stage);
        // And it waits again, once, when it is asked again
        _busy = true;
        _checker.Answers(UpdatePromptKind.Forced);
        await service.CheckAsync();
        _busy = false;
        _time.Pass(TimeSpan.FromMinutes(1));
        Assert.Equal(UpdateStage.Forced, service.View.Stage);
    }

    [Theory]
    [InlineData(UpdatePromptKind.Forced)]
    [InlineData(UpdatePromptKind.RequiredBar)]
    [InlineData(UpdatePromptKind.Notice)]
    public async Task Switched_off_on_the_server_takes_everything_away(UpdatePromptKind kind)
    {
        _checker.Answers(kind);
        var service = Service();
        await service.CheckAsync();
        Assert.True(service.IsSectionVisible);
        _checker.Answers(kind, enabled: false);
        await service.CheckAsync();
        Assert.Equal(UpdateStage.Hidden, service.View.Stage);
        Assert.False(service.IsSectionVisible);
        Assert.False(Service().IsSectionVisible);
    }

    [Fact]
    public async Task The_section_is_there_once_the_switch_was_seen_on()
    {
        var service = Service();
        Assert.False(service.IsSectionVisible);
        _checker.Fails();
        await service.CheckAsync();
        Assert.False(service.IsSectionVisible);
        _checker.Answers(UpdatePromptKind.None);
        var told = new List<string?>();
        service.PropertyChanged += (_, e) => told.Add(e.PropertyName);
        await service.CheckAsync();
        Assert.True(service.IsSectionVisible);
        Assert.Contains(nameof(UpdateService.IsSectionVisible), told);
        Assert.True(Service().IsSectionVisible);
        Assert.Equal(_time.Now, Service().LastChecked);
        Assert.Equal("core-state", new UpdateStore(_file).Load().Core);
    }

    [Theory]
    [InlineData(UpdateInstall.MicrosoftStore)]
    [InlineData(UpdateInstall.Development)]
    public async Task A_store_build_and_a_development_build_ask_nothing(UpdateInstall install)
    {
        _checker.Answers(UpdatePromptKind.Forced);
        await Service().CheckAsync();
        var service = Service(install);
        _checker.Checks = 0;
        _checker.Known = UpdatePromptKind.RequiredBar;
        await service.RunAsync();
        Assert.False(await service.CheckAsync(isAsked: true));
        Assert.Equal(0, _checker.Checks);
        Assert.False(service.IsSectionVisible);
        Assert.Equal(UpdateStage.Hidden, service.View.Stage);
    }

    [Fact]
    public async Task Closing_the_app_ends_the_schedule_also_in_a_check()
    {
        using var stop = new CancellationTokenSource();
        var service = Service();
        var run = service.RunAsync(stop.Token);
        stop.Cancel();
        await run;
        Assert.Equal(0, _checker.Checks);

        using var later = new CancellationTokenSource();
        var other = Service();
        var running = other.RunAsync(later.Token);
        _checker.Next = async () =>
        {
            await _time.Delay(Hours(1), later.Token);
            return UpdateAnswer.Off;
        };
        _time.Pass(Seconds(30));
        later.Cancel();
        await running;
        Assert.Equal(UpdateStage.Hidden, other.View.Stage);
    }

    [Fact]
    public void A_clock_that_is_set_back_does_not_put_the_check_off()
    {
        _checker.Answers(UpdatePromptKind.None);
        _ = Service().RunAsync();
        _time.Pass(Seconds(30));
        _time.Off = -TimeSpan.FromDays(365);
        _time.Pass(Hours(6) + UpdateService.LongestWait);
        Assert.Equal(2, _checker.Checks);
    }

    /// <summary>The only place the app sends a person: our own releases page, from the app, over https.</summary>
    [Fact]
    public async Task The_button_opens_the_page_that_is_compiled_in_and_nothing_else()
    {
        var browser = new Browser();
        var delivery = new DownloadPageDelivery(browser);
        delivery.Begin();
        Assert.Equal("https://github.com/Al-Ummah-Now/AlUmmahNowDesktop/releases", Assert.Single(browser.Opened).AbsoluteUri);
        browser.Breaks = new InvalidOperationException("no browser");
        delivery.Begin();

        _checker.Answers(UpdatePromptKind.Forced);
        var service = Service();
        await service.CheckAsync();
        Assert.Equal(0, _delivery.Begun);
        service.Begin();
        Assert.Equal(1, _delivery.Begun);
    }

    /// <summary>No link reaches the app: what it takes from the server has no room for one.</summary>
    [Fact]
    public void The_answer_has_no_room_for_a_link()
    {
        foreach (var type in new[] { typeof(UpdateAnswer), typeof(UpdateView), typeof(UpdateMemory) })
        {
            foreach (var property in type.GetProperties())
            {
                Assert.NotEqual(typeof(Uri), property.PropertyType);
                Assert.DoesNotMatch("(?i)ur[il]|link|href|page|manifest", property.Name);
            }
        }
    }

    [Theory]
    [InlineData("1.5.0", true)]
    [InlineData("2026.11.24.2", true)]
    [InlineData("1.5.0-preview.3", true)]
    [InlineData("1.5", false)]
    [InlineData("v1.5.0", false)]
    [InlineData("1.5.0 \u202Eevil", false)]
    [InlineData("1.5.0\n", false)]
    [InlineData("https://example.com/1.5.0", false)]
    [InlineData("1.5.0-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData(null, false)]
    public async Task A_version_that_is_none_is_not_told_of(string? version, bool told)
    {
        _checker.Answers(UpdatePromptKind.Notice, version);
        var service = Service();
        await service.CheckAsync();
        Assert.Equal(told ? UpdateStage.Available : UpdateStage.Hidden, service.View.Stage);
        Assert.Equal(told ? UpdateResult.Found : UpdateResult.UpToDate, service.Result);
        Assert.Equal(told ? version : null, service.Found.Version);
        Assert.Equal(told ? "n" : "", service.Found.NotesEn);
    }

    [Fact]
    public async Task Notes_and_messages_are_plain_text()
    {
        _checker.Next = () => Task.FromResult(new UpdateAnswer
        {
            Enabled = true,
            Kind = UpdatePromptKind.Forced,
            Version = "1.5.0",
            NotesEn = " a\r\nb\u0007\u202Ec\u2068d <b>e</b> ",
            NotesAr = new string('ن', 5_000),
            MessageEn = new string('x', UpdateService.MaxText - 1) + "😀",
        });
        var service = Service();
        await service.CheckAsync();
        Assert.Equal("a\nbcd <b>e</b>", service.View.NotesEn);
        Assert.Equal(UpdateService.MaxText + 1, service.View.NotesAr.Length);
        Assert.Equal(new string('x', UpdateService.MaxText - 1) + "…", service.View.MessageEn);
        Assert.Equal("", service.View.MessageAr);
    }

    [Fact]
    public void A_store_file_that_cannot_be_read_or_written_starts_anew()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "{ not json");
        Assert.False(new UpdateStore(_file).Load().Enabled);
        Assert.True(new UpdateStore(_file).Save(new UpdateMemory { Enabled = true }));
        Assert.True(new UpdateStore(_file).Load().Enabled);
        Assert.False(new UpdateStore(Path.Combine(_file, "below-a-file.json")).Save(new UpdateMemory()));
    }

    [Theory]
    [InlineData(true, true, true, @"C:\Program Files\WindowsApps\x", UpdateInstall.MicrosoftStore)]
    [InlineData(false, true, true, @"C:\Program Files\WindowsApps\x", UpdateInstall.MicrosoftStore)]
    [InlineData(true, true, false, @"C:\Program Files\WindowsApps\x", UpdateInstall.Msix)]
    [InlineData(true, false, false, @"c:\program files\Al Ummah Now\", UpdateInstall.Installer)]
    [InlineData(true, false, false, @"C:\Program Files Old\Al Ummah Now", UpdateInstall.Portable)]
    [InlineData(true, false, false, @"D:\apps\studio", UpdateInstall.Portable)]
    [InlineData(false, false, false, @"C:\Program Files\Al Ummah Now", UpdateInstall.Development)]
    public void How_windows_installed_it(bool release, bool packaged, bool store, string folder, UpdateInstall install) =>
        Assert.Equal(install, UpdateInstalls.OnWindows(release, packaged, store, folder, @"C:\Program Files"));

    [Theory]
    [InlineData(true, true, "/app/lib/alummahnow", UpdateApp.Viewer, UpdateInstall.Flatpak)]
    [InlineData(true, false, "/opt/alummahnow/", UpdateApp.Viewer, UpdateInstall.Deb)]
    [InlineData(true, false, "/opt/alummahnow-studio", UpdateApp.Studio, UpdateInstall.Deb)]
    [InlineData(true, false, "/opt/alummahnow-studio", UpdateApp.Viewer, UpdateInstall.LinuxFolder)]
    [InlineData(true, false, "/home/x/studio", UpdateApp.Studio, UpdateInstall.LinuxFolder)]
    [InlineData(false, true, "/app/lib/alummahnow", UpdateApp.Viewer, UpdateInstall.Development)]
    public void How_linux_installed_it(bool release, bool flatpak, string folder, UpdateApp app, UpdateInstall install) =>
        Assert.Equal(install, UpdateInstalls.OnLinux(release, flatpak, folder, app));

    [Fact]
    public void A_build_of_a_developer_or_of_CI_is_no_release() => Assert.False(UpdateInstalls.IsReleaseBuild);

    /// <summary>The names in the commands are the packages' own (release.yml, build-deb.sh).</summary>
    [Fact]
    public void The_commands_name_the_packages_of_the_release_workflow()
    {
        var root = Root();
        var release = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
        foreach (var app in new[] { UpdateApp.Viewer, UpdateApp.Studio })
        {
            Assert.Contains("deb: " + UpdateInstalls.DebPackage(app) + "\n", release);
            Assert.Equal("sudo apt install ./" + UpdateInstalls.DebPackage(app) + "_*_amd64.deb", UpdateInstalls.Command(UpdateInstall.Deb, app));
            var flatpak = UpdateInstalls.Command(UpdateInstall.Flatpak, app)!;
            var package = app == UpdateApp.Studio ? "AlUmmahNow-Studio-Linux" : "AlUmmahNow-Linux";
            Assert.Contains("package: " + package + "\n", release);
            Assert.Equal("flatpak install ./" + package + "-*-x86_64.flatpak", flatpak);
        }

        Assert.Contains("${{ matrix.package }}-${{ needs.verify.outputs.version }}-x86_64.flatpak", release);
        Assert.Contains("/opt/$package", File.ReadAllText(Path.Combine(root, "scripts", "build-deb.sh")));
        // The define that makes a build a release is what the workflow passes, to every app
        Assert.True(release.Split("-p:AunProd=").Length - 1 >= 4);
        Assert.Contains("'$(AunProd)' != ''\">$(DefineConstants);AUN_RELEASE", File.ReadAllText(Path.Combine(root, "Directory.Build.props")));
        foreach (var install in Enum.GetValues<UpdateInstall>())
        {
            foreach (var language in Localizer.SupportedLanguages) Assert.True(new Localizer().Bundle(language).ContainsKey(UpdateInstalls.HowKey(install)));
        }
    }

    [Theory]
    [InlineData("en", "1.4.2", "\u20681.4.2\u2069")]
    [InlineData("ar", "1.4.2", "\u2068١.٤.٢\u2069")]
    [InlineData("ar", "2026.11.24", "\u2068٢٠٢٦.١١.٢٤\u2069")]
    [InlineData("ar", "1.5.0-preview.3", "\u2068\u202A١.٥.٠-preview.٣\u202C\u2069")]
    public void A_version_is_no_decimal_number(string language, string version, string shown) => Assert.Equal(shown, new Localizer(language).Version(version));

    [Fact]
    public async Task The_words_are_in_the_language_with_its_digits()
    {
        _checker.Next = () => Task.FromResult(new UpdateAnswer { Enabled = true, Kind = UpdatePromptKind.Notice, Version = "1.5.0", NotesEn = "new", NotesAr = "" });
        var service = Service(UpdateInstall.Deb);
        Assert.Equal("لم يُبحث بعد", Text(service, "ar").LastChecked);
        await service.CheckAsync();
        Assert.Equal("A new version, \u20681.5.0\u2069, is available.", Text(service, "en").Bar(service.View));
        Assert.Equal("يتوفّر إصدار جديد، \u2068١.٥.٠\u2069.", Text(service, "ar").Bar(service.View));
        Assert.Equal("What's new in \u20681.5.0\u2069", Text(service, "en").NotesTitle(service.View));
        // No notes in the language: nothing to show, not the other language's
        Assert.Equal("new", Text(service, "en").Notes(service.View));
        Assert.Equal("", Text(service, "ar").Notes(service.View));
        Assert.Equal("Last checked: \u2068Nov 24, 2026, 12:00 PM\u2069", Text(service, "en").LastChecked);
        Assert.Equal("آخر بحث: \u2068٢٤\u200F/١١\u200F/٢٠٢٦، ١٢:٠٠ م\u2069", Text(service, "ar").LastChecked);
        Assert.Equal("الإصدار \u2068١.٤.٢\u2069", Text(service, "ar").Version);
        Assert.Equal("يتوفّر الإصدار \u2068١.٥.٠\u2069.", Text(service, "ar").Result);
        Assert.Equal("sudo apt install ./alummahnow_*_amd64.deb", Text(service, "ar").Command);
        Assert.Contains("\u2066github.com/Al-Ummah-Now/AlUmmahNowDesktop/releases\u2069", Text(service, "ar").Page);
    }

    [Fact]
    public async Task The_screen_names_the_running_and_the_needed_version()
    {
        _checker.Answers(UpdatePromptKind.Forced);
        var service = Service();
        await service.CheckAsync();
        Assert.Equal("لديك الإصدار \u2068١.٤.٢\u2069. يلزم الإصدار \u2068١.٤.٠\u2069 أو أحدث.", Text(service, "ar").ForcedBody(service.View));
        _checker.Next = () => Task.FromResult(new UpdateAnswer { Enabled = true, Kind = UpdatePromptKind.Forced, MinimumVersion = "?" });
        await service.CheckAsync();
        Assert.Equal("You have version \u20681.4.2\u2069. Update to keep using Al Ummah Now.", Text(service, "en").ForcedBody(service.View));
    }

    [Fact]
    public void An_assembly_without_a_stamp_has_its_own_version() =>
        Assert.Matches(@"^\d+\.\d+\.\d+$", UpdateText.VersionOf(Assembly.GetExecutingAssembly()));

    private static UpdateText Text(UpdateService service, string language) => new(new Localizer(language, TimeZoneInfo.Utc), UpdateApp.Viewer, service);

    private static string Root()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "AlUmmahNow.sln"))) root = root.Parent;
        return root!.FullName;
    }
}
