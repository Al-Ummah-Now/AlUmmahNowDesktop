using System.Text.Json;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Shell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlUmmahNow.Studio.Upload;

/// <summary>What to do once an upload has created its video: things the start of an upload can't set.</summary>
public sealed class FinishStep
{
    public string ChannelId { get; set; } = "";
    public string? Language { get; set; }
    public List<string> PlaylistIds { get; set; } = new();
    public DateTimeOffset? PublishAt { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// The Upload page: stage files (one, many or a folder), apply "set for all" values, start them on
/// the active account's resumable queue, show progress and finish each video (language, playlists,
/// schedule) once the server has it. Finish steps are saved, so they also run after a restart.
/// Call <see cref="Tick"/> from a UI timer; it applies queue changes on the UI thread.
/// </summary>
public sealed partial class UploadsViewModel : ObservableObject, IDisposable
{
    public const int MaxFinishAttempts = 5;
    private static readonly Logger Logger = Log.For("upload");

    private readonly StudioShellViewModel _shell;
    private readonly IStudioApi _api;
    private readonly Func<string, IUploadService> _serviceFor;
    private readonly string _stateDir;
    private readonly Func<DateTimeOffset> _now;
    private readonly HashSet<string> _finishing = new();
    private Dictionary<string, FinishStep> _steps = new();
    private IUploadService? _service;
    private int _dirty = 1;
    private FolderWatch? _watch;

    [ObservableProperty] private UploadDefaults _defaults = new();
    [ObservableProperty] private IReadOnlyList<PlannedUpload> _staged = Array.Empty<PlannedUpload>();
    [ObservableProperty] private IReadOnlyList<UploadSnapshot> _items = Array.Empty<UploadSnapshot>();
    [ObservableProperty] private IReadOnlyList<StudioPlaylist> _playlists = Array.Empty<StudioPlaylist>();
    [ObservableProperty] private string? _errorKey;
    [ObservableProperty] private string? _watchFolder;

    public UploadsViewModel(StudioShellViewModel shell, IStudioApi api, Func<string, IUploadService> serviceFor, string stateDir, Func<DateTimeOffset>? now = null)
    {
        _shell = shell;
        _api = api;
        _serviceFor = serviceFor;
        _stateDir = stateDir;
        _now = now ?? (() => DateTimeOffset.Now);
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StudioShellViewModel.ActiveAccount)) SwitchAccount();
        };
        _shell.ChannelChanged += (_, _) => _ = LoadPlaylistsAsync();
        SwitchAccount();
    }

    public bool CanStart => Staged.Count > 0 && _service != null && _shell.ActiveChannel != null;

    /// <summary>Count of uploads still moving (for the window title and the quit warning).</summary>
    public int ActiveCount => Items.Count(i => i.IsActive);

    public void AddFiles(IEnumerable<string> paths)
    {
        var files = Staged.Select(s => s.FilePath).Concat(paths.Where(UploadPlanner.IsVideo)).Distinct(StringComparer.Ordinal).ToList();
        Replan(files);
    }

    public void AddFolder(string folder, bool recursive) => AddFiles(UploadPlanner.VideosIn(folder, recursive));

    public void RemoveStaged(string path) => Replan(Staged.Select(s => s.FilePath).Where(p => p != path).ToList());

    public void ClearStaged() => Replan(new List<string>());

    /// <summary>Re-applies the defaults after the user changed them.</summary>
    public void ApplyDefaults() => Replan(Staged.Select(s => s.FilePath).ToList());

    /// <summary>Lets the user change one staged title after the pattern filled it.</summary>
    public void RenameStaged(string path, string title)
    {
        var p = Staged.FirstOrDefault(s => s.FilePath == path);
        if (p == null || string.IsNullOrWhiteSpace(title)) return;
        p.Title = TitlePatterns.Truncate(title.Trim(), TitlePatterns.MaxTitleLength);
        OnPropertyChanged(nameof(Staged));
    }

    /// <summary>Queues every staged file on the active channel. Returns how many were queued.</summary>
    public int Start()
    {
        var service = _service;
        var channel = _shell.ActiveChannel;
        if (service == null || channel == null || Staged.Count == 0) return 0;
        ErrorKey = null;
        var queued = 0;
        foreach (var plan in Staged)
        {
            try
            {
                var id = service.Enqueue(new UploadSpec
                {
                    FilePath = plan.FilePath,
                    Title = plan.Title,
                    Description = string.IsNullOrWhiteSpace(plan.Description) ? null : plan.Description,
                    ChannelId = channel.Id,
                    // Scheduled videos stay private until the scheduler publishes them.
                    IsPublic = plan.IsPublic && plan.PublishAt == null,
                    Tags = plan.Tags,
                    Category = plan.Category,
                    Priority = 0,
                });
                if (plan.Language != null || plan.PlaylistIds.Count > 0 || plan.PublishAt != null)
                {
                    _steps[id] = new FinishStep { ChannelId = channel.Id, Language = plan.Language, PlaylistIds = plan.PlaylistIds.ToList(), PublishAt = plan.PublishAt, TimeZone = plan.TimeZone };
                }

                queued++;
            }
            catch (StudioApiException ex)
            {
                Logger.Warn("a file was not queued", ex);
                ErrorKey = "studio.upload.notQueued";
            }
        }

        SaveSteps();
        Logger.Info($"queued {queued} of {Staged.Count} files");
        Staged = Array.Empty<PlannedUpload>();
        OnPropertyChanged(nameof(CanStart));
        Interlocked.Exchange(ref _dirty, 1);
        Tick();
        return queued;
    }

    public void Pause(string id) => Act(s => s.Pause(id));
    public void Resume(string id) => Act(s => s.Resume(id));
    public void Retry(string id) => Act(s => s.Retry(id));

    /// <summary>The UI confirms first: the partial upload is discarded on the server.</summary>
    public async Task CancelAsync(string id)
    {
        if (_service is { } s) await s.CancelAsync(id);
        Interlocked.Exchange(ref _dirty, 1);
    }

    public void Remove(string id)
    {
        Act(s => s.Remove(id));
        if (_steps.Remove(id)) SaveSteps();
    }

    /// <summary>Removes every finished upload from the list.</summary>
    public void ClearFinished()
    {
        foreach (var done in Items.Where(i => i.Phase == UploadPhase.Done && !_steps.ContainsKey(i.Id)).ToList()) Remove(done.Id);
    }

    /// <summary>Queues new video files that appear in the folder with the current defaults.</summary>
    public void Watch(string? folder)
    {
        WatchFolder = string.IsNullOrEmpty(folder) ? null : folder;
        _watch = WatchFolder == null ? null : new FolderWatch(WatchFolder);
    }

    public FinishStep? FinishStepFor(string id) => _steps.TryGetValue(id, out var s) ? s : null;

    public async Task LoadPlaylistsAsync()
    {
        var channel = _shell.ActiveChannel;
        if (channel == null)
        {
            Playlists = Array.Empty<StudioPlaylist>();
            return;
        }

        try
        {
            var list = await _api.ListPlaylistsAsync(channel.Id);
            if (_shell.ActiveChannel?.Id == channel.Id) Playlists = list;
        }
        catch (AuthRequiredException)
        {
            _shell.ReportAuthRequired();
        }
        catch (StudioApiException ex)
        {
            Logger.Warn("playlists did not load", ex);
        }
    }

    public async Task<StudioPlaylist?> CreatePlaylistAsync(string title, bool isPublic)
    {
        var channel = _shell.ActiveChannel;
        if (channel == null || string.IsNullOrWhiteSpace(title)) return null;
        try
        {
            var p = await _api.CreatePlaylistAsync(channel.Id, title.Trim(), isPublic);
            Playlists = Playlists.Append(p).ToList();
            return p;
        }
        catch (StudioApiException ex)
        {
            Logger.Warn("playlist not created", ex);
            ErrorKey = "studio.upload.playlistFailed";
            return null;
        }
    }

    /// <summary>Applies queue changes, scans the watched folder and finishes completed uploads.</summary>
    public void Tick()
    {
        ScanWatchFolder();
        if (Interlocked.Exchange(ref _dirty, 0) == 0 || _service == null) return;
        IReadOnlyList<UploadSnapshot> list;
        try
        {
            list = _service.List();
        }
        catch (StudioApiException ex)
        {
            Logger.Warn("upload list unavailable", ex);
            return;
        }

        Items = list;
        OnPropertyChanged(nameof(ActiveCount));
        foreach (var done in list.Where(i => i.Phase == UploadPhase.Done && i.VideoId != null && _steps.ContainsKey(i.Id)))
        {
            bool start;
            lock (_finishing) start = _finishing.Add(done.Id);
            if (start) _ = FinishAsync(done.Id, done.VideoId!);
        }
    }

    public void Dispose()
    {
        _service?.Dispose();
        _service = null;
    }

    private async Task FinishAsync(string uploadId, string videoId)
    {
        var step = _steps[uploadId];
        try
        {
            if (step.Language != null) await _api.UpdateVideoAsync(step.ChannelId, videoId, new VideoChanges { Language = step.Language });
            foreach (var playlist in step.PlaylistIds) await _api.AddToPlaylistAsync(step.ChannelId, playlist, new[] { videoId });
            if (step.PublishAt is { } at) await _api.ScheduleAsync(step.ChannelId, videoId, at, step.TimeZone);
            _steps.Remove(uploadId);
            Logger.Info("finished an upload's details");
        }
        catch (AuthRequiredException)
        {
            _shell.ReportAuthRequired();
        }
        catch (StudioApiException ex)
        {
            step.Attempts++;
            step.LastError = ex.Message;
            Logger.Warn($"finishing an upload failed (attempt {step.Attempts})", ex);
            if (step.Attempts >= MaxFinishAttempts) ErrorKey = "studio.upload.finishFailed";
        }
        finally
        {
            SaveSteps();
            if (_steps.TryGetValue(uploadId, out var left) && left.Attempts < MaxFinishAttempts)
            {
                // Try again on a later tick.
                _ = Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * left.Attempts))).ContinueWith(_ =>
                {
                    lock (_finishing) _finishing.Remove(uploadId);
                    Interlocked.Exchange(ref _dirty, 1);
                }, TaskScheduler.Default);
            }

            Interlocked.Exchange(ref _dirty, 1);
        }
    }

    private void Replan(List<string> files)
    {
        Staged = UploadPlanner.Plan(files, Defaults, _now());
        OnPropertyChanged(nameof(CanStart));
    }

    private void ScanWatchFolder()
    {
        if (_watch == null || _service == null || _shell.ActiveChannel == null) return;
        var found = _watch.Scan().Select(f => f.Path).ToList();
        if (found.Count == 0) return;
        var keep = Staged;
        Staged = UploadPlanner.Plan(found, Defaults, _now());
        Start();
        Staged = keep;
    }

    private void Act(Action<IUploadService> action)
    {
        if (_service == null) return;
        try
        {
            action(_service);
        }
        catch (StudioApiException ex)
        {
            Logger.Warn("upload action failed", ex);
        }

        Interlocked.Exchange(ref _dirty, 1);
    }

    private void SwitchAccount()
    {
        var account = _shell.ActiveAccount;
        if (_service?.AccountId == account?.UserId) return;
        _service?.Dispose();
        _service = null;
        _steps = new Dictionary<string, FinishStep>();
        Items = Array.Empty<UploadSnapshot>();
        if (account == null) return;
        try
        {
            _service = _serviceFor(account.UserId);
            _service.Changed += (_, _) => Interlocked.Exchange(ref _dirty, 1);
            _steps = LoadSteps(account.UserId);
        }
        catch (StudioApiException ex)
        {
            Logger.Error("the upload queue could not open", ex);
            ErrorKey = "studio.upload.queueUnavailable";
        }

        Interlocked.Exchange(ref _dirty, 1);
        OnPropertyChanged(nameof(CanStart));
    }

    private string StepsPath(string accountId) => Path.Combine(_stateDir, "finish-" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(accountId)) + ".json");

    private Dictionary<string, FinishStep> LoadSteps(string accountId)
    {
        try
        {
            var path = StepsPath(accountId);
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, FinishStep>>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException)
        {
            Logger.Warn("saved finish steps unreadable", ex);
            return new();
        }
    }

    private void SaveSteps()
    {
        if (_service == null) return;
        try
        {
            Directory.CreateDirectory(_stateDir);
            var path = StepsPath(_service.AccountId);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_steps));
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException ex)
        {
            Logger.Warn("finish steps not saved", ex);
        }
    }
}
