using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Studio;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlUmmahNow.Studio.Search;

/// <summary>
/// Live autocomplete (#23): debounced (200 ms), the previous request cancelled when typing
/// continues, stale answers dropped, a 429 pauses suggestions for Retry-After, recent searches on
/// an empty field. Suggestions come from aun-search, plus local matches the server can't return
/// (private videos already loaded). Never logs the typed text.
/// </summary>
public sealed partial class SuggestViewModel : ObservableObject
{
    public const int MaxRecents = 20;
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

    private static readonly Logger Logger = Log.For("search");

    private readonly Func<string, CancellationToken, Task<IReadOnlyList<Suggestion>>> _remote;
    private readonly Func<string, IEnumerable<string>> _local;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<IReadOnlyList<string>> _saveRecents;
    private CancellationTokenSource? _pending;
    private DateTimeOffset _pausedUntil;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private IReadOnlyList<Suggestion> _suggestions = Array.Empty<Suggestion>();
    [ObservableProperty] private IReadOnlyList<string> _recents;

    public SuggestViewModel(
        Func<string, CancellationToken, Task<IReadOnlyList<Suggestion>>> remote,
        Func<string, IEnumerable<string>> local,
        IReadOnlyList<string> recents,
        Action<IReadOnlyList<string>> saveRecents,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? now = null)
    {
        _remote = remote;
        _local = local;
        _recents = recents.Take(MaxRecents).ToList();
        _saveRecents = saveRecents;
        _delay = delay ?? Task.Delay;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Raised when the user submits a search (Enter or a picked suggestion).</summary>
    public event EventHandler<string>? Submitted;

    /// <summary>The user typed: schedule suggestions for the new text.</summary>
    public Task TypedAsync(string text)
    {
        Text = text;
        _pending?.Cancel();
        _pending = new CancellationTokenSource();
        return RefreshAsync(text, _pending.Token);
    }

    public void Submit(string? text = null)
    {
        var query = (text ?? Text).Trim();
        _pending?.Cancel();
        Text = query;
        Suggestions = Array.Empty<Suggestion>();
        if (query.Length > 0) AddRecent(query);
        Submitted?.Invoke(this, query);
    }

    public void RemoveRecent(string query)
    {
        Recents = Recents.Where(r => !Same(r, query)).ToList();
        _saveRecents(Recents);
    }

    /// <summary>The UI confirms first.</summary>
    public void ClearRecents()
    {
        Recents = Array.Empty<string>();
        _saveRecents(Recents);
    }

    private async Task RefreshAsync(string text, CancellationToken ct)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            Suggestions = Array.Empty<Suggestion>();
            return;
        }

        var matchingRecents = Recents.Where(r => Contains(r, trimmed)).Take(5).Select(r => new Suggestion(r, SuggestionKind.Query, null, null)).ToList();
        var local = _local(trimmed).Where(t => Contains(t, trimmed)).Distinct(StringComparer.CurrentCultureIgnoreCase).Take(5).Select(t => new Suggestion(t, SuggestionKind.Video, null, null)).ToList();
        Suggestions = Merge(matchingRecents, local, Array.Empty<Suggestion>());

        try
        {
            await _delay(Debounce, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested || _now() < _pausedUntil) return;
        try
        {
            var remote = await _remote(trimmed, ct);
            // Never show results for text the field no longer contains.
            if (ct.IsCancellationRequested || Text.Trim() != trimmed) return;
            Suggestions = Merge(matchingRecents, local, remote);
        }
        catch (OperationCanceledException)
        {
        }
        catch (RateLimitedException ex)
        {
            _pausedUntil = _now() + ex.RetryAfter;
            Logger.Info("suggestions paused by the server");
        }
        catch (StudioApiException ex)
        {
            // Keep local and recent suggestions; nothing alarming in the UI.
            Logger.Warn("suggestions unavailable", ex);
        }
        catch (AlUmmahNow.CoreBridge.AuthRequiredException)
        {
        }
    }

    private static IReadOnlyList<Suggestion> Merge(IEnumerable<Suggestion> recents, IEnumerable<Suggestion> local, IEnumerable<Suggestion> remote)
    {
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var list = new List<Suggestion>();
        foreach (var s in recents.Concat(local).Concat(remote))
        {
            if (seen.Add(s.Text)) list.Add(s);
            if (list.Count >= 10) break;
        }

        return list;
    }

    private void AddRecent(string query)
    {
        Recents = new[] { query }.Concat(Recents.Where(r => !Same(r, query))).Take(MaxRecents).ToList();
        _saveRecents(Recents);
    }

    private static bool Same(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    private static bool Contains(string haystack, string needle) => Normalize(haystack).Contains(Normalize(needle), StringComparison.Ordinal);

    /// <summary>Case, tashkeel and common Arabic letter variants folded, as the server does.</summary>
    public static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s.Normalize(System.Text.NormalizationForm.FormKC))
        {
            if (ch >= 'ً' && ch <= 'ٟ' || ch == 'ٰ' || ch == 'ـ') continue; // tashkeel, tatweel
            sb.Append(ch switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ة' => 'ه',
                'ى' => 'ي',
                _ => char.ToLowerInvariant(ch),
            });
        }

        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
