using AlUmmahNow.App.Media;
using Xunit;

namespace AlUmmahNow.App.Tests;

public class MediaParsingTests
{
    private const string Master = "#EXTM3U\n#EXT-X-VERSION:3\n\n" +
        "#EXT-X-STREAM-INF:BANDWIDTH=518504,CODECS=\"avc1.640015,mp4a.40.2\",RESOLUTION=426x240\n240p/video.m3u8\n" +
        "#EXT-X-STREAM-INF:BANDWIDTH=817800,CODECS=\"avc1.64001e,mp4a.40.2\",RESOLUTION=640x360\n360p/video.m3u8\n";

    [Fact]
    public void Variants_are_sorted_and_keep_path_tokens()
    {
        const string master = "https://cdn/bcdn_token=abc&expires=1&token_path=%2Fid%2F/id/playlist.m3u8";
        var v = HlsPlaylist.ParseVariants(Master, master);
        Assert.Equal(new[] { "360p", "240p" }, v.Select(x => x.Label));
        Assert.Equal("https://cdn/bcdn_token=abc&expires=1&token_path=%2Fid%2F/id/360p/video.m3u8", v[0].Url);
        Assert.Equal(817800, v[0].Bandwidth);
    }

    [Fact]
    public void Commas_inside_quotes_do_not_split_attributes()
    {
        var v = HlsPlaylist.ParseVariants(Master, "https://cdn/a/m.m3u8");
        Assert.Equal(360, v[0].Height);
    }

    [Fact]
    public void Vtt_parses_cues_strips_tags_and_handles_crlf()
    {
        var cues = WebVtt.Parse("WEBVTT\r\n\r\n1\r\n00:00:01.000 --> 00:00:02.500 align:start\r\n<b>بسم</b> الله\r\n\r\n01:00.000 --> 01:01.000\r\nSecond\r\nline\r\n");
        Assert.Equal(2, cues.Count);
        Assert.Equal(1, cues[0].Start);
        Assert.Equal(2.5, cues[0].End);
        Assert.Equal("بسم الله", cues[0].Text);
        Assert.Equal(60, cues[1].Start);
        Assert.Equal("Second\nline", cues[1].Text);
    }

    [Theory]
    [InlineData(0.5, null)]
    [InlineData(1, "a")]
    [InlineData(1.99, "a")]
    [InlineData(2, null)]
    [InlineData(5, "b")]
    public void At_finds_active_cue(double position, string? expected)
    {
        var cues = new[] { new Cue(1, 2, "a"), new Cue(4, 6, "b") };
        Assert.Equal(expected, WebVtt.At(cues, position)?.Text);
    }
}
