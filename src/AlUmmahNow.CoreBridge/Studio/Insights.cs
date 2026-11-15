using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace AlUmmahNow.CoreBridge.Studio
{
    /// <summary>Lifetime totals (<c>GET /api/analytics/summary</c>).</summary>
    public sealed class AnalyticsSummary
    {
        public long TotalViews { get; set; }
        public long TotalLikes { get; set; }
        public long TotalComments { get; set; }
        public long TotalSubscribers { get; set; }
        public long TotalVideos { get; set; }
        public double AverageViewsPerVideo { get; set; }

        /// <summary>Percent.</summary>
        public double EngagementRate { get; set; }

        public static AnalyticsSummary Read(JsonElement e) => new AnalyticsSummary
        {
            TotalViews = J.Long(e, "totalViews"),
            TotalLikes = J.Long(e, "totalLikes"),
            TotalComments = J.Long(e, "totalComments"),
            TotalSubscribers = J.Long(e, "totalSubscribers"),
            TotalVideos = J.Long(e, "totalVideos"),
            AverageViewsPerVideo = J.Num(e, "avgViewsPerVideo"),
            EngagementRate = J.Num(e, "engagementRate"),
        };
    }

    /// <summary>One UTC day of numbers (<c>Metric</c>).</summary>
    public sealed class DailyMetric
    {
        public DateTime Day { get; set; }
        public long Views { get; set; }
        public long Likes { get; set; }
        public long Comments { get; set; }
        public long Subscribers { get; set; }

        /// <summary>Seconds.</summary>
        public double WatchTimeSeconds { get; set; }

        public static DailyMetric? Read(JsonElement m)
        {
            var date = J.Str(m, "date");
            if (date == null || date.Length < 10) return null;
            if (!DateTime.TryParseExact(date.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var day)) return null;
            return new DailyMetric
            {
                Day = DateTime.SpecifyKind(day, DateTimeKind.Utc),
                Views = J.Long(m, "views"),
                Likes = J.Long(m, "likes"),
                Comments = J.Long(m, "comments"),
                Subscribers = J.Long(m, "subscribers"),
                WatchTimeSeconds = J.Num(m, "watchTime"),
            };
        }
    }

    /// <summary>A video's numbers in a range (<c>GET /api/analytics/videos/:id</c>).</summary>
    public sealed class VideoStats
    {
        public string VideoId { get; set; } = "";
        public string Title { get; set; } = "";
        public long Views { get; set; }
        public long Likes { get; set; }
        public long Comments { get; set; }
        public long Shares { get; set; }

        /// <summary>Seconds watched in the range.</summary>
        public double WatchTimeSeconds { get; set; }

        /// <summary>Seconds.</summary>
        public double AverageViewSeconds { get; set; }

        /// <summary>Percent.</summary>
        public double EngagementRate { get; set; }

        /// <summary>The range's numbers when the server sends them; null from servers that only send lifetime numbers.</summary>
        public long? RangeViews { get; set; }
        public long? RangeLikes { get; set; }
        public long? RangeComments { get; set; }

        public IReadOnlyList<DailyMetric> Daily { get; set; } = Array.Empty<DailyMetric>();

        public long PeriodViews => RangeViews ?? Views;
        public long PeriodLikes => RangeLikes ?? Likes;
        public long PeriodComments => RangeComments ?? Comments;

        public static VideoStats Read(JsonElement v) => new VideoStats
        {
            VideoId = J.Str(v, "videoId") ?? "",
            Title = J.Str(v, "title") ?? "",
            Views = J.Long(v, "views"),
            Likes = J.Long(v, "likes"),
            Comments = J.Long(v, "comments"),
            Shares = J.Long(v, "shares"),
            WatchTimeSeconds = J.Num(v, "watchTime"),
            AverageViewSeconds = J.Num(v, "avgViewDuration"),
            EngagementRate = J.Num(v, "engagementRate"),
            RangeViews = J.NumOrNull(v, "rangeViews") is double a ? (long)a : (long?)null,
            RangeLikes = J.NumOrNull(v, "rangeLikes") is double b ? (long)b : (long?)null,
            RangeComments = J.NumOrNull(v, "rangeComments") is double c ? (long)c : (long?)null,
            Daily = J.Array(v, "recentMetrics").Select(DailyMetric.Read).Where(d => d != null).Select(d => d!).OrderBy(d => d.Day).ToList(),
        };
    }

    /// <summary>Share of viewers still watching at each 5 % of a video. <see cref="Points"/> is null below the server's minimum of starts.</summary>
    public sealed class RetentionCurve
    {
        public const int Buckets = 20;

        public RetentionCurve(long starts, IReadOnlyList<RetentionPoint>? points)
        {
            Starts = starts;
            Points = points;
        }

        public long Starts { get; }
        public IReadOnlyList<RetentionPoint>? Points { get; }

        public static RetentionCurve Read(JsonElement e)
        {
            var points = e.TryGetProperty("points", out var p) && p.ValueKind == JsonValueKind.Array
                ? p.EnumerateArray().Select(x => new RetentionPoint((int)J.Num(x, "bucket"), Math.Max(0, Math.Min(1, J.Num(x, "share"))))).OrderBy(x => x.Bucket).ToList()
                : null;
            return new RetentionCurve(J.Long(e, "starts"), points);
        }
    }

    public sealed class RetentionPoint
    {
        public RetentionPoint(int bucket, double share)
        {
            Bucket = bucket;
            Share = share;
        }

        /// <summary>0–19: the bucket's 5 % slice of the video.</summary>
        public int Bucket { get; }

        /// <summary>0–1.</summary>
        public double Share { get; }

        /// <summary>Where the slice starts, in percent of the video.</summary>
        public int Percent => Bucket * (100 / RetentionCurve.Buckets);
    }
}
