using System.Globalization;
using System.Text;
using AlUmmahNow.CoreBridge.Studio;

namespace AlUmmahNow.Studio.Engines;

/// <summary>
/// CSV for spreadsheet apps: UTF-8 with a byte-order mark so Excel opens Arabic, RFC 4180 quoting, CRLF line ends,
/// ISO 8601 dates and numbers without grouping. Text a spreadsheet would run as a formula gets a leading apostrophe.
/// </summary>
public sealed class CsvWriter
{
    private readonly List<IReadOnlyList<string>> _rows = new();

    public CsvWriter(IReadOnlyList<string> header) => _rows.Add(header);

    public IReadOnlyList<IReadOnlyList<string>> Rows => _rows;

    public void Append(IReadOnlyList<string> row) => _rows.Add(row);

    public static string Field(string value)
    {
        var text = value ?? "";
        if (text.Length > 0)
        {
            var first = text[0];
            if (first is '=' or '+' or '@' or '\t' or '\r' || (first == '-' && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                text = "'" + text;
            }
        }

        var quote = text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 || (text.Length > 0 && (text[0] == ' ' || text[^1] == ' '));
        return quote ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }

    public static string Date(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    public static string Day(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Number(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

    public static string Number(long v) => v.ToString(CultureInfo.InvariantCulture);

    public string Text
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var row in _rows)
            {
                sb.Append(string.Join(",", row.Select(Field)));
                sb.Append("\r\n");
            }

            return sb.ToString();
        }
    }

    /// <summary>The file's bytes, BOM first.</summary>
    public byte[] Bytes => new byte[] { 0xEF, 0xBB, 0xBF }.Concat(new UTF8Encoding(false).GetBytes(Text)).ToArray();
}

/// <summary>Studio analytics as CSV. Column names follow the app language; values stay machine-readable.</summary>
public static class AnalyticsCsv
{
    public static CsvWriter Daily(IEnumerable<DailyMetric> days, IReadOnlyList<string> header)
    {
        var csv = new CsvWriter(header);
        foreach (var d in days)
        {
            csv.Append(new[] { CsvWriter.Day(d.Day), CsvWriter.Number(d.Views), CsvWriter.Number(d.WatchTimeSeconds / 60.0), CsvWriter.Number(d.Likes), CsvWriter.Number(d.Comments), CsvWriter.Number(d.Subscribers) });
        }

        return csv;
    }

    public static CsvWriter Videos(IEnumerable<VideoStats> videos, IReadOnlyList<string> header)
    {
        var csv = new CsvWriter(header);
        foreach (var v in videos)
        {
            csv.Append(new[]
            {
                v.VideoId, v.Title, CsvWriter.Number(v.PeriodViews), CsvWriter.Number(v.WatchTimeSeconds / 60.0), CsvWriter.Number(v.AverageViewSeconds),
                CsvWriter.Number(v.PeriodLikes), CsvWriter.Number(v.PeriodComments), CsvWriter.Number(v.EngagementRate),
            });
        }

        return csv;
    }

    public static CsvWriter Retention(RetentionCurve curve, IReadOnlyList<string> header)
    {
        var csv = new CsvWriter(header);
        foreach (var p in curve.Points ?? Array.Empty<RetentionPoint>())
        {
            csv.Append(new[] { CsvWriter.Number(p.Percent), CsvWriter.Number(p.Share) });
        }

        return csv;
    }
}
