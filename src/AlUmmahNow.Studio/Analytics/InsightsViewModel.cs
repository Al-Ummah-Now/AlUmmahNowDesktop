using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Engines;
using AlUmmahNow.Studio.Shell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlUmmahNow.Studio.Analytics;

/// <summary>
/// Deep analytics (E19 19.6) for the active channel or one of its videos: lifetime summary, daily metrics over a
/// chosen range, per-video numbers and the retention curve, with CSV exports.
/// </summary>
public sealed partial class InsightsViewModel : ObservableObject
{
    public static readonly IReadOnlyList<int> Ranges = new[] { 7, 28, 90, 365 };
    private const int TopVideos = 10;
    private static readonly Logger Logger = Log.For("insights");

    private readonly StudioShellViewModel _shell;
    private readonly IStudioApi _api;
    private readonly IInsightsApi _insights;
    private readonly Func<DateTimeOffset> _now;
    private int _generation;

    [ObservableProperty] private int _days = 28;

    /// <summary>Null for the whole channel.</summary>
    [ObservableProperty] private string? _videoId;

    [ObservableProperty] private IReadOnlyList<StudioVideo> _videos = Array.Empty<StudioVideo>();
    [ObservableProperty] private AnalyticsSummary? _summary;
    [ObservableProperty] private IReadOnlyList<DailyMetric> _daily = Array.Empty<DailyMetric>();
    [ObservableProperty] private IReadOnlyList<VideoStats> _videoStats = Array.Empty<VideoStats>();
    [ObservableProperty] private RetentionCurve? _retention;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorKey;

    public InsightsViewModel(StudioShellViewModel shell, IStudioApi api, IInsightsApi insights, Func<DateTimeOffset>? now = null)
    {
        _shell = shell;
        _api = api;
        _insights = insights;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _shell.ChannelChanged += (_, _) =>
        {
            VideoId = null;
            _ = LoadAsync();
        };
    }

    partial void OnDaysChanged(int value) => _ = LoadAsync();

    partial void OnVideoIdChanged(string? value) => _ = LoadAsync();

    /// <summary>"Last N days": today (UTC) and the N−1 days before it, as the API counts both ends.</summary>
    public (DateTimeOffset From, DateTimeOffset To) Period()
    {
        var to = _now();
        var start = new DateTimeOffset(to.UtcDateTime.Date, TimeSpan.Zero).AddDays(-(Days - 1));
        return (start, to);
    }

    public VideoStats? SelectedVideo => VideoId == null ? null : VideoStats.FirstOrDefault(v => v.VideoId == VideoId);

    partial void OnVideoStatsChanged(IReadOnlyList<VideoStats> value) => OnPropertyChanged(nameof(SelectedVideo));

    public async Task LoadAsync()
    {
        if (_shell.ActiveChannel is not { } channel)
        {
            Summary = null;
            Daily = Array.Empty<DailyMetric>();
            VideoStats = Array.Empty<VideoStats>();
            Retention = null;
            return;
        }

        var gen = Interlocked.Increment(ref _generation);
        IsLoading = true;
        ErrorKey = null;
        var (from, to) = Period();
        try
        {
            if (VideoId is { } videoId)
            {
                var stats = _insights.VideoAsync(videoId, from, to);
                var curve = _insights.RetentionAsync(videoId, from, to);
                await Task.WhenAll(stats, curve);
                if (gen != _generation) return;
                VideoStats = new[] { stats.Result };
                Daily = stats.Result.Daily;
                Retention = curve.Result;
            }
            else
            {
                var summary = _insights.SummaryAsync(channel.Id, from, to);
                var daily = _insights.DailyAsync(channel.Id, from, to);
                var videos = _api.ListVideosAsync(new StudioVideoQuery { ChannelId = channel.Id, Limit = 50 });
                var top = _api.ChannelAnalyticsAsync(channel.Id, from, to);
                await Task.WhenAll(summary, daily, videos, top);
                if (gen != _generation) return;
                Summary = summary.Result;
                Daily = daily.Result;
                Videos = videos.Result.Items;
                Retention = null;
                var ids = top.Result.TopVideos.Select(v => v.Id).Take(TopVideos).ToList();
                var stats = await Task.WhenAll(ids.Select(id => _insights.VideoAsync(id, from, to)));
                if (gen == _generation) VideoStats = stats.OrderByDescending(s => s.PeriodViews).ToList();
            }
        }
        catch (AuthRequiredException)
        {
            _shell.ReportAuthRequired();
        }
        catch (Exception ex) when (ex is StudioApiException or RateLimitedException)
        {
            Logger.Warn("insights did not load", ex);
            if (gen == _generation)
            {
                ErrorKey = ex is StudioApiException { Status: 403 } ? "studio.analytics.ownerOnly"
                    : ex is StudioApiException { Status: 503 } ? "studio.insights.rebuilding"
                    : "studio.analytics.failed";
            }
        }
        finally
        {
            if (gen == _generation) IsLoading = false;
        }
    }

    /// <summary>A CSV file: its suggested name and bytes. Headers come from the page in the app language.</summary>
    public (string FileName, byte[] Bytes) ExportDaily(IReadOnlyList<string> header) => (FileName("daily"), AnalyticsCsv.Daily(Daily, header).Bytes);

    public (string FileName, byte[] Bytes) ExportVideos(IReadOnlyList<string> header) => (FileName("videos"), AnalyticsCsv.Videos(VideoStats, header).Bytes);

    public (string FileName, byte[] Bytes)? ExportRetention(IReadOnlyList<string> header) =>
        Retention?.Points == null ? null : (FileName("retention"), AnalyticsCsv.Retention(Retention, header).Bytes);

    private string FileName(string what)
    {
        var (from, to) = Period();
        var scope = VideoId ?? _shell.ActiveChannel?.Handle ?? _shell.ActiveChannel?.Id ?? "channel";
        var safe = new string(scope.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return $"alummahnow-{safe}-{what}-{from.UtcDateTime:yyyy-MM-dd}-{to.UtcDateTime:yyyy-MM-dd}.csv";
    }
}
