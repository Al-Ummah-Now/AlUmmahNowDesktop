using AlUmmahNow.App.I18n;
using AlUmmahNow.Studio.Analytics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>Analytics: channel or video, summary cards, daily chart and table, per-video table, retention, CSV export.</summary>
internal sealed class InsightsPage
{
    private readonly Localizer _l;
    private readonly InsightsViewModel _vm;
    private readonly Func<string, Task<string?>> _pickSave;
    private readonly ComboBox _scope;
    private readonly TextBlock _status = Ui.Text("", dim: true);
    private readonly Grid _cards = new() { ColumnSpacing = 12 };
    private readonly BarChart _daily;
    private readonly TextBlock _dailySummary = Ui.Text("", dim: true);
    private readonly Grid _dailyTable = new() { ColumnSpacing = 24, RowSpacing = 2 };
    private readonly Grid _videos = new() { ColumnSpacing = 20, RowSpacing = 6 };
    private readonly StackPanel _retentionBox = new() { Spacing = 6 };
    private readonly BarChart _retention;
    private readonly TextBlock _retentionSummary = Ui.Text("", dim: true);
    private readonly InfoBar _saved = Ui.Bar(InfoBarSeverity.Success);
    private readonly Button _exportRetention;
    private IReadOnlyList<string?> _scopeIds = Array.Empty<string?>();
    private bool _syncing;

    public InsightsPage(Localizer l, InsightsViewModel vm, Func<string, Task<string?>> pickSave)
    {
        _l = l;
        _vm = vm;
        _pickSave = pickSave;
        _daily = new BarChart(l.IsRtl);
        _retention = new BarChart(l.IsRtl, 160);
        Root = new StackPanel { Spacing = 12, Padding = new Thickness(20) };

        var controls = Ui.Row(12);
        var range = new ComboBox { Header = l["studio.analytics.range"].Replace("_", "", StringComparison.Ordinal), ItemsSource = InsightsViewModel.Ranges.Select(d => l.Format("studio.analytics.days", d)).ToList(), SelectedIndex = 1 };
        range.SelectionChanged += (_, _) => _vm.Days = InsightsViewModel.Ranges[Math.Max(0, range.SelectedIndex)];
        controls.Children.Add(range);
        _scope = new ComboBox { Header = l["studio.insights.scope"].Replace("_", "", StringComparison.Ordinal), MinWidth = 320 };
        AutomationProperties.SetAutomationId(_scope, "AnalyticsScope");
        _scope.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            var i = _scope.SelectedIndex;
            if (i >= 0 && i < _scopeIds.Count) _vm.VideoId = _scopeIds[i];
        };
        controls.Children.Add(_scope);
        var metrics = new[] { "views", "watch" };
        var metric = new ComboBox { Header = l["studio.analytics.metric"], ItemsSource = new[] { l["studio.analytics.views"], l["studio.analytics.watchHours"] }, SelectedIndex = 0 };
        AutomationProperties.SetAutomationId(metric, "AnalyticsMetric");
        metric.SelectionChanged += (_, _) => _vm.Metric = metrics[Math.Max(0, metric.SelectedIndex)];
        controls.Children.Add(metric);
        Root.Children.Add(controls);
        Root.Children.Add(_status);
        Root.Children.Add(_cards);

        Root.Children.Add(Header(l["studio.analytics.daily"], Ui.Button(l["studio.insights.exportDaily"], () => _ = SaveAsync(_vm.ExportDaily(DailyHeader())))));
        Root.Children.Add(_daily.View);
        Root.Children.Add(_dailySummary);
        Root.Children.Add(new Expander { Header = l["studio.analytics.table"], Content = _dailyTable, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left });

        Root.Children.Add(Header(l["studio.insights.videos"], Ui.Button(l["studio.insights.exportVideos"], () => _ = SaveAsync(_vm.ExportVideos(VideosHeader())))));
        Root.Children.Add(_videos);

        _exportRetention = Ui.Button(l["studio.insights.exportRetention"], () =>
        {
            if (_vm.ExportRetention(new[] { l["studio.insights.csv.percent"], l["studio.insights.csv.share"] }) is { } file) _ = SaveAsync(file);
        });
        _retentionBox.Children.Add(Header(l["studio.insights.retention"], _exportRetention));
        _retentionBox.Children.Add(_retention.View);
        _retentionBox.Children.Add(_retentionSummary);
        Root.Children.Add(_retentionBox);
        Root.Children.Add(_saved);

