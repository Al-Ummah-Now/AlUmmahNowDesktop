using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Search;
using AlUmmahNow.Studio.Shell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlUmmahNow.Studio.Videos;

/// <summary>
/// The Videos page: the active channel's videos with search (live autocomplete) and a visibility
/// filter, paging, selection for bulk edit, single edit, delete to Recently deleted and restore.
/// </summary>
public sealed partial class VideosViewModel : ObservableObject
{
    private static readonly Logger Logger = Log.For("videos");

    private readonly StudioShellViewModel _shell;
    private readonly IStudioApi _api;
    private readonly HashSet<string> _selected = new();
    private int _generation;

    [ObservableProperty] private IReadOnlyList<StudioVideo> _items = Array.Empty<StudioVideo>();
    [ObservableProperty] private string? _cursor;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorKey;
    [ObservableProperty] private string? _noticeKey;
    [ObservableProperty] private StudioVisibilityFilter _visibility;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private IReadOnlyList<DeletedVideo> _deleted = Array.Empty<DeletedVideo>();

    public VideosViewModel(StudioShellViewModel shell, IStudioApi api, Func<IReadOnlyList<string>> loadRecents, Action<IReadOnlyList<string>> saveRecents, Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _shell = shell;
        _api = api;
        Search = new SuggestViewModel(
            (text, ct) => _api.SuggestAsync(text, _shell.ActiveChannel?.Id, ct),
            text => Items.Select(v => v.Title),
            loadRecents(),
            saveRecents,
            delay,
            now);
        Search.Submitted += (_, q) =>
        {
            Query = q;
            _ = LoadAsync();
        };
        _shell.ChannelChanged += (_, _) =>
        {
            Query = "";
            Search.Text = "";
            _ = LoadAsync();
        };
    }

    public SuggestViewModel Search { get; }

    public IReadOnlyCollection<string> Selected => _selected.ToList();

    public bool HasMore => Cursor != null;

    public bool IsSelected(string id) => _selected.Contains(id);

    public void SetSelected(string id, bool selected)
    {
        if (selected) _selected.Add(id);
        else _selected.Remove(id);
        OnPropertyChanged(nameof(Selected));
    }

    public void SelectAll(bool selected)
    {
        _selected.Clear();
        if (selected) foreach (var v in Items) _selected.Add(v.Id);
        OnPropertyChanged(nameof(Selected));
    }

    partial void OnVisibilityChanged(StudioVisibilityFilter value) => _ = LoadAsync();

    public async Task LoadAsync()
    {
        var gen = Interlocked.Increment(ref _generation);
        _selected.Clear();
        OnPropertyChanged(nameof(Selected));
        Items = Array.Empty<StudioVideo>();
        Cursor = null;
        await FetchAsync(gen, null);
        await LoadDeletedAsync();
    }

    /// <summary>Re-reads Recently deleted from the server.</summary>
    public async Task LoadDeletedAsync()
    {
        if (_shell.ActiveChannel is not { } channel)
        {
            Deleted = Array.Empty<DeletedVideo>();
            return;
        }

        try
        {
            var list = await _api.ListDeletedAsync(channel.Id);
            if (_shell.ActiveChannel?.Id == channel.Id) Deleted = list;
        }
        catch (Exception ex) when (ex is StudioApiException || ex is RateLimitedException)
        {
            // Owner-only on the server; editors simply don't see the list.
            Logger.Warn("recently deleted unavailable", ex);
            Deleted = Array.Empty<DeletedVideo>();
        }
        catch (AuthRequiredException)
        {
            _shell.ReportAuthRequired();
        }
    }

    public Task LoadMoreAsync() => Cursor == null || IsLoading ? Task.CompletedTask : FetchAsync(_generation, Cursor);

    public async Task<bool> SaveAsync(string videoId, VideoChanges changes)
    {
        if (_shell.ActiveChannel is not { } channel) return false;
        try
        {
            await _api.UpdateVideoAsync(channel.Id, videoId, changes);
            NoticeKey = "studio.videos.saved";
            await RefreshOneAsync(videoId);
            return true;
        }
        catch (Exception ex) when (Handle(ex, "studio.videos.saveFailed"))
        {
            return false;
        }
    }

    /// <summary>Applies the same changes to every selected video.</summary>
    public async Task<bool> BulkEditAsync(BulkVideoChanges changes)
    {
        if (_shell.ActiveChannel is not { } channel || _selected.Count == 0 || changes.IsEmpty) return false;
        var ids = _selected.ToList();
        try
        {
            await _api.BulkUpdateAsync(channel.Id, ids, changes);
            Logger.Info($"bulk edit applied to {ids.Count} videos");
            NoticeKey = "studio.videos.bulkSaved";
            await LoadAsync();
            return true;
        }
        catch (Exception ex) when (Handle(ex, "studio.videos.saveFailed"))
        {
            return false;
        }
    }

