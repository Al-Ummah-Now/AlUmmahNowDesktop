using AlUmmahNow.App.Demo;
using AlUmmahNow.App.Media;
using AlUmmahNow.CoreBridge;
using Xunit;

namespace AlUmmahNow.App.Tests;

/// <summary>CI's watch scenario plays the committed fixture, never a remote stream.</summary>
public class FixtureMediaCatalogTests
{
    private static string Folder()
    {
        var dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "tests", "fixtures", "watch"))) dir = Path.GetDirectoryName(dir)!;
        return Path.Combine(dir, "tests", "fixtures", "watch");
    }

    [Fact]
    public async Task The_fixture_video_plays_the_local_file_with_its_captions()
    {
        var catalog = new FixtureMediaCatalog(new DemoCatalogClient(), Folder());
        var video = await catalog.GetVideoAsync(FixtureMediaCatalog.VideoId);
        var file = new Uri(video.PlayUrl!);
        Assert.True(file.IsFile);
        Assert.True(File.Exists(file.LocalPath));

        var tracks = await catalog.GetSubtitleTracksAsync(FixtureMediaCatalog.VideoId);
        Assert.Equal(new[] { "en", "ar" }, tracks.Select(t => t.Language));
        foreach (var track in tracks)
        {
            var text = await catalog.GetTextAsync(track.Url);
            // In-fiction captions: the frames never announce a fixture.
            Assert.DoesNotContain("fixture", text, StringComparison.OrdinalIgnoreCase);
        }

        var cues = WebVtt.Parse(await catalog.GetTextAsync(tracks[0].Url));
        // A caption on screen from the first second to past the scenario's 26 s.
        Assert.Equal(0, cues[0].Start);
        Assert.True(cues[cues.Count - 1].End > 26);
        await Assert.ThrowsAsync<CatalogException>(() => catalog.GetTextAsync(video.PlayUrl!));
    }

    [Fact]
    public async Task Everything_else_comes_from_the_real_catalog()
    {
        var catalog = new FixtureMediaCatalog(new DemoCatalogClient(), Folder());
        Assert.Equal("demo-v1", (await catalog.GetVideoAsync("demo-v1")).Id);
        Assert.Empty(await catalog.GetSubtitleTracksAsync("demo-v1"));
    }
}
