using AlUmmahNow.Studio.Analytics;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

/// <summary>The analytics chart's value axis (owner report 2026-11-11: "views chart only has time axis").</summary>
public sealed class ChartTests
{
    [Fact]
    public void TicksStartAtZeroOnRoundStepsAboveTheLargestValue()
    {
        Assert.Equal(new double[] { 0, 2000, 4000, 6000, 8000 }, ChartScale.Ticks(6075, integer: true));
        Assert.Equal(new double[] { 0, 1, 2, 3 }, ChartScale.Ticks(3, integer: true));
        Assert.Equal(new double[] { 0, 0.5, 1, 1.5, 2 }, ChartScale.Ticks(1.6, integer: false));
    }

    [Fact]
    public void AnEmptySeriesStillHasAnAxis()
    {
        Assert.Equal(new double[] { 0, 1 }, ChartScale.Ticks(0, integer: true));
        var layout = ChartLayout.Compute(400, 200, new double[] { 0, 0, 0 }, integer: true, rtl: false);
        Assert.True(layout.Empty);
        Assert.Equal(2, layout.Ticks.Count);
    }

    [Fact]
    public void TheAxisIsOnTheLeftAndTimeRunsRightInEnglish()
    {
        var layout = ChartLayout.Compute(452, 200, new double[] { 10, 40 }, integer: true, rtl: false);
        Assert.Equal(ChartLayout.AxisWidth, layout.PlotLeft);
        Assert.True(layout.Bars[0].X < layout.Bars[1].X);
        Assert.Equal(layout.PlotBottom, layout.Ticks[0].Y);
        Assert.Equal(layout.PlotTop, layout.Ticks[^1].Y, 6);
        Assert.Equal(new[] { 0, 1 }, layout.Dates.Select(d => d.Index));
    }

    [Fact]
    public void TheAxisIsOnTheRightAndTimeRunsLeftInArabic()
    {
        var layout = ChartLayout.Compute(452, 200, new double[] { 10, 40, 5 }, integer: true, rtl: true);
        Assert.Equal(452 - ChartLayout.AxisWidth, layout.PlotRight);
        Assert.True(layout.Bars[0].X > layout.Bars[2].X);
        Assert.True(layout.AxisLabelEdge > layout.PlotRight);
        Assert.Equal(0, layout.IndexAt(layout.PlotRight - 1));
        Assert.Equal(2, layout.IndexAt(1));
    }

    [Fact]
    public void BarsAreScaledToTheTopTick()
    {
        var layout = ChartLayout.Compute(452, 222, new double[] { 2000, 6075 }, integer: true, rtl: false);
        // Ticks 0..8000 over a 192 px plot: 2000 is a quarter of it
        Assert.Equal(8000, layout.Ticks[^1].Value);
        Assert.Equal(48, layout.Bars[0].Height, 6);
    }
}
