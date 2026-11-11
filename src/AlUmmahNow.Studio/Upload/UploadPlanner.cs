using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AlUmmahNow.Studio.Upload;

public enum UploadVisibility { Public, Private, Scheduled }

/// <summary>"Set for all" values applied to every file in a batch.</summary>
public sealed class UploadDefaults
{
    /// <summary>Tokens: {filename} (name without extension), {n} (1-based, zero-padded), {date} (yyyy-MM-dd).</summary>
    public string TitlePattern { get; set; } = TitlePatterns.Default;

    public string Description { get; set; } = "";

    /// <summary>A server category key, or null for none.</summary>
    public string? Category { get; set; }

    /// <summary>en, ar, ur or fr; null leaves it unset.</summary>
    public string? Language { get; set; }

    public UploadVisibility Visibility { get; set; } = UploadVisibility.Private;
    public List<string> PlaylistIds { get; set; } = new();
    public List<string> Tags { get; set; } = new();

    /// <summary>When the first video publishes (Scheduled only).</summary>
    public DateTimeOffset? ScheduleStart { get; set; }

    /// <summary>Time between consecutive scheduled videos; zero publishes them together.</summary>
    public TimeSpan ScheduleInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>IANA zone sent with schedules, e.g. Asia/Riyadh.</summary>
    public string TimeZone { get; set; } = "UTC";
}

/// <summary>One file with the values it will be uploaded with.</summary>
public sealed class PlannedUpload
{
    public string FilePath { get; set; } = "";
    public long Size { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Category { get; set; }
    public string? Language { get; set; }
    public bool IsPublic { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<string> PlaylistIds { get; set; } = new();
    public DateTimeOffset? PublishAt { get; set; }
    public string TimeZone { get; set; } = "UTC";
}

public static class TitlePatterns
{
    public const string Default = "{filename}";
    public const int MaxTitleLength = 100;

    private static readonly Regex Token = new(@"\{(filename|n|date)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Fills the tokens for file <paramref name="index"/> (0-based) of <paramref name="count"/>.</summary>
    public static string Apply(string pattern, string filePath, int index, int count, DateTimeOffset date)
    {
        if (string.IsNullOrWhiteSpace(pattern)) pattern = Default;
        var digits = Math.Max(1, count.ToString(CultureInfo.InvariantCulture).Length);
        var title = Token.Replace(pattern, m => m.Groups[1].Value.ToLowerInvariant() switch
        {
            "filename" => Path.GetFileNameWithoutExtension(filePath),
            "n" => (index + 1).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0'),
            _ => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        });
        title = Regex.Replace(title, @"\s+", " ").Trim();
        if (title.Length == 0) title = Path.GetFileNameWithoutExtension(filePath);
        return Truncate(title, MaxTitleLength);
    }

    /// <summary>Cuts at a text-element boundary so a combining mark or emoji is never split.</summary>
    public static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;
        var e = StringInfo.GetTextElementEnumerator(text);
        var sb = new StringBuilder();
        while (e.MoveNext())
        {
            var el = e.GetTextElement();
            if (sb.Length + el.Length > max) break;
            sb.Append(el);
        }

        return sb.ToString().TrimEnd();
    }
}

public static class UploadPlanner
{
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".mts", ".ts", ".wmv", ".flv", ".3gp", ".mpg", ".mpeg",
    };

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    /// <summary>The video files in a folder, in natural name order ("2" before "10").</summary>
    public static IReadOnlyList<string> VideosIn(string folder, bool recursive)
    {
        if (!Directory.Exists(folder)) return Array.Empty<string>();
        return Directory.EnumerateFiles(folder, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(IsVideo)
            .Where(p => !Path.GetFileName(p).StartsWith('.'))
            .OrderBy(p => p, NaturalComparer.Instance)
            .ToList();
    }

    /// <summary>Applies the defaults to each file in order. Scheduled times step by the interval.</summary>
    public static IReadOnlyList<PlannedUpload> Plan(IReadOnlyList<string> files, UploadDefaults d, DateTimeOffset now)
    {
        var plans = new List<PlannedUpload>(files.Count);
        var start = d.ScheduleStart ?? now.AddHours(1);
        if (start < now) start = now.AddMinutes(5);
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            long size = 0;
            try
            {
                size = new FileInfo(f).Length;
            }
            catch (IOException)
            {
            }

            var scheduled = d.Visibility == UploadVisibility.Scheduled;
            plans.Add(new PlannedUpload
            {
                FilePath = f,
                Size = size,
                Title = TitlePatterns.Apply(d.TitlePattern, f, i, files.Count, now),
                Description = d.Description,
                Category = string.IsNullOrEmpty(d.Category) ? null : d.Category,
                Language = string.IsNullOrEmpty(d.Language) ? null : d.Language,
                IsPublic = d.Visibility == UploadVisibility.Public,
                Tags = d.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList(),
                PlaylistIds = d.PlaylistIds.Distinct().ToList(),
                PublishAt = scheduled ? start + TimeSpan.FromTicks(d.ScheduleInterval.Ticks * i) : null,
                TimeZone = d.TimeZone,
            });
        }

        return plans;
    }

    /// <summary>Splits a comma (Latin or Arabic) separated tag field.</summary>
    public static List<string> ParseTags(string text) =>
        text.Split(new[] { ',', '،', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().TrimStart('#'))
            .Where(t => t.Length > 0)
            .Select(t => t.Length > 50 ? t[..50] : t)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();
}

/// <summary>Orders "lesson 2" before "lesson 10".</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x == null || y == null) return string.CompareOrdinal(x, y);
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var si = i;
                var sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                var c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                if (c != 0) return c;
            }
            else
            {
                var c = string.Compare(x[i].ToString(), y[j].ToString(), StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                i++;
                j++;
            }
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
