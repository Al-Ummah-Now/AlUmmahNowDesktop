using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Analytics;
using AlUmmahNow.Studio.Demo;
using AlUmmahNow.Studio.Shell;
using AlUmmahNow.Studio.Upload;
using AlUmmahNow.Studio.Videos;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

/// <summary>
/// Owner review 2026-11-16: no hollow pages, no endless spinners, the display-name rule. Every load ends in content,
/// an empty state, an error or a hidden section, whatever the server answers.
/// </summary>
public sealed class FollowUpTests
{
    private const string FeaturesRoute = "GET " + FeaturesClient.Route;
    private const string ProfileRoute = "GET " + ProfileClient.Route;
    private readonly FakeAccounts _accounts = new();
    private readonly FakeTransport _t = new();

    public FollowUpTests()
    {
        _t.Responses["get_ChannelAccessList"] = _ => FakeTransport.Json(new { data = new[] { new { id = "c1", userId = "u1", name = "C", isMain = true, role = "owner", capabilities = Array.Empty<string>() } } });
        _accounts.Saved.Add(new StudioAccount("u1", "amina@example.com", null, null, null, false));
        _accounts.ActiveId = "u1";
    }

    private StudioShellViewModel Shell(IStudioApi api, Dictionary<string, string>? saved = null) =>
        new(_accounts, api, new FeaturesClient(_t), profiles: new ProfileClient(_t), language: "ar", knownNames: saved, saveNames: n =>
        {
            if (saved == null) return;
            saved.Clear();
            foreach (var kv in n) saved[kv.Key] = kv.Value;
        });

    /// <summary>The outcomes a load can end in, other than content.</summary>
    public static TheoryData<string> Outcomes => new() { "feature_off", "payment", "not_found", "server", "offline", "rate_limited", "cancelled" };

    private static Exception Failure(string outcome) => outcome switch
    {
        "feature_off" => StudioApiException.FromServer("off", 404, "feature_off"),
        "payment" => StudioApiException.FromServer("x", 402, "upgrade_required"),
        "not_found" => StudioApiException.FromServer("x", 404, null),
        "server" => StudioApiException.FromServer("x", 500, null),
        "offline" => StudioApiException.Network("offline", new HttpRequestException()),
        "rate_limited" => new RateLimitedException(TimeSpan.FromSeconds(1)),
        _ => new OperationCanceledException(),
    };

    // No endless spinners

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task AnalyticsLoadingEnds(string outcome)
    {
        _t.Responses["get_AnalyticsChannelGet"] = _ => throw Failure(outcome);
        var api = new StudioApiClient(_t);
        var shell = Shell(api);
        var vm = new AnalyticsViewModel(shell, api);
        await shell.InitializeAsync();
        await vm.LoadAsync();
        Assert.False(vm.IsLoading);
        // Never a blank page: hidden, or a message (cancellation leaves the last state; nothing was shown yet).
        if (outcome is "feature_off" or "payment") Assert.True(vm.IsHidden);
        else if (outcome != "cancelled") Assert.Equal("studio.analytics.failed", vm.ErrorKey);
    }

