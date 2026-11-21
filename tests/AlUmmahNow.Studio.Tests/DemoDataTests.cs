using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Demo;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

/// <summary>Review frames show the demo channel; its numbers must agree with each other and look like a real channel.</summary>
public class DemoDataTests
{
    [Fact]
    public async Task AnalyticsAgreeWithTheVideoLibrary()
    {
        var api = new StudioApiClient(new DemoTransport());
        var videos = await api.ListVideosAsync(new StudioVideoQuery { ChannelId = "demo-c1", Limit = 100 });
        var analytics = await api.ChannelAnalyticsAsync("demo-c1", DateTimeOffset.UtcNow.AddDays(-28), DateTimeOffset.UtcNow);

        Assert.Equal(28, analytics.Daily.Count);
        Assert.InRange(analytics.Totals.Views, 1, videos.Items.Sum(v => v.Views));
        Assert.True(analytics.Totals.Likes > 0);
        Assert.True(analytics.Totals.Comments > 0);
        Assert.True(analytics.Totals.Subscribers > 0);
        Assert.All(analytics.TopVideos, t => Assert.Contains(videos.Items, v => v.Id == t.Id && v.Views == t.Views));
        Assert.Equal(analytics.TopVideos.Select(t => t.Views).OrderByDescending(v => v), analytics.TopVideos.Select(t => t.Views));
    }

    [Fact]
    public void TheDailySeriesIsNotARepeatingRamp()
    {
        var days = DemoSeries.Days(new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc), 28, 5960, 208, 36, 46);
        var weeks = Enumerable.Range(0, 4).Select(w => days.Skip(w * 7).Take(7).Select(d => d.Views).ToArray()).ToList();
        // No two weeks repeat, and a week is not a steady climb.
        Assert.Equal(4, weeks.Select(w => string.Join(",", w)).Distinct().Count());
        Assert.All(weeks, w => Assert.False(w.Zip(w.Skip(1), (a, b) => b > a).All(up => up)));
        // Fridays are above the week's average (the weekly rhythm).
        Assert.True(days.Where(d => d.Date.DayOfWeek == DayOfWeek.Friday).Average(d => d.Views) > days.Average(d => d.Views));
        Assert.Equal((long)Math.Round(5960 * 0.82), days.Sum(d => d.Views));
        Assert.Equal(46, days.Sum(d => d.Subscribers));
    }

    [Fact]
    public void SplitAddsUpExactly()
    {
        Assert.Equal(new long[] { 3, 3, 4 }, DemoSeries.Split(10, new[] { 1.0, 1.0, 1.2 }));
        Assert.Equal(101, DemoSeries.Split(101, new[] { 0.3, 2.0, 1.7, 0.01 }).Sum());
    }

    [Theory]
    [InlineData("en", "thumb-1.jpg")]
    [InlineData("ar", "thumb-1.ar.jpg")]
    public async Task ThumbnailsAreTitleCardsInTheUiLanguage(string language, string file)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(dir.FullName, "thumb-1.jpg"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(dir.FullName, "thumb-1.ar.jpg"), new byte[] { 1 });
            var page = await new StudioApiClient(new DemoTransport(null, dir.FullName, language)).ListVideosAsync(new StudioVideoQuery { ChannelId = "demo-c1" });
            Assert.EndsWith("/" + file, page.Items.First(v => v.Id == "demo-v1").ThumbnailUrl);
            Assert.Null(page.Items.First(v => v.Id == "demo-v2").ThumbnailUrl);
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
