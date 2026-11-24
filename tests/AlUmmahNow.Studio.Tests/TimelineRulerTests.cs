using AlUmmahNow.Studio.Timeline;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

public sealed class TimelineRulerTests
{
    [Fact]
    public void ThePlayheadIsUnderTheLabelsAndOverTheTracks()
    {
        // A label is a line of 11 to 13 px text
        Assert.True(TimelineRuler.LabelRoom >= 20);
        Assert.True(TimelineRuler.LabelRoom + TimelineRuler.HandleHeight <= TimelineRuler.Height);
        Assert.True(TimelineRuler.Height - TimelineRuler.Tick >= TimelineRuler.LabelRoom);
    }

    /// <summary>Both systems draw from the label room down, and nothing of the playhead from the top.</summary>
    [Theory]
    [InlineData("AlUmmahNow.Studio.Linux", "TimelinePage.cs", "const double head = TimelineRuler.LabelRoom;", "px - 1, head, 2, h - head", "cr.MoveTo(px - 7, head)")]
    [InlineData("AlUmmahNow.Studio.Windows", "Pages.cs", "const double under = TimelineRuler.LabelRoom;", "Canvas.SetTop(head, under)", "Point(px - 7, under)")]
    public void BothShellsDrawItThere(string project, string file, params string[] parts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "AlUmmahNow.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", project, file));
        foreach (var part in parts) Assert.Contains(part, source);
    }
}
