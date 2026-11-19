using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Analytics;

namespace AlUmmahNow.Studio.Linux;

/// <summary>Analytics: channel or video, summary cards, daily chart and table, per-video table, retention, CSV export.</summary>
public sealed class InsightsPage
{
    private readonly Localizer _l;
    private readonly InsightsViewModel _vm;
    private readonly Gtk.Window _window;
    private readonly Gtk.DropDown _scope = Gtk.DropDown.NewFromStrings(Array.Empty<string>());
    private readonly Gtk.Label _status = Ui.Label("", dim: true);
    private readonly Gtk.Box _cards = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
    private readonly BarChartView _daily;
    private readonly Gtk.Label _dailySummary = Ui.Label("", dim: true);
    private readonly Gtk.Grid _dailyTable = Gtk.Grid.New();
    private readonly Gtk.Grid _videos = Gtk.Grid.New();
    private readonly Gtk.Box _retentionBox = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
    private readonly BarChartView _retention;
    private readonly Gtk.Label _retentionSummary = Ui.Label("", dim: true);
    private readonly Gtk.Label _saved = Ui.Label("", dim: true);
    private readonly Gtk.Button _exportRetention;
    private IReadOnlyList<string?> _scopeIds = Array.Empty<string?>();
    private bool _syncing;

