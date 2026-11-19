using AlUmmahNow.Studio.Analytics;

namespace AlUmmahNow.Studio.Linux;

/// <summary>
/// A bar chart drawn with the shared <see cref="ChartLayout"/>: 0-based axis on the start side, dashed gridlines,
/// first/middle/last labels below and the hovered bar in the tooltip. A text summary sits next to it for screen readers.
/// </summary>
public sealed class BarChartView
{
    private readonly bool _rtl;
    private IReadOnlyList<double> _values = Array.Empty<double>();
    private IReadOnlyList<string> _labels = Array.Empty<string>();
    private Func<double, string> _axis = v => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private Func<int, string> _readout = _ => "";
    private bool _integer = true;
    private string _empty = "";
    private ChartLayout? _layout;

    public BarChartView(bool rtl, int height = 200)
    {
        _rtl = rtl;
        Area = Gtk.DrawingArea.New();
        Area.SetHexpand(true);
        Area.SetContentHeight(height);
        Area.SetDrawFunc((_, cr, w, h) => Draw(cr, w, h));
        var motion = Gtk.EventControllerMotion.New();
        motion.OnMotion += (_, e) =>
        {
            var i = _layout?.IndexAt(e.X);
            Area.SetTooltipText(i is { } x && x < _values.Count ? _readout(x) : null);
        };
        Area.AddController(motion);
        Area.SetHasTooltip(true);
    }

    public Gtk.DrawingArea Area { get; }

    public void Set(IReadOnlyList<double> values, IReadOnlyList<string> labels, bool integer, Func<double, string> axis, Func<int, string> readout, string empty)
    {
        _values = values;
        _labels = labels;
        _integer = integer;
        _axis = axis;
        _readout = readout;
        _empty = empty;
        Area.QueueDraw();
    }

    private void Draw(Cairo.Context cr, int w, int h)
    {
        if (_values.Count == 0)
        {
            _layout = null;
            return;
        }

        var layout = ChartLayout.Compute(w, h, _values, _integer, _rtl);
        _layout = layout;
        Area.GetColor(out var fg);
        cr.LineWidth = 1;
        foreach (var tick in layout.Ticks)
        {
            var y = Math.Round(tick.Y) + 0.5;
            cr.SetSourceRgba(fg.Red, fg.Green, fg.Blue, tick.Value == 0 ? 0.6 : 0.18);
            cr.SetDash(tick.Value == 0 ? Array.Empty<double>() : new double[] { 4, 4 }, 0);
            cr.MoveTo(layout.PlotLeft, y);
            cr.LineTo(layout.PlotRight, y);
            cr.Stroke();
            Text(cr, fg, _axis(tick.Value), layout.AxisLabelEdge, tick.Y, layout.Rtl ? 0 : 1, 0.5);
        }

        cr.SetDash(Array.Empty<double>(), 0);
        cr.SetSourceRgb(1, 0.627, 0.216);
        foreach (var bar in layout.Bars.Where(b => b.Height > 0)) cr.Rectangle(bar.X, bar.Y, bar.Width, bar.Height);
        cr.Fill();

        foreach (var (index, x) in layout.Dates)
        {
            if (index >= _labels.Count) continue;
            var align = index == 0 ? (layout.Rtl ? 1 : 0) : index == _values.Count - 1 ? (layout.Rtl ? 0 : 1) : 0.5;
            Text(cr, fg, _labels[index], x, layout.PlotBottom + 4, align, 0);
        }

        if (layout.Empty) Text(cr, fg, _empty, (layout.PlotLeft + layout.PlotRight) / 2, (layout.PlotTop + layout.PlotBottom) / 2, 0.5, 0.5);
    }

    private void Text(Cairo.Context cr, Gdk.RGBA fg, string text, double x, double y, double alignX, double alignY)
    {
        var layout = Area.CreatePangoLayout(text);
        layout.GetPixelSize(out var tw, out var th);
        cr.SetSourceRgba(fg.Red, fg.Green, fg.Blue, 0.75);
        cr.MoveTo(x - (tw * alignX), y - (th * alignY));
        PangoCairo.Functions.ShowLayout(cr, layout);
    }
}
