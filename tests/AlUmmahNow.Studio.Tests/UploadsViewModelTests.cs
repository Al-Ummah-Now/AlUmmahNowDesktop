using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Shell;
using AlUmmahNow.Studio.Upload;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

public sealed class UploadsViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;
    private readonly FakeAccounts _accounts = new();
    private readonly FakeTransport _t = new();
    private readonly List<FakeUploads> _services = new();

    public UploadsViewModelTests()
    {
        _t.Responses["get_ChannelAccessList"] = _ => FakeTransport.Json(new { data = new[] { new { id = "c1", userId = "u1", name = "C", isMain = true, role = "owner", capabilities = Array.Empty<string>() } } });
        _accounts.Saved.Add(new StudioAccount("u1", null, "A", null, null, false));
        _accounts.Saved.Add(new StudioAccount("u2", null, "B", null, null, false));
        _accounts.ActiveId = "u1";
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class FakeUploads : IUploadService
    {
        public readonly List<UploadSpec> Specs = new();
        public readonly Dictionary<string, UploadSnapshot> Items = new();
        public bool Disposed;

        public FakeUploads(string account) => AccountId = account;

        public event EventHandler<string>? Changed;
        public string AccountId { get; }
        public IReadOnlyList<UploadSnapshot> List() => Items.Values.ToList();

        public string Enqueue(UploadSpec spec)
        {
            Specs.Add(spec);
            var id = "up" + Specs.Count;
            Items[id] = new UploadSnapshot(id, spec.FilePath, spec.Title, UploadPhase.Queued, 0, 100, null, null);
            return id;
        }

        public void Finish(string id, string videoId)
        {
            var i = Items[id];
            Items[id] = new UploadSnapshot(id, i.FilePath, i.Title, UploadPhase.Done, 100, 100, videoId, null);
            Changed?.Invoke(this, id);
        }

        public void Pause(string id) { }
        public void Resume(string id) { }
        public void Retry(string id) { }
        public Task CancelAsync(string id) => Task.CompletedTask;
        public void Remove(string id) => Items.Remove(id);
        public Task? Stops;
        public int Stopped;

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            Stopped++;
            if (Stops != null) await Stops.WaitAsync(cancellationToken);
        }

        public void Dispose() => Disposed = true;
    }

    private async Task<(StudioShellViewModel Shell, UploadsViewModel Vm)> CreateAsync()
    {
        var api = new StudioApiClient(_t);
        var shell = new StudioShellViewModel(_accounts, api);
        await shell.InitializeAsync();
        var vm = new UploadsViewModel(shell, api, a =>
        {
            var s = new FakeUploads(a);
            _services.Add(s);
            return s;
        }, _dir, () => new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        return (shell, vm);
    }

    private string Video(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "x");
        return p;
    }

    [Theory]
    [InlineData("en", "English")]
    [InlineData("ar", "العربية")]
    public async Task PlaylistCreatedFromBothNamesIsAvailableToThePicker(string language, string expected)
    {
        _t.Responses["post_PlaylistCreate"] = _ => """
            {"result":{"playlist":{"id":"p1","title":"English","titleLocalized":{"en":"English","ar":"العربية"}}}}
            """;
        var (_, vm) = await CreateAsync();
        Assert.Null(await vm.CreatePlaylistAsync(" ", "", isPublic: true));
        Assert.Empty(_t.Calls("post_PlaylistCreate"));
        var created = await vm.CreatePlaylistAsync(" English ", " العربية ", isPublic: true);
        var playlist = Assert.Single(vm.Playlists);
        Assert.Same(created, playlist);
        Assert.Equal(expected, playlist.TitleFor(language));
    }

    [Fact]
    public async Task StagesAppliesDefaultsAndQueuesOnTheActiveChannel()
    {
        var (_, vm) = await CreateAsync();
        vm.AddFiles(new[] { Video("b.mp4"), Video("a.mp4"), Path.Combine(_dir, "notes.txt") });
        Assert.Equal(2, vm.Staged.Count);
        vm.Defaults.TitlePattern = "{n}. {filename}";
        vm.Defaults.Visibility = UploadVisibility.Public;
        vm.Defaults.Tags = UploadPlanner.ParseTags("fiqh");
        vm.ApplyDefaults();
        Assert.Equal("1. b", vm.Staged[0].Title);
        vm.RenameStaged(vm.Staged[1].FilePath, "  Custom  ");
        Assert.True(vm.CanStart);
        Assert.Equal(2, vm.Start());
        var s = _services.Single();
        Assert.Equal(new[] { "1. b", "Custom" }, s.Specs.Select(x => x.Title));
        Assert.All(s.Specs, x => Assert.Equal("c1", x.ChannelId));
        Assert.All(s.Specs, x => Assert.True(x.IsPublic));
        Assert.Empty(vm.Staged);
        Assert.Equal(2, vm.Items.Count);
        Assert.Null(vm.FinishStepFor("up1"));
    }

    private async Task<(UploadsViewModel Vm, FakeUploads Service)> WithUploadAsync()
    {
        var (_, vm) = await CreateAsync();
        vm.Defaults.PlaylistIds = new List<string> { "p1" };
        vm.AddFiles(new[] { Video("a.mp4") });
        vm.Start();
        return (vm, _services.Single());
    }

    private string SavedSteps => File.ReadAllText(Directory.GetFiles(_dir, "finish-*.json").Single());

    /// <summary>What an update must not interrupt: as the queue says now, not as it was at the last tick.</summary>
    [Fact]
    public async Task WorkingIsAsTheQueueSaysNow()
    {
        var (vm, service) = await WithUploadAsync();
        Assert.True(vm.IsWorking);
        // Done, and nothing has ticked since: the list itself says so
        service.Finish("up1", "v1");
        Assert.Equal(1, vm.ActiveCount);
        Assert.False(vm.IsWorking);
    }

    [Fact]
    public async Task DetailsBeingSentAreWork()
    {
        var (vm, service) = await WithUploadAsync();
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _t.Waits = answer.Task;
        service.Finish("up1", "v1");
        vm.Tick();
        Assert.True(vm.IsWorking);
        answer.SetResult();
        await vm.WhenDetailsSentAsync();
        Assert.False(vm.IsWorking);
    }

    [Fact]
    public async Task StoppingWaitsForDetailsSavesWhatIsLeftAndClosesTheQueue()
    {
        var (vm, service) = await WithUploadAsync();
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _t.Waits = answer.Task;
        service.Finish("up1", "v1");
        vm.Tick();
        var stopping = vm.StopAsync();
        Assert.False(stopping.IsCompleted);
        Assert.Equal(0, service.Stopped);
        answer.SetResult();
        await stopping;
        // Sent, so not again at the next start
        Assert.DoesNotContain("up1", SavedSteps);
        Assert.Equal(1, service.Stopped);
        Assert.False(vm.IsWorking);
        // Nothing begins after it
        vm.AddFiles(new[] { Video("b.mp4") });
        Assert.Equal(0, vm.Start());
        vm.Tick();
        Assert.Single(service.Specs);
        // Closing it again is nothing
        vm.Dispose();
        Assert.False(service.Disposed);
    }

    /// <summary>The app's end, with its message loop blocked by the end itself or gone.</summary>
    [Fact]
    public async Task StoppingNeedsNoMessageLoop()
    {
        var (vm, service) = await WithUploadAsync();
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _t.Waits = answer.Task;
        service.Finish("up1", "v1");
        var loop = DeadLoop.Around(vm.Tick);
        answer.SetResult();
        await Task.Run(() => vm.StopAsync());
        Assert.Equal(0, loop.Posted);
        Assert.DoesNotContain("up1", SavedSteps);
        Assert.Equal(1, service.Stopped);
    }

    /// <summary>What was still on its way when the app had to end does not write into the next start's file.</summary>
    [Fact]
    public async Task DetailsThatArriveAfterTheEndChangeNothingOnDisk()
    {
        var (vm, service) = await WithUploadAsync();
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _t.Waits = answer.Task;
        service.Finish("up1", "v1");
        vm.Tick();
        await Assert.ThrowsAsync<TimeoutException>(() => vm.StopAsync(detailsLimit: TimeSpan.FromMilliseconds(50)));
        var saved = SavedSteps;
        answer.SetResult();
        await vm.WhenDetailsSentAsync();
        Assert.Equal(saved, SavedSteps);
        Assert.Contains("up1", saved);
    }

    [Fact]
    public async Task StepsAreSavedFromManyThreadsAtOnce()
    {
        var path = Path.Combine(_dir, "finish-many.json");
        var steps = new FinishSteps(path);
        await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 10; n++)
            {
                steps.Add($"{i}-{n}", new FinishStep { ChannelId = "c1" });
                steps.Save();
                if (n % 2 == 0) steps.Remove($"{i}-{n}");
            }
        })));
        steps.Close();
        var again = new FinishSteps(path);
        Assert.NotNull(again.Get("3-1"));
        Assert.Null(again.Get("3-2"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task DetailsThatAreNotSentInTimeAreKeptAndTheQueueIsClosedAllTheSame()
    {
        var (vm, service) = await WithUploadAsync();
        _t.Waits = new TaskCompletionSource().Task;
        service.Finish("up1", "v1");
        vm.Tick();
        await Assert.ThrowsAsync<TimeoutException>(() => vm.StopAsync(detailsLimit: TimeSpan.FromMilliseconds(50)));
        Assert.Contains("up1", SavedSteps);
        Assert.Equal(1, service.Stopped);
    }

    [Fact]
    public async Task AQueueThatDoesNotLetGoFailsTheStop()
    {
        var (vm, service) = await WithUploadAsync();
        service.Stops = new TaskCompletionSource().Task;
        using var late = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.StopAsync(late.Token));
    }

    [Fact]
    public async Task NothingStartsAtATimeThatIsTooSoon()
    {
        var (_, vm) = await CreateAsync();
        vm.Defaults.Visibility = UploadVisibility.Scheduled;
        vm.Defaults.ScheduleStart = vm.Now.AddMinutes(14);
        vm.AddFiles(new[] { Video("a.mp4") });
        Assert.True(vm.ScheduleIsTooSoon);
        Assert.False(vm.CanStart);
        var said = 0;
        vm.PropertyChanged += (_, e) => said += e.PropertyName == nameof(UploadsViewModel.CanStart) ? 1 : 0;
        Assert.Equal(0, vm.Start());
        Assert.Equal(1, said);
        Assert.Single(vm.Staged);
        // What a watched folder finds does not wait for someone to choose
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "watched")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "b.mp4"), new byte[10]);
        vm.Watch(folder);
        vm.Tick();
        vm.Tick();
        Assert.Equal("b", Assert.Single(_services[0].Specs).Title);
        Assert.Equal(vm.Now.AddMinutes(15), vm.FinishStepFor("up1")?.PublishAt);
        vm.Defaults.ScheduleStart = vm.Now.AddMinutes(15);
        vm.ApplyDefaults();
        Assert.True(vm.CanStart);
        // Not scheduled: its time is not asked for
        vm.Defaults.ScheduleStart = vm.Now.AddMinutes(-1);
        vm.Defaults.Visibility = UploadVisibility.Private;
        Assert.True(vm.CanStart);
    }

    [Fact]
    public async Task FinishStepsRunOnceTheVideoExistsAndSurviveARestart()
    {
        var (_, vm) = await CreateAsync();
        vm.Defaults.Visibility = UploadVisibility.Scheduled;
        vm.Defaults.ScheduleStart = new DateTimeOffset(2026, 11, 12, 6, 0, 0, TimeSpan.Zero);
        vm.Defaults.Language = "ar";
        vm.Defaults.PlaylistIds = new List<string> { "p1" };
        vm.AddFiles(new[] { Video("a.mp4") });
        vm.Start();
        Assert.False(_services[0].Specs[0].IsPublic);
        Assert.NotNull(vm.FinishStepFor("up1"));

        // Restart: a new view-model reads the saved step.
        vm.Dispose();
        var (_, again) = await CreateAsync();
        var service = _services[1];
        service.Items["up1"] = new UploadSnapshot("up1", "a.mp4", "a", UploadPhase.Uploading, 50, 100, "vid1", null);
        Assert.NotNull(again.FinishStepFor("up1"));
        service.Finish("up1", "vid1");
        again.Tick();
        await again.WhenDetailsSentAsync();
        Assert.Null(again.FinishStepFor("up1"));
        Assert.Equal("{\"language\":\"ar\"}", Assert.Single(_t.Calls("put_VideoUpdate")).BodyJson);
        Assert.Equal("p1", Assert.Single(_t.Calls("post_PlaylistVideosBulkAdd")).PathParams["id"]);
        Assert.Contains("2026-11-12T06:00:00Z", Assert.Single(_t.Calls("post_VideoScheduleCreate")).BodyJson);
    }

    [Fact]
    public async Task AFailedFinishIsKeptForRetry()
    {
        _t.Responses["post_VideoScheduleCreate"] = _ => throw new StudioApiException("busy", 503);
        var (_, vm) = await CreateAsync();
        vm.Defaults.Visibility = UploadVisibility.Scheduled;
        vm.AddFiles(new[] { Video("a.mp4") });
        vm.Start();
        _services[0].Finish("up1", "vid1");
        vm.Tick();
        await vm.WhenDetailsSentAsync();
        var step = vm.FinishStepFor("up1");
        Assert.NotNull(step);
        Assert.Equal(1, step!.Attempts);
    }

    [Fact]
    public async Task SwitchingAccountsOpensThatAccountsQueue()
    {
        var (shell, vm) = await CreateAsync();
        Assert.Equal("u1", _services[0].AccountId);
        await shell.SwitchAccountAsync("u2");
        Assert.True(_services[0].Disposed);
        Assert.Equal("u2", _services[1].AccountId);
        await shell.SignOutAsync("u2");
        await shell.SignOutAsync("u1");
        Assert.False(vm.CanStart);
    }

    [Fact]
    public async Task FolderAddAndWatch()
    {
        var (_, vm) = await CreateAsync();
        var folder = Path.Combine(_dir, "batch");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "2.mp4"), "x");
        File.WriteAllText(Path.Combine(folder, "10.mp4"), "x");
        vm.AddFolder(folder, recursive: false);
        Assert.Equal(new[] { "2", "10" }, vm.Staged.Select(s => s.Title));
        vm.ClearStaged();

        vm.Watch(folder);
        vm.Tick(); // first scan records sizes
        vm.Tick(); // unchanged sizes: queued
        Assert.Equal(2, _services[0].Specs.Count);
        Assert.Empty(vm.Staged);
    }
}
