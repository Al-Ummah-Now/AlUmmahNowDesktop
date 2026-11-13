using CommunityToolkit.Mvvm.ComponentModel;

namespace AlUmmahNow.Admin.Review;

/// <summary>
/// The queue list: Action needed or History, sorted by SLA deadline then risk, with a live search.
/// Rows are text only, so the last list stays on screen offline (decisions do not).
/// </summary>
public sealed class ReviewQueue : ObservableObject
{
    public static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(250);

    private readonly IReviewService _service;
    private readonly TimeProvider _time;
    private readonly Dictionary<ReviewTab, IReadOnlyList<ReviewItem>> _byTab = new();
    private CancellationTokenSource? _load;
    private ReviewTab _tab = ReviewTab.ActionNeeded;
    private string _query = "";
    private IReadOnlyList<ReviewItem> _items = Array.Empty<ReviewItem>();
    private bool _isOffline;
    private bool _isLoading;
    private ReviewException? _error;
    private string? _nextCursor;

    public ReviewQueue(IReviewService service, TimeProvider? time = null)
    {
        _service = service;
        _time = time ?? TimeProvider.System;
    }

    public ReviewTab Tab
    {
        get => _tab;
        set
        {
            if (!SetProperty(ref _tab, value)) return;
            Items = _byTab.TryGetValue(value, out var cached) ? Filter(cached, _query) : Array.Empty<ReviewItem>();
            _ = RefreshAsync();
        }
    }

    public string Query => _query;

    public IReadOnlyList<ReviewItem> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

    /// <summary>The last request could not reach the server: rows are the last ones seen, decisions are off.</summary>
    public bool IsOffline
    {
        get => _isOffline;
        private set => SetProperty(ref _isOffline, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public ReviewException? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public bool HasMore => _nextCursor != null;

    /// <summary>Waiting items whose SLA has passed (shown in red, read by Narrator as "overdue").</summary>
    public int OverdueCount => Items.Count(i => i.IsOpen && i.SlaDueAt is { } due && due <= _time.GetUtcNow());

    /// <summary>
    /// The queue's order: earliest SLA deadline first (none last), then highest risk, then oldest.
    /// History is newest decision first.
    /// </summary>
    public static IReadOnlyList<ReviewItem> Order(IEnumerable<ReviewItem> items, ReviewTab tab) => tab == ReviewTab.History
        ? items.OrderByDescending(i => i.DecidedAt ?? i.CreatedAt ?? DateTimeOffset.MinValue).ThenBy(i => i.Id, StringComparer.Ordinal).ToList()
        : items.OrderBy(i => i.SlaDueAt ?? DateTimeOffset.MaxValue)
            .ThenByDescending(i => i.Risk ?? -1)
            .ThenBy(i => i.CreatedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>Matches title, channel or id, any case, every word.</summary>
    public static bool Matches(ReviewItem item, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var hay = string.Join(" ", item.Title, item.ChannelName, item.Id, item.SubjectId, item.TopCategory);
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(w => hay.Contains(w, StringComparison.CurrentCultureIgnoreCase));
    }

    private static IReadOnlyList<ReviewItem> Filter(IReadOnlyList<ReviewItem> items, string query) =>
        string.IsNullOrWhiteSpace(query) ? items : items.Where(i => Matches(i, query)).ToList();

    /// <summary>Autocomplete for the search box: titles and channels of rows already loaded, as the reviewer types.</summary>
    public IReadOnlyList<string> Suggestions(string text, int limit = 8)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var t = text.Trim();
        return _byTab.Values.SelectMany(v => v)
            .SelectMany(i => new[] { i.Title, i.ChannelName })
            .Where(s => !string.IsNullOrEmpty(s) && s!.Contains(t, StringComparison.CurrentCultureIgnoreCase))
            .Select(s => s!)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(s => s.StartsWith(t, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
            .ThenBy(s => s.Length)
            .Take(limit)
            .ToList();
    }

    /// <summary>Live search: filters what is loaded at once, then asks the server after a short pause.</summary>
    public Task SearchAsync(string text)
    {
        _query = text ?? "";
        OnPropertyChanged(nameof(Query));
        if (_byTab.TryGetValue(_tab, out var cached)) Items = Filter(cached, _query);
        return RefreshAsync(SearchDelay);
    }

    public async Task RefreshAsync(TimeSpan delay = default)
    {
        _load?.Cancel();
        var cts = _load = new CancellationTokenSource();
        var tab = _tab;
        var query = _query;
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, _time, cts.Token).ConfigureAwait(true);
            IsLoading = true;
            var page = await _service.QueueAsync(tab, query, null, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;
            var ordered = Order(page.Items, tab);
            if (string.IsNullOrWhiteSpace(query)) _byTab[tab] = ordered;
            else _byTab[tab] = Order(Merge(_byTab.TryGetValue(tab, out var old) ? old : Array.Empty<ReviewItem>(), ordered), tab);
            _nextCursor = page.NextCursor;
            Items = Filter(ordered, query);
            IsOffline = false;
            Error = null;
            OnPropertyChanged(nameof(HasMore));
            OnPropertyChanged(nameof(OverdueCount));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (OfflineException)
        {
            IsOffline = true;
        }
        catch (ReviewException ex)
        {
            Error = ex;
        }
        finally
        {
            if (ReferenceEquals(_load, cts)) IsLoading = false;
        }
    }

    public async Task LoadMoreAsync()
    {
        if (_nextCursor == null) return;
        try
        {
            var page = await _service.QueueAsync(_tab, _query, _nextCursor).ConfigureAwait(true);
            _nextCursor = page.NextCursor;
            var all = Order(Merge(Items, page.Items), _tab);
            if (string.IsNullOrWhiteSpace(_query)) _byTab[_tab] = all;
            Items = all;
            OnPropertyChanged(nameof(HasMore));
        }
        catch (OfflineException)
        {
            IsOffline = true;
        }
        catch (ReviewException ex)
        {
            Error = ex;
        }
    }

    private static IEnumerable<ReviewItem> Merge(IEnumerable<ReviewItem> a, IEnumerable<ReviewItem> b)
    {
        var map = new Dictionary<string, ReviewItem>(StringComparer.Ordinal);
        foreach (var i in a) map[i.Id] = i;
        foreach (var i in b) map[i.Id] = i;
        return map.Values;
    }

    /// <summary>A decision (or its undo) changed a row: move it between the tabs at once, without waiting for a refresh.</summary>
    public void Apply(ReviewItem updated)
    {
        foreach (var tab in new[] { ReviewTab.ActionNeeded, ReviewTab.History })
        {
            var rows = _byTab.TryGetValue(tab, out var r) ? r.Where(i => i.Id != updated.Id).ToList() : new List<ReviewItem>();
            var belongs = tab == ReviewTab.ActionNeeded ? updated.IsOpen : !updated.IsOpen;
            if (belongs) rows.Add(updated);
            _byTab[tab] = Order(rows, tab);
        }

        Items = Filter(_byTab[_tab], _query);
        OnPropertyChanged(nameof(OverdueCount));
    }

    public ReviewItem? Find(string id) => _byTab.Values.SelectMany(v => v).FirstOrDefault(i => i.Id == id);
}
