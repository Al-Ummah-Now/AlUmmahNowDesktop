using AlUmmahNow.Studio.Analytics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>
/// A bar chart drawn with the shared <see cref="ChartLayout"/>: 0-based axis on the start side, dashed gridlines,
/// first/middle/last labels below and a tooltip per bar. The canvas is hidden from Narrator; pages put a text
/// summary next to it.
/// </summary>
internal sealed class BarChart
{
    private readonly bool _rtl;
    private IReadOnlyList<double> _values = Array.Empty<double>();
    private IReadOnlyList<string> _labels = Array.Empty<string>();
    private Func<double, string> _axis = v => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private Func<int, string> _readout = _ => "";
    private bool _integer = true;
    private string _empty = "";

    public BarChart(bool rtl, double height = 222)
    {
        _rtl = rtl;
        View = new Canvas { Height = height, FlowDirection = FlowDirection.LeftToRight };
        AutomationProperties.SetAccessibilityView(View, AccessibilityView.Raw);
        View.SizeChanged += (_, _) => Draw();
    }

    public Canvas View { get; }

    public void Set(IReadOnlyList<double> values, IReadOnlyList<string> labels, bool integer, Func<double, string> axis, Func<int, string> readout, string empty)
    {
        _values = values;
        _labels = labels;
        _integer = integer;
        _axis = axis;
        _readout = readout;
        _empty = empty;
        Draw();
    }

    private void Draw()
    {
        View.Children.Clear();
        if (_values.Count == 0) return;
        var w = View.ActualWidth > 0 ? View.ActualWidth : 900;
        var layout = ChartLayout.Compute(w, View.Height, _values, _integer, _rtl);
        var text = Ui.Brush("TextFillColorSecondaryBrush");
        var grid = Ui.Brush("DividerStrokeColorDefaultBrush");
        var baseline = Ui.Brush("ControlStrongStrokeColorDefaultBrush");
        var accent = Ui.Brush("AccentFillColorDefaultBrush");

        foreach (var tick in layout.Ticks)
        {
            var line = new Line { X1 = layout.PlotLeft, X2 = layout.PlotRight, Y1 = tick.Y, Y2 = tick.Y, Stroke = tick.Value == 0 ? baseline : grid, StrokeThickness = 1 };
            if (tick.Value != 0) line.StrokeDashArray = new DoubleCollection { 4, 4 };
            View.Children.Add(line);
            var label = Label(_axis(tick.Value), text);
            label.Width = ChartLayout.AxisWidth - 8;
            label.TextAlignment = layout.Rtl ? TextAlignment.Left : TextAlignment.Right;
            Canvas.SetLeft(label, layout.Rtl ? layout.AxisLabelEdge : layout.AxisLabelEdge - label.Width);
            Canvas.SetTop(label, tick.Y - 9);
            View.Children.Add(label);
        }

        foreach (var bar in layout.Bars)
        {
            if (bar.Height > 0)
            {
                var rect = new Rectangle { Width = bar.Width, Height = bar.Height, Fill = accent, RadiusX = 2, RadiusY = 2 };
                Canvas.SetLeft(rect, bar.X);
                Canvas.SetTop(rect, bar.Y);
                View.Children.Add(rect);
            }

            var hit = new Rectangle { Width = bar.SlotWidth, Height = layout.PlotBottom - layout.PlotTop, Fill = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0)) };
            ToolTipService.SetToolTip(hit, _readout(bar.Index));
            Canvas.SetLeft(hit, bar.SlotX);
            Canvas.SetTop(hit, layout.PlotTop);
            View.Children.Add(hit);
        }

        foreach (var (index, x) in layout.Dates)
        {
            if (index >= _labels.Count) continue;
            var label = Label(_labels[index], text);
            label.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var width = label.DesiredSize.Width;
            var first = index == 0;
            var last = index == _values.Count - 1;
            var left = first ? (layout.Rtl ? x - width : x) : last ? (layout.Rtl ? x : x - width) : x - (width / 2);
            Canvas.SetLeft(label, Math.Clamp(left, 0, Math.Max(0, w - width)));
            Canvas.SetTop(label, layout.PlotBottom + 3);
            View.Children.Add(label);
        }

        if (layout.Empty)
        {
            var empty = Label(_empty, text);
            empty.Width = layout.PlotRight - layout.PlotLeft;
            empty.TextAlignment = TextAlignment.Center;
            Canvas.SetLeft(empty, layout.PlotLeft);
            Canvas.SetTop(empty, ((layout.PlotTop + layout.PlotBottom) / 2) - 10);
            View.Children.Add(empty);
        }
    }

    private static TextBlock Label(string value, Brush brush) => new() { Text = value, FontSize = 12, Foreground = brush };
}
