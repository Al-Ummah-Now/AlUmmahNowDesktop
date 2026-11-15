using System.Text.Json;
using AlUmmahNow.CoreBridge.Native.Studio;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Timeline;
using Xunit;

namespace AlUmmahNow.CoreBridge.Native.Tests;

/// <summary>The editor's edits run through the real Core edit list; only probing and rendering are faked.</summary>
public sealed class EditorViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aun-editor-").FullName;
    private readonly FakeRenderer _renderer = new();

    public void Dispose() => Directory.Delete(_dir, true);

    private EditorViewModel Make() => new(new CoreEdlEditing(), new FakeProbe(), _renderer, () => DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));

    /// <summary>An (empty) file in the test folder; the fake probe decides what it contains.</summary>
    private string Media(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[] { 0 });
        return path;
    }

    private async Task<EditorViewModel> WithTwoVideos()
    {
        var vm = Make();
        await vm.AddVideosAsync(new[] { Media("talk.mp4"), Media("walk.mp4") });
        return vm;
    }

    private string SavedProjectWithSource(string sourcePath)
    {
        var json = CoreStudioMediaTests.Project.Replace("/media/cam.mp4", JsonEncodedText.Encode(sourcePath).ToString(), StringComparison.Ordinal);
        var file = Path.Combine(_dir, "shared" + EditorTime.ProjectExtension);
        File.WriteAllText(file, json);
        return file;
    }

    [Theory]
    [InlineData(@"\\attacker.example\share\cam.mp4")]
    [InlineData("//attacker.example/share/cam.mp4")]
    [InlineData(@"\/attacker.example/share/cam.mp4")]
    [InlineData(@"/\attacker.example\share\cam.mp4")]
    [InlineData(@"\\?\UNC\attacker.example\share\cam.mp4")]
    [InlineData("smb://attacker.example/share/cam.mp4")]
    [InlineData("relative/cam.mp4")]
    public async Task ProjectsWithNonLocalSourcesAreRefused(string source)
    {
        var vm = Make();
        Assert.False(await vm.OpenAsync(SavedProjectWithSource(source)));
        Assert.Equal("studio.editor.error.source", vm.ErrorKey);
        Assert.Empty(vm.Clips);
    }

    [Fact]
    public async Task ProjectsWithMissingSourcesAreRefused()
    {
        var vm = Make();
        Assert.False(await vm.OpenAsync(SavedProjectWithSource(Path.Combine(_dir, "gone.mp4"))));
        Assert.Equal("studio.editor.error.source", vm.ErrorKey);
    }

    [Fact]
    public async Task ProjectsWithLocalSourcesOpen()
    {
        var vm = Make();
        Assert.True(await vm.OpenAsync(SavedProjectWithSource(Media("cam.mp4"))));
        Assert.Equal(2, vm.Clips.Count);
    }

    [Fact]
    public async Task AddedVideosBecomeClipsEndToEnd()
    {
        var vm = await WithTwoVideos();
        Assert.Equal(2, vm.Clips.Count);
        Assert.Equal(0, vm.Clips[0].TimelineStartMs);
        Assert.Equal(10_000, vm.Clips[1].TimelineStartMs);
        Assert.Equal(16_000, vm.Timeline.DurationMs);
        Assert.Equal("talk", vm.SourceNames[vm.Clips[0].SourceId]);
        Assert.Equal(new ProjectSettings(1280, 720, 30, 48000, 2), EdlProject.Settings(vm.Project));
        Assert.True(vm.CanUndo);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task FilesWithoutVideoAreSkippedWithAMessage()
    {
        var vm = Make();
        await vm.AddVideosAsync(new[] { Media("voice.m4a"), Media("talk.mp4") });
        Assert.Single(vm.Clips);
        Assert.Equal("studio.editor.error.noVideo", vm.ErrorKey);
        Assert.Equal("voice.m4a", vm.ErrorDetail);
    }

    [Fact]
    public async Task SplitThenJoinRestoresOneClip()
    {
        var vm = await WithTwoVideos();
        vm.ClickAt(4_000);
        Assert.True(vm.Split());
        Assert.Equal(3, vm.Clips.Count);
        Assert.Equal(4_000, vm.Clips[1].TimelineStartMs);
        Assert.Equal(4_000, vm.Clips[1].InMs);
        Assert.Equal(vm.Clips[1].ClipId, vm.SelectedClipId);

        vm.Select(vm.Clips[0].ClipId);
        Assert.True(vm.CanJoin);
        Assert.True(vm.JoinWithNext());
        Assert.Equal(2, vm.Clips.Count);
        Assert.Equal(10_000, vm.Clips[0].TimelineDurationMs);
    }

    [Fact]
    public async Task SplitTooCloseToAnEdgeIsNotOffered()
    {
        var vm = await WithTwoVideos();
        vm.ClickAt(50);
        Assert.False(vm.CanSplit);
        Assert.False(vm.Split());
        Assert.Equal(2, vm.Clips.Count);
    }

    [Fact]
    public async Task TrimsCutToThePlayheadWithoutRippling()
    {
        var vm = await WithTwoVideos();
        vm.ClickAt(3_000);
        Assert.True(vm.TrimStartToPlayhead());
        Assert.Equal(3_000, vm.Clips[0].InMs);
        Assert.Equal(3_000, vm.Clips[0].TimelineStartMs);
        Assert.Equal(10_000, vm.Clips[1].TimelineStartMs);

        vm.ClickAt(8_000);
        Assert.True(vm.TrimEndToPlayhead());
        Assert.Equal(8_000, vm.Clips[0].OutMs);
    }

    [Fact]
    public async Task DeleteRipplesAndUndoRedoWalkSnapshots()
    {
        var vm = await WithTwoVideos();
        vm.ClickAt(1_000);
        Assert.True(vm.Delete());
        Assert.Single(vm.Clips);
        Assert.Equal(0, vm.Clips[0].TimelineStartMs);
        Assert.Equal(6_000, vm.Timeline.DurationMs);

        vm.Undo();
        Assert.Equal(2, vm.Clips.Count);
        vm.Redo();
        Assert.Single(vm.Clips);
    }

    [Fact]
    public async Task MoveBySwapsNeighboursAsOneUndoStep()
    {
        var vm = await WithTwoVideos();
        var first = vm.Clips[0].ClipId;
        vm.Select(first);
        Assert.True(vm.MoveBy(1));
        Assert.Equal(first, vm.Clips[1].ClipId);
        Assert.Equal(0, vm.Clips[0].TimelineStartMs);
        Assert.Equal(6_000, vm.Clips[1].TimelineStartMs);
        Assert.Equal(16_000, vm.Timeline.DurationMs);

        vm.Undo();
        Assert.Equal(first, vm.Clips[0].ClipId);
    }

    [Fact]
    public async Task MovingOntoAnotherClipIsRefused()
    {
        var vm = await WithTwoVideos();
        vm.Select(vm.Clips[1].ClipId);
        var before = vm.Project;
        Assert.False(vm.MoveTo(2_000));
        Assert.Equal(before, vm.Project);
        Assert.StartsWith("studio.editor.error.", vm.ErrorKey);
    }

    [Fact]
    public async Task SavedProjectsOpenAgain()
    {
        var vm = await WithTwoVideos();
        vm.ClickAt(4_000);
        vm.Split();
        var file = Path.Combine(_dir, "edit" + EditorTime.ProjectExtension);
        Assert.True(await vm.SaveAsync(file));
        Assert.False(vm.IsDirty);

        var other = Make();
        Assert.True(await other.OpenAsync(file));
        Assert.Equal(3, other.Clips.Count);
        Assert.False(other.CanUndo);
    }

    [Fact]
    public async Task ABrokenProjectFileIsReported()
    {
        var file = Path.Combine(_dir, "broken" + EditorTime.ProjectExtension);
        File.WriteAllText(file, "{\"schemaVersion\": 99}");
        var vm = Make();
        Assert.False(await vm.OpenAsync(file));
        Assert.Equal("studio.editor.error.open", vm.ErrorKey);
        Assert.Empty(vm.Clips);
    }

    [Fact]
    public async Task ExportRendersTheFlattenedCut()
    {
        var vm = await WithTwoVideos();
        vm.ClickAt(1_000);
        vm.Delete();
        var output = Path.Combine(_dir, "out.mp4");
        Assert.Equal(output, await vm.ExportAsync(output));
        Assert.Equal(output, vm.ExportedPath);
        Assert.Single(_renderer.Rendered!.Tracks.Single(t => t.Kind == "video").Clips);
        Assert.Equal(1, vm.ExportProgress);
    }

    [Fact]
    public async Task AFailedExportLeavesNoFileAndSaysWhy()
    {
        var vm = await WithTwoVideos();
        _renderer.Fail = true;
        var output = Path.Combine(_dir, "out.mp4");
        Assert.Null(await vm.ExportAsync(output));
        Assert.False(File.Exists(output));
        Assert.Equal("studio.editor.error.export", vm.ErrorKey);
        Assert.Null(vm.ExportedPath);
        Assert.False(vm.IsExporting);
    }

    private sealed class FakeProbe : IMediaProbe
    {
        public Task<MediaSourceInfo> ProbeAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(path.EndsWith(".m4a", StringComparison.Ordinal)
                ? new MediaSourceInfo { DurationMs = 5_000, HasAudio = true, AudioCodec = "aac", SampleRate = 48000, Channels = 2 }
                : new MediaSourceInfo
                {
                    DurationMs = path.Contains("walk", StringComparison.Ordinal) ? 6_000 : 10_000,
                    Width = 1280, Height = 720, Fps = 30, HasVideo = true, VideoCodec = "h264",
                    HasAudio = true, AudioCodec = "aac", SampleRate = 48000, Channels = 2,
                });
    }

    private sealed class FakeRenderer : ITimelineRenderer
    {
        public FlattenedTimeline? Rendered { get; private set; }

        public bool Fail { get; set; }

        public Task RenderAsync(FlattenedTimeline timeline, ProjectSettings settings, string output, IProgress<double> progress, CancellationToken cancellationToken)
        {
            File.WriteAllBytes(output, new byte[] { 1, 2, 3 });
            if (Fail) throw new InvalidOperationException("encoder missing");
            Rendered = timeline;
            progress.Report(0.5);
            return Task.CompletedTask;
        }
    }
}
