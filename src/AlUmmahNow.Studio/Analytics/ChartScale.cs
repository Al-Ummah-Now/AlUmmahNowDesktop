namespace AlUmmahNow.Studio.Analytics;

/// <summary>
/// Value-axis maths for the analytics chart, shared by Windows and Linux: a 0-based axis on round
/// steps (1, 2, 2.5, 5 × 10ⁿ), the same scale as the web's Studio charts (shared-types chart-scale.ts).
/// </summary>
public static class ChartScale
{
    private static readonly double[] Steps = { 1, 2, 2.5, 5, 10 };

    /// <summary>Ticks from 0 to at least <paramref name="max"/>. Counts (<paramref name="integer"/>) get whole steps.</summary>
    public static IReadOnlyList<double> Ticks(double max, bool integer, int target = 4)
    {
        if (double.IsNaN(max) || max <= 0) return new double[] { 0, 1 };
        var raw = max / target;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = magnitude * Steps.First(s => s * magnitude >= raw);
        if (integer) step = Math.Max(1, Math.Ceiling(step));
        var count = Math.Max(1, (int)Math.Ceiling((max / step) - 1e-9));
        return Enumerable.Range(0, count + 1).Select(i => Math.Round(i * step, 9)).ToList();
    }

    /// <summary>Indices to label on the time axis: the first, the middle and the last.</summary>
    public static IReadOnlyList<int> LabelIndices(int count) =>
        count <= 0 ? Array.Empty<int>()
        : count <= 2 ? Enumerable.Range(0, count).ToList()
        : new[] { 0, (count - 1) / 2, count - 1 };
}

/// <summary>One tick on the value axis: its value and the y of its gridline.</summary>
public readonly record struct ChartTick(double Value, double Y);

/// <summary>One day's bar; <see cref="Index"/> is the day's index in the series.</summary>
public readonly record struct ChartBar(int Index, double X, double Y, double Width, double Height, double SlotX, double SlotWidth);

/// <summary>
/// Where everything of the daily bar chart goes in a <c>width × height</c> area: the value axis on the
/// start side (right in Arabic), gridlines, bars running from the axis side, and the date labels below.
/// </summary>
public sealed class ChartLayout
{
    public const double AxisWidth = 52;
    public const double TopPad = 8;
    public const double DateRow = 22;
    private const double Gap = 2;

    private ChartLayout(double plotLeft, double plotRight, double plotTop, double plotBottom, bool rtl, IReadOnlyList<ChartTick> ticks, IReadOnlyList<ChartBar> bars, IReadOnlyList<(int Index, double X)> dates)
    {
        PlotLeft = plotLeft;
        PlotRight = plotRight;
        PlotTop = plotTop;
        PlotBottom = plotBottom;
        Rtl = rtl;
        Ticks = ticks;
        Bars = bars;
        Dates = dates;
    }

    public double PlotLeft { get; }
    public double PlotRight { get; }
    public double PlotTop { get; }
    public double PlotBottom { get; }
    public bool Rtl { get; }

    /// <summary>The x where tick labels end (LTR, right-aligned) or start (RTL, left-aligned).</summary>
    public double AxisLabelEdge => Rtl ? PlotRight + 6 : PlotLeft - 6;

    public IReadOnlyList<ChartTick> Ticks { get; }
    public IReadOnlyList<ChartBar> Bars { get; }

    /// <summary>Date labels: the series index and the centre x of its bar.</summary>
    public IReadOnlyList<(int Index, double X)> Dates { get; }

    public bool Empty => Bars.All(b => b.Height <= 0);

    public static ChartLayout Compute(double width, double height, IReadOnlyList<double> values, bool integer, bool rtl)
    {
        var plotLeft = rtl ? 0 : AxisWidth;
        var plotRight = rtl ? width - AxisWidth : width;
        var plotTop = TopPad;
        var plotBottom = height - DateRow;
        var plotHeight = Math.Max(1, plotBottom - plotTop);
        var ticks = ChartScale.Ticks(values.Count == 0 ? 0 : values.Max(), integer);
        var top = ticks[^1];
        double Y(double v) => plotBottom - (v / top * plotHeight);

        var n = values.Count;
        var slot = n == 0 ? 0 : (plotRight - plotLeft) / n;
        var gap = Math.Min(Gap, slot / 4);
        var bars = new List<ChartBar>(n);
        for (var i = 0; i < n; i++)
        {
            // Time runs from the axis side: right to left in Arabic
            var position = rtl ? n - 1 - i : i;
            var slotX = plotLeft + (position * slot);
            var h = values[i] <= 0 ? 0 : Math.Max(1, values[i] / top * plotHeight);
            bars.Add(new ChartBar(i, slotX + (gap / 2), plotBottom - h, Math.Max(1, slot - gap), h, slotX, slot));
        }

        var dates = ChartScale.LabelIndices(n).Select(i => (i, bars[i].SlotX + (slot / 2))).ToList();
        return new ChartLayout(plotLeft, plotRight, plotTop, plotBottom, rtl, ticks.Select(t => new ChartTick(t, Y(t))).ToList(), bars, dates);
    }

    /// <summary>The series index under <paramref name="x"/>, or null outside the plot.</summary>
    public int? IndexAt(double x) =>
        Bars.FirstOrDefault(b => x >= b.SlotX && x < b.SlotX + b.SlotWidth) is { SlotWidth: > 0 } bar ? bar.Index : null;
}
