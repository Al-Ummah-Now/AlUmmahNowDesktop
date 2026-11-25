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

    /// <summary>How long the app's end waits for details that are being sent.</summary>
    public static readonly TimeSpan DetailsLimit = TimeSpan.FromSeconds(4);

    private static readonly Logger Logger = Log.For("upload");

    private readonly StudioShellViewModel _shell;
    private readonly IStudioApi _api;
    private readonly Func<string, IUploadService> _serviceFor;
    private readonly string _stateDir;
    private readonly Func<DateTimeOffset> _now;
    private readonly HashSet<string> _finishing = new();
    private Dictionary<string, FinishStep> _steps = new();
    private IUploadService? _service;
    private readonly List<Task> _details = new();
    private volatile bool _isStopping;
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
            if (e.PropertyName == nameof(StudioShellViewModel.Features))
            {
                OnPropertyChanged(nameof(BulkScheduleOn));
                ApplyDefaults();
            }
        };
        _shell.ChannelChanged += (_, _) => _ = LoadPlaylistsAsync();
        SwitchAccount();
    }

    /// <summary>
    /// "Publish every … hours" across a batch is bulk scheduling (<c>bulkSchedule</c>). Off: the field is hidden and
    /// every scheduled file publishes at the chosen time.
    /// </summary>
    /// <summary>The page's clock: the real local time, or the demo's pinned day (AUN_DEMO_TODAY).</summary>
    public DateTimeOffset Now => _now();

    public bool BulkScheduleOn => _shell.Features.IsOn(UserFeatures.BulkSchedule);

    public bool CanStart => Staged.Count > 0 && _service != null && _shell.ActiveChannel != null && !ScheduleIsTooSoon;

    /// <summary>The first video's time has no room for the upload: nothing starts until another is chosen.</summary>
    public bool ScheduleIsTooSoon => Defaults.Visibility == UploadVisibility.Scheduled && Defaults.ScheduleStart is { } at && SchedulePick.IsSoon(at, _now());

    /// <summary>Count of uploads still moving (for the window title and the quit warning).</summary>
    public int ActiveCount => Items.Count(i => i.IsActive);

    /// <summary>An upload is moving or waits for its turn, as the queue says now, or a finished one's details are being sent.</summary>
    public bool IsWorking
    {
        get
        {
            // Stopped, it works no more
            if (_isStopping) return false;
            lock (_finishing)
            {
                if (_details.Count > 0) return true;
            }

            try
            {
                return _service?.List().Any(i => i.IsActive) == true;
            }
            catch (StudioApiException ex)
            {
                Logger.Warn("upload list unavailable", ex);
                return ActiveCount > 0;
            }
        }
    }

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
        if (!ScheduleIsTooSoon) return Queue();
        // The time has come while the page was open
        OnPropertyChanged(nameof(CanStart));
        return 0;
    }

    private int Queue()
    {
        var service = _service;
        var channel = _shell.ActiveChannel;
        if (_isStopping || service == null || channel == null || Staged.Count == 0) return 0;
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
        catch (StudioApiException ex) when (ex.HidesFeature)
        {
            Logger.Info("playlist not created: feature off: " + ex.Message);
            _ = _shell.RefreshFeaturesAsync(force: true);
            return null;
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
        if (_isStopping) return;
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
            if (start) Send(done.Id, done.VideoId!);
        }
    }

    /// <summary>The details of finished uploads that are being sent now.</summary>
    public Task WhenDetailsSentAsync()
    {
        lock (_finishing) return Task.WhenAll(_details);
    }

    /// <summary>
    /// The app ends: nothing new begins, the details that are being sent are waited for (<see cref="DetailsLimit"/>), what is
    /// left of them is saved, and the queue is stopped until its store is free.
    /// </summary>
    /// <exception cref="TimeoutException">Details were still being sent: they are sent again at the next start.</exception>
    public async Task StopAsync(CancellationToken cancellationToken = default, TimeSpan? detailsLimit = null)
    {
        _isStopping = true;
        var details = WhenDetailsSentAsync();
        var areSent = await Task.WhenAny(details, Task.Delay(detailsLimit ?? DetailsLimit, cancellationToken)).ConfigureAwait(false) == details;
        SaveSteps(_service?.AccountId, _steps);
        if (_service is { } service) await service.StopAsync(cancellationToken).ConfigureAwait(false);
        _service = null;
        if (!areSent) throw new TimeoutException("the details of an upload were still being sent");
    }

    public void Dispose()
    {
        _isStopping = true;
        _service?.Dispose();
        _service = null;
    }

    private void Send(string uploadId, string videoId)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_finishing) _details.Add(done.Task);
        _ = SendAsync();

        async Task SendAsync()
        {
            try
            {
                await FinishAsync(uploadId, videoId);
            }
            finally
            {
                // Out of the list before who waits for it goes on
                lock (_finishing) _details.Remove(done.Task);
                done.SetResult();
            }
        }
    }

    private async Task FinishAsync(string uploadId, string videoId)
    {
        // Of the account it began with, also when another one is chosen, or the app ends, meanwhile
        var steps = _steps;
        var account = _service?.AccountId;
        var step = steps[uploadId];
        try
        {
            if (step.Language != null) await _api.UpdateVideoAsync(step.ChannelId, videoId, new VideoChanges { Language = step.Language });
            foreach (var playlist in step.PlaylistIds) await _api.AddToPlaylistAsync(step.ChannelId, playlist, new[] { videoId });
            if (step.PublishAt is { } at) await _api.ScheduleAsync(step.ChannelId, videoId, at, step.TimeZone);
            steps.Remove(uploadId);
            Logger.Info("finished an upload's details");
        }
        catch (AuthRequiredException)
        {
            _shell.ReportAuthRequired();
        }
        catch (StudioApiException ex) when (ex.HidesFeature)
        {
            // Scheduling or another detail is off for this person (404 feature_off / 402): drop it quietly, no retry.
            Logger.Info("an upload detail is off: " + ex.Message);
            steps.Remove(uploadId);
            _ = _shell.RefreshFeaturesAsync(force: true);
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
            SaveSteps(account, steps);
            if (steps.TryGetValue(uploadId, out var left) && left.Attempts < MaxFinishAttempts)
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
        Staged = UploadPlanner.Plan(files, Defaults, _now(), BulkScheduleOn);
        OnPropertyChanged(nameof(CanStart));
    }

    private void ScanWatchFolder()
    {
        if (_watch == null || _service == null || _shell.ActiveChannel == null) return;
        var found = _watch.Scan().Select(f => f.Path).ToList();
        if (found.Count == 0) return;
        var keep = Staged;
        Staged = UploadPlanner.Plan(found, Defaults, _now(), BulkScheduleOn, movePast: true);
        Queue();
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
        if (_isStopping) return;
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

    private void SaveSteps() => SaveSteps(_service?.AccountId, _steps);

    private void SaveSteps(string? account, Dictionary<string, FinishStep> steps)
    {
        if (account == null) return;
        try
        {
            Directory.CreateDirectory(_stateDir);
            var path = StepsPath(account);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(steps));
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException ex)
        {
            Logger.Warn("finish steps not saved", ex);
        }
    }
}
