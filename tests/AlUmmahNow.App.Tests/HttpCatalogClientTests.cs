using System.Net;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Temporary;
using Xunit;

namespace AlUmmahNow.App.Tests;

public class HttpCatalogClientTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public string Body = "{}";
        public HttpStatusCode Status = HttpStatusCode.OK;
        public Uri? Last;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Last = r.RequestUri;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
        }
    }

    private static (HttpCatalogClient, Handler) Make(string body)
    {
        var h = new Handler { Body = body };
        return (new HttpCatalogClient(new HttpClient(h), new Uri("https://api.test")), h);
    }

    [Fact]
    public async Task List_builds_query_and_parses_qa_shape()
    {
        var (c, h) = Make("""
        {"cursor":"next","data":[{"id":"v1","title":"T","titleLocalized":{"en":"T","ar":"ع"},"playUrl":"https://cdn/p.m3u8",
        "isPublic":true,"uploadStatus":"ready","duration":70,"views":5,"channelName":"C","dateCreated":"2026-11-07T14:16:47.310Z",
        "chapters":{"chapters":[{"startTime":0,"endTime":15,"title":"I","titleLocalized":{"en":"I","ar":"م"}}],"version":1}}]}
        """);
        var page = await c.ListVideosAsync(new VideoQuery { Limit = 2, Sort = VideoSort.Views, Category = "fiqh", Cursor = "a b" });
        Assert.Equal("/api/videos?limit=2&sort=views&category=fiqh&cursor=a%20b", h.Last!.PathAndQuery);
        Assert.Equal("next", page.Cursor);
        var v = Assert.Single(page.Items);
        Assert.True(v.IsPlayable);
        Assert.Equal("ع", v.Title.For("ar"));
        Assert.Equal(70, v.DurationSeconds);
        Assert.Equal("م", Assert.Single(v.Chapters).Title.For("ar"));
    }

    [Fact]
    public async Task Blank_translation_falls_back_and_null_chapters_are_empty()
    {
        var (c, _) = Make("""{"data":[{"id":"x","title":"Reload","titleLocalized":{"en":"Reload","ar":""},"chapters":null}]}""");
        var v = (await c.ListVideosAsync(new VideoQuery())).Items[0];
        Assert.Equal("Reload", v.Title.For("ar"));
        Assert.Empty(v.Chapters);
        Assert.False(v.IsPlayable);
    }

    [Fact]
    public async Task Subtitles_keep_only_ready_tracks()
    {
        var (c, _) = Make("""{"success":true,"data":{"tracks":[{"language":"ar","label":"ع","status":"ready","url":"https://u/ar","isDefault":true},{"language":"en","status":"processing","url":"https://u/en"}]}}""");
        var t = Assert.Single(await c.GetSubtitleTracksAsync("v"));
        Assert.Equal("ar", t.Language);
        Assert.True(t.IsDefault);
    }

    [Fact]
    public async Task Http_error_is_a_catalog_exception()
    {
        var (c, h) = Make("x");
        h.Status = HttpStatusCode.InternalServerError;
        await Assert.ThrowsAsync<CatalogException>(() => c.GetVideoAsync("v"));
    }
}
