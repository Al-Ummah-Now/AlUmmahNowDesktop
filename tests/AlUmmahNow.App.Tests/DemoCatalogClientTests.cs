using AlUmmahNow.App.Demo;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Navigation;
using AlUmmahNow.App.ViewModels;
using AlUmmahNow.CoreBridge;
using Xunit;

namespace AlUmmahNow.App.Tests;

public class DemoCatalogClientTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public async Task EveryHomeRailHasPlayableFixtureVideos(string language)
    {
        var home = new HomeViewModel(new DemoCatalogClient(), new Localizer(language), new Navigator());
        await home.LoadAsync();
        Assert.Equal(LoadState.Content, home.State);
        Assert.Equal(9, home.Rails.Count);
        Assert.All(home.Rails, r => Assert.NotEmpty(r.Items));
        Assert.All(home.Rails.SelectMany(r => r.Items), c => Assert.False(string.IsNullOrWhiteSpace(c.Title)));
    }

    [Fact]
    public async Task TitlesAreTranslated()
    {
        var video = await new DemoCatalogClient().GetVideoAsync("demo-v1");
        Assert.NotEqual(video.Title.For("en"), video.Title.For("ar"));
        await Assert.ThrowsAsync<CatalogException>(() => new DemoCatalogClient().GetVideoAsync("nope"));
    }

    [Fact]
    public async Task ThumbnailsComeFromTheFolderWhenPresent()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            Assert.Null((await new DemoCatalogClient(dir.FullName).GetVideoAsync("demo-v1")).ThumbnailUrl);
            File.WriteAllBytes(Path.Combine(dir.FullName, "thumb-1.png"), new byte[] { 1 });
            var url = (await new DemoCatalogClient(dir.FullName).GetVideoAsync("demo-v1")).ThumbnailUrl;
            Assert.StartsWith("file:///", url);
            Assert.EndsWith("thumb-1.png", url);
        }
        finally { dir.Delete(true); }
    }
}
