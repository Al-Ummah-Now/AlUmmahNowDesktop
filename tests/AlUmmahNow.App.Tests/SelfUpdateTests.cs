using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Updates;
using AlUmmahNow.CoreBridge;
using Xunit;

namespace AlUmmahNow.App.Tests;

/// <summary>Installing in place (wiki AUN-APP-UPDATES §5.3, §3.7): when it downloads, what it tells, and when it installs.</summary>
public sealed class SelfUpdateTests
{
    private sealed class Checker : IUpdateChecker
    {
        public UpdateAnswer Next = new() { Enabled = true };
        public Task<UpdateAnswer> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(Next);
        public UpdatePromptKind LastKnown() => UpdatePromptKind.None;
        public string Learned = "";
        public event Action? Checked;
        public string State() => Learned;

        /// <summary>A manifest was checked, and the core learned of it.</summary>
        public void Learn(string what)
        {
            Learned = what;
            Checked?.Invoke();
        }
    }

    private sealed class Self : ISelfUpdate
    {
        public TaskCompletionSource? Gate;
        public Exception? Breaks, StagedBreaks, ApplyBreaks;
        public string? BreaksOnly;
        public Action? Checks;
        public Action? OnApply;
        public int Verified, Staged, Discarded, OnExit, Restarts;
        public string NotesEn = "signed notes", Version = "1.5.0";
        public List<string> Reports = new();

        public Task<VerifiedUpdate> VerifyAsync(string tag, string version, CancellationToken cancellationToken = default)
        {
            Verified++;
            Checks?.Invoke();
            return Breaks == null ? Task.FromResult(new VerifiedUpdate(Version, tag, NotesEn, "ملاحظات")) : Task.FromException<VerifiedUpdate>(Breaks);
        }

        public async Task<VerifiedUpdate> StageAsync(string tag, string version, IProgress<double>? progress, CancellationToken cancellationToken = default)
        {
            Staged++;
            Checks?.Invoke();
            progress?.Report(0.42);
            if (Gate != null) await Gate.Task;
            if (Breaks != null && (BreaksOnly == null || BreaksOnly == tag)) throw Breaks;
            return new VerifiedUpdate(Version, tag, NotesEn, "ملاحظات");
        }

        public void VerifyStaged()
        {
            Checks?.Invoke();
            if (StagedBreaks != null) throw StagedBreaks;
        }

        public void Discard() => Discarded++;
        public bool IsOpenElsewhere { get; set; }
        public void ApplyOnExit(bool restart)
        {
            OnApply?.Invoke();
            if (ApplyBreaks != null) throw ApplyBreaks;
            if (restart) Restarts++;
            else OnExit++;
        }

        public Task ReportAsync(string tag, string reason, CancellationToken cancellationToken = default)
        {
            Reports.Add(tag + " " + reason);
            return Task.CompletedTask;
        }
    }

    private sealed class Page : IUpdateDelivery
    {
        public int Opened;
        public void Begin() => Opened++;
    }

    private readonly Checker _checker = new();
    private readonly Self _self = new();
    private readonly Page _page = new();
    private readonly List<TaskCompletionSource> _waits = new();
    private readonly string _file = Path.Combine(Path.GetTempPath(), "aun-self-" + Guid.NewGuid().ToString("N"), "updates.json");
    private bool _busy;

    private int _closes;

