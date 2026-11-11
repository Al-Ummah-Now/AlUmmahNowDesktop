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
    private readonly Func<string, RecentlyDeleted> _trashFor;
    private readonly Func<DateTimeOffset> _now;
    private readonly HashSet<string> _selected = new();
    private RecentlyDeleted? _trash;
    private string? _trashAccount;
    private int _generation;

    [ObservableProperty] private IReadOnlyList<StudioVideo> _items = Array.Empty<StudioVideo>();
    [ObservableProperty] private string? _cursor;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorKey;
    [ObservableProperty] private string? _noticeKey;
    [ObservableProperty] private StudioVisibilityFilter _visibility;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private IReadOnlyList<DeletedVideo> _deleted = Array.Empty<DeletedVideo>();

    public VideosViewModel(StudioShellViewModel shell, IStudioApi api, Func<string, RecentlyDeleted> trashFor, Func<IReadOnlyList<string>> loadRecents, Action<IReadOnlyList<string>> saveRecents, Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _shell = shell;
        _api = api;
        _trashFor = trashFor;
        _now = now ?? (() => DateTimeOffset.UtcNow);
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
        SyncTrash();
        _selected.Clear();
        OnPropertyChanged(nameof(Selected));
        Items = Array.Empty<StudioVideo>();
        Cursor = null;
        await FetchAsync(gen, null);
        await PurgeDueAsync();
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

    /// <summary>Moves videos to Recently deleted (private, hidden in Studio). The UI confirms first.</summary>
    public async Task<int> DeleteAsync(IReadOnlyCollection<string> videoIds)
    {
        if (_shell.ActiveChannel is not { } channel || _trash == null) return 0;
        var targets = Items.Where(v => videoIds.Contains(v.Id)).ToList();
        var done = 0;
        try
        {
            var wasPublic = targets.Where(v => v.IsPublic).Select(v => v.Id).ToList();
            if (wasPublic.Count > 0) await _api.BulkUpdateAsync(channel.Id, wasPublic, new BulkVideoChanges { IsPublic = false });
            foreach (var v in targets)
            {
                _trash.Add(new DeletedVideo { VideoId = v.Id, ChannelId = channel.Id, Title = v.Title, WasPublic = v.IsPublic, DeletedAt = _now() });
                done++;
            }
        }
        catch (Exception ex) when (Handle(ex, "studio.videos.deleteFailed"))
        {
        }

        Items = Items.Where(v => !_trash.Contains(v.Id)).ToList();
        foreach (var v in targets) _selected.Remove(v.Id);
        OnPropertyChanged(nameof(Selected));
        Deleted = _trash.Items.Where(d => d.ChannelId == channel.Id).ToList();
        if (done > 0) NoticeKey = "studio.videos.movedToDeleted";
        return done;
    }

    public async Task<bool> RestoreAsync(string videoId)
    {
        var item = _trash?.Items.FirstOrDefault(d => d.VideoId == videoId);
        if (item == null) return false;
        try
        {
            if (item.WasPublic) await _api.UpdateVideoAsync(item.ChannelId, videoId, new VideoChanges { IsPublic = true });
            _trash!.Remove(videoId);
            NoticeKey = "studio.videos.restored";
            await LoadAsync();
            return true;
        }
        catch (Exception ex) when (Handle(ex, "studio.videos.restoreFailed"))
        {
            return false;
        }
    }

    /// <summary>Deletes on the server now. The UI confirms first: this can't be undone.</summary>
    public async Task<bool> DeleteNowAsync(string videoId)
    {
        var item = _trash?.Items.FirstOrDefault(d => d.VideoId == videoId);
        if (item == null) return false;
        try
        {
            await _api.DeleteVideoAsync(item.ChannelId, videoId);
            _trash!.Remove(videoId);
            Deleted = _trash.Items.Where(d => d.ChannelId == _shell.ActiveChannel?.Id).ToList();
            NoticeKey = "studio.videos.deletedForever";
            return true;
        }
        catch (StudioApiException ex) when (ex.Status == 404)
        {
            _trash!.Remove(videoId);
            Deleted = _trash.Items.Where(d => d.ChannelId == _shell.ActiveChannel?.Id).ToList();
            return true;
        }
        catch (Exception ex) when (Handle(ex, "studio.videos.deleteFailed"))
        {
            return false;
        }
    }

    private async Task PurgeDueAsync()
    {
        if (_trash == null) return;
        foreach (var due in _trash.Due(_now()))
        {
            try
            {
                await _api.DeleteVideoAsync(due.ChannelId, due.VideoId);
                _trash.Remove(due.VideoId);
            }
            catch (StudioApiException ex) when (ex.Status == 404)
            {
                _trash.Remove(due.VideoId);
            }
            catch (Exception ex) when (ex is StudioApiException || ex is AuthRequiredException)
            {
                Logger.Warn("a due deletion will be tried again later", ex);
            }
        }

        Deleted = _trash.Items.Where(d => d.ChannelId == _shell.ActiveChannel?.Id).ToList();
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
            var fresh = page.Items.Where(v => _trash?.Contains(v.Id) != true);
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

    private void SyncTrash()
    {
        var account = _shell.ActiveAccount?.UserId;
        if (account == null)
        {
            _trash = null;
            _trashAccount = null;
            Deleted = Array.Empty<DeletedVideo>();
            return;
        }

        if (_trashAccount != account)
        {
            _trash = _trashFor(account);
            _trashAccount = account;
        }

        Deleted = _trash!.Items.Where(d => d.ChannelId == _shell.ActiveChannel?.Id).ToList();
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
