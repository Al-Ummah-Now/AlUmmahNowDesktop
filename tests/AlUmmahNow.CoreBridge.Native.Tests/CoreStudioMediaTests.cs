using AlUmmahNow.CoreBridge.Native.Studio;
using AlUmmahNow.CoreBridge.Studio;
using Xunit;

namespace AlUmmahNow.CoreBridge.Native.Tests;

/// <summary>The Studio media adapters against the real native Core (no network, no media files).</summary>
public sealed class CoreStudioMediaTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aun-core-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    internal const string Project = """
    {
      "schemaVersion": 1, "id": "p1",
      "metadata": { "title": "Demo", "createdAtMs": 0, "updatedAtMs": 0 },
      "settings": { "width": 1280, "height": 720, "fps": 30.0, "sampleRate": 48000, "channels": 2 },
      "sources": [ { "id": "cam", "path": "/media/cam.mp4", "durationMs": 60000,
        "video": { "codec": "h264", "width": 1280, "height": 720, "fps": 30.0 },
        "audio": { "codec": "aac", "sampleRate": 48000, "channels": 2 } } ],
      "tracks": [ { "id": "v1", "kind": "video", "clips": [
        { "id": "a", "sourceId": "cam", "inMs": 0, "outMs": 10000, "timelineStartMs": 0 },
        { "id": "b", "sourceId": "cam", "inMs": 12000, "outMs": 20000, "timelineStartMs": 10000 } ] } ]
    }
    """;

    [Fact]
    public void LadderIsTheServersAndNeverUpscales()
    {
        var ladder = new CoreRenditionPackaging().Ladder(1280, 720, 0);
        Assert.NotEmpty(ladder);
        Assert.All(ladder, r =>
        {
            Assert.True(r.Height <= 720 && r.Width <= 1280, r.Name);
            Assert.True(r.Maxrate > 0 && r.Bufsize > 0, r.Name);
            Assert.Equal(2, r.GopSeconds);
        });
        Assert.Equal(ladder.Select(r => r.Name).Distinct().Count(), ladder.Count);
    }

    [Fact]
    public void PortraitLadderFollowsRotation()
    {
        var ladder = new CoreRenditionPackaging().Ladder(1920, 1080, 90);
        Assert.All(ladder, r => Assert.True(r.Height >= r.Width, r.Name));
    }

    [Fact]
    public void RenditionFilesListsEachRungFolder()
    {
        var root = Path.Combine(_dir, "hls");
        Directory.CreateDirectory(Path.Combine(root, "360p"));
        File.WriteAllText(Path.Combine(root, "360p", "index.m3u8"), "#EXTM3U\n");
        File.WriteAllBytes(Path.Combine(root, "360p", "init.mp4"), new byte[12]);
        var files = new CoreRenditionPackaging().Files(root, new[] { "360p" });
        Assert.Contains(new StagedFile("360p/init.mp4", 12), files);
        Assert.Contains(files, f => f.Path == "360p/index.m3u8");
    }

    [Fact]
    public void EditsGoThroughCoreAndFlattenInTimelineOrder()
    {
        var edl = new CoreEdlEditing();
        var split = edl.Apply(Project, EdlOp.Split("a", 4000, "a2"));
        var flat = edl.Flatten(split);
        Assert.Equal(new[] { "a", "a2", "b" }, flat.Tracks.Single().Clips.Select(c => c.ClipId));
        Assert.Equal(18000, flat.DurationMs);
        Assert.Equal("/media/cam.mp4", flat.Tracks[0].Clips[1].Path);

        var deleted = edl.Flatten(edl.Apply(split, EdlOp.RippleDeleteClip("a2")));
        Assert.Equal(12000, deleted.DurationMs);
        Assert.Equal(4000, deleted.Tracks[0].Clips[1].TimelineStartMs);

        var joined = edl.Flatten(edl.Apply(split, EdlOp.Join("a", "a2")));
        Assert.Equal(new[] { "a", "b" }, joined.Tracks[0].Clips.Select(c => c.ClipId));
        Assert.Empty(edl.Validate(split));
    }

    [Fact]
    public void RefusedEditsSayWhyAndChangeNothing()
    {
        var edl = new CoreEdlEditing();
        var outside = Assert.Throws<EdlEditException>(() => edl.Apply(Project, EdlOp.Split("a", 15000, "x")));
        Assert.Equal(EdlErrorKind.InvalidInput, outside.Kind);
        Assert.Equal(EdlErrorKind.NotFound, Assert.Throws<EdlEditException>(() => edl.Apply(Project, EdlOp.RippleDeleteClip("nope"))).Kind);
        // Landing on another clip is refused; Core reports it as invalid input with the reason.
        var overlap = Assert.Throws<EdlEditException>(() => edl.Apply(Project, EdlOp.Move("b", "v1", 5000)));
        Assert.DoesNotContain("@detail", overlap.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TranscriberListsTheCatalogWithNothingInstalled()
    {
        using var t = new CoreOnDeviceTranscriber(Path.Combine(_dir, "models"));
        var models = t.Models();
        Assert.NotEmpty(models);
        Assert.All(models, m =>
        {
            Assert.False(m.Installed);
            Assert.True(m.SizeBytes > 0);
        });
    }

    [Fact]
    public async Task TranscribingWithAModelThatIsNotDownloadedFails()
    {
        using var t = new CoreOnDeviceTranscriber(Path.Combine(_dir, "models"));
        var id = t.Models().First().Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => t.TranscribeAsync(id, "/no/such.mp4", "ar", new Progress<double>(), CancellationToken.None));
    }

    [Fact]
    public void SegmentsParseAndDropEmptyOrBackwardsOnes()
    {
        var s = CoreOnDeviceTranscriber.ParseSegments("""[{"startMs":0,"endMs":1500,"text":" السلام عليكم "},{"startMs":2000,"endMs":1000,"text":"x"},{"startMs":3000,"endMs":4000,"text":""}]""");
        Assert.Equal(new[] { new TranscriptSegment(0, 1500, "السلام عليكم") }, s);
    }
}
