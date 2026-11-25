using AlUmmahNow.App.Demo;
using Xunit;

namespace AlUmmahNow.App.Tests;

public sealed class AutomationEndTests
{
    private static readonly DateTimeOffset Over = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_scenario_that_waits_for_nothing_ends_by_its_own_time(string? file) =>
        Assert.False(new AutomationEnd(file, () => Over).Waits());

    [Fact]
    public void It_waits_for_the_script()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var now = Over;
            var file = Path.Combine(dir, "end");
            var end = new AutomationEnd(file, () => now);
            Assert.True(end.Waits());
            now = Over.AddSeconds(30);
            Assert.True(end.Waits());
            File.WriteAllText(file, "");
            Assert.False(end.Waits());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void It_does_not_wait_longer_than_the_limit()
    {
        var now = Over;
        var end = new AutomationEnd(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), () => now);
        Assert.True(end.Waits());
        now = Over + AutomationEnd.Limit - TimeSpan.FromSeconds(1);
        Assert.True(end.Waits());
        now = Over + AutomationEnd.Limit;
        Assert.False(end.Waits());
    }
}
