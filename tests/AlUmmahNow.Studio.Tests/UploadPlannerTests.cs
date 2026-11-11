using AlUmmahNow.Studio.Upload;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

public sealed class UploadPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("{filename}", "/v/lesson-03.mp4", 2, 12, "lesson-03")]
    [InlineData("درس {n} — {filename}", "/v/a.mp4", 2, 12, "درس 03 — a")]
    [InlineData("Part {N} ({date})", "/v/a.mp4", 0, 3, "Part 1 (2026-11-10)")]
    [InlineData("   ", "/v/talk.mov", 0, 1, "talk")]
    [InlineData("{unknown} {filename}", "/v/x.mp4", 0, 1, "{unknown} x")]
    public void FillsTitleTokens(string pattern, string file, int index, int count, string expected) =>
        Assert.Equal(expected, TitlePatterns.Apply(pattern, file, index, count, Now));

    [Fact]
    public void LongTitlesAreCutWithoutSplittingCharacters()
    {
        var title = TitlePatterns.Apply(new string('ب', 99) + "👍🏽", "/a.mp4", 0, 1, Now);
        Assert.Equal(99, title.Length);
    }

    [Fact]
    public void PlansAScheduledBatchAtIntervals()
    {
        var d = new UploadDefaults
        {
            TitlePattern = "Lesson {n}",
            Visibility = UploadVisibility.Scheduled,
            ScheduleStart = Now.AddDays(1),
            ScheduleInterval = TimeSpan.FromHours(6),
            Tags = new List<string> { "fiqh", " Fiqh ", "" },
            PlaylistIds = new List<string> { "p1", "p1" },
            Category = "fiqh",
            Language = "ar",
            TimeZone = "Asia/Riyadh",
        };
        var plans = UploadPlanner.Plan(new[] { "/a.mp4", "/b.mp4", "/c.mp4" }, d, Now);
        Assert.Equal(new[] { "Lesson 1", "Lesson 2", "Lesson 3" }, plans.Select(p => p.Title));
        Assert.Equal(Now.AddDays(1).AddHours(12), plans[2].PublishAt);
        Assert.All(plans, p => Assert.False(p.IsPublic));
        Assert.Equal(new[] { "fiqh" }, plans[0].Tags);
        Assert.Equal(new[] { "p1" }, plans[0].PlaylistIds);
        Assert.Equal("Asia/Riyadh", plans[0].TimeZone);
    }

    [Fact]
    public void ASchedulePutInThePastStartsSoon()
    {
        var d = new UploadDefaults { Visibility = UploadVisibility.Scheduled, ScheduleStart = Now.AddDays(-1) };
        Assert.Equal(Now.AddMinutes(5), UploadPlanner.Plan(new[] { "/a.mp4" }, d, Now)[0].PublishAt);
        Assert.Null(UploadPlanner.Plan(new[] { "/a.mp4" }, new UploadDefaults { Visibility = UploadVisibility.Public }, Now)[0].PublishAt);
    }

    [Fact]
    public void FolderScanFindsVideosInNaturalOrder()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            foreach (var n in new[] { "lesson 10.mp4", "lesson 2.MOV", "notes.txt", ".hidden.mp4", "lesson 1.mkv" }) File.WriteAllText(Path.Combine(dir, n), "x");
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "sub", "extra.mp4"), "x");
            Assert.Equal(new[] { "lesson 1.mkv", "lesson 2.MOV", "lesson 10.mp4" }, UploadPlanner.VideosIn(dir, false).Select(Path.GetFileName));
            Assert.Equal(4, UploadPlanner.VideosIn(dir, true).Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ParsesLatinAndArabicCommaTags() =>
        Assert.Equal(new[] { "فقه", "seerah", "X" }, UploadPlanner.ParseTags("فقه، #seerah, , X,x"));
}
