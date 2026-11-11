using System.Globalization;
using AlUmmahNow.App.I18n;
using AlUmmahNow.Studio.Analytics;

namespace AlUmmahNow.Studio.Linux;

/// <summary>Channel analytics: totals, a daily bar chart with a text summary and a table, top videos.</summary>
public sealed class AnalyticsPage
{
    private readonly Localizer _l;
    private readonly AnalyticsViewModel _vm;
    private readonly Gtk.Box _totals = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
    private readonly Gtk.DrawingArea _chart = Gtk.DrawingArea.New();
    private readonly Gtk.Label _summary = Ui.Label("", dim: true);
    private readonly Gtk.Grid _table = Gtk.Grid.New();
    private readonly Gtk.Box _top = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
    private readonly Gtk.Label _status = Ui.Label("", dim: true);

    public AnalyticsPage(Localizer l, AnalyticsViewModel vm)
    {
        _l = l;
        _vm = vm;
        Root = Gtk.ScrolledWindow.New();
        var page = Ui.Page(14);
        Root.SetChild(page);

        var controls = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        var range = Gtk.DropDown.NewFromStrings(AnalyticsViewModel.Ranges.Select(d => l.Format("studio.analytics.days", d)).ToArray());
        range.SetSelected(1);
        range.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() == "selected") _vm.Days = AnalyticsViewModel.Ranges[(int)range.GetSelected()];
        };
        controls.Append(Ui.Labelled(l["studio.analytics.range"], range));
        var metrics = new[] { "views", "watch" };
        var metric = Gtk.DropDown.NewFromStrings(new[] { l["studio.analytics.views"], l["studio.analytics.watchHours"] });
        metric.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() == "selected") _vm.Metric = metrics[(int)metric.GetSelected()];
        };
        controls.Append(Ui.Labelled(l["studio.analytics.metric"], metric));
        page.Append(controls);
        page.Append(_status);
        page.Append(_totals);

        var heading = Ui.Label(l["studio.analytics.daily"]);
        heading.AddCssClass("heading");
        page.Append(heading);
        _chart.SetContentHeight(200);
        _chart.SetHexpand(true);
        _chart.SetDrawFunc((_, cr, w, h) => DrawChart(cr, w, h));
        page.Append(_chart);
        page.Append(_summary);
        var tableExpander = Gtk.Expander.New(l["studio.analytics.table"]);
        _table.SetColumnSpacing(24);
        _table.SetRowSpacing(2);
        tableExpander.SetChild(_table);
        page.Append(tableExpander);

        var topHeading = Ui.Label(l["studio.analytics.top"]);
        topHeading.AddCssClass("heading");
        page.Append(topHeading);
        page.Append(_top);

        _vm.PropertyChanged += (_, _) => Render();
        Render();
    }

    public Gtk.ScrolledWindow Root { get; }

    private void Render()
    {
        _status.SetText(_vm.IsLoading ? _l["studio.shell.loading"] : _vm.ErrorKey is { } k ? _l[k] : _vm.Data is { Daily.Count: 0 } ? _l["studio.analytics.empty"] : "");
        Ui.Clear(_totals);
        if (_vm.Data is { } d)
        {
            Card(_l["studio.analytics.views"], _l.Number(d.Totals.Views));
            Card(_l["studio.analytics.watchHours"], _l.Number((long)Math.Round(d.Totals.WatchTimeMinutes / 60)));
            Card(_l["studio.analytics.likes"], _l.Number(d.Totals.Likes));
            Card(_l["studio.analytics.comments"], _l.Number(d.Totals.Comments));
            Card(_l["studio.analytics.subscribers"], _l.Number(d.Totals.Subscribers));
        }

        var series = _vm.Series;
        while (_table.GetFirstChild() is { } c) _table.Remove(c);
        for (var i = 0; i < series.Count; i++)
        {
            _table.Attach(Ui.Label(series[i].Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), 0, i, 1, 1);
            _table.Attach(Ui.Label(series[i].Value.ToString("0.#", CultureInfo.InvariantCulture)), 1, i, 1, 1);
        }

        _summary.SetText(Summary(series));
        _chart.QueueDraw();
        Ui.Clear(_top);
        foreach (var v in _vm.Data?.TopVideos ?? Array.Empty<CoreBridge.Studio.TopVideo>())
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            var t = Ui.Label(v.Title);
            t.SetHexpand(true);
            row.Append(t);
            row.Append(Ui.Label(_l.Format("studio.videos.views", _l.Number(v.Views)), dim: true));
            _top.Append(row);
        }
    }

    private string Summary(IReadOnlyList<(DateTime Date, double Value)> s)
    {
        if (s.Count == 0) return "";
        var max = s.OrderByDescending(p => p.Value).First();
        return _l.Format("studio.analytics.chartSummary",
            _vm.Metric == "watch" ? _l["studio.analytics.watchHours"] : _l["studio.analytics.views"],
            s[0].Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            s[^1].Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            max.Value.ToString("0.#", CultureInfo.InvariantCulture),
            max.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private void Card(string label, string value)
    {
        var card = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        card.AddCssClass("card");
        card.SetSizeRequest(150, -1);
        var v = Ui.Label(value);
        v.AddCssClass("title-2");
        v.SetMarginStart(12);
        v.SetMarginTop(8);
        var name = Ui.Label(label, dim: true);
        name.SetMarginStart(12);
        name.SetMarginBottom(8);
        card.Append(v);
        card.Append(name);
        _totals.Append(card);
    }

    private void DrawChart(Cairo.Context cr, int w, int h)
    {
        var s = _vm.Series;
        if (s.Count == 0) return;
        var max = Math.Max(1, s.Max(p => p.Value));
        var gap = 2.0;
        var bar = Math.Max(1, (w - (gap * (s.Count - 1))) / s.Count);
        cr.SetSourceRgb(0.03, 0.40, 0.47);
        for (var i = 0; i < s.Count; i++)
        {
            // Time runs right to left in Arabic.
            var index = _l.IsRtl ? s.Count - 1 - i : i;
            var height = (h - 10) * (s[index].Value / max);
            cr.Rectangle(i * (bar + gap), h - height, bar, height);
        }

        cr.Fill();
    }
}
