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
        _chart.SetHexpand(true);
        _chart.SetContentHeight(222);
        _chart.SetDrawFunc((_, cr, w, h) => DrawChart(cr, w, h));
        var motion = Gtk.EventControllerMotion.New();
        motion.OnMotion += (_, e) => OnHover(e.X);
        _chart.AddController(motion);
        _chart.SetHasTooltip(true);
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
            Card(_l["studio.analytics.watchHours"], _l.Decimal(d.Totals.WatchTimeMinutes / 60));
            Card(_l["studio.analytics.likes"], _l.Number(d.Totals.Likes));
            Card(_l["studio.analytics.comments"], _l.Number(d.Totals.Comments));
            Card(_l["studio.analytics.subscribers"], _l.Number(d.Totals.Subscribers));
        }

        var series = _vm.Series;
        while (_table.GetFirstChild() is { } c) _table.Remove(c);
        for (var i = 0; i < series.Count; i++)
        {
            _table.Attach(Ui.Label(_l.ShortDate(series[i].Date)), 0, i, 1, 1);
            _table.Attach(Ui.Label(ValueLabel(series[i].Value)), 1, i, 1, 1);
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
            _l.ShortDate(s[0].Date),
            _l.ShortDate(s[^1].Date),
            ValueLabel(max.Value),
            _l.ShortDate(max.Date));
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

    private ChartLayout? _layout;

    /// <summary>
    /// Bars with a 0-based value axis and dashed gridlines on the start side (right in Arabic), the
    /// first, middle and last dates below, and the hovered day in the tooltip. Text and lines use the
    /// theme's foreground colour; bars use the app's accent (@accent_color in Program.cs).
    /// </summary>
    private void DrawChart(Cairo.Context cr, int w, int h)
    {
        var s = _vm.Series;
        if (s.Count == 0)
        {
            _layout = null;
            return;
        }

        var layout = ChartLayout.Compute(w, h, s.Select(p => p.Value).ToList(), integer: _vm.Metric != "watch", _l.IsRtl);
        _layout = layout;
        _chart.GetColor(out var fg);

        cr.LineWidth = 1;
        foreach (var tick in layout.Ticks)
        {
            var y = Math.Round(tick.Y) + 0.5;
            cr.SetSourceRgba(fg.Red, fg.Green, fg.Blue, tick.Value == 0 ? 0.6 : 0.18);
            cr.SetDash(tick.Value == 0 ? System.Array.Empty<double>() : new double[] { 4, 4 }, 0);
            cr.MoveTo(layout.PlotLeft, y);
            cr.LineTo(layout.PlotRight, y);
            cr.Stroke();
            var label = AxisLabel(tick.Value);
            DrawText(cr, fg, label, layout.AxisLabelEdge, tick.Y, layout.Rtl ? 0 : 1, 0.5);
        }

        cr.SetDash(System.Array.Empty<double>(), 0);
        cr.SetSourceRgb(1, 0.627, 0.216);
        foreach (var bar in layout.Bars.Where(b => b.Height > 0)) cr.Rectangle(bar.X, bar.Y, bar.Width, bar.Height);
        cr.Fill();

        foreach (var (index, x) in layout.Dates)
        {
            var align = index == 0 ? (layout.Rtl ? 1 : 0) : index == s.Count - 1 ? (layout.Rtl ? 0 : 1) : 0.5;
            DrawText(cr, fg, _l.ShortDate(s[index].Date), x, layout.PlotBottom + 4, align, 0);
        }

        if (layout.Empty)
        {
            var empty = _l[_vm.Metric == "watch" ? "studio.analytics.emptyWatch" : "studio.analytics.emptyViews"];
            DrawText(cr, fg, empty, (layout.PlotLeft + layout.PlotRight) / 2, (layout.PlotTop + layout.PlotBottom) / 2, 0.5, 0.5);
        }
    }

    private string AxisLabel(double value) => _vm.Metric == "watch" ? _l.Decimal(value) : _l.Compact(value);

    private string ValueLabel(double value) => _vm.Metric == "watch" ? _l.Decimal(value) : _l.Number((long)value);

    /// <summary>Text at (x, y), aligned by fractions of its own size (0 = start edge, 1 = end edge).</summary>
    private void DrawText(Cairo.Context cr, Gdk.RGBA fg, string text, double x, double y, double alignX, double alignY)
    {
        var layout = _chart.CreatePangoLayout(text);
        layout.GetPixelSize(out var tw, out var th);
        cr.SetSourceRgba(fg.Red, fg.Green, fg.Blue, 0.75);
        cr.MoveTo(x - (tw * alignX), y - (th * alignY));
        PangoCairo.Functions.ShowLayout(cr, layout);
    }

    /// <summary>The hovered day and its value, as the chart's tooltip.</summary>
    private void OnHover(double x)
    {
        var s = _vm.Series;
        var index = _layout?.IndexAt(x);
        _chart.SetTooltipText(index is { } i && i < s.Count ? _l.Format("studio.analytics.readout", _l.ShortDate(s[i].Date), ValueLabel(s[i].Value)) : null);
    }
}
