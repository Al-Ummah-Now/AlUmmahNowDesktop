using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Shell;
using AlUmmahNow.Studio.Videos;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

/// <summary>A video suggestion shows the title its card shows, in the page language.</summary>
public sealed class LocalizedSuggestionsTests
{
    [Theory]
    [InlineData("ar", "خطبة الجمعة: الصبر")]
    [InlineData("en", "Friday khutbah: patience")]
    public async Task VideoSuggestionsUseTheCardsTitle(string language, string expected)
    {
        var accounts = new FakeAccounts();
        accounts.Saved.Add(new StudioAccount("u1", null, "A", null, null, false));
        accounts.ActiveId = "u1";
        var t = new FakeTransport();
        t.Responses["get_ChannelAccessList"] = _ => FakeTransport.Json(new { data = new[] { new { id = "c1", userId = "u1", name = "C", isMain = true, role = "owner", capabilities = Array.Empty<string>() } } });
        t.Responses["get_VideosList"] = _ => FakeTransport.Json(new
        {
            data = new object[] { new { id = "v1", title = "خطبة الجمعة: الصبر", channelId = "c1", titleLocalized = new { en = "Friday khutbah: patience", ar = "خطبة الجمعة: الصبر" } } },
        });
        // The API's suggestion carries the original title only.
        t.Suggestions = (_, _) => new[]
        {
            new Suggestion("خطبة الجمعة: الصبر", SuggestionKind.Video, "v1", null),
            new Suggestion("Not on this page", SuggestionKind.Video, "v9", null),
        };
        var api = new StudioApiClient(t);
        var shell = new StudioShellViewModel(accounts, api);
        var vm = new VideosViewModel(shell, api, () => Array.Empty<string>(), _ => { }, null, (_, _) => Task.CompletedTask, () => language);
        await shell.InitializeAsync();
        await vm.LoadAsync();

        await vm.Search.TypedAsync(language == "ar" ? "خطبة" : "Friday");

        var videos = vm.Search.Suggestions.Where(s => s.Kind == SuggestionKind.Video).Select(s => s.Text).ToList();
        Assert.Contains(expected, videos);
        Assert.Contains("Not on this page", videos);
        if (language == "en") Assert.DoesNotContain("خطبة الجمعة: الصبر", videos);
    }
}
