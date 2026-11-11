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

    public async Task LoadAsync()
    {
        if (_shell.ActiveChannel is not { } channel)
        {
            Data = null;
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
            if (gen == _generation) Data = data;
        }
        catch (AuthRequiredException)
        {
            _shell.ReportAuthRequired();
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