    private UpdateService Service(UpdateInstall install = UpdateInstall.SelfUpdating, Action<UpdateService>? close = null)
    {
        UpdateService service = null!;
        return service = new UpdateService(_checker, new UpdateStore(_file), install, "1.4.2", () => _busy, _page, () => new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero), (_, _) =>
        {
            var wait = new TaskCompletionSource();
            _waits.Add(wait);
            return wait.Task;
        }, self: _self, close: () =>
        {
            _closes++;
            if (close == null) End(service);
            else close(service);
        });
    }

    private void TimePasses()
    {
        var waits = _waits.ToList();
        _waits.Clear();
        foreach (var w in waits) w.SetResult();
    }

    private void Offers(UpdatePromptKind kind = UpdatePromptKind.Notice, string version = "1.5.0", bool install = true, bool byDefault = true) => _checker.Next = new UpdateAnswer
    {
        Enabled = true, Kind = kind, Version = version, MinimumVersion = "1.4.8", NotesEn = "answer's notes", NotesAr = "ن", CanInstall = install, Tag = "v" + version, InstallsByDefault = byDefault,
    };

    private static UpdateText Text(UpdateService s, string language = "en", UpdateApp app = UpdateApp.Studio) => new(new Localizer(language, TimeZoneInfo.Utc), app, s);

    [Fact]
    public async Task Automatically_it_downloads_without_a_bar_and_tells_when_it_is_ready()
    {
        Offers();
        _self.Gate = new TaskCompletionSource();
        var service = Service();
        await service.CheckAsync();
        Assert.Equal(1, _self.Staged);
        Assert.Equal(UpdateStage.Hidden, service.View.Stage);
        _self.Gate.SetResult();
        await Until(() => service.View.Step == UpdateStep.Ready);
        Assert.Equal(UpdateStage.Available, service.View.Stage);
        Assert.Equal("Update ready. It installs when you close the app.", Text(service).Bar(service.View));
        Assert.Equal("Restart now", Text(service).Button(service.View));
        // The signed manifest's notes, not the answer's
        Assert.Equal("signed notes", service.View.NotesEn);
        Assert.Equal(0, _self.OnExit + _self.Restarts + _page.Opened);
        End(service);
        Assert.Equal(1, _self.OnExit);
        End(service);
        Assert.Equal(1, _self.OnExit);
    }

    [Fact]
    public async Task Asked_for_it_shows_the_download_and_then_restarts_when_told()
    {
        Offers(byDefault: false);
        _self.Gate = new TaskCompletionSource();
        var service = Service();
        await service.CheckAsync();
        Assert.Equal(0, _self.Staged);
        Assert.Equal("A new version, \u20681.5.0\u2069, is available.", Text(service).Bar(service.View));
        Assert.Equal("Update", Text(service).Button(service.View));
        service.Begin();
        Assert.Equal(0, _page.Opened);
        Assert.Equal(UpdateStep.Downloading, service.View.Step);
        Assert.Equal("جارٍ تنزيل التحديث… ٤٢٪", Text(service, "ar").Bar(service.View));
        Assert.False(service.View.CanClose);
        Assert.False(service.View.CanBegin);
        service.Begin();
        Assert.Equal(1, _self.Staged);
        _self.Gate.SetResult();
        await Until(() => service.View.Step == UpdateStep.Ready);
        service.Begin();
        Assert.Equal(1, _self.Restarts);
        // The updater is running: closing starts no second one
        End(service);
        Assert.Equal(0, _self.OnExit);
    }

    [Theory]
    [InlineData(UpdateApp.Studio, "Update ready. Finish or pause your uploads and encodes first.")]
    [InlineData(UpdateApp.Viewer, "Update ready. You can restart when the live stream ends or you leave it.")]
    public async Task Nothing_is_installed_while_work_is_running(UpdateApp app, string told)
    {
        Offers();
        _busy = true;
        var service = Service();
        await service.CheckAsync();
        Assert.Equal(told, Text(service, app: app).Bar(service.View));
        Assert.False(service.View.CanBegin);
        service.Begin();
        Assert.Equal(0, _self.Restarts);
        _busy = false;
        TimePasses();
        Assert.True(service.View.CanBegin);
        _busy = true;
        TimePasses();
        Assert.False(service.View.CanBegin);
        End(service);
        Assert.Equal(0, _self.OnExit + _self.Restarts);
    }

    /// <summary>Velopack's updater ends every process of the folder: a second window may be uploading.</summary>
    [Fact]
    public async Task Nothing_is_installed_while_the_app_is_open_a_second_time()
    {
        Offers();
        _self.IsOpenElsewhere = true;
        var service = Service();
        await service.CheckAsync();
        Assert.Equal("Update ready. Close the app's other window to install it.", Text(service).Bar(service.View));
        Assert.False(service.View.CanBegin);
        service.Begin();
        _self.IsOpenElsewhere = false;
        TimePasses();
        Assert.True(service.View.CanBegin);
        // Opened after the bar was painted
        _self.IsOpenElsewhere = true;
        service.Begin();
        Assert.False(service.View.CanBegin);
        End(service);
        Assert.Equal(0, _self.OnExit + _self.Restarts);
    }

    [Fact]
    public async Task A_refused_update_is_reported_and_leaves_nothing_behind()
    {
        Offers();
        _self.Breaks = new UpdateRefusedException("bad_signature");
        var service = Service();
        await service.CheckAsync();
        Assert.Equal(new[] { "v1.5.0 bad_signature" }, _self.Reports);
        Assert.Equal(UpdateStage.Hidden, service.View.Stage);
        Assert.NotEqual(UpdateResult.Found, service.Result);
        Assert.Null(service.Found.Version);
        Assert.Equal("", Text(service).Result);
        service.Begin();
        End(service);
        Assert.Equal(0, _self.OnExit + _self.Restarts);
        // The next check tries again
        _self.Breaks = null;
        await service.CheckAsync();
        Assert.Equal(UpdateStep.Ready, service.View.Step);
    }

    [Fact]
    public async Task What_is_staged_is_checked_again_at_every_answer_before_restart_and_at_exit()
    {
        Offers();
        var service = Service();
        await service.CheckAsync();
        // A newer key list came with the answer: yanked
        _self.StagedBreaks = new UpdateRefusedException("yanked");
        await service.CheckAsync();
        Assert.Equal(new[] { "v1.5.0 yanked" }, _self.Reports);
        Assert.Equal(UpdateStage.Hidden, service.View.Stage);

        foreach (var act in new Action<UpdateService>[] { s => s.Begin(), s => End(s) })
        {
            _self.StagedBreaks = null;
            var other = Service();
            await other.CheckAsync();
            Assert.Equal(UpdateStep.Ready, other.View.Step);
            _self.StagedBreaks = new UpdateRefusedException("hash");
            act(other);
            Assert.Equal(0, _self.Restarts + _self.OnExit);
        }
    }

    [Theory]
    [InlineData("other")]
    [InlineData("withdrawn")]
    [InlineData("off")]
    public async Task What_is_no_longer_offered_is_dropped(string how)
    {
        Offers();
        var service = Service();
        await service.CheckAsync();
        var before = _self.Discarded;
        if (how == "other") Offers(version: "1.5.1");
        else if (how == "withdrawn") _checker.Next = new UpdateAnswer { Enabled = true };
        else _checker.Next = new UpdateAnswer();
        _self.Version = "1.5.1";
        await service.CheckAsync();
        Assert.True(_self.Discarded > before);
        Assert.Equal(how == "other" ? "1.5.1" : null, service.View.Version);
        End(service);
        Assert.Equal(how == "other" ? 1 : 0, _self.OnExit);
    }

    [Fact]
    public async Task Trouble_with_the_network_is_no_refusal()
    {
        Offers(byDefault: false);
        _self.Breaks = new UpdateCheckFailedException("offline");
        var service = Service();
        await service.CheckAsync();
        service.Begin();
        Assert.Equal("Couldn't download the update. Try again later.", Text(service).Bar(service.View));
        Assert.True(service.View.CanBegin);
        Assert.Empty(_self.Reports);
        _self.Breaks = null;
        service.Begin();
        Assert.Equal(UpdateStep.Ready, service.View.Step);
    }

    [Fact]
    public async Task In_the_background_trouble_is_silent()
    {
        Offers();
        _self.Breaks = new UpdateCheckFailedException("offline");
        var service = Service();
        await service.CheckAsync();
        Assert.Equal(UpdateStage.Hidden, service.View.Stage);
        Assert.Empty(_self.Reports);
    }

    [Fact]
    public async Task Closed_it_still_installs_as_the_app_closes()
    {
        Offers();
        var service = Service();
        await service.CheckAsync();
        service.Dismiss();
        Assert.Equal(UpdateStage.Hidden, service.View.Stage);
        await service.CheckAsync();
        Assert.Equal(UpdateStage.Hidden, service.View.Stage);
        End(service);
        Assert.Equal(1, _self.OnExit);
    }

    [Fact]
    public async Task The_persons_choice_wins_and_is_kept()
    {
        Offers();
        var service = Service();
        Assert.True(service.InstallsAutomatically);
        service.InstallsAutomatically = false;
        await service.CheckAsync();
        Assert.Equal(0, _self.Staged);
        Assert.Equal(UpdateStage.Available, service.View.Stage);
        Assert.False(Service().InstallsAutomatically);
        service.InstallsAutomatically = true;
        Assert.Equal(1, _self.Staged);
        // Off again: what was downloaded without being asked is not installed
        service.InstallsAutomatically = false;
        Assert.Equal(UpdateStep.None, service.View.Step);
        End(service);
        Assert.Equal(0, _self.OnExit);

        Offers(byDefault: false);
        var fresh = new UpdateService(_checker, new UpdateStore(null), UpdateInstall.SelfUpdating, "1.4.2", () => false, _page, self: _self);
        await fresh.CheckAsync();
        Assert.False(fresh.InstallsAutomatically);
    }

    [Fact]
    public async Task Below_the_minimum_the_screen_needs_an_update_that_verifies()
    {
        Offers(UpdatePromptKind.Forced);
        var service = Service();
        _self.Breaks = new UpdateRefusedException("bad_signature");
        await service.CheckAsync();
        Assert.Equal(UpdateStage.Required, service.View.Stage);
        Assert.Equal(new[] { "v1.5.0 bad_signature" }, _self.Reports);
        Assert.Null(service.View.Version);

        _self.Breaks = new UpdateCheckFailedException("offline");
        await service.CheckAsync();
        Assert.Equal(UpdateStage.Required, service.View.Stage);
        Assert.Single(_self.Reports);

        _self.Breaks = null;
        await service.CheckAsync();
        Assert.Equal(UpdateStage.Forced, service.View.Stage);
        Assert.Equal(0, _self.Staged);
        service.Begin();
        Assert.Equal(UpdateStage.Forced, service.View.Stage);
        Assert.Equal("Update ready. Restart to install it.", Text(service).Step(service.View));
        service.Begin();
        Assert.Equal(1, _self.Restarts);
    }

    [Fact]
    public async Task A_late_refusal_does_not_touch_what_is_offered_by_then()
    {
        Offers(byDefault: false);
        _self.Gate = new TaskCompletionSource();
        var service = Service();
        await service.CheckAsync();
        service.Begin();
        Offers(version: "1.5.1", byDefault: false);
        await service.CheckAsync();
        _self.Breaks = new UpdateRefusedException("hash");
        _self.BreaksOnly = "v1.5.0";
        _self.Version = "1.5.1";
        _self.Gate.SetResult();
        await Until(() => service.View.Step == UpdateStep.Ready);
        Assert.Single(_self.Reports);
        Assert.Equal("v1.5.0 hash", _self.Reports[0]);
        Assert.Equal("1.5.1", service.View.Version);
        Assert.Equal(UpdateStage.Available, service.View.Stage);
    }

    [Fact]
    public async Task The_notes_stay_the_manifests_when_the_server_is_asked_again()
    {
        Offers();
        var service = Service();
        await service.CheckAsync();
        Offers();
        await service.CheckAsync();
        Assert.Equal(UpdateStep.Ready, service.View.Step);
        Assert.Equal("signed notes", service.View.NotesEn);
    }

    /// <summary>The minimum was raised while the update was downloading: the screen needs a manifest that verifies all the same.</summary>
    [Fact]
    public async Task Forced_while_it_downloads_is_verified_first()
    {
        Offers();
        _self.Gate = new TaskCompletionSource();
        var service = Service();
        await service.CheckAsync();
        Offers(UpdatePromptKind.Forced);
        _self.Breaks = new UpdateRefusedException("bad_signature");
        await service.CheckAsync();
        Assert.Equal(1, _self.Verified);
        Assert.Equal(UpdateStage.Required, service.View.Stage);
        _self.Breaks = null;
        _self.Gate.SetResult();
        await Task.Delay(50);
        // What the download brought is not what is offered any more
        Assert.Equal(UpdateStage.Required, service.View.Stage);
        End(service);
        Assert.Equal(0, _self.OnExit);
    }

    /// <summary>Not at the next check, hours later.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_newer_release_during_the_download_is_staged_right_after_it(bool automatically)
    {
        Offers(byDefault: automatically);
        _self.Gate = new TaskCompletionSource();
        var service = Service();
        await service.CheckAsync();
        if (!automatically) service.Begin();
        Offers(version: "1.5.1", byDefault: automatically);
        await service.CheckAsync();
        Assert.Equal(1, _self.Staged);
        _self.Version = "1.5.1";
        _self.Gate.SetResult();
        await Until(() => service.View.Step == UpdateStep.Ready);
        Assert.Equal(2, _self.Staged);
        Assert.Equal("1.5.1", service.View.Version);
        End(service);
        Assert.Equal(1, _self.OnExit);
    }

    [Fact]
    public async Task A_newer_release_is_not_staged_when_the_choice_was_taken_back()
    {
        Offers();
        _self.Gate = new TaskCompletionSource();
        var service = Service();
        await service.CheckAsync();
        Offers(version: "1.5.1");
        await service.CheckAsync();
        service.InstallsAutomatically = false;
        _self.Gate.SetResult();
        await Task.Delay(50);
        Assert.Equal(1, _self.Staged);
    }

    private string Kept() => new UpdateStore(_file).Load().Core ?? "";

    /// <summary>The update state is saved after every check of a manifest: the app may end at any moment after it.</summary>
    [Fact]
    public async Task What_a_check_of_a_manifest_taught_is_kept_while_the_package_still_downloads()
    {
        Offers();
        _self.Gate = new TaskCompletionSource();
        _self.Checks = () => _checker.Learn("1.5.0 was seen");
        var service = Service();
        await service.CheckAsync();
        Assert.Equal(1, _self.Staged);
        Assert.Equal("1.5.0 was seen", Kept());
        _self.Breaks = new UpdateCheckFailedException("offline");
        _self.Gate.SetResult();
        await Task.Delay(50);
        Assert.Equal("1.5.0 was seen", Kept());

        // Started again: the core gets it
        Assert.Equal("1.5.0 was seen", new UpdateStore(_file).Load().Core);
    }

    [Theory]
    [InlineData("required")]
    [InlineData("exit")]
    [InlineData("restart")]
    public async Task What_a_check_taught_is_kept_before_anything_else_is_done_with_it(string how)
    {
        Offers(how == "required" ? UpdatePromptKind.Forced : UpdatePromptKind.Notice);
        var service = Service();
        if (how == "required")
        {
            _self.Checks = () => _checker.Learn("learned");
            await service.CheckAsync();
            Assert.Equal("learned", Kept());
            return;
        }

        await service.CheckAsync();
        Assert.Equal("", Kept());
        _self.Checks = () => _checker.Learn("learned");
        var atHandOver = "";
        _self.OnApply = () => atHandOver = Kept();
        if (how == "exit") End(service);
        else service.Begin();
        Assert.Equal("learned", atHandOver);
    }

    [Fact]
    public void The_state_is_replaced_in_one_step_and_leaves_nothing_beside_it()
    {
        var folder = Path.GetDirectoryName(_file)!;
        var store = new UpdateStore(_file);
        Assert.True(store.Save(new UpdateMemory { Core = "first" }));
        // A leftover of another's does not matter, and two that save at once do not meet
        File.WriteAllText(_file + ".tmp", "half");
        Parallel.For(0, 40, i => new UpdateStore(_file).Save(new UpdateMemory { Core = "c" + i }));
        Assert.StartsWith("c", store.Load().Core);
        Assert.Equal(2, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public async Task The_screen_still_waits_for_work()
    {
        Offers(UpdatePromptKind.Forced);
        _busy = true;
        var service = Service();
        await service.CheckAsync();
        Assert.Equal(UpdateStage.Waiting, service.View.Stage);
        _busy = false;
        TimePasses();
        Assert.Equal(UpdateStage.Forced, service.View.Stage);
    }

    [Theory]
    [InlineData(UpdateInstall.Installer, true)]
    [InlineData(UpdateInstall.Msix, true)]
    [InlineData(UpdateInstall.Portable, true)]
    [InlineData(UpdateInstall.Deb, true)]
    [InlineData(UpdateInstall.SelfUpdating, false)]
    public async Task Every_other_copy_and_every_other_answer_is_the_notice_with_the_fixed_page(UpdateInstall install, bool offered)
    {
        Offers(install: offered);
        var service = Service(install);
        await service.CheckAsync();
        Assert.Equal(install == UpdateInstall.SelfUpdating, service.CanInstall);
        Assert.Equal(UpdateStage.Available, service.View.Stage);
        Assert.False(service.View.Installs);
        Assert.Equal("Download", Text(service).Button(service.View));
        service.Begin();
        End(service);
        Assert.Equal(1, _page.Opened);
        Assert.Equal(0, _self.Staged + _self.Verified + _self.OnExit + _self.Restarts);
    }

    /// <summary>The app's end, as the windows' owner of it does it (AppEnd).</summary>
    private static bool End(UpdateService service)
    {
        service.BeginEnd();
        return service.HandOver();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_updater_that_does_not_start_is_no_hand_over(bool restart)
    {
        Offers();
        Exception? thrown = null;
        var service = Service(close: s => thrown = Record.Exception(() => End(s)));
        await service.CheckAsync();
        _self.ApplyBreaks = new InvalidOperationException("no updater");
        if (restart) service.Begin();
        else thrown = Record.Exception(() => End(service));
        // AppEnd is told, and starts the app again when that was asked for
        Assert.NotNull(thrown);
        Assert.Equal(restart, service.IsRestartAsked);
        Assert.Equal(0, _self.OnExit + _self.Restarts);
    }

    [Fact]
    public async Task Whether_work_runs_is_decided_before_it_stops()
    {
        Offers();
        var service = Service();
        await service.CheckAsync();
        _busy = true;
        service.BeginEnd();
        // Stopped, it no longer says that it was running
        _busy = false;
        Assert.False(service.HandOver());
        Assert.Equal(0, _self.OnExit + _self.Restarts);
    }

    /// <summary>The updater ends every process of the folder: one started while the work stopped, too.</summary>
    [Theory]
    [InlineData("while the work stopped")]
    [InlineData("during the last check")]
    public async Task A_second_copy_that_starts_meanwhile_is_not_ended(string when)
    {
        Offers();
        var service = Service();
        await service.CheckAsync();
        service.BeginEnd();
        if (when == "while the work stopped") _self.IsOpenElsewhere = true;
        else _self.Checks = () => _self.IsOpenElsewhere = true;
        Assert.False(service.HandOver());
        Assert.Equal(0, _self.OnExit + _self.Restarts);
    }

    [Fact]
    public async Task What_the_last_check_refuses_is_reported_and_not_handed_over()
    {
        Offers();
        var service = Service();
        await service.CheckAsync();
        service.BeginEnd();
        _self.StagedBreaks = new UpdateRefusedException("hash");
        Assert.False(service.HandOver());
        Assert.Equal(new[] { "v1.5.0 hash" }, _self.Reports);
        Assert.Equal(0, _self.OnExit + _self.Restarts);
    }

    [Fact]
    public async Task An_answer_that_comes_during_the_end_takes_nothing_away_under_it()
    {
        Offers();
        var service = Service();
        await service.CheckAsync();
        var discarded = _self.Discarded;
        service.BeginEnd();
        _checker.Next = new UpdateAnswer { Enabled = true };
        await service.CheckAsync();
        Assert.Equal(discarded, _self.Discarded);
        // Nor is the update written into the answer that has none
        Assert.Null(service.Found.Version);
        Assert.Null(UpdateAnswer.Off.Version);
        Assert.True(service.HandOver());
        Assert.False(service.HandOver());
        Assert.Equal(1, _self.OnExit);
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("busy")]
    [InlineData("busy after the check")]
    [InlineData("open elsewhere after the check")]
    public async Task Restart_now_closes_nothing_when_it_cannot_install(string why)
    {
        Offers();
        var service = Service();
        await service.CheckAsync();
        if (why == "refused") _self.StagedBreaks = new UpdateRefusedException("hash");
        else if (why == "busy") _busy = true;
        else _self.Checks = () =>
        {
            _busy = why.StartsWith("busy", StringComparison.Ordinal);
            _self.IsOpenElsewhere = !_busy;
        };
        service.Begin();
        Assert.Equal(0, _closes);
        Assert.False(service.IsRestartAsked);
    }

    private static async Task Until(Func<bool> met)
    {
        for (var i = 0; i < 500 && !met(); i++) await Task.Delay(10);
        Assert.True(met());
    }
}
