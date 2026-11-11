using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Analytics;
using AlUmmahNow.Studio.Search;
using AlUmmahNow.Studio.Shell;
using AlUmmahNow.Studio.Videos;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

public sealed class SuggestViewModelTests
{
    private sealed class ManualDelay
    {
        public readonly List<TaskCompletionSource> Waiting = new();

        public Task Delay(TimeSpan _, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled());
            lock (Waiting) Waiting.Add(tcs);
            return tcs.Task;
        }

        public void ReleaseAll()
        {
            lock (Waiting)
            {
                foreach (var t in Waiting) t.TrySetResult();
                Waiting.Clear();
            }
        }
    }

    [Fact]
    public async Task DebouncesCancelsStaleRequestsAndMergesLocalMatches()
    {
        var delay = new ManualDelay();
        var calls = new List<string>();
        var saved = new List<IReadOnlyList<string>>();
        var vm = new SuggestViewModel(
            (text, ct) =>
            {
                calls.Add(text);
                return Task.FromResult<IReadOnlyList<Suggestion>>(new[] { new Suggestion("تفسير سورة الكهف", SuggestionKind.Video, "v1", null) });
            },
            _ => new[] { "تَفسير خاص", "Other" },
            new[] { "تفسير" },
            saved.Add,
            delay.Delay);

        var first = vm.TypedAsync("تف");
        var second = vm.TypedAsync("تفس");
        // Local matches (tashkeel ignored) and recents show at once, before the debounce.
        Assert.Equal(new[] { "تفسير", "تَفسير خاص" }, vm.Suggestions.Select(s => s.Text));
        delay.ReleaseAll();
        await Task.WhenAll(first, second);
        Assert.Equal(new[] { "تفس" }, calls);
        Assert.Equal(3, vm.Suggestions.Count);

        string? submitted = null;
        vm.Submitted += (_, q) => submitted = q;
        vm.Submit(" Seerah ");
        Assert.Equal("Seerah", submitted);
        Assert.Equal(new[] { "Seerah", "تفسير" }, vm.Recents);
        vm.Submit("seerah");
        Assert.Equal(new[] { "seerah", "تفسير" }, vm.Recents);
        vm.RemoveRecent("تفسير");
        vm.ClearRecents();
        Assert.Empty(vm.Recents);
        Assert.Empty(saved.Last());
    }

    [Fact]
    public async Task RateLimitPausesSuggestions()
    {
        var now = DateTimeOffset.UtcNow;
        var calls = 0;
        var vm = new SuggestViewModel(
            (_, _) =>
            {
                calls++;
                throw new RateLimitedException(TimeSpan.FromSeconds(30));
            },
            _ => Array.Empty<string>(), Array.Empty<string>(), _ => { }, (_, _) => Task.CompletedTask, () => now);
        await vm.TypedAsync("a");
        await vm.TypedAsync("ab");
        Assert.Equal(1, calls);
        now = now.AddSeconds(31);
        await vm.TypedAsync("abc");
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("إِسْلَام", "اسلام")]
    [InlineData("مكتبةٌ", "مكتبه")]
    [InlineData("مُصطفى  ", "مصطفي")]
    [InlineData("Fiqh", "fiqh")]
    public void NormalizesLikeTheServer(string input, string expected) => Assert.Equal(expected, SuggestViewModel.Normalize(input));
}

