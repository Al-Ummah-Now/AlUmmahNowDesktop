using System.Text.Json;
using AlUmmahNow.CoreBridge.Studio;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

public sealed class StudioApiClientTests
{
    private readonly FakeTransport _t = new();
    private StudioApiClient Api => new(_t);

    [Fact]
    public async Task MapsMyChannelsWithRolesAndCapabilities()
    {
        _t.Responses["get_ChannelAccessList"] = _ => FakeTransport.Json(new
        {
            success = true,
            data = new object[]
            {
                new { id = "c1", userId = "u1", name = "قناة", isMain = true, role = "owner", capabilities = Array.Empty<string>() },
                new { id = "c2", userId = "u9", name = "Other", isMain = false, role = "editor", capabilities = new[] { "video.edit" }, isSuspended = true },
            },
        });
        var list = await Api.ListMyChannelsAsync();
        Assert.Equal(2, list.Count);
        Assert.Equal(ChannelRole.Owner, list[0].Role);
        Assert.True(list[0].Can("video.delete"));
        Assert.Equal(ChannelRole.Editor, list[1].Role);
        Assert.True(list[1].Can("video.edit"));
        Assert.False(list[1].Can("video.delete"));
        Assert.True(list[1].IsSuspended);
    }

    [Fact]
    public async Task ListsVideosForTheChannelWithSearchAndCursor()
    {
        _t.Responses["get_VideosList"] = _ => FakeTransport.Json(new
        {
            cursor = "next",
            data = new object[]
            {
                new { id = "v1", title = "درس", channelId = "c1", isPublic = false, uploadStatus = "processing", views = 12, tags = new[] { "fiqh" }, dateCreated = "2026-11-01T10:00:00Z" },
                new { id = "vX", title = "foreign", channelId = "c2" },
            },
        });
        var page = await Api.ListVideosAsync(new StudioVideoQuery { ChannelId = "c1", Text = "  درس ", Visibility = StudioVisibilityFilter.Private, Cursor = "abc" });
        var v = Assert.Single(page.Items);
        Assert.Equal(StudioVideoStatus.Processing, v.Status);
        Assert.Equal(new[] { "fiqh" }, v.Tags);
        Assert.Equal("next", page.Cursor);
        var call = Assert.Single(_t.Calls("get_VideosList"));
        Assert.Equal("c1", call.Query["channelId"]);
        Assert.Equal("c1", call.Headers["channelId"]);
        Assert.Equal("درس", call.Query["q"]);
        Assert.Equal("private", call.Query["filter"]);
        Assert.Equal("abc", call.Query["cursor"]);
    }

    [Fact]
    public async Task BulkUpdateBatchesByFiftyAndSetsLanguagePerVideo()
    {
        var ids = Enumerable.Range(0, 120).Select(i => "v" + i).ToList();
        await Api.BulkUpdateAsync("c1", ids, new BulkVideoChanges { IsPublic = true, Category = "", AddTags = new[] { " a ", "A", "b" }, Language = "ar" });
        var bulk = _t.Calls("post_VideoBulkUpdate");
        Assert.Equal(3, bulk.Count);
        using var first = JsonDocument.Parse(bulk[0].BodyJson!);
        Assert.Equal(50, first.RootElement.GetProperty("videoIds").GetArrayLength());
        var changes = first.RootElement.GetProperty("changes");
        Assert.True(changes.GetProperty("isPublic").GetBoolean());
        Assert.Equal(JsonValueKind.Null, changes.GetProperty("category").ValueKind);
        Assert.Equal(new[] { "a", "b" }, changes.GetProperty("addTags").EnumerateArray().Select(x => x.GetString()));
        Assert.All(bulk, b => Assert.NotNull(b.IdempotencyKey));
        var puts = _t.Calls("put_VideoUpdate");
        Assert.Equal(120, puts.Count);
        Assert.Equal("{\"language\":\"ar\"}", puts[0].BodyJson);
    }

    [Fact]
    public async Task UpdateSendsOnlyChangedFields()
    {
        await Api.UpdateVideoAsync("c1", "v1", new VideoChanges { Title = "t", Tags = new[] { "x", "" } });
        var put = Assert.Single(_t.Calls("put_VideoUpdate"));
        Assert.Equal("v1", put.PathParams["id"]);
        Assert.Equal("{\"title\":\"t\",\"tags\":[\"x\"]}", put.BodyJson);
        await Api.UpdateVideoAsync("c1", "v1", new VideoChanges());
        Assert.Single(_t.Calls("put_VideoUpdate"));
    }

    [Fact]
    public async Task SchedulesInUtcWithTheTimeZone()
    {
        await Api.ScheduleAsync("c1", "v1", new DateTimeOffset(2026, 12, 1, 9, 30, 0, TimeSpan.FromHours(3)), "Asia/Riyadh");
        var call = Assert.Single(_t.Calls("post_VideoScheduleCreate"));
        Assert.Equal("{\"scheduledPublishDate\":\"2026-12-01T06:30:00Z\",\"timezone\":\"Asia/Riyadh\"}", call.BodyJson);
    }

    [Fact]
    public async Task ReadsChannelAnalytics()
    {
        _t.Responses["get_AnalyticsChannelGet"] = _ => FakeTransport.Json(new
        {
            totalViews = 1000,
            totalLikes = 40,
            totalComments = 7,
            totalSubscribers = 3,
            topPerformingVideos = new[] { new { videoId = "v1", title = "Top", views = 600 } },
            recentMetrics = new[]
            {
                new { date = "2026-11-02", views = 30, watchTime = 600, likes = 2, comments = 1, subscribers = 1 },
                new { date = "2026-11-01", views = 10, watchTime = 120, likes = 1, comments = 0, subscribers = 0 },
            },
        });
        var a = await Api.ChannelAnalyticsAsync("c1", DateTimeOffset.UtcNow.AddDays(-28), DateTimeOffset.UtcNow);
        // The period's numbers (sum of the chart), not the lifetime total* fields
        Assert.Equal(40, a.Totals.Views);
        Assert.Equal(3, a.Totals.Likes);
        Assert.Equal(1, a.Totals.Comments);
        Assert.Equal(1, a.Totals.Subscribers);
        Assert.Equal(12, a.Totals.WatchTimeMinutes);
        Assert.Equal(new DateTime(2026, 11, 1), a.Daily[0].Date);
        Assert.Equal("Top", Assert.Single(a.TopVideos).Title);
    }

    [Fact]
    public async Task PlaylistsListCreateAndBulkAdd()
    {
        _t.Responses["get_PlaylistsList"] = _ => FakeTransport.Json(new { data = new[] { new { id = "p1", title = "Tafsir", videos = new[] { "a", "b" }, isPublic = true } } });
        _t.Responses["post_PlaylistCreate"] = _ => FakeTransport.Json(new { success = true, result = new { playlist = new { id = "p2", title = "New", videosCount = 0, isPublic = false } } });
        var p = Assert.Single(await Api.ListPlaylistsAsync("c1"));
        Assert.Equal(2, p.VideoCount);
        var created = await Api.CreatePlaylistAsync("c1", "New", false);
        Assert.Equal("p2", created.Id);
        await Api.AddToPlaylistAsync("c1", "p2", Enumerable.Range(0, 60).Select(i => "v" + i).ToList());
        Assert.Equal(2, _t.Calls("post_PlaylistVideosBulkAdd").Count);
    }
}
