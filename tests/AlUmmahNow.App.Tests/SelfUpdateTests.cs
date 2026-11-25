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
        public string State() => "";
    }

    private sealed class Self : ISelfUpdate
    {
        public TaskCompletionSource? Gate;
        public Exception? Breaks, StagedBreaks, ApplyBreaks;
        public int Verified, Staged, Discarded, OnExit, Restarts;
        public string NotesEn = "signed notes", Version = "1.5.0";
        public List<string> Reports = new();

        public Task<VerifiedUpdate> VerifyAsync(string tag, string version, CancellationToken cancellationToken = default)
        {
            Verified++;
            return Breaks == null ? Task.FromResult(new VerifiedUpdate(Version, tag, NotesEn, "ملاحظات")) : Task.FromException<VerifiedUpdate>(Breaks);
        }

        public async Task<VerifiedUpdate> StageAsync(string tag, string version, IProgress<double>? progress, CancellationToken cancellationToken = default)
        {
            Staged++;
            progress?.Report(0.42);
            if (Gate != null) await Gate.Task;
            if (Breaks != null) throw Breaks;
            return new VerifiedUpdate(Version, tag, NotesEn, "ملاحظات");
        }

        public void VerifyStaged()
        {
            if (StagedBreaks != null) throw StagedBreaks;
        }

        public void Discard() => Discarded++;
        public bool IsOpenElsewhere { get; set; }
        public void ApplyOnExit() => OnExit++;

        public void ApplyAndRestart()
        {
            if (ApplyBreaks != null) throw ApplyBreaks;
            Restarts++;
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

    private UpdateService Service(UpdateInstall install = UpdateInstall.SelfUpdating) =>
        new(_checker, new UpdateStore(_file), install, "1.4.2", () => _busy, _page, () => new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero), (_, _) =>
        {
            var wait = new TaskCompletionSource();
            _waits.Add(wait);
            return wait.Task;
        }, self: _self);

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
        service.OnExit();
        Assert.Equal(1, _self.OnExit);
        service.OnExit();
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
        service.OnExit();
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
        service.OnExit();
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
        service.OnExit();
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
        service.OnExit();
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

        foreach (var act in new Action<UpdateService>[] { s => s.Begin(), s => s.OnExit() })
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
        service.OnExit();
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
        service.OnExit();
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
        service.OnExit();
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
        _self.Gate.SetResult();
        await Until(() => _self.Reports.Count == 1);
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
        service.OnExit();
        Assert.Equal(0, _self.OnExit);
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
        service.OnExit();
        Assert.Equal(1, _page.Opened);
        Assert.Equal(0, _self.Staged + _self.Verified + _self.OnExit + _self.Restarts);
    }

    [Fact]
    public async Task An_updater_that_does_not_start_leaves_the_app_as_it_is()
    {
        Offers();
        var service = Service();
        await service.CheckAsync();
        _self.ApplyBreaks = new InvalidOperationException("no updater");
        service.Begin();
        Assert.Equal(UpdateStep.Failed, service.View.Step);
        _self.ApplyBreaks = null;
        service.Begin();
        service.Begin();
        Assert.Equal(1, _self.Restarts);
    }

    private static async Task Until(Func<bool> met)
    {
        for (var i = 0; i < 500 && !met(); i++) await Task.Delay(10);
        Assert.True(met());
    }
}
