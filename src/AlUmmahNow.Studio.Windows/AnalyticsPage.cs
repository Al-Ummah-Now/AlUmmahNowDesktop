using System.Globalization;
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
    private readonly Canvas _chart = new() { Height = 200, FlowDirection = FlowDirection.LeftToRight };
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
            Card(_l["studio.analytics.watchHours"], _l.Number((long)Math.Round(d.Totals.WatchTimeMinutes / 60)));
            Card(_l["studio.analytics.likes"], _l.Number(d.Totals.Likes));
            Card(_l["studio.analytics.comments"], _l.Number(d.Totals.Comments));
            Card(_l["studio.analytics.subscribers"], _l.Number(d.Totals.Subscribers));
        }

        var s = _vm.Series;
        _table.Children.Clear();
        foreach (var p in s) _table.Children.Add(new TextBlock { Text = p.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "    " + p.Value.ToString("0.#", CultureInfo.InvariantCulture) });
        if (s.Count > 0)
        {
            var max = s.OrderByDescending(p => p.Value).First();
            _summary.Text = _l.Format("studio.analytics.chartSummary",
                _vm.Metric == "watch" ? _l["studio.analytics.watchHours"] : _l["studio.analytics.views"],
                s[0].Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), s[^1].Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                max.Value.ToString("0.#", CultureInfo.InvariantCulture), max.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
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

    private void DrawChart()
    {
        _chart.Children.Clear();
        var s = _vm.Series;
        var w = _chart.ActualWidth > 0 ? _chart.ActualWidth : 900;
        if (s.Count == 0) return;
        var max = Math.Max(1, s.Max(p => p.Value));
        const double gap = 2;
        var bar = Math.Max(1, (w - (gap * (s.Count - 1))) / s.Count);
        var brush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 8, 102, 120));
        for (var i = 0; i < s.Count; i++)
        {
            var index = _l.IsRtl ? s.Count - 1 - i : i;
            var height = (_chart.Height - 10) * (s[index].Value / max);
            var rect = new Rectangle { Width = bar, Height = Math.Max(1, height), Fill = brush };
            Canvas.SetLeft(rect, i * (bar + gap));
            Canvas.SetTop(rect, _chart.Height - height);
            _chart.Children.Add(rect);
        }
    }
}
