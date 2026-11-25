using AlUmmahNow.App.I18n;
using AlUmmahNow.Studio.Analytics;
using AlUmmahNow.Studio.Shell;

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

    /// <summary>A metric tile's height in pixels, above the natural height of either script's two lines.</summary>
    private const int KpiHeight = 84;

    public AnalyticsPage(Localizer l, AnalyticsViewModel vm)
    {
        _l = l;
        _vm = vm;
        // Lantern Glass: metric tiles (Views and Watch time pick the chart), the chart card beside Top videos.
        Root = Gtk.ScrolledWindow.New();
        Root.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        var page = Ui.Page(18);
        page.SetMarginStart(24);
        page.SetMarginEnd(24);
        Root.SetChild(page);

        var controls = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        var range = Gtk.DropDown.NewFromStrings(AnalyticsViewModel.Ranges.Select(d => l.Format("studio.analytics.days", d)).ToArray());
        range.SetSelected(1);
        range.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() == "selected") _vm.Days = AnalyticsViewModel.Ranges[(int)range.GetSelected()];
        };
        controls.Append(Ui.Labelled(l["studio.analytics.range"], range));
        page.Append(controls);
        page.Append(_status);
        _totals.SetHomogeneous(true);
        page.Append(_totals);

        var columns = Gtk.Box.New(Gtk.Orientation.Horizontal, 20);
        var chartBody = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        chartBody.Append(Ui.CardTitle(l["studio.analytics.daily"]));
        _chart.SetHexpand(true);
        _chart.SetContentHeight(300);
        _chart.SetDrawFunc((_, cr, w, h) => DrawChart(cr, w, h));
        var motion = Gtk.EventControllerMotion.New();
        motion.OnMotion += (_, e) => OnHover(e.X);
        _chart.AddController(motion);
        _chart.SetHasTooltip(true);
        chartBody.Append(_chart);
        chartBody.Append(_summary);
        _table.SetColumnSpacing(24);
        _table.SetRowSpacing(2);
        chartBody.Append(new Disclosure(l, l["studio.analytics.table"], _table).Root);
        var chartCard = Ui.Card(chartBody);
        chartCard.SetHexpand(true);
        chartCard.SetValign(Gtk.Align.Start);
        columns.Append(chartCard);

        var topBody = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        topBody.Append(Ui.CardTitle(l["studio.analytics.top"]));
        topBody.Append(_top);
        var topCard = Ui.Card(topBody);
        topCard.SetSizeRequest(380, -1);
        topCard.SetValign(Gtk.Align.Start);
        columns.Append(topCard);
        page.Append(columns);

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
            Card(_l["studio.analytics.views"], _l.Stat(d.Totals.Views), "views");
            Card(_l["studio.analytics.watchHours"], _l.Stat(d.Totals.WatchTimeMinutes / 60, decimals: true), "watch");
            Card(_l["studio.analytics.likes"], _l.Stat(d.Totals.Likes));
            Card(_l["studio.analytics.comments"], _l.Stat(d.Totals.Comments));
            Card(_l["studio.analytics.subscribers"], _l.Stat(d.Totals.Subscribers));
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
        var rank = 0;
        foreach (var v in _vm.Data?.TopVideos ?? Array.Empty<CoreBridge.Studio.TopVideo>())
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
            row.SetMarginTop(4);
            row.SetMarginBottom(4);
            var n = Ui.Label(_l.Number(++rank), dim: true);
            n.AddCssClass("heading");
            n.SetSizeRequest(18, -1);
            n.SetValign(Gtk.Align.Center);
            row.Append(n);
            row.Append(Ui.Thumbnail(v.ThumbnailUrl, 72, 40));
            var text = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
            text.SetHexpand(true);
            text.SetValign(Gtk.Align.Center);
            // Two lines before an ellipsis, as on Windows: the card is narrow at the 1024 window.
            var t = Ui.Label(Localizer.Isolate(v.TitleFor(_l.Language)));
            t.SetWrap(true);
            t.SetLines(2);
            t.SetEllipsize(Pango.EllipsizeMode.End);
            t.AddCssClass("heading");
            text.Append(t);
            var views = Ui.Label(_l.Views(v.Views), dim: true);
            views.AddCssClass("caption");
            text.Append(views);
            row.Append(text);
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

    /// <summary>A metric tile; Views and Watch time are buttons that pick the chart's metric (the picked one glows).</summary>
    private void Card(string label, string value, string? metric = null)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        var name = Ui.Label(label, dim: true);
        name.AddCssClass("caption-heading");
        var v = Ui.Label(value);
        v.AddCssClass("title-1");
        box.Append(name);
        box.Append(v);
        Gtk.Widget tile;
        if (metric != null)
        {
            var button = Gtk.ToggleButton.New();
            button.SetChild(box);
            button.SetActive(_vm.Metric == metric);
            button.OnToggled += (_, _) =>
            {
                if (button.GetActive() && _vm.Metric != metric) _vm.Metric = metric;
                else if (!button.GetActive() && _vm.Metric == metric) button.SetActive(true);
            };
            Ui.Named(button, _l.Format("studio.analytics.readout", _l["studio.analytics.metric"], label));
            tile = button;
        }
        else
        {
            // A box of its own around the text, as the button is for Views and Watch time: the text's margins then sit
            // inside every tile, so all five tiles take the same width and the gaps between them are equal.
            var plain = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            plain.Append(box);
            tile = plain;
        }

        tile.AddCssClass("card");
        tile.AddCssClass("kpi");
        // One height in both scripts: the Arabic name and value lines are shorter than the Latin ones, so the tiles came
        // out 76 px tall in Arabic against 80 in English and the cards below started 4 px apart.
        tile.SetSizeRequest(-1, KpiHeight);
        box.SetMarginStart(4);
        box.SetMarginEnd(4);
        _totals.Append(tile);
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
        // Lantern gold bars: a soft glow behind, then a gold gradient (glow #fbc661 to pressed #e0962a), and in light mode
        // an edge, on whole pixels so that it is one line.
        var edge = StudioTheme.BarEdge(Adw.StyleManager.GetDefault().GetDark());
        foreach (var bar in layout.Bars.Where(b => b.Height > 0))
        {
            cr.SetSourceRgba(0.957, 0.682, 0.227, 0.22);
            cr.Rectangle(bar.X - 2, bar.Y - 2, bar.Width + 4, bar.Height + 2);
            cr.Fill();
            using var gold = new Cairo.LinearGradient(0, bar.Y, 0, bar.Y + bar.Height);
            gold.AddColorStopRgb(0, 0.984, 0.776, 0.380);
            gold.AddColorStopRgb(1, 0.878, 0.588, 0.165);
            cr.SetSource(gold);
            var (x, y, right, bottom) = edge == null
                ? (bar.X, bar.Y, bar.X + bar.Width, bar.Y + bar.Height)
                : (Math.Round(bar.X), Math.Round(bar.Y), Math.Round(bar.X + bar.Width), Math.Round(bar.Y + bar.Height));
            cr.Rectangle(x, y, right - x, bottom - y);
            cr.Fill();
            if (edge is not { } c || right - x < 2 || bottom - y < 2) continue;
            cr.SetSourceRgb(((c >> 16) & 255) / 255.0, ((c >> 8) & 255) / 255.0, (c & 255) / 255.0);
            cr.Rectangle(x + 0.5, y + 0.5, right - x - 1, bottom - y - 1);
            cr.Stroke();
        }

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
