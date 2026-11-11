using AlUmmahNow.App.I18n;
using AlUmmahNow.Studio.Analytics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>Channel analytics: totals, a daily bar chart with a text summary and a table, top videos.</summary>
internal sealed class AnalyticsPage
{
    private readonly Localizer _l;
    private readonly AnalyticsViewModel _vm;
    private readonly StackPanel _totals = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
    private readonly Canvas _chart = new() { Height = 222, FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBlock _summary = new() { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _table = new();
    private readonly StackPanel _top = new() { Spacing = 4 };
    private readonly TextBlock _status = new() { Opacity = 0.8 };

    public AnalyticsPage(Localizer l, AnalyticsViewModel vm)
    {
        _l = l;
        _vm = vm;
        Root = new StackPanel { Spacing = 14, Padding = new Thickness(20) };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var range = new ComboBox { Header = l["studio.analytics.range"], ItemsSource = AnalyticsViewModel.Ranges.Select(d => l.Format("studio.analytics.days", d)).ToList(), SelectedIndex = 1 };
        range.SelectionChanged += (_, _) => _vm.Days = AnalyticsViewModel.Ranges[Math.Max(0, range.SelectedIndex)];
        var metrics = new[] { "views", "watch" };
        var metric = new ComboBox { Header = l["studio.analytics.metric"], ItemsSource = new[] { l["studio.analytics.views"], l["studio.analytics.watchHours"] }, SelectedIndex = 0 };
        metric.SelectionChanged += (_, _) => _vm.Metric = metrics[Math.Max(0, metric.SelectedIndex)];
        controls.Children.Add(range);
        controls.Children.Add(metric);
        Root.Children.Add(controls);
        Root.Children.Add(_status);
        Root.Children.Add(_totals);
        Root.Children.Add(Heading(l["studio.analytics.daily"]));
        AutomationProperties.SetAccessibilityView(_chart, AccessibilityView.Raw);
        _chart.SizeChanged += (_, _) => DrawChart();
        Root.Children.Add(_chart);
        Root.Children.Add(_summary);
        Root.Children.Add(new Expander { Header = l["studio.analytics.table"], Content = _table, HorizontalAlignment = HorizontalAlignment.Stretch });
        Root.Children.Add(Heading(l["studio.analytics.top"]));
        Root.Children.Add(_top);
        _vm.PropertyChanged += (_, _) => Render();
        Render();
    }

    public StackPanel Root { get; }

    private static TextBlock Heading(string text)
    {
        var t = new TextBlock { Text = text, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] };
        AutomationProperties.SetHeadingLevel(t, AutomationHeadingLevel.Level2);
        return t;
    }

    private void Render()
    {
        _status.Text = _vm.IsLoading ? _l["studio.shell.loading"] : _vm.ErrorKey is { } k ? _l[k] : _vm.Data is { Daily.Count: 0 } ? _l["studio.analytics.empty"] : "";
        _totals.Children.Clear();
        if (_vm.Data is { } d)
        {
            Card(_l["studio.analytics.views"], _l.Number(d.Totals.Views));
            Card(_l["studio.analytics.watchHours"], _l.Decimal(d.Totals.WatchTimeMinutes / 60));
            Card(_l["studio.analytics.likes"], _l.Number(d.Totals.Likes));
            Card(_l["studio.analytics.comments"], _l.Number(d.Totals.Comments));
            Card(_l["studio.analytics.subscribers"], _l.Number(d.Totals.Subscribers));
        }

        var s = _vm.Series;
        _table.Children.Clear();
        foreach (var p in s) _table.Children.Add(new TextBlock { Text = _l.ShortDate(p.Date) + "    " + ValueLabel(p.Value) });
        if (s.Count > 0)
        {
            var max = s.OrderByDescending(p => p.Value).First();
            _summary.Text = _l.Format("studio.analytics.chartSummary",
                _vm.Metric == "watch" ? _l["studio.analytics.watchHours"] : _l["studio.analytics.views"],
                _l.ShortDate(s[0].Date), _l.ShortDate(s[^1].Date), ValueLabel(max.Value), _l.ShortDate(max.Date));
        }
        else
        {
            _summary.Text = "";
        }

        DrawChart();
        _top.Children.Clear();
        foreach (var v in _vm.Data?.TopVideos ?? Array.Empty<CoreBridge.Studio.TopVideo>())
        {
            _top.Children.Add(new TextBlock { Text = v.Title + " · " + _l.Format("studio.videos.views", _l.Number(v.Views)) });
        }
    }

    private void Card(string label, string value)
    {
        var card = new StackPanel { Padding = new Thickness(14, 10, 14, 10), MinWidth = 140, Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"], CornerRadius = new CornerRadius(6) };
        card.Children.Add(new TextBlock { Text = value, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        card.Children.Add(new TextBlock { Text = label, Opacity = 0.8 });
        AutomationProperties.SetName(card, label + ": " + value);
        _totals.Children.Add(card);
    }

    /// <summary>
    /// Bars with a 0-based value axis and dashed gridlines on the start side (right in Arabic), the
    /// first, middle and last dates below, and a tooltip with the day and value on each day's column.
    /// Colours are WinUI theme brushes, so light, dark and high contrast all apply.
    /// </summary>
    private void DrawChart()
    {
        _chart.Children.Clear();
        var s = _vm.Series;
        var w = _chart.ActualWidth > 0 ? _chart.ActualWidth : 900;
        if (s.Count == 0) return;
        var layout = ChartLayout.Compute(w, _chart.Height, s.Select(p => p.Value).ToList(), integer: _vm.Metric != "watch", _l.IsRtl);
        var text = ThemeBrush("TextFillColorSecondaryBrush");
        var grid = ThemeBrush("DividerStrokeColorDefaultBrush");
        var baseline = ThemeBrush("ControlStrongStrokeColorDefaultBrush");
        var accent = ThemeBrush("AccentFillColorDefaultBrush");

        foreach (var tick in layout.Ticks)
        {
            var line = new Line { X1 = layout.PlotLeft, X2 = layout.PlotRight, Y1 = tick.Y, Y2 = tick.Y, Stroke = tick.Value == 0 ? baseline : grid, StrokeThickness = 1 };
            if (tick.Value != 0) line.StrokeDashArray = new DoubleCollection { 4, 4 };
            _chart.Children.Add(line);
            var label = Label(AxisLabel(tick.Value), text);
            label.Width = ChartLayout.AxisWidth - 8;
            label.TextAlignment = layout.Rtl ? TextAlignment.Left : TextAlignment.Right;
            Canvas.SetLeft(label, layout.Rtl ? layout.AxisLabelEdge : layout.AxisLabelEdge - label.Width);
            Canvas.SetTop(label, tick.Y - 9);
            _chart.Children.Add(label);
        }

        foreach (var bar in layout.Bars)
        {
            if (bar.Height > 0)
            {
                var rect = new Rectangle { Width = bar.Width, Height = bar.Height, Fill = accent, RadiusX = 2, RadiusY = 2 };
                Canvas.SetLeft(rect, bar.X);
                Canvas.SetTop(rect, bar.Y);
                _chart.Children.Add(rect);
            }

            // A transparent column per day carries the tooltip, so even a short bar is easy to hover
            var hit = new Rectangle { Width = bar.SlotWidth, Height = layout.PlotBottom - layout.PlotTop, Fill = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0)) };
            ToolTipService.SetToolTip(hit, _l.Format("studio.analytics.readout", _l.ShortDate(s[bar.Index].Date), ValueLabel(s[bar.Index].Value)));
            Canvas.SetLeft(hit, bar.SlotX);
            Canvas.SetTop(hit, layout.PlotTop);
            _chart.Children.Add(hit);
        }

        foreach (var (index, x) in layout.Dates)
        {
            var label = Label(_l.ShortDate(s[index].Date), text);
            label.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var width = label.DesiredSize.Width;
            var first = index == 0;
            var last = index == s.Count - 1;
            var left = first ? (layout.Rtl ? x - width : x) : last ? (layout.Rtl ? x : x - width) : x - (width / 2);
            Canvas.SetLeft(label, Math.Clamp(left, 0, Math.Max(0, w - width)));
            Canvas.SetTop(label, layout.PlotBottom + 3);
            _chart.Children.Add(label);
        }

        if (layout.Empty)
        {
            var empty = Label(_l[_vm.Metric == "watch" ? "studio.analytics.emptyWatch" : "studio.analytics.emptyViews"], text);
            empty.Width = layout.PlotRight - layout.PlotLeft;
            empty.TextAlignment = TextAlignment.Center;
            Canvas.SetLeft(empty, layout.PlotLeft);
            Canvas.SetTop(empty, ((layout.PlotTop + layout.PlotBottom) / 2) - 10);
            _chart.Children.Add(empty);
        }
    }

    private static Brush ThemeBrush(string key) => (Brush)Application.Current.Resources[key];

    private static TextBlock Label(string value, Brush brush) => new() { Text = value, FontSize = 12, Foreground = brush };

    private string AxisLabel(double value) => _vm.Metric == "watch" ? _l.Decimal(value) : _l.Compact(value);

    private string ValueLabel(double value) => _vm.Metric == "watch" ? _l.Decimal(value) : _l.Number((long)value);
}