        _vm.PropertyChanged += (_, _) => Render();
        Render();
    }

    public StackPanel Root { get; }

    private static Grid Header(string title, Button action)
    {
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Ui.Heading(title));
        action.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(action, 1);
        g.Children.Add(action);
        return g;
    }

    private string[] DailyHeader() => new[]
    {
        _l["studio.insights.csv.date"], _l["studio.analytics.views"], _l["studio.insights.csv.watchMinutes"], _l["studio.analytics.likes"], _l["studio.analytics.comments"], _l["studio.analytics.subscribers"],
    };

    private string[] VideosHeader() => new[]
    {
        _l["studio.insights.csv.videoId"], _l["studio.insights.csv.title"], _l["studio.analytics.views"], _l["studio.insights.csv.watchMinutes"],
        _l["studio.insights.csv.avgSeconds"], _l["studio.analytics.likes"], _l["studio.analytics.comments"], _l["studio.insights.csv.engagement"],
    };

    private void Render()
    {
        _syncing = true;
        try
        {
            var ids = new List<string?> { null };
            var names = new List<string> { _l["studio.insights.wholeChannel"] };
            foreach (var v in _vm.Videos)
            {
                ids.Add(v.Id);
                names.Add(v.Title);
            }

            if (_vm.VideoId != null && !ids.Contains(_vm.VideoId))
            {
                ids.Add(_vm.VideoId);
                names.Add(_vm.SelectedVideo?.Title ?? _vm.VideoId);
            }

            _scopeIds = ids;
            _scope.ItemsSource = names;
            _scope.SelectedIndex = Math.Max(0, ids.IndexOf(_vm.VideoId));
        }
        finally
        {
            _syncing = false;
        }

        _status.Text = _vm.IsLoading ? _l["studio.shell.loading"] : _vm.ErrorKey is { } k ? _l[k] : _vm.Daily.Count == 0 ? _l["studio.analytics.empty"] : "";

        _cards.Children.Clear();
        _cards.ColumnDefinitions.Clear();
        if (_vm.VideoId == null && _vm.Summary is { } s)
        {
            Card(_l["studio.insights.totalViews"], _l.Number(s.TotalViews));
            Card(_l["studio.insights.totalVideos"], _l.Number(s.TotalVideos));
            Card(_l["studio.insights.avgViews"], _l.Number((long)Math.Round(s.AverageViewsPerVideo)));
            Card(_l["studio.insights.engagement"], _l.Format("studio.insights.percent", _l.Decimal(s.EngagementRate)));
            Card(_l["studio.analytics.subscribers"], _l.Number(s.TotalSubscribers));
        }
        else if (_vm.SelectedVideo is { } v)
        {
            Card(_l["studio.analytics.views"], _l.Number(v.PeriodViews));
            Card(_l["studio.analytics.watchHours"], _l.Decimal(v.WatchTimeSeconds / 3600));
            Card(_l["studio.insights.avgView"], _l.Duration(v.AverageViewSeconds));
            Card(_l["studio.analytics.likes"], _l.Number(v.PeriodLikes));
            Card(_l["studio.insights.engagement"], _l.Format("studio.insights.percent", _l.Decimal(v.EngagementRate)));
        }

        var days = _vm.Daily;
        var dates = days.Select(d => _l.ShortDate(d.Day)).ToList();
        var watch = _vm.Metric == "watch";
        var series = _vm.DailySeries;
        string Value(double v) => watch ? _l.Decimal(v) : _l.Number((long)v);
        _daily.Set(series, dates, !watch, v => _l.Compact(v),
            i => _l.Format("studio.analytics.readout", dates[i], Value(series[i])), _l[watch ? "studio.analytics.emptyWatch" : "studio.analytics.emptyViews"]);
        if (days.Count > 0)
        {
            var top = Enumerable.Range(0, series.Count).OrderByDescending(i => series[i]).First();
            _dailySummary.Text = _l.Format("studio.analytics.chartSummary", _l[watch ? "studio.analytics.watchHours" : "studio.analytics.views"], dates[0], dates[^1], Value(series[top]), _l.ShortDate(days[top].Day));
        }
        else
        {
            _dailySummary.Text = "";
        }

        var head = new[] { _l["studio.insights.csv.date"], _l["studio.analytics.views"], _l["studio.analytics.watchHours"], _l["studio.analytics.likes"], _l["studio.analytics.comments"], _l["studio.analytics.subscribers"] };
        Table(_dailyTable, head, days.Select((d, i) => new[] { dates[i], _l.Number(d.Views), _l.Decimal(d.WatchTimeSeconds / 3600), _l.Number(d.Likes), _l.Number(d.Comments), _l.Number(d.Subscribers) }).ToList(), null);

        var vhead = new[] { _l["studio.insights.csv.title"], _l["studio.analytics.views"], _l["studio.analytics.watchHours"], _l["studio.insights.avgView"], _l["studio.analytics.likes"], _l["studio.analytics.comments"], _l["studio.insights.engagement"] };
        Table(_videos, vhead, _vm.VideoStats.Select(v => new[]
        {
            v.Title, _l.Number(v.PeriodViews), _l.Decimal(v.WatchTimeSeconds / 3600), _l.Duration(v.AverageViewSeconds),
            _l.Number(v.PeriodLikes), _l.Number(v.PeriodComments), _l.Format("studio.insights.percent", _l.Decimal(v.EngagementRate)),
        }).ToList(), _l["studio.analytics.empty"]);

        _retentionBox.Visibility = _vm.VideoId != null ? Visibility.Visible : Visibility.Collapsed;
        var points = _vm.Retention?.Points;
        _exportRetention.IsEnabled = points != null;
        if (points != null)
        {
            var labels = points.Select(p => _l.Format("studio.insights.percent", _l.Number(p.Percent))).ToList();
            _retention.Set(points.Select(p => Math.Round(p.Share * 100, 1)).ToList(), labels, false, v => _l.Format("studio.insights.percent", _l.Number((long)v)),
                i => _l.Format("studio.insights.retentionReadout", labels[i], _l.Format("studio.insights.percent", _l.Decimal(points[i].Share * 100))), "");
            var half = points.FirstOrDefault(p => p.Share < 0.5);
            _retentionSummary.Text = _l.Format("studio.insights.retentionSummary", _l.Number(_vm.Retention!.Starts),
                half == null ? _l["studio.insights.retentionAllHalf"] : _l.Format("studio.insights.retentionHalf", _l.Format("studio.insights.percent", _l.Number(half.Percent))));
        }
        else
        {
            _retention.Set(Array.Empty<double>(), Array.Empty<string>(), true, _ => "", _ => "", "");
            _retentionSummary.Text = _vm.Retention != null ? _l.Format("studio.insights.retentionFew", _l.Number(_vm.Retention.Starts)) : "";
        }
    }

    private static void Table(Grid grid, string[] head, IReadOnlyList<string[]> rows, string? empty)
    {
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();
        for (var col = 0; col < head.Length; col++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = col == 0 && empty != null ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var count = Math.Max(rows.Count, empty != null ? 1 : 0) + 1;
        for (var r = 0; r < count; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var col = 0; col < head.Length; col++) Cell(grid, Ui.Text(head[col], dim: true), 0, col);
        if (rows.Count == 0 && empty != null)
        {
            var e = Ui.Text(empty, dim: true);
            Grid.SetColumnSpan(e, head.Length);
            Cell(grid, e, 1, 0);
        }

        for (var r = 0; r < rows.Count; r++)
        {
            for (var col = 0; col < rows[r].Length; col++)
            {
                var t = new TextBlock { Text = rows[r][col], TextTrimming = TextTrimming.CharacterEllipsis };
                if (col == 0 && empty != null) t.MaxWidth = 420;
                Cell(grid, t, r + 1, col);
            }
        }
    }

    private static void Cell(Grid grid, FrameworkElement e, int row, int col)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        grid.Children.Add(e);
    }

    private void Card(string label, string value)
    {
        // Equal columns that share the width, so five cards fit in Arabic and English alike.
        var card = Ui.Card(2);
        _cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(card, _cards.ColumnDefinitions.Count - 1);
        card.Children.Add(new TextBlock { Text = value, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        card.Children.Add(Ui.Text(label, dim: true));
        AutomationProperties.SetName(card, label + ": " + value);
        _cards.Children.Add(card);
    }

    private async Task SaveAsync((string FileName, byte[] Bytes) file)
    {
        try
        {
            if (await _pickSave(file.FileName) is not { } path) return;
            await File.WriteAllBytesAsync(path, file.Bytes);
            _saved.Severity = InfoBarSeverity.Success;
            Ui.Show(_saved, _l.Format("studio.insights.saved", Path.GetFileName(path)));
        }
        catch (IOException)
        {
            _saved.Severity = InfoBarSeverity.Error;
            Ui.Show(_saved, _l["studio.insights.saveFailed"]);
        }
        catch (UnauthorizedAccessException)
        {
            _saved.Severity = InfoBarSeverity.Error;
            Ui.Show(_saved, _l["studio.insights.saveFailed"]);
        }
    }
}
