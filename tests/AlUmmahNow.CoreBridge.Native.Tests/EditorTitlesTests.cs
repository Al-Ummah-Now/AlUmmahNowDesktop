using System.Text.Json;
using AlUmmahNow.CoreBridge.Native.Studio;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Timeline;
using Xunit;

namespace AlUmmahNow.CoreBridge.Native.Tests;

/// <summary>Titles and lower thirds (19.5 + 19.9) through the real Core edit list: only probing, painting and rendering are faked.</summary>
public sealed class EditorTitlesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aun-titles-").FullName;
    private readonly EditorViewModelTests.FakeRenderer _renderer = new();
    private readonly FakePainter _painter = new();

    public void Dispose() => Directory.Delete(_dir, true);

    private static BrandKit Kit(LowerThirdStyle style = LowerThirdStyle.Bar) => new()
    {
        Primary = "#FFA037",
        Secondary = "#093041",
        TextColor = "#F7F1E8",
        Background = "#101010",
        LowerThirdStyle = style,
    };

    /// <summary>Two videos (10 s and 6 s) on the timeline: 16 s of picture.</summary>
    private async Task<EditorViewModel> Editor()
    {
        var vm = new EditorViewModel(new CoreEdlEditing(), new EditorViewModelTests.FakeProbe(), _renderer, () => DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), _painter) { Brand = Kit() };
        var files = new[] { "talk.mp4", "walk.mp4" }.Select(n =>
        {
            var path = Path.Combine(_dir, n);
            File.WriteAllBytes(path, new byte[] { 0 });
            return path;
        });
        await vm.AddVideosAsync(files);
        return vm;
    }

    [Fact]
    public async Task ALowerThirdIsAddedAtThePlayheadInTheBrandKitsLook()
    {
        var vm = await Editor();
        vm.Seek(2_000);
        Assert.True(vm.CanAddTitle);
        Assert.True(vm.AddTitle(TitleKind.LowerThird, " قناة النور ", "Al Noor channel"));

        var t = Assert.Single(vm.Titles);
        Assert.Equal(t.Id, vm.SelectedTitleId);
        Assert.Equal(TitleTemplates.TrackId, t.TrackId);
        Assert.Equal(2_000, t.StartMs);
        Assert.Equal(EditorViewModel.DefaultTitleMs, t.DurationMs);
        Assert.Equal("قناة النور", t.In("ar"));
        Assert.Equal("Al Noor channel", t.In("en"));
        Assert.Equal(TitlePosition.LowerThird, t.Style.Position);
        Assert.Equal("#093041", t.Style.Background);
        Assert.Equal("#f7f1e8", t.Style.Color);
        Assert.Equal(TitleTemplates.FontFamily, t.Style.FontFamily);
        Assert.Empty(new CoreEdlEditing().Validate(vm.Project));
        Assert.Null(vm.Target);
    }

    [Fact]
    public async Task ACentredTitleHasNoBoxAndTheBoxTemplateUsesThePrimaryColour()
    {
        var vm = await Editor();
        Assert.True(vm.AddTitle(TitleKind.Title, null, "Damascus market"));
        Assert.Equal(TitlePosition.Center, vm.SelectedTitle!.Style.Position);
        Assert.Null(vm.SelectedTitle.Style.Background);

        vm.Brand = Kit(LowerThirdStyle.Box);
        Assert.True(vm.SetTitleKind(TitleKind.LowerThird));
        Assert.Equal("#ffa037", vm.SelectedTitle!.Style.Background);
        vm.Brand = Kit(LowerThirdStyle.Minimal);
        Assert.True(vm.SetTitleKind(TitleKind.LowerThird));
        Assert.Null(vm.SelectedTitle!.Style.Background);
    }

    [Fact]
    public async Task ATitleNeedsText()
    {
        var vm = await Editor();
        Assert.False(vm.AddTitle(TitleKind.LowerThird, "  ", null));
        Assert.Equal("studio.editor.error.titleText", vm.ErrorKey);
        Assert.Empty(vm.Titles);
        Assert.False(vm.CanUndo && vm.Titles.Count > 0);
    }

    [Fact]
    public async Task NewTitlesStopAtTheNextTitleAndTheEndOfTheVideo()
    {
        var vm = await Editor();
        vm.Seek(5_000);
        Assert.True(vm.AddTitle(TitleKind.LowerThird, "ب", null));
        vm.Seek(3_000);
        Assert.True(vm.AddTitle(TitleKind.LowerThird, "أ", null));
        Assert.Equal(2_000, vm.SelectedTitle!.DurationMs);

        vm.Seek(14_500);
        Assert.True(vm.AddTitle(TitleKind.Title, null, "End"));
        Assert.Equal(16_000, vm.SelectedTitle!.EndMs);

        vm.Seek(6_000);
        Assert.False(vm.CanAddTitle);
        Assert.False(vm.AddTitle(TitleKind.LowerThird, "ج", null));
        Assert.Equal("studio.editor.error.titleRoom", vm.ErrorKey);
        Assert.Equal(new[] { 3_000L, 5_000, 14_500 }, vm.Titles.Select(t => t.StartMs));
    }

    [Fact]
    public async Task RewordingKeepsOtherLanguagesAndUndoes()
    {
        var vm = await Editor();
        vm.AddTitle(TitleKind.LowerThird, "قناة النور", "Al Noor channel");
        Assert.True(vm.SetTitleText("قناة النور الجديدة", ""));
        Assert.Equal("قناة النور الجديدة", vm.SelectedTitle!.In("ar"));
        Assert.Equal("", vm.SelectedTitle.In("en"));
        Assert.False(vm.SetTitleText(" ", null));
        Assert.Equal("studio.editor.error.titleText", vm.ErrorKey);

        vm.Undo();
        Assert.Equal("Al Noor channel", Assert.Single(vm.Titles).In("en"));
        vm.Redo();
        Assert.Equal("", Assert.Single(vm.Titles).In("en"));
    }

    [Fact]
    public async Task MovingAndRetimingStayInsideTheVideoAndOffOtherTitles()
    {
        var vm = await Editor();
        vm.Seek(1_000);
        vm.AddTitle(TitleKind.LowerThird, "أ", null);
        var first = vm.SelectedTitleId!;
        vm.Seek(8_000);
        vm.AddTitle(TitleKind.LowerThird, "ب", null);

        vm.SelectTitle(first);
        Assert.True(vm.MoveTitleTo(-500));
        Assert.Equal(0, vm.SelectedTitle!.StartMs);
        Assert.False(vm.MoveTitleTo(6_000));
        Assert.Equal("studio.editor.error.titleOverlap", vm.ErrorKey);
        Assert.True(vm.MoveTitleTo(20_000));
        Assert.Equal((12_000L, 16_000L), (vm.SelectedTitle!.StartMs, vm.SelectedTitle.EndMs));
        Assert.True(vm.MoveTitleTo(0));

        vm.Seek(2_500);
        Assert.True(vm.TitleEndToPlayhead());
        Assert.Equal((0L, 2_500L), (vm.SelectedTitle!.StartMs, vm.SelectedTitle.EndMs));
        vm.Seek(500);
        Assert.True(vm.TitleStartToPlayhead());
        Assert.Equal(500, vm.SelectedTitle!.StartMs);
        Assert.False(vm.RetimeTitle(500, 550));
        Assert.Equal("studio.editor.error.titleLength", vm.ErrorKey);
        Assert.False(vm.RetimeTitle(500, 99_000));
        Assert.Equal("studio.editor.error.titleOverlap", vm.ErrorKey);

        vm.Seek(12_000);
        Assert.True(vm.MoveTitleToPlayhead());
        Assert.Equal(12_000, vm.SelectedTitle!.StartMs);
        Assert.Equal(2, vm.Titles.Count);
        Assert.Empty(new CoreEdlEditing().Validate(vm.Project));
    }

    [Fact]
    public async Task RemovingATitleUndoesAndSelectingAClipLeavesTheTitle()
    {
        var vm = await Editor();
        vm.AddTitle(TitleKind.LowerThird, "أ", null);
        vm.Select(vm.Clips[0].ClipId);
        Assert.Null(vm.SelectedTitleId);
        Assert.False(vm.RemoveTitle());

        vm.ClickTitleAt(1_000);
        Assert.NotNull(vm.SelectedTitleId);
        Assert.Null(vm.SelectedClipId);
        Assert.True(vm.RemoveTitle());
        Assert.Empty(vm.Titles);
        Assert.Equal(2, vm.Clips.Count);
        vm.Undo();
        Assert.Single(vm.Titles);
    }

    [Fact]
    public async Task RippleDeletingAClipMovesTheTitlesAfterIt()
    {
        var vm = await Editor();
        vm.Seek(11_000);
        vm.AddTitle(TitleKind.LowerThird, "ب", null);
        vm.Seek(8_000);
        vm.AddTitle(TitleKind.Title, null, "Across the cut");
        vm.Seek(2_000);
        vm.AddTitle(TitleKind.LowerThird, "أ", null);
        vm.ClickAt(1_000);
        Assert.True(vm.Delete());
        // After the deleted clip: moved back with the video. Across the cut: trimmed. Inside it: gone.
        Assert.Equal(new[] { (0L, 1_000L), (1_000L, 4_000L) }, vm.Titles.Select(t => (t.StartMs, t.DurationMs)));
        vm.Undo();
        Assert.Equal(3, vm.Titles.Count);
    }

    [Fact]
    public async Task TitlesSaveAndOpenWithTheProject()
    {
        var vm = await Editor();
        vm.Seek(2_000);
        vm.AddTitle(TitleKind.LowerThird, "قناة النور", "Al Noor channel");
        var file = Path.Combine(_dir, "titled" + EditorTime.ProjectExtension);
        Assert.True(await vm.SaveAsync(file));

        var other = new EditorViewModel(new CoreEdlEditing(), new EditorViewModelTests.FakeProbe(), _renderer);
        Assert.True(await other.OpenAsync(file));
        var t = Assert.Single(other.Titles);
        Assert.Equal("قناة النور", t.In("ar"));
        Assert.Equal(2_000, t.StartMs);
    }

    [Fact]
    public async Task ATitleTrackFromAnotherAppIsUsed()
    {
        var json = CoreStudioMediaTests.Project
            .Replace("/media/cam.mp4", JsonEncodedText.Encode(Path.Combine(_dir, "cam.mp4")).ToString(), StringComparison.Ordinal)
            .Replace("""{ "id": "v1", "kind": "video", "clips": [""", """{ "id": "brand", "kind": "title" }, { "id": "v1", "kind": "video", "clips": [""", StringComparison.Ordinal);
        File.WriteAllBytes(Path.Combine(_dir, "cam.mp4"), new byte[] { 0 });
        var file = Path.Combine(_dir, "mac" + EditorTime.ProjectExtension);
        File.WriteAllText(file, json);
        var vm = new EditorViewModel(new CoreEdlEditing(), new EditorViewModelTests.FakeProbe(), _renderer);
        Assert.True(await vm.OpenAsync(file));
        Assert.True(vm.AddTitle(TitleKind.Title, null, "Hello"));
        Assert.Equal("brand", vm.SelectedTitle!.TrackId);
        Assert.DoesNotContain("\"id\":\"titles\"", vm.Project, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportPaintsEachTitleCutToTheVideoAndCleansUp()
    {
        var vm = await Editor();
        vm.BrandLogo = new byte[] { 1, 2, 3 };
        vm.Seek(1_000);
        vm.AddTitle(TitleKind.LowerThird, "قناة النور", "Al Noor channel");
        vm.Seek(14_000);
        vm.AddTitle(TitleKind.Title, null, "End");
        vm.SelectTitle(vm.Titles[1].Id);
        Assert.True(vm.RetimeTitle(14_000, 15_500));

        var output = Path.Combine(_dir, "out.mp4");
        Assert.Equal(output, await vm.ExportAsync(output));
        Assert.Equal(1, vm.ExportProgress);
        Assert.Equal(new[] { (1_000L, 4_000L), (14_000L, 1_500L) }, _renderer.Titles.Select(t => (t.Overlay.StartMs, t.Overlay.DurationMs)));
        Assert.All(_renderer.Titles, t => Assert.True(t.Existed));
        Assert.All(_renderer.Titles, t => Assert.False(File.Exists(t.Overlay.ImagePath)));
        Assert.Equal((1280, 720), _painter.Size);
        Assert.Equal(new byte[] { 1, 2, 3 }, _painter.Decor!.Logo);
        Assert.Equal(LowerThirdStyle.Bar, _painter.Decor.Style);
    }

    [Fact]
    public async Task AnUndoneTitleIsGoneFromThePreviewAndTheExport()
    {
        var vm = await Editor();
        vm.Seek(1_000);
        vm.AddTitle(TitleKind.LowerThird, "أ", null);
        Assert.Single(vm.TitlesOnScreen);
        vm.Undo();
        Assert.Empty(vm.TitlesOnScreen);
        Assert.Null(vm.SelectedTitleId);
        await vm.ExportAsync(Path.Combine(_dir, "out.mp4"));
        Assert.Empty(_renderer.Titles);
    }

    private sealed class FakePainter : ITitlePainter
    {
        public (int, int) Size { get; private set; }

        public TitleDecor? Decor { get; private set; }

        public void WritePng(EdlTitle title, TitleDecor decor, int width, int height, string path)
        {
            Size = (width, height);
            Decor = decor;
            File.WriteAllBytes(path, new byte[] { 0x89, 0x50, 0x4e, 0x47 });
        }
    }
}
