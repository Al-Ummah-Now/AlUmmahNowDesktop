using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Analytics;
using AlUmmahNow.Studio.Demo;
using AlUmmahNow.Studio.Shell;
using AlUmmahNow.Studio.Videos;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

/// <summary>GET /api/me/features and "hidden when off" (owner rule 2026-11-16): parsing, error mapping, shell refresh, page visibility.</summary>
public sealed class FeaturesTests
{
    private const string Route = "GET " + FeaturesClient.Route;
    private readonly FakeAccounts _accounts = new();
    private readonly FakeTransport _t = new();
    private DateTimeOffset _now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    public FeaturesTests()
    {
        _t.Responses["get_ChannelAccessList"] = _ => FakeTransport.Json(new
        {
            data = new[]
            {
                new { id = "c1", userId = "u1", name = "One", isMain = true, role = "owner", capabilities = Array.Empty<string>() },
                new { id = "c2", userId = "u9", name = "Two", isMain = false, role = "editor", capabilities = Array.Empty<string>() },
            },
        });
        _t.Responses["get_AnalyticsChannelGet"] = _ => FakeTransport.Json(new { recentMetrics = Array.Empty<object>() });
        _accounts.Saved.Add(new StudioAccount("u1", null, "A", null, null, false));
        _accounts.ActiveId = "u1";
    }

    private static string Features(object features) => FakeTransport.Json(new { version = 1, features });

    private StudioShellViewModel Shell(IStudioApi api) => new(_accounts, api, new FeaturesClient(_t), () => _now);

    private static StudioApiException FeatureOff() => StudioApiException.FromServer("off", 404, "feature_off");

    // Parsing

