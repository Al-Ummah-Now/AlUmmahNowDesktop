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
    private readonly Grid _totals = new() { ColumnSpacing = 14 };
    private readonly Canvas _chart = new() { Height = 300, FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBlock _summary = new() { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _table = new();
    private readonly StackPanel _top = new() { Spacing = 4 };
    private readonly TextBlock _status = new() { Opacity = 0.8 };
    private Button? _picked;

    public AnalyticsPage(Localizer l, AnalyticsViewModel vm)
    {
        _l = l;
        _vm = vm;
        // Lantern Glass: metric tiles (Views and Watch time pick the chart), the chart card beside Top videos.
        Root = new StackPanel { Spacing = 18, Padding = new Thickness(16, 4, 16, 8) };
        var range = new ComboBox { Header = l["studio.analytics.range"], ItemsSource = AnalyticsViewModel.Ranges.Select(d => l.Format("studio.analytics.days", d)).ToList(), SelectedIndex = 1, MinWidth = 180 };
        range.SelectionChanged += (_, _) => _vm.Days = AnalyticsViewModel.Ranges[Math.Max(0, range.SelectedIndex)];
        Root.Children.Add(range);
        Root.Children.Add(_status);
        Root.Children.Add(_totals);

        var columns = new Grid { ColumnSpacing = 20 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        columns.SizeChanged += (_, e) => columns.ColumnDefinitions[1].Width = new GridLength(e.NewSize.Width >= 1500 ? 440 : e.NewSize.Width < 900 ? 300 : 380);
        var chartBody = new StackPanel { Spacing = 10 };
        chartBody.Children.Add(Heading(l["studio.analytics.daily"]));
        AutomationProperties.SetAccessibilityView(_chart, AccessibilityView.Raw);
        _chart.SizeChanged += (_, _) => DrawChart();
        chartBody.Children.Add(_chart);
        chartBody.Children.Add(_summary);
        chartBody.Children.Add(new Expander { Header = l["studio.analytics.table"], Content = _table, HorizontalAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) });
        var chartCard = LanternGlass.Card(chartBody);
        chartCard.VerticalAlignment = VerticalAlignment.Top;
        columns.Children.Add(chartCard);
        var topBody = new StackPanel { Spacing = 8 };
        topBody.Children.Add(Heading(l["studio.analytics.top"]));
        topBody.Children.Add(_top);
        var topCard = LanternGlass.Card(topBody);
        topCard.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(topCard, 1);
        columns.Children.Add(topCard);
        Root.Children.Add(columns);
        _vm.PropertyChanged += (_, _) => Render();
        Render();
    }

    public StackPanel Root { get; }

    /// <summary>The pointer is over the picked card (CI's scenario "cardhover" asks).</summary>
    public bool PickedIsHovered { get; private set; }

    /// <summary>The text of the picked card can be read on what is drawn behind it; null with no card on screen (CI's scenarios ask).</summary>
    public bool? PickedIsReadable => _picked is not { IsLoaded: true, ActualHeight: > 0 } card ? null
        : LanternGlass.Presenter(card) is { } drawn && ((StackPanel)card.Content).Children.OfType<TextBlock>().All(t => LanternGlass.IsReadable(t.Foreground, drawn.Background));

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
        _totals.ColumnDefinitions.Clear();
        if (_vm.Data is { } d)
        {
            Card(_l["studio.analytics.views"], _l.Stat(d.Totals.Views), "views");
            Card(_l["studio.analytics.watchHours"], _l.Stat(d.Totals.WatchTimeMinutes / 60, decimals: true), "watch");
            Card(_l["studio.analytics.likes"], _l.Stat(d.Totals.Likes));
            Card(_l["studio.analytics.comments"], _l.Stat(d.Totals.Comments));
            Card(_l["studio.analytics.subscribers"], _l.Stat(d.Totals.Subscribers));
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
        var rank = 0;
        foreach (var v in _vm.Data?.TopVideos ?? Array.Empty<CoreBridge.Studio.TopVideo>())
        {
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = _l.Number(++rank), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = LanternGlass.Res("StudioMutedBrush"), VerticalAlignment = VerticalAlignment.Center });
            // The video's thumbnail when the API gives one (clipped by the corner radius as a brush), else the night tile.
            var hasThumb = Uri.TryCreate(v.ThumbnailUrl, UriKind.Absolute, out var thumbUrl);
            var tile = new Border
            {
                Width = 72,
                Height = 40,
                CornerRadius = new CornerRadius(6),
                Background = hasThumb
                    ? new ImageBrush { ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(thumbUrl) { DecodePixelWidth = 144 }, Stretch = Stretch.UniformToFill }
                    : new SolidColorBrush(global::Windows.UI.Color.FromArgb(0xFF, 0x18, 0x44, 0x56)),
                Child = hasThumb ? null : new FontIcon { Glyph = "\uE8B2", FontSize = 16, Foreground = new SolidColorBrush(LanternGlass.Gold) },
            };
            Grid.SetColumn(tile, 1);
            row.Children.Add(tile);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            // The page's direction orders and aligns each line, never the title's script: an Arabic title on the
            // English page stays at the start, and the views line never turns around. Titles wrap to two lines
            // (the card is narrow at the 1024 window).
            text.Children.Add(new TextBlock { Text = Localizer.Isolate(v.TitleFor(_l.Language)), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.WrapWholeWords, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, TextReadingOrder = TextReadingOrder.UseFlowDirection });
            text.Children.Add(new TextBlock { Text = _l.Views(v.Views), FontSize = 12, Foreground = LanternGlass.Res("StudioMutedBrush"), TextReadingOrder = TextReadingOrder.UseFlowDirection });
            Grid.SetColumn(text, 2);
            row.Children.Add(text);
            _top.Children.Add(row);
        }
    }

    /// <summary>A metric tile; Views and Watch time are buttons that pick the chart's metric (the picked one glows gold).</summary>
    private void Card(string label, string value, string? metric = null)
    {
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(new TextBlock { Text = label, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = LanternGlass.Res("StudioMutedBrush") });
        content.Children.Add(new TextBlock { Text = value, FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = LanternGlass.Res("StudioTextBrush") });
        var on = metric != null && _vm.Metric == metric;
        FrameworkElement inner;
        if (metric != null)
        {
            var b = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(14), Background = LanternGlass.Res(on ? "StudioPickedCardBrush" : "StudioCardBrush"), BorderBrush = LanternGlass.Res(on ? "StudioPickedEdgeBrush" : "StudioEdgeBrush"), BorderThickness = new Thickness(on ? 2 : 1) };
            // Under the pointer the card stays as it is, with a stronger edge (the framework's fill lets the glow through)
            foreach (var state in new[] { "PointerOver", "Pressed" })
            {
                b.Resources["ButtonBackground" + state] = b.Background;
                b.Resources["ButtonBorderBrush" + state] = LanternGlass.Res(on ? "StudioPickedEdgeBrush" : "StudioEdgeStrongBrush");
            }

            b.Click += (_, _) => _vm.Metric = metric;
            b.PointerEntered += (_, _) => PickedIsHovered = on;
            b.PointerExited += (_, _) => PickedIsHovered = false;
            if (on) _picked = b;
            AutomationProperties.SetName(b, _l.Format("studio.analytics.readout", _l["studio.analytics.metric"], label) + ", " + value);
            inner = b;
        }
        else
        {
            inner = new Border { Child = content, Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(14), Background = LanternGlass.Res("StudioCardBrush"), BorderBrush = LanternGlass.Res("StudioEdgeBrush"), BorderThickness = new Thickness(1) };
            AutomationProperties.SetName(inner, label + ": " + value);
        }

        var card = LanternGlass.WithGlow(inner, 14, on ? 0.5f : 0.2f, on ? 30f : 20f);
        _totals.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(card, _totals.Children.Count);
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