    public async Task<bool> AddSelectedToPlaylistAsync(string playlistId)
    {
        if (_shell.ActiveChannel is not { } channel || _selected.Count == 0) return false;
        try
        {
            await _api.AddToPlaylistAsync(channel.Id, playlistId, _selected.ToList());
            NoticeKey = "studio.videos.addedToPlaylist";
            return true;
        }
        catch (Exception ex) when (Handle(ex, "studio.videos.saveFailed"))
        {
            return false;
        }
    }

    /// <summary>Soft delete: gone from the channel at once, restorable for 30 days. The UI confirms first.</summary>
    public async Task<int> DeleteAsync(IReadOnlyCollection<string> videoIds)
    {
        if (_shell.ActiveChannel is not { } channel || videoIds.Count == 0) return 0;
        try
        {
            var outcome = await _api.DeleteVideosAsync(channel.Id, videoIds.ToList());
            var gone = outcome.Succeeded.ToHashSet();
            Items = Items.Where(v => !gone.Contains(v.Id)).ToList();
            foreach (var id in gone) _selected.Remove(id);
            OnPropertyChanged(nameof(Selected));
            if (outcome.Failed.Count > 0)
            {
                Logger.Warn($"{outcome.Failed.Count} of {videoIds.Count} deletes refused");
                ErrorKey = "studio.videos.deleteFailed";
            }

            if (gone.Count > 0) NoticeKey = "studio.videos.movedToDeleted";
            await LoadDeletedAsync();
            return gone.Count;
        }
        catch (Exception ex) when (Handle(ex, "studio.videos.deleteFailed"))
        {
            return 0;
        }
    }

    public async Task<bool> RestoreAsync(string videoId)
    {
        if (_shell.ActiveChannel is not { } channel) return false;
        try
        {
            var outcome = await _api.RestoreVideosAsync(channel.Id, new[] { videoId });
            if (outcome.Failed.Count > 0)
            {
                ErrorKey = "studio.videos.restoreFailed";
                await LoadDeletedAsync();
                return false;
            }

            NoticeKey = "studio.videos.restored";
            await LoadAsync();
            return true;
        }
        catch (Exception ex) when (Handle(ex, "studio.videos.restoreFailed"))
        {
            return false;
        }
    }

    /// <summary>"Delete permanently": queued for the hourly cleanup. The UI confirms first: it can't be undone.</summary>
    public async Task<bool> DeleteNowAsync(string videoId)
    {
        if (_shell.ActiveChannel is not { } channel) return false;
        try
        {
            var outcome = await _api.PurgeVideosAsync(channel.Id, new[] { videoId });
            if (outcome.Failed.Count > 0) ErrorKey = "studio.videos.deleteFailed";
            else NoticeKey = "studio.videos.deletedForever";
            await LoadDeletedAsync();
            return outcome.Failed.Count == 0;
        }
        catch (Exception ex) when (Handle(ex, "studio.videos.deleteFailed"))
        {
            return false;
        }
    }

    private async Task FetchAsync(int gen, string? cursor)
    {
        if (_shell.ActiveChannel is not { } channel) return;
        IsLoading = true;
        ErrorKey = null;
        try
        {
            var page = await _api.ListVideosAsync(new StudioVideoQuery { ChannelId = channel.Id, Text = Query, Visibility = Visibility, Cursor = cursor });
            if (gen != _generation) return;
            var fresh = page.Items;
            Items = cursor == null ? fresh.ToList() : Items.Concat(fresh).GroupBy(v => v.Id).Select(g => g.First()).ToList();
            Cursor = page.Cursor;
            OnPropertyChanged(nameof(HasMore));
        }
        catch (Exception ex) when (Handle(ex, "studio.videos.loadFailed"))
        {
        }
        finally
        {
            if (gen == _generation) IsLoading = false;
        }
    }

    private async Task RefreshOneAsync(string videoId)
    {
        if (_shell.ActiveChannel is not { } channel) return;
        try
        {
            var fresh = await _api.GetVideoAsync(channel.Id, videoId);
            Items = Items.Select(v => v.Id == videoId ? fresh : v).ToList();
        }
        catch (StudioApiException ex)
        {
            Logger.Warn("video refresh failed", ex);
        }
    }

    private bool Handle(Exception ex, string key)
    {
        switch (ex)
        {
            case AuthRequiredException:
                _shell.ReportAuthRequired();
                return true;
            case StudioApiException api:
                Logger.Warn(key, api);
                ErrorKey = api.Status == 403 ? "studio.videos.notAllowed" : api.Status == null ? "studio.error.offline" : key;
                return true;
            case RateLimitedException:
                ErrorKey = "studio.error.server";
                return true;
            default:
                return false;
        }
    }
}