public sealed class VideosViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;
    private readonly FakeAccounts _accounts = new();
    private readonly FakeTransport _t = new();
    private DateTimeOffset _now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    public VideosViewModelTests()
    {
        _t.Responses["get_ChannelAccessList"] = _ => FakeTransport.Json(new { data = new[] { new { id = "c1", userId = "u1", name = "C", isMain = true, role = "owner", capabilities = Array.Empty<string>() } } });
        _t.Responses["get_VideosList"] = r => FakeTransport.Json(new
        {
            cursor = r.Query.ContainsKey("cursor") ? null : "p2",
            data = r.Query.ContainsKey("cursor")
                ? new object[] { new { id = "v3", title = "Three", channelId = "c1", isPublic = true } }
                : new object[] { new { id = "v1", title = "One", channelId = "c1", isPublic = true, tags = new[] { "a" } }, new { id = "v2", title = "Two", channelId = "c1", isPublic = false } },
        });
        _t.Responses["get_VideoFetch"] = r => FakeTransport.Json(new { id = r.PathParams["id"], title = "Edited", channelId = "c1", isPublic = true });
        _accounts.Saved.Add(new StudioAccount("u1", null, "A", null, null, false));
        _accounts.ActiveId = "u1";
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private async Task<VideosViewModel> CreateAsync()
    {
        var api = new StudioApiClient(_t);
        var shell = new StudioShellViewModel(_accounts, api);
        var vm = new VideosViewModel(shell, api, a => new RecentlyDeleted(Path.Combine(_dir, a + ".json")), () => Array.Empty<string>(), _ => { }, () => _now, (_, _) => Task.CompletedTask);
        await shell.InitializeAsync();
        await vm.LoadAsync();
        return vm;
    }

    [Fact]
    public async Task ListsPagesAndSearches()
    {
        var vm = await CreateAsync();
        Assert.Equal(new[] { "v1", "v2" }, vm.Items.Select(v => v.Id));
        Assert.True(vm.HasMore);
        await vm.LoadMoreAsync();
        Assert.Equal(3, vm.Items.Count);
        Assert.False(vm.HasMore);
        vm.Search.Submit("One");
        for (var i = 0; i < 20 && _t.Calls("get_VideosList").All(c => !c.Query.ContainsKey("q")); i++) await Task.Delay(10);
        Assert.Equal("One", _t.Calls("get_VideosList").Last().Query["q"]);
    }

    [Fact]
    public async Task EditSendsOnlyChangesAndBulkEditUsesTheSelection()
    {
        var vm = await CreateAsync();
        var edit = new VideoEdit(vm.Items[0]) { Title = " Edited ", Tags = "a" };
        Assert.Null(edit.Problem);
        Assert.True(await vm.SaveAsync("v1", edit.ChangesFrom()));
        Assert.Equal("{\"title\":\"Edited\"}", Assert.Single(_t.Calls("put_VideoUpdate")).BodyJson);
        Assert.Equal("Edited", vm.Items[0].Title);
        Assert.Equal("studio.videos.titleRequired", new VideoEdit(vm.Items[0]) { Title = " " }.Problem);

        vm.SelectAll(true);
        Assert.Equal(2, vm.Selected.Count);
        vm.SetSelected("v2", false);
        Assert.True(await vm.BulkEditAsync(new BulkVideoChanges { Category = "fiqh" }));
        Assert.Contains("\"videoIds\":[\"v1\"]", Assert.Single(_t.Calls("post_VideoBulkUpdate")).BodyJson);
        Assert.Empty(vm.Selected);
    }

    [Fact]
    public async Task DeleteHidesRestoreBringsBackAndOldDeletionsArePurged()
    {
        var vm = await CreateAsync();
        Assert.Equal(2, await vm.DeleteAsync(new[] { "v1", "v2" }));
        // Only the public one needed hiding.
        Assert.Contains("\"videoIds\":[\"v1\"]", Assert.Single(_t.Calls("post_VideoBulkUpdate")).BodyJson);
        Assert.Empty(vm.Items);
        Assert.Equal(2, vm.Deleted.Count);
        Assert.Empty(_t.Calls("delete_VideoDelete"));

        // Reloading keeps them out of the list.
        await vm.LoadAsync();
        Assert.Empty(vm.Items);

        Assert.True(await vm.RestoreAsync("v1"));
        Assert.Equal("{\"isPublic\":true}", Assert.Single(_t.Calls("put_VideoUpdate")).BodyJson);
        Assert.Contains(vm.Items, v => v.Id == "v1");
        Assert.True(await vm.RestoreAsync("v2"));

        await vm.DeleteAsync(new[] { "v1" });
        _now = _now.AddDays(31);
        await vm.LoadAsync();
        Assert.Equal("v1", Assert.Single(_t.Calls("delete_VideoDelete")).PathParams["id"]);
        Assert.Empty(vm.Deleted);
    }

    [Fact]
    public async Task DeleteNowIsPermanent()
    {
        var vm = await CreateAsync();
        await vm.DeleteAsync(new[] { "v2" });
        Assert.True(await vm.DeleteNowAsync("v2"));
        Assert.Single(_t.Calls("delete_VideoDelete"));
        Assert.Empty(vm.Deleted);
    }

    [Fact]
    public async Task ForbiddenEditsSayWhy()
    {
        _t.Responses["put_VideoUpdate"] = _ => throw new StudioApiException("no", 403);
        var vm = await CreateAsync();
        Assert.False(await vm.SaveAsync("v1", new VideoChanges { Title = "x" }));
        Assert.Equal("studio.videos.notAllowed", vm.ErrorKey);
    }
}

public sealed class AnalyticsViewModelTests
{
    [Fact]
    public async Task LoadsTheRangeAndSwitchesMetric()
    {
        var accounts = new FakeAccounts();
        accounts.Saved.Add(new StudioAccount("u1", null, "A", null, null, false));
        accounts.ActiveId = "u1";
        var t = new FakeTransport();
        t.Responses["get_ChannelAccessList"] = _ => FakeTransport.Json(new { data = new[] { new { id = "c1", userId = "u1", name = "C", isMain = true, role = "owner", capabilities = Array.Empty<string>() } } });
        t.Responses["get_AnalyticsChannelGet"] = _ => FakeTransport.Json(new { totalViews = 10, recentMetrics = new[] { new { date = "2026-11-01", views = 4, watchTime = 7200 } } });
        var api = new StudioApiClient(t);
        var shell = new StudioShellViewModel(accounts, api);
        var vm = new AnalyticsViewModel(shell, api, () => new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero));
        await shell.InitializeAsync();
        await vm.LoadAsync();
        Assert.Equal(10, vm.Data!.Totals.Views);
        Assert.Equal(4, vm.Series.Single().Value);
        vm.Metric = "watch";
        Assert.Equal(2, vm.Series.Single().Value);
        vm.Days = 7;
        for (var i = 0; i < 20 && t.Calls("get_AnalyticsChannelGet").Count < 3; i++) await Task.Delay(10);
        Assert.Equal("2026-11-03T00:00:00Z", t.Calls("get_AnalyticsChannelGet").Last().Query["dateFrom"]);
    }
}