    [Fact]
    public async Task AnalyticsWithoutAChannelStopsAnInFlightLoad()
    {
        var gate = new TaskCompletionSource<string>();
        var api = new StudioApiClient(new GatedTransport(_t, "get_AnalyticsChannelGet", gate.Task));
        var shell = Shell(api);
        var vm = new AnalyticsViewModel(shell, api);
        await shell.InitializeAsync();
        var pending = vm.LoadAsync();
        Assert.True(vm.IsLoading);
        await shell.SignOutAsync("u1");
        _accounts.Saved.Clear();
        _accounts.ActiveId = null;
        await shell.RetryAsync();
        await vm.LoadAsync();
        Assert.False(vm.IsLoading);
        gate.SetResult(FakeTransport.Json(new { recentMetrics = Array.Empty<object>() }));
        await pending;
        // The old channel's answer doesn't land on the signed-out page.
        Assert.Null(vm.Data);
        Assert.False(vm.IsLoading);
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task VideosLoadingEnds(string outcome)
    {
        _t.Responses["get_VideosList"] = _ => throw Failure(outcome);
        var api = new StudioApiClient(_t);
        var shell = Shell(api);
        var vm = new VideosViewModel(shell, api, () => Array.Empty<string>(), _ => { });
        await shell.InitializeAsync();
        await vm.LoadAsync();
        Assert.False(vm.IsLoading);
        // An error shows its message instead of "No videos here yet"; feature_off, 402 and cancellation show the plain empty state.
        if (outcome is "not_found" or "server" or "offline" or "rate_limited")
        {
            Assert.NotNull(vm.ErrorKey);
            Assert.False(vm.ShowEmpty);
        }
        else
        {
            Assert.Null(vm.ErrorKey);
            Assert.True(vm.ShowEmpty);
        }
    }

    [Theory]
    [InlineData("rate_limited")]
    [InlineData("cancelled")]
    [InlineData("server")]
    public async Task ShellNeverStaysOnLoading(string outcome)
    {
        _t.Responses["get_ChannelAccessList"] = _ => throw Failure(outcome);
        var shell = Shell(new StudioApiClient(_t));
        await shell.InitializeAsync();
        Assert.Equal(ShellState.Error, shell.State);
        Assert.NotNull(shell.ErrorKey);
    }

    // No hollow pages

    [Fact]
    public async Task AChannelWithNoVideosShowsTheEmptyStateAfterLoading()
    {
        _t.Responses["get_VideosList"] = _ => FakeTransport.Json(new { data = Array.Empty<object>() });
        var api = new StudioApiClient(_t);
        var shell = Shell(api);
        var vm = new VideosViewModel(shell, api, () => Array.Empty<string>(), _ => { });
        var raised = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VideosViewModel.ShowEmpty)) raised.Add(vm.ShowEmpty);
        };
        await shell.InitializeAsync();
        await vm.LoadAsync();
        Assert.True(vm.ShowEmpty);
        // The page is told after the spinner stops, not only when the list changes (the empty label used to stay hidden).
        Assert.True(raised.Last());
    }

    [Fact]
    public async Task WithUploadAndAnalyticsOffTheLandingPageHasContent()
    {
        var demo = new DemoTransport(DemoTransport.FeaturesOff("upload,analytics"));
        var api = new StudioApiClient(demo);
        var shell = new StudioShellViewModel(new DemoAccounts("en"), api, new FeaturesClient(demo));
        var analytics = new AnalyticsViewModel(shell, api);
        var pages = new StudioPages(shell, analytics, StudioPages.Uploads);
        var videos = new VideosViewModel(shell, api, () => Array.Empty<string>(), _ => { });
        await shell.InitializeAsync();
        await analytics.LoadAsync();
        await videos.LoadAsync();
        Assert.Equal(StudioPages.Videos, pages.Current);
        Assert.Equal(new[] { StudioPages.Videos, StudioPages.Encode, StudioPages.Timeline, StudioPages.Settings }, StudioPages.All.Where(pages.IsVisible));
        Assert.NotEmpty(videos.Items);
        Assert.False(videos.IsLoading);
        Assert.False(analytics.IsLoading);
    }

    // Bulk scheduling (bulkSchedule) on the upload form

    [Fact]
    public void PlannerStepsOnlyWithBulkScheduling()
    {
        var now = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
        var d = new UploadDefaults { Visibility = UploadVisibility.Scheduled, ScheduleStart = now.AddDays(1), ScheduleInterval = TimeSpan.FromHours(6) };
        var files = new[] { "/a.mp4", "/b.mp4", "/c.mp4" };
        Assert.Equal(now.AddDays(1).AddHours(12), UploadPlanner.Plan(files, d, now, stepSchedule: true)[2].PublishAt);
        Assert.All(UploadPlanner.Plan(files, d, now, stepSchedule: false), p => Assert.Equal(now.AddDays(1), p.PublishAt));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UploadFormFollowsBulkSchedule(bool on)
    {
        _t.Responses[FeaturesRoute] = _ => FakeTransport.Json(new { version = 1, features = new { upload = true, bulkSchedule = on } });
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var api = new StudioApiClient(_t);
            var shell = Shell(api);
            var vm = new UploadsViewModel(shell, api, a => new SimulatedUploadService(a, TimeSpan.FromMilliseconds(10)), dir, () => new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));
            vm.Defaults.Visibility = UploadVisibility.Scheduled;
            vm.Defaults.ScheduleStart = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
            vm.Defaults.ScheduleInterval = TimeSpan.FromHours(6);
            vm.AddFiles(new[] { "/x/a.mp4", "/x/b.mp4" });
            Assert.False(vm.BulkScheduleOn);
            await shell.InitializeAsync();
            Assert.Equal(on, vm.BulkScheduleOn);
            Assert.Equal(on ? TimeSpan.FromHours(6) : TimeSpan.Zero, vm.Staged[1].PublishAt - vm.Staged[0].PublishAt);
            vm.Dispose();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Display name

    [Theory]
    [InlineData("Amina Studio", "Typed", "Amina", "Khan", "Amina Studio")]
    [InlineData("", "Typed", "Amina", "Khan", "Typed")]
    [InlineData(null, "  ", "Amina", "Khan", "Amina Khan")]
    [InlineData(null, null, "Amina", null, "Amina")]
    [InlineData(null, null, null, "Khan", "Khan")]
    [InlineData(null, null, null, null, null)]
    [InlineData("@amina", "@amina", null, null, null)]
    public void NameRule(string? resolved, string? display, string? first, string? last, string? expected) =>
        Assert.Equal(expected, AccountName.Resolve(resolved, display, first, last));

    [Fact]
    public void ProfileJsonNeverUsesTheHandle()
    {
        Assert.Equal("أمينة", AccountName.FromProfileJson("""{"success":true,"result":{"profile":{"userName":"amina","resolvedDisplayName":"أمينة","firstName":"Amina"}}}"""));
        Assert.Null(AccountName.FromProfileJson("""{"success":true,"result":{"profile":{"userName":"amina","resolvedDisplayName":""}}}"""));
        Assert.Equal("Amina Khan", AccountName.FromProfileJson("""{"result":{"profile":{"userName":"amina","firstName":"Amina","lastName":"Khan"}}}"""));
        Assert.Null(AccountName.FromProfileJson("not json"));
        // The label's last fallbacks: email, then the id; never a handle.
        Assert.Equal("amina@example.com", AccountName.Label(null, "amina@example.com", "u1"));
        Assert.Equal("amina@example.com", AccountName.Label("@amina", "amina@example.com", "u1"));
        Assert.Equal("u1", AccountName.Label(null, null, "u1"));
    }

    [Fact]
    public async Task ShellShowsTheProfileNameThenEmail()
    {
        _t.Responses[ProfileRoute] = _ => FakeTransport.Json(new { success = true, result = new { profile = new { userName = "amina", resolvedDisplayName = "أمينة خان" } } });
        var saved = new Dictionary<string, string>();
        var shell = Shell(new StudioApiClient(_t), saved);
        // Before the profile answers: the email, not the handle.
        Assert.Equal("amina@example.com", shell.NameOf(_accounts.Saved[0]));
        await shell.InitializeAsync();
        Assert.Equal("أمينة خان", shell.ActiveAccountName);
        Assert.Equal("ar", _t.Calls(ProfileRoute).Last().Query["lang"]);
        Assert.Equal("أمينة خان", saved["u1"]);

        // Next launch: the saved name shows straight away, even if the profile can't be read.
        _t.Responses[ProfileRoute] = _ => throw StudioApiException.Network("offline", new HttpRequestException());
        var again = Shell(new StudioApiClient(_t), saved);
        Assert.Equal("أمينة خان", again.NameOf(_accounts.Saved[0]));
        await again.InitializeAsync();
        Assert.Equal("أمينة خان", again.ActiveAccountName);

        // A profile with no name at all: the email.
        _t.Responses[ProfileRoute] = _ => FakeTransport.Json(new { success = true, result = new { profile = new { userName = "amina", resolvedDisplayName = "" } } });
        await again.RetryAsync();
        Assert.Equal("amina@example.com", again.ActiveAccountName);

        // Signing out forgets the name.
        _t.Responses[ProfileRoute] = _ => FakeTransport.Json(new { result = new { profile = new { resolvedDisplayName = "أمينة خان" } } });
        await again.RetryAsync();
        await again.SignOutAsync("u1");
        Assert.False(saved.ContainsKey("u1"));
    }

    /// <summary>Holds one operation until <paramref name="answer"/> completes; the rest go to the fake.</summary>
    private sealed class GatedTransport : IApiTransport
    {
        private readonly FakeTransport _inner;
        private readonly string _operation;
        private readonly Task<string> _answer;

        public GatedTransport(FakeTransport inner, string operation, Task<string> answer)
        {
            _inner = inner;
            _operation = operation;
            _answer = answer;
        }

        public Task<string> SendAsync(ApiRequest request, CancellationToken cancellationToken = default) =>
            request.OperationId == _operation ? _answer : _inner.SendAsync(request, cancellationToken);

        public Task<IReadOnlyList<Suggestion>> SuggestAsync(string text, string? channelId, int limit, CancellationToken cancellationToken = default) =>
            _inner.SuggestAsync(text, channelId, limit, cancellationToken);
    }
}