    public InsightsPage(Localizer l, InsightsViewModel vm, Gtk.Window window)
    {
        _l = l;
        _vm = vm;
        _window = window;
        _daily = new BarChartView(l.IsRtl);
        _retention = new BarChartView(l.IsRtl, 160);
        Root = Gtk.ScrolledWindow.New();
        var page = Ui.Page(14);
        Root.SetChild(page);

        var controls = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        var range = Gtk.DropDown.NewFromStrings(InsightsViewModel.Ranges.Select(d => l.Format("studio.analytics.days", d)).ToArray());
        range.SetSelected(1);
        range.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() == "selected") _vm.Days = InsightsViewModel.Ranges[(int)range.GetSelected()];
        };
        controls.Append(Ui.Labelled(l["studio.analytics.range"], range));
        _scope.OnNotify += (_, e) =>
        {
            if (_syncing || e.Pspec.GetName() != "selected") return;
            var i = (int)_scope.GetSelected();
            if (i >= 0 && i < _scopeIds.Count) _vm.VideoId = _scopeIds[i];
        };
        controls.Append(Ui.Labelled(l["studio.insights.scope"], _scope));
        var metrics = new[] { "views", "watch" };
        var metric = Gtk.DropDown.NewFromStrings(new[] { l["studio.analytics.views"], l["studio.analytics.watchHours"] });
        metric.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() == "selected") _vm.Metric = metrics[(int)metric.GetSelected()];
        };
        controls.Append(Ui.Labelled(l["studio.analytics.metric"], metric));
        page.Append(controls);
        page.Append(_status);
        page.Append(_cards);

        var dailyHead = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var dh = Ui.Heading(l["studio.analytics.daily"]);
        dh.SetHexpand(true);
        dailyHead.Append(dh);
        dailyHead.Append(Ui.Button(l["studio.insights.exportDaily"], () => _ = SaveAsync(_vm.ExportDaily(DailyHeader()))));
        page.Append(dailyHead);
        page.Append(_daily.Area);
        page.Append(_dailySummary);
        var dailyExpander = Gtk.Expander.New(l["studio.analytics.table"]);
        _dailyTable.SetColumnSpacing(24);
        _dailyTable.SetRowSpacing(2);
        dailyExpander.SetChild(_dailyTable);
        page.Append(dailyExpander);

        var videosHead = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var vh = Ui.Heading(l["studio.insights.videos"]);
        vh.SetHexpand(true);
        videosHead.Append(vh);
        videosHead.Append(Ui.Button(l["studio.insights.exportVideos"], () => _ = SaveAsync(_vm.ExportVideos(VideosHeader()))));
        page.Append(videosHead);
        _videos.SetColumnSpacing(20);
        _videos.SetRowSpacing(6);
        page.Append(_videos);

        var retentionHead = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var rh = Ui.Heading(l["studio.insights.retention"]);
        rh.SetHexpand(true);
        retentionHead.Append(rh);
        _exportRetention = Ui.Button(l["studio.insights.exportRetention"], () =>
        {
            if (_vm.ExportRetention(new[] { l["studio.insights.csv.percent"], l["studio.insights.csv.share"] }) is { } file) _ = SaveAsync(file);
        });
        retentionHead.Append(_exportRetention);
        _retentionBox.Append(retentionHead);
        _retentionBox.Append(_retention.Area);
        _retentionBox.Append(_retentionSummary);
        page.Append(_retentionBox);
        _saved.SetVisible(false);
        page.Append(_saved);

        _vm.PropertyChanged += (_, _) => Render();
        Render();
    }

    public Gtk.ScrolledWindow Root { get; }

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
            _scope.SetModel(Gtk.StringList.New(names.ToArray()));
            _scope.SetSelected((uint)Math.Max(0, ids.IndexOf(_vm.VideoId)));
        }
        finally
        {
            _syncing = false;
        }

        Ui.Status(_status, _vm.IsLoading ? _l["studio.shell.loading"] : _vm.ErrorKey is { } k ? _l[k] : _vm.Daily.Count == 0 ? _l["studio.analytics.empty"] : null);

        Ui.Clear(_cards);
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
            _dailySummary.SetText(_l.Format("studio.analytics.chartSummary", _l[watch ? "studio.analytics.watchHours" : "studio.analytics.views"], dates[0], dates[^1], Value(series[top]), _l.ShortDate(days[top].Day)));
        }
        else
        {
            _dailySummary.SetText("");
        }

        while (_dailyTable.GetFirstChild() is { } c) _dailyTable.Remove(c);
        var head = new[] { _l["studio.insights.csv.date"], _l["studio.analytics.views"], _l["studio.analytics.watchHours"], _l["studio.analytics.likes"], _l["studio.analytics.comments"], _l["studio.analytics.subscribers"] };
        for (var col = 0; col < head.Length; col++) _dailyTable.Attach(Ui.Label(head[col], dim: true), col, 0, 1, 1);
        for (var i = 0; i < days.Count; i++)
        {
            var d = days[i];
            var cells = new[] { dates[i], _l.Number(d.Views), _l.Decimal(d.WatchTimeSeconds / 3600), _l.Number(d.Likes), _l.Number(d.Comments), _l.Number(d.Subscribers) };
            for (var col = 0; col < cells.Length; col++) _dailyTable.Attach(Ui.Label(cells[col]), col, i + 1, 1, 1);
        }

        while (_videos.GetFirstChild() is { } c) _videos.Remove(c);
        var vhead = new[] { _l["studio.insights.csv.title"], _l["studio.analytics.views"], _l["studio.analytics.watchHours"], _l["studio.insights.avgView"], _l["studio.analytics.likes"], _l["studio.analytics.comments"], _l["studio.insights.engagement"] };
        for (var col = 0; col < vhead.Length; col++) _videos.Attach(Ui.Label(vhead[col], dim: true), col, 0, 1, 1);
        if (_vm.VideoStats.Count == 0) _videos.Attach(Ui.Label(_l["studio.analytics.empty"], dim: true), 0, 1, vhead.Length, 1);
        for (var i = 0; i < _vm.VideoStats.Count; i++)
        {
            var v = _vm.VideoStats[i];
            var title = Ui.Label(v.Title);
            title.SetHexpand(true);
            title.SetEllipsize(Pango.EllipsizeMode.End);
            title.SetMaxWidthChars(40);
            var cells = new Gtk.Widget[]
            {
                title, Ui.Label(_l.Number(v.PeriodViews)), Ui.Label(_l.Decimal(v.WatchTimeSeconds / 3600)), Ui.Label(_l.Duration(v.AverageViewSeconds)),
                Ui.Label(_l.Number(v.PeriodLikes)), Ui.Label(_l.Number(v.PeriodComments)), Ui.Label(_l.Format("studio.insights.percent", _l.Decimal(v.EngagementRate))),
            };
            for (var col = 0; col < cells.Length; col++) _videos.Attach(cells[col], col, i + 1, 1, 1);
        }

        _retentionBox.SetVisible(_vm.VideoId != null);
        var points = _vm.Retention?.Points;
        _exportRetention.SetSensitive(points != null);
        if (points != null)
        {
            var labels = points.Select(p => _l.Format("studio.insights.percent", _l.Number(p.Percent))).ToList();
            _retention.Set(points.Select(p => Math.Round(p.Share * 100, 1)).ToList(), labels, false, v => _l.Format("studio.insights.percent", _l.Number((long)v)),
                i => _l.Format("studio.insights.retentionReadout", labels[i], _l.Format("studio.insights.percent", _l.Decimal(points[i].Share * 100))), "");
            var half = points.FirstOrDefault(p => p.Share < 0.5);
            _retentionSummary.SetText(_l.Format("studio.insights.retentionSummary", _l.Number(_vm.Retention!.Starts),
                half == null ? _l["studio.insights.retentionAllHalf"] : _l.Format("studio.insights.retentionHalf", _l.Format("studio.insights.percent", _l.Number(half.Percent)))));
        }
        else
        {
            _retention.Set(Array.Empty<double>(), Array.Empty<string>(), true, v => "", _ => "", "");
            _retentionSummary.SetText(_vm.Retention != null ? _l.Format("studio.insights.retentionFew", _l.Number(_vm.Retention.Starts)) : "");
        }
    }

    private void Card(string label, string value)
    {
        var card = Ui.Card(2);
        card.SetSizeRequest(150, -1);
        var v = Ui.Label(value);
        v.AddCssClass("title-2");
        card.Append(v);
        card.Append(Ui.Label(label, dim: true));
        _cards.Append(card);
    }

    private async Task SaveAsync((string FileName, byte[] Bytes) file)
    {
        try
        {
            var dialog = Gtk.FileDialog.New();
            dialog.SetTitle(_l["studio.insights.saveCsv"]);
            dialog.SetInitialName(file.FileName);
            var target = await dialog.SaveAsync(_window);
            if (target?.GetPath() is not { } path) return;
            await File.WriteAllBytesAsync(path, file.Bytes);
            Ui.Status(_saved, _l.Format("studio.insights.saved", Path.GetFileName(path)));
        }
        catch (GLib.GException)
        {
            // dismissed
        }
        catch (IOException)
        {
            Ui.Status(_saved, _l["studio.insights.saveFailed"]);
        }
    }
}
