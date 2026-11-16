using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Shell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlUmmahNow.Studio.Analytics;

/// <summary>Channel analytics for the last 7, 28 or 90 days: totals, a daily series and top videos.</summary>
public sealed partial class AnalyticsViewModel : ObservableObject
{
    public static readonly IReadOnlyList<int> Ranges = new[] { 7, 28, 90 };
    private static readonly Logger Logger = Log.For("analytics");

    private readonly StudioShellViewModel _shell;
    private readonly IStudioApi _api;
    private readonly Func<DateTimeOffset> _now;
    private int _generation;

    [ObservableProperty] private int _days = 28;
    [ObservableProperty] private ChannelAnalytics? _data;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorKey;
    [ObservableProperty] private string _metric = "views";

    /// <summary>
    /// Analytics answered 404 <c>feature_off</c> or 402 for this channel: the page is hidden, with no text.
    /// The next load (channel switch) shows it again if the server allows it there.
    /// </summary>
    [ObservableProperty] private bool _isHidden;

    public AnalyticsViewModel(StudioShellViewModel shell, IStudioApi api, Func<DateTimeOffset>? now = null)
    {
        _shell = shell;
        _api = api;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _shell.ChannelChanged += (_, _) => _ = LoadAsync();
    }

    partial void OnDaysChanged(int value) => _ = LoadAsync();

    /// <summary>The selected metric per day: views, or watch time in hours.</summary>
    public IReadOnlyList<(DateTime Date, double Value)> Series =>
        Data?.Daily.Select(d => (d.Date, Metric == "watch" ? Math.Round(d.WatchTimeMinutes / 60.0, 1) : d.Views)).ToList()
        ?? new List<(DateTime, double)>();

    partial void OnMetricChanged(string value) => OnPropertyChanged(nameof(Series));

    partial void OnDataChanged(ChannelAnalytics? value) => OnPropertyChanged(nameof(Series));

    /// <summary>
    /// The analytics API sends top videos with their original title only. Their per-language titles come
    /// from the channel's video list (one request), so the list shows the same titles as the Videos tab.
    /// On failure the original titles stay.
    /// </summary>
    private async Task<IReadOnlyList<TopVideo>> WithLocalizedTitlesAsync(string channelId, IReadOnlyList<TopVideo> top)
    {
        if (top.Count == 0 || top.All(v => v.TitleLocalized.Count > 0)) return top;
        try
        {
            var page = await _api.ListVideosAsync(new StudioVideoQuery { ChannelId = channelId, Limit = 100 });
            var byId = page.Items.GroupBy(v => v.Id).ToDictionary(g => g.Key, g => g.First().TitleLocalized);
            return top.Select(v => v.TitleLocalized.Count == 0 && byId.TryGetValue(v.Id, out var localized)
                ? new TopVideo(v.Id, v.Title, v.Views, localized)
                : v).ToList();
        }
        catch (StudioApiException ex)
        {
            Logger.Warn("top video titles kept as uploaded", ex);
            return top;
        }
    }

    public async Task LoadAsync()
    {
        if (_shell.ActiveChannel is not { } channel)
        {
            // Supersede any load still in flight for the previous channel, and stop its spinner.
            Interlocked.Increment(ref _generation);
            Data = null;
            ErrorKey = null;
            IsLoading = false;
            return;
        }

        var gen = Interlocked.Increment(ref _generation);
        IsLoading = true;
        ErrorKey = null;
        try
        {
            var to = _now();
            // "Last 28 days" is today and the 27 before it (the API counts both ends)
            var data = await _api.ChannelAnalyticsAsync(channel.Id, to.AddDays(-(Days - 1)), to);
            data.TopVideos = await WithLocalizedTitlesAsync(channel.Id, data.TopVideos);
            if (gen == _generation)
            {
                Data = data;
                IsHidden = false;
            }
        }
        catch (AuthRequiredException)
        {
            _shell.ReportAuthRequired();
        }
        catch (StudioApiException ex) when (ex.HidesFeature)
        {
            Logger.Info("analytics is off for this channel: " + ex.Message);
            if (gen == _generation)
            {
                Data = null;
                IsHidden = true;
            }

            _ = _shell.RefreshFeaturesAsync(force: true);
        }
        catch (RateLimitedException ex)
        {
            Logger.Warn("analytics rate limited", ex);
            if (gen == _generation) ErrorKey = "studio.analytics.failed";
        }
        catch (OperationCanceledException)
        {
            // Nothing to show for a cancelled load; the finally block stops the spinner.
        }
        catch (StudioApiException ex)
        {
            Logger.Warn("analytics did not load", ex);
            if (gen == _generation) ErrorKey = ex.Status == 403 ? "studio.analytics.ownerOnly" : "studio.analytics.failed";
        }
        finally
        {
            if (gen == _generation) IsLoading = false;
        }
    }
}