    [Fact]
    public void ParseReadsOnlyLiteralTrue()
    {
        var f = UserFeatures.Parse("""{"version":1,"features":{"upload":true,"live":false,"captionsTranscribe":"true","audioTracks":1,"futureThing":true}}""");
        Assert.True(f.IsOn(UserFeatures.Upload));
        Assert.False(f.IsOn(UserFeatures.Live));
        // Not a JSON true: off.
        Assert.False(f.IsOn(UserFeatures.CaptionsTranscribe));
        Assert.False(f.IsOn(UserFeatures.AudioTracks));
        // Missing: off.
        Assert.False(f.IsOn(UserFeatures.BulkSchedule));
        // An unknown key is harmless.
        Assert.True(f.IsOn("futureThing"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""{"version":1}""")]
    [InlineData("""{"features":[true]}""")]
    public void UnreadableAnswersAreAllOff(string json)
    {
        var f = UserFeatures.Parse(json);
        Assert.Empty(f.On);
        Assert.False(f.IsOn(UserFeatures.Upload));
    }

    [Fact]
    public void ParseAcceptsADataEnvelope()
    {
        Assert.True(UserFeatures.Parse("""{"success":true,"data":{"version":1,"features":{"upload":true}}}""").IsOn(UserFeatures.Upload));
    }

    [Fact]
    public async Task ClientSendsTheChannel()
    {
        _t.Responses[Route] = _ => Features(new { upload = true });
        var f = await new FeaturesClient(_t).MyFeaturesAsync("c1");
        Assert.True(f.IsOn(UserFeatures.Upload));
        var sent = Assert.Single(_t.Calls(Route));
        Assert.Equal("GET", sent.Method);
        Assert.Equal("/api/me/features", sent.PathTemplate);
        Assert.Equal("c1", sent.Query["channelId"]);
        await new FeaturesClient(_t).MyFeaturesAsync(null);
        Assert.False(_t.Calls(Route).Last().Query.ContainsKey("channelId"));
    }

    // Error mapping

    [Theory]
    [InlineData(404, "feature_off", true, true)]
    [InlineData(402, null, false, true)]
    [InlineData(402, "upgrade_required", false, true)]
    [InlineData(403, "upgrade_required", false, true)]
    [InlineData(404, null, false, false)]
    [InlineData(404, "range_not_found", false, false)]
    [InlineData(403, null, false, false)]
    [InlineData(429, "upload_limit", false, false)]
    public void FeatureOffAndPaymentRequiredHide(int status, string? code, bool featureOff, bool hides)
    {
        var e = StudioApiException.FromServer("x", status, code);
        Assert.Equal(featureOff, e.IsFeatureOff);
        Assert.Equal(hides, e.HidesFeature);
    }

    [Theory]
    [InlineData("""{"success":false,"code":"feature_off"}""", "feature_off")]
    [InlineData("""{"error":{"code":"upgrade_required","message":"x"}}""", "upgrade_required")]
    [InlineData("Video not found", null)]
    [InlineData("{broken", null)]
    [InlineData(null, null)]
    public void CodeFromBody(string? body, string? code) => Assert.Equal(code, StudioApiException.CodeFromBody(body));

    // Shell

    [Fact]
    public async Task ShellReadsFeaturesForTheActiveChannelAfterSignIn()
    {
        _t.Responses[Route] = r => Features(new { upload = r.Query["channelId"] == "c1" });
        var shell = Shell(new StudioApiClient(_t));
        Assert.False(shell.Features.IsOn(UserFeatures.Upload));
        await shell.InitializeAsync();
        Assert.True(shell.Features.IsOn(UserFeatures.Upload));
        Assert.Equal("c1", _t.Calls(Route).Last().Query["channelId"]);

        shell.SelectChannel(shell.Channels[1]);
        for (var i = 0; i < 50 && shell.Features.IsOn(UserFeatures.Upload); i++) await Task.Delay(10);
        Assert.False(shell.Features.IsOn(UserFeatures.Upload));
        Assert.Equal("c2", _t.Calls(Route).Last().Query["channelId"]);
    }

    [Fact]
    public async Task FailedRefreshKeepsTheLastAnswer()
    {
        var fail = false;
        _t.Responses[Route] = _ => fail ? throw new StudioApiException("offline") : Features(new { upload = true });
        var shell = Shell(new StudioApiClient(_t));
        await shell.InitializeAsync();
        Assert.True(shell.Features.IsOn(UserFeatures.Upload));
        fail = true;
        await shell.RefreshFeaturesAsync(force: true);
        Assert.True(shell.Features.IsOn(UserFeatures.Upload));
    }

    [Fact]
    public async Task FailedFirstReadHidesEverything()
    {
        _t.Responses[Route] = _ => throw new StudioApiException("offline");
        var shell = Shell(new StudioApiClient(_t));
        await shell.InitializeAsync();
        Assert.Equal(ShellState.Ready, shell.State);
        Assert.Empty(shell.Features.On);
    }

    [Fact]
    public async Task ForegroundRefreshIsThrottled()
    {
        _t.Responses[Route] = _ => Features(new { upload = true });
        var shell = Shell(new StudioApiClient(_t));
        await shell.InitializeAsync();
        var before = _t.Calls(Route).Count;
        await shell.RefreshFeaturesAsync();
        Assert.Equal(before, _t.Calls(Route).Count);
        _now += StudioShellViewModel.FeaturesFreshFor;
        await shell.RefreshFeaturesAsync();
        Assert.Equal(before + 1, _t.Calls(Route).Count);
    }

    [Fact]
    public async Task AnotherAccountStartsHidden()
    {
        _accounts.Saved.Add(new StudioAccount("u2", null, "B", null, null, false));
        _t.Responses[Route] = _ => Features(new { upload = true });
        var shell = Shell(new StudioApiClient(_t));
        await shell.InitializeAsync();
        Assert.True(shell.Features.IsOn(UserFeatures.Upload));

        _t.Responses[Route] = _ => Features(new { upload = false });
        await shell.SwitchAccountAsync("u2");
        Assert.False(shell.Features.IsOn(UserFeatures.Upload));

        await shell.SignOutAsync("u2");
        _accounts.ActiveId = null;
        _accounts.Saved.Clear();
        await shell.RetryAsync();
        Assert.Empty(shell.Features.On);
    }

    // Pages

    [Fact]
    public async Task UploadPageFollowsTheUploadFeature()
    {
        var upload = false;
        _t.Responses[Route] = _ => Features(new { upload });
        var api = new StudioApiClient(_t);
        var shell = Shell(api);
        var pages = new StudioPages(shell, new AnalyticsViewModel(shell, api), StudioPages.Uploads);
        // Before any answer: hidden, Videos shows instead.
        Assert.False(pages.IsVisible(StudioPages.Uploads));
        Assert.Equal(StudioPages.Videos, pages.Current);

        await shell.InitializeAsync();
        Assert.False(pages.IsVisible(StudioPages.Uploads));
        Assert.Equal(StudioPages.Videos, pages.Current);

        upload = true;
        var changed = 0;
        pages.Changed += (_, _) => changed++;
        await shell.RefreshFeaturesAsync(force: true);
        Assert.True(pages.IsVisible(StudioPages.Uploads));
        // The startup tab comes back once it exists.
        Assert.Equal(StudioPages.Uploads, pages.Current);
        Assert.True(changed > 0);

        upload = false;
        await shell.RefreshFeaturesAsync(force: true);
        Assert.False(pages.IsVisible(StudioPages.Uploads));
        Assert.Equal(StudioPages.Videos, pages.Current);
        Assert.True(pages.IsVisible(StudioPages.Videos));
        Assert.True(pages.IsVisible(StudioPages.Encode));
        Assert.True(pages.IsVisible(StudioPages.Timeline));
    }

    [Fact]
    public async Task AChosenPageIsNotOverriddenWhenUploadAppears()
    {
        var upload = false;
        _t.Responses[Route] = _ => Features(new { upload });
        var api = new StudioApiClient(_t);
        var shell = Shell(api);
        var pages = new StudioPages(shell, new AnalyticsViewModel(shell, api), StudioPages.Uploads);
        await shell.InitializeAsync();
        pages.Select(StudioPages.Encode);
        upload = true;
        await shell.RefreshFeaturesAsync(force: true);
        Assert.Equal(StudioPages.Encode, pages.Current);
    }

    [Fact]
    public async Task AnalyticsFeatureOffHidesThePageWithoutText()
    {
        _t.Responses[Route] = _ => Features(new { upload = true });
        _t.Responses["get_AnalyticsChannelGet"] = _ => throw FeatureOff();
        var api = new StudioApiClient(_t);
        var shell = Shell(api);
        var analytics = new AnalyticsViewModel(shell, api);
        var pages = new StudioPages(shell, analytics, StudioPages.Analytics);
        await shell.InitializeAsync();
        await analytics.LoadAsync();
        Assert.True(analytics.IsHidden);
        Assert.Null(analytics.ErrorKey);
        Assert.Null(analytics.Data);
        Assert.False(pages.IsVisible(StudioPages.Analytics));
        Assert.Equal(StudioPages.Uploads, pages.Current);

        // On again (another channel, or switched back on): the page returns.
        _t.Responses["get_AnalyticsChannelGet"] = _ => FakeTransport.Json(new { recentMetrics = Array.Empty<object>() });
        await analytics.LoadAsync();
        Assert.False(analytics.IsHidden);
        Assert.True(pages.IsVisible(StudioPages.Analytics));
        Assert.Equal(StudioPages.Analytics, pages.Current);
    }

    [Fact]
    public async Task AnalyticsPaymentRequiredHides()
    {
        _t.Responses["get_AnalyticsChannelGet"] = _ => throw StudioApiException.FromServer("x", 402, "upgrade_required");
        var api = new StudioApiClient(_t);
        var shell = Shell(api);
        var analytics = new AnalyticsViewModel(shell, api);
        await shell.InitializeAsync();
        await analytics.LoadAsync();
        Assert.True(analytics.IsHidden);
        Assert.Null(analytics.ErrorKey);
    }

    [Fact]
    public async Task AnalyticsOwnerOnlyStaysVisible()
    {
        _t.Responses["get_AnalyticsChannelGet"] = _ => throw StudioApiException.FromServer("x", 403, null);
        var api = new StudioApiClient(_t);
        var shell = Shell(api);
        var analytics = new AnalyticsViewModel(shell, api);
        var pages = new StudioPages(shell, analytics);
        await shell.InitializeAsync();
        await analytics.LoadAsync();
        Assert.False(analytics.IsHidden);
        Assert.Equal("studio.analytics.ownerOnly", analytics.ErrorKey);
        Assert.True(pages.IsVisible(StudioPages.Analytics));
    }

    [Fact]
    public async Task AnalyticsPlainNotFoundIsAnErrorNotHidden()
    {
        _t.Responses["get_AnalyticsChannelGet"] = _ => throw StudioApiException.FromServer("x", 404, null);
        var api = new StudioApiClient(_t);
        var shell = Shell(api);
        var analytics = new AnalyticsViewModel(shell, api);
        await shell.InitializeAsync();
        await analytics.LoadAsync();
        Assert.False(analytics.IsHidden);
        Assert.Equal("studio.analytics.failed", analytics.ErrorKey);
    }

    [Fact]
    public async Task VideosFeatureOffShowsNoText()
    {
        _t.Responses["get_VideosList"] = _ => FakeTransport.Json(new { data = new[] { new { id = "v1", title = "One", channelId = "c1", isPublic = true } } });
        _t.Responses["put_VideoUpdate"] = _ => throw StudioApiException.FromServer("x", 402, "upgrade_required");
        var api = new StudioApiClient(_t);
        var shell = Shell(api);
        var vm = new VideosViewModel(shell, api, () => Array.Empty<string>(), _ => { }, () => _now, (_, _) => Task.CompletedTask);
        await shell.InitializeAsync();
        await vm.LoadAsync();
        var before = _t.Calls(Route).Count;
        Assert.False(await vm.SaveAsync("v1", new VideoChanges { Title = "x" }));
        Assert.Null(vm.ErrorKey);
        for (var i = 0; i < 50 && _t.Calls(Route).Count == before; i++) await Task.Delay(10);
        Assert.True(_t.Calls(Route).Count > before);
    }

    // Demo (CI and screenshots)

    [Fact]
    public async Task DemoAnswersAllOnAndCanTurnFeaturesOff()
    {
        var on = await new FeaturesClient(new DemoTransport()).MyFeaturesAsync("demo-c1");
        Assert.True(on.IsOn(UserFeatures.Upload));
        Assert.Equal(15, on.On.Count);

        var demo = new DemoTransport(DemoTransport.FeaturesOff(" upload , analytics"));
        var off = await new FeaturesClient(demo).MyFeaturesAsync("demo-c1");
        Assert.False(off.IsOn(UserFeatures.Upload));
        Assert.True(off.IsOn(UserFeatures.Live));
        var api = new StudioApiClient(demo);
        var ex = await Assert.ThrowsAsync<StudioApiException>(() => api.ChannelAnalyticsAsync("demo-c1", _now.AddDays(-27), _now));
        Assert.True(ex.IsFeatureOff);
    }
}
