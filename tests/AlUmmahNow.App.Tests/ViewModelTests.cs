using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Media;
using AlUmmahNow.App.Navigation;
using AlUmmahNow.App.ViewModels;
using AlUmmahNow.CoreBridge;
using Xunit;

namespace AlUmmahNow.App.Tests;

public class HomeViewModelTests
{
    [Fact]
    public async Task Hides_empty_rails_and_unplayable_videos()
    {
        var c = new FakeCatalog();
        c.Videos.Add(FakeCatalog.Ready("a", "fiqh"));
        c.Videos.Add(new Video { Id = "up", IsPublic = true, UploadStatus = "uploading", PlayUrl = "u" });
        var vm = new HomeViewModel(c, new Localizer("en"), new Navigator());
        await vm.LoadAsync();
        Assert.Equal(LoadState.Content, vm.State);
        Assert.Equal(new[] { "Latest", "Most watched", "Fiqh" }, vm.Rails.Select(r => r.Title));
        Assert.DoesNotContain(vm.Rails.SelectMany(r => r.Items), i => i.Id == "up");
        Assert.Equal("a", vm.Featured!.Id);
    }

    [Fact]
    public async Task One_failed_rail_is_hidden_but_all_failed_is_error()
    {
        var c = new FakeCatalog();
        c.Videos.Add(FakeCatalog.Ready("a"));
        c.FailWhen = q => q.Sort == VideoSort.Views;
        var vm = new HomeViewModel(c, new Localizer("en"), new Navigator());
        await vm.LoadAsync();
        Assert.Equal(LoadState.Content, vm.State);
        Assert.DoesNotContain(vm.Rails, r => r.TitleKey == "home.rail.popular");

        c.FailWhen = _ => true;
        await vm.LoadAsync();
        Assert.Equal(LoadState.Error, vm.State);
    }

    [Fact]
    public async Task No_content_is_empty_state()
    {
        var vm = new HomeViewModel(new FakeCatalog(), new Localizer("en"), new Navigator());
        await vm.LoadAsync();
        Assert.Equal(LoadState.Empty, vm.State);
    }

    [Fact]
    public async Task Open_navigates_to_watch()
    {
        var c = new FakeCatalog();
        c.Videos.Add(FakeCatalog.Ready("a"));
        var nav = new Navigator();
        var vm = new HomeViewModel(c, new Localizer("en"), nav);
        await vm.LoadAsync();
        vm.Open(vm.Featured!);
        Assert.Equal("a", Assert.IsType<Route.Watch>(nav.Current).VideoId);
        Assert.True(nav.Back());
        Assert.False(nav.Back());
    }
}

public class WatchViewModelTests
{
    private const string Master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\n360p/v.m3u8\n";
    private const string PlayUrl = "https://cdn/v/playlist.m3u8";

    private static (WatchViewModel Vm, FakePlayer Player, FakeSession Session, FakeCatalog Catalog) Make(string language = "ar")
    {
        var c = new FakeCatalog();
        var v = FakeCatalog.Ready("v", title: "Fallback");
        v.Title = new LocalizedText("Fallback", new Dictionary<string, string> { ["ar"] = "عنوان" });
        v.Chapters = new[] { new Chapter(0, 15, new LocalizedText("c1")), new Chapter(15, 30, new LocalizedText("c2")) };
        c.Videos.Add(v);
        c.Texts[PlayUrl] = Master;
        c.Tracks.Add(new SubtitleTrack("en", "English", "https://s/en", true));
        c.Tracks.Add(new SubtitleTrack("ar", "العربية", "https://s/ar", false));
        c.Texts["https://s/en"] = "WEBVTT\n\n00:00.000 --> 00:05.000\nHello\n";
        c.Texts["https://s/ar"] = "WEBVTT\n\n00:00.000 --> 00:05.000\nمرحبا\n";
        var p = new FakePlayer();
        var s = new FakeSession();
        return (new WatchViewModel(c, p, s, new Localizer(language)), p, s, c);
    }

    [Fact]
    public async Task Load_opens_auto_and_lists_qualities()
    {
        var (vm, p, s, _) = Make();
        await vm.LoadAsync("v");
        Assert.Equal(LoadState.Content, vm.State);
        Assert.Equal(new[] { "تلقائي", "360p" }, vm.Qualities.Select(q => q.Label));
        Assert.Equal((PlayUrl, 0d, true), Assert.Single(p.Opened));
        Assert.Equal("عنوان", s.Title);
    }

    [Fact]
    public async Task Quality_change_keeps_position_and_paused_state()
    {
        var (vm, p, _, _) = Make();
        await vm.LoadAsync("v");
        p.Tick(42);
        p.Pause();
        vm.SelectQuality(vm.Qualities[1]);
        Assert.Equal(("https://cdn/v/360p/v.m3u8", 42d, false), p.Opened[^1]);
    }

    [Theory]
    [InlineData("ar", "مرحبا")]
    [InlineData("en", "Hello")]
    public async Task Subtitles_prefer_ui_language(string language, string expected)
    {
        var (vm, p, _, _) = Make(language);
        await vm.LoadAsync("v");
        p.Tick(1);
        Assert.Equal(expected, vm.Caption);
        p.Tick(6);
        Assert.Equal("", vm.Caption);
    }

    [Fact]
    public async Task Subtitles_off_clears_caption()
    {
        var (vm, p, _, _) = Make();
        await vm.LoadAsync("v");
        p.Tick(1);
        await vm.SelectSubtitleAsync(null);
        p.Tick(2);
        Assert.Equal("", vm.Caption);
    }

    [Fact]
    public async Task Chapter_tracks_position()
    {
        var (vm, p, _, _) = Make();
        await vm.LoadAsync("v");
        p.Tick(20);
        Assert.Equal("c2", vm.CurrentChapter!.Title.Fallback);
    }

    [Fact]
    public async Task Seek_clamps_and_skip_moves_ten_seconds()
    {
        var (vm, p, _, _) = Make();
        await vm.LoadAsync("v");
        p.Tick(5);
        vm.SkipBack();
        Assert.Equal(0, p.Position);
        vm.Seek(500);
        Assert.Equal(100, p.Position);
    }

    [Fact]
    public async Task Speed_is_applied()
    {
        var (vm, p, _, _) = Make();
        await vm.LoadAsync("v");
        vm.SelectSpeed(1.5);
        Assert.Equal(1.5, p.Rate);
    }

    [Theory]
    [InlineData(MediaCommand.Pause, PlayerState.Paused)]
    [InlineData(MediaCommand.Toggle, PlayerState.Paused)]
    public async Task Media_keys_control_player(MediaCommand command, PlayerState expected)
    {
        var (vm, p, s, _) = Make();
        await vm.LoadAsync("v");
        s.Raise(command);
        Assert.Equal(expected, p.State);
        Assert.Equal(expected, s.LastState);
        vm.Dispose();
        Assert.True(p.Stopped);
        Assert.True(s.Cleared);
    }

    [Fact]
    public async Task Missing_video_is_error_and_failed_player_flags()
    {
        var (vm, p, _, _) = Make();
        await vm.LoadAsync("nope");
        Assert.Equal(LoadState.Error, vm.State);
        await vm.LoadAsync("v");
        p.Set(PlayerState.Failed);
        Assert.True(vm.PlaybackFailed);
    }

    [Fact]
    public async Task Master_fetch_failure_still_plays_auto()
    {
        var (vm, p, _, c) = Make();
        c.Texts.Remove(PlayUrl);
        await vm.LoadAsync("v");
        Assert.Single(vm.Qualities);
        Assert.Single(p.Opened);
    }
}
