using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Engines;
using AlUmmahNow.Studio.Shell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlUmmahNow.Studio.Captions;

/// <summary>
/// Captions for one video (E19 19.4): drafts with their origin, server transcription (caption minutes) and free
/// transcription on this computer, and the draft editor.
/// </summary>
public sealed partial class CaptionsViewModel : ObservableObject
{
    /// <summary>Languages a new draft can be made in.</summary>
    public static readonly IReadOnlyList<string> Languages = new[] { "ar", "en" };

    private static readonly Logger Logger = Log.For("captions");

    private readonly StudioShellViewModel _shell;
    private readonly IStudioApi _api;
    private readonly ICaptionsApi _captions;
    private CancellationTokenSource? _onDevice;
    private int _generation;

    [ObservableProperty] private IReadOnlyList<StudioVideo> _videos = Array.Empty<StudioVideo>();
    [ObservableProperty] private StudioVideo? _video;
    [ObservableProperty] private CaptionDraftList? _drafts;
    [ObservableProperty] private IReadOnlyList<CaptionJob> _jobs = Array.Empty<CaptionJob>();
    [ObservableProperty] private CaptionMinutes? _minutes;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorKey;
    [ObservableProperty] private string? _noticeKey;
    [ObservableProperty] private string _language = "ar";
    [ObservableProperty] private IReadOnlyList<TranscriptionModel> _models = Array.Empty<TranscriptionModel>();
    [ObservableProperty] private string? _model;
    [ObservableProperty] private OnDeviceStage _stage;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private CueEditorViewModel? _editor;

    public CaptionsViewModel(StudioShellViewModel shell, IStudioApi api, ICaptionsApi captions, IOnDeviceTranscriber? transcriber)
    {
        _shell = shell;
        _api = api;
        _captions = captions;
        Transcriber = transcriber;
        _shell.ChannelChanged += (_, _) => _ = LoadVideosAsync();
    }

    public IOnDeviceTranscriber? Transcriber { get; }

    /// <summary>Asks the creator to confirm (an i18n key with its arguments); pages show a dialog.</summary>
    public Func<string, object[], Task<bool>> Confirm { get; set; } = (_, _) => Task.FromResult(false);

    /// <summary>Lets tests skip the waits of the draft poll.</summary>
    public Func<TimeSpan, CancellationToken, Task> Pause { get; set; } = Task.Delay;

    public bool OnDeviceAvailable => Transcriber?.IsAvailable == true;

    public bool IsBusyOnDevice => Stage is OnDeviceStage.Downloading or OnDeviceStage.Transcribing or OnDeviceStage.Sending or OnDeviceStage.Waiting;

    partial void OnStageChanged(OnDeviceStage value) => OnPropertyChanged(nameof(IsBusyOnDevice));

    private string? ChannelId => _shell.ActiveChannel?.Id;

    public async Task LoadVideosAsync()
    {
        Editor = null;
        if (ChannelId is not { } channel)
        {
            Videos = Array.Empty<StudioVideo>();
            Video = null;
            return;
        }

        try
        {
            var page = await _api.ListVideosAsync(new StudioVideoQuery { ChannelId = channel, Limit = 50 });
            Videos = page.Items;
            if (Video == null || Videos.All(v => v.Id != Video.Id)) Video = Videos.FirstOrDefault();
            else await LoadAsync();
        }
        catch (AuthRequiredException)
        {
            _shell.ReportAuthRequired();
        }
        catch (StudioApiException ex)
        {
            Logger.Warn("videos did not load", ex);
            ErrorKey = "studio.captions.failed";
        }
    }

    partial void OnVideoChanged(StudioVideo? value)
    {
        Editor = null;
        _ = LoadAsync();
    }

    /// <summary>Drafts, jobs and minutes of the selected video.</summary>
    public async Task LoadAsync()
    {
        if (Video is not { } video)
        {
            Drafts = null;
            Jobs = Array.Empty<CaptionJob>();
            Minutes = null;
            return;
        }

        var gen = Interlocked.Increment(ref _generation);
        IsLoading = true;
        ErrorKey = null;
        try
        {
            var drafts = _captions.DraftsAsync(video.Id, ChannelId);
            var jobs = _captions.JobsAsync(video.Id, ChannelId);
            var minutes = _captions.MinutesAsync(video.Id, ChannelId);
            await Task.WhenAll(drafts, jobs, minutes);
            if (gen != _generation) return;
            Drafts = drafts.Result;
            Jobs = jobs.Result;
            Minutes = minutes.Result;
        }
        catch (AuthRequiredException)
        {
            _shell.ReportAuthRequired();
        }
        catch (Exception ex) when (ex is StudioApiException or RateLimitedException)
        {
            Logger.Warn("captions did not load", ex);
            if (gen == _generation) ErrorKey = ex is StudioApiException { Status: 403 } ? "studio.captions.notAllowed" : "studio.captions.failed";
        }
        finally
        {
            if (gen == _generation) IsLoading = false;
        }
    }

    public bool HasDraft(string language) => Drafts?.Drafts.Any(d => d.Language == language) == true;

    /// <summary>Server transcription of the video in <see cref="Language"/>, using caption minutes.</summary>
    public async Task StartServerJobAsync()
    {
        if (Video is not { } video) return;
        ErrorKey = null;
        var replace = false;
        if (HasDraft(Language))
        {
            if (!await Confirm("studio.captions.replaceDraft", new object[] { LanguageKey(Language) })) return;
            replace = true;
        }

        try
        {
            var job = await _captions.StartJobAsync(video.Id, Language, replace, ChannelId);
            Jobs = new[] { job }.Concat(Jobs.Where(j => j.Id != job.Id)).ToList();
            NoticeKey = "studio.captions.jobStarted";
            Minutes = await _captions.MinutesAsync(video.Id, ChannelId);
        }
        catch (AuthRequiredException)
        {
            _shell.ReportAuthRequired();
        }
        catch (StudioApiException ex)
        {
            Logger.Warn("caption job did not start", ex);
            ErrorKey = ex.Status == 402 || ex.Code == "upgrade_required" ? "studio.captions.noMinutes"
                : ex.Code == "video_not_ready" ? "studio.captions.notReady"
                : "studio.captions.jobFailed";
        }
    }

    public async Task CancelJobAsync(string jobId)
    {
        if (Video is not { } video) return;
        try
        {
            var job = await _captions.CancelJobAsync(video.Id, jobId, ChannelId);
            Jobs = Jobs.Select(j => j.Id == job.Id ? job : j).ToList();
        }
        catch (StudioApiException ex)
        {
            Logger.Warn("caption job did not cancel", ex);
            ErrorKey = "studio.captions.jobFailed";
        }
    }

    public void RefreshModels()
    {
        if (!OnDeviceAvailable) return;
        try
        {
            Models = Transcriber!.Models();
            Model ??= Models.FirstOrDefault(m => m.Installed)?.Id ?? Models.FirstOrDefault()?.Id;
        }
        catch (Exception ex)
        {
            Logger.Warn("models unavailable", ex);
            ErrorKey = "studio.captions.device.failed";
        }
    }

    /// <summary>
    /// Free transcription on this computer: downloads the model when needed, transcribes <paramref name="mediaPath"/>
    /// (the video's file on this computer), sends the segments, then waits for the draft to appear.
    /// </summary>
    public async Task TranscribeOnDeviceAsync(string mediaPath)
    {
        if (Video is not { } video || !OnDeviceAvailable || Model is not { } model || IsBusyOnDevice) return;
        var replace = false;
        if (HasDraft(Language))
        {
            if (!await Confirm("studio.captions.replaceDraft", new object[] { LanguageKey(Language) })) return;
            replace = true;
        }

        ErrorKey = null;
        _onDevice = new CancellationTokenSource();
        var ct = _onDevice.Token;
        var language = Language;
        try
        {
            if (Models.FirstOrDefault(m => m.Id == model) is { Installed: false })
            {
                Stage = OnDeviceStage.Downloading;
                Progress = 0;
                await Transcriber!.DownloadAsync(model, new SyncProgress(p => Progress = p), ct);
                RefreshModels();
            }

            Stage = OnDeviceStage.Transcribing;
            Progress = 0;
            var segments = await Transcriber!.TranscribeAsync(model, mediaPath, language, new SyncProgress(p => Progress = p), ct);
            if (segments.Count == 0)
            {
                Stage = OnDeviceStage.Failed;
                ErrorKey = "studio.captions.device.noSpeech";
                return;
            }

            Stage = OnDeviceStage.Sending;
            var since = DateTimeOffset.UtcNow;
            try
            {
                await _captions.SubmitTranscriptAsync(video.Id, language, new OnDeviceTranscript(segments, model, replace), ChannelId, ct);
            }
            catch (StudioApiException ex) when (ex.Code == "draft_exists" && !replace)
            {
                if (!await Confirm("studio.captions.replaceDraft", new object[] { LanguageKey(language) }))
                {
                    Stage = OnDeviceStage.Idle;
                    return;
                }

                await _captions.SubmitTranscriptAsync(video.Id, language, new OnDeviceTranscript(segments, model, true), ChannelId, ct);
            }

            Stage = OnDeviceStage.Waiting;
            var poller = new ClaimPoller(_captions) { Pause = Pause };
            var (result, list, _) = await poller.WaitAsync(video.Id, language, ChannelId, since, ct);
            if (list != null && Video?.Id == video.Id) Drafts = list;
            Stage = result switch { ClaimResult.Ready => OnDeviceStage.Done, ClaimResult.Cancelled => OnDeviceStage.Idle, _ => OnDeviceStage.Failed };
            if (result == ClaimResult.Failed) ErrorKey = "studio.captions.device.claimFailed";
            if (result == ClaimResult.Ready) NoticeKey = "studio.captions.device.ready";
        }
        catch (OperationCanceledException)
        {
            Stage = OnDeviceStage.Idle;
        }
        catch (AuthRequiredException)
        {
            Stage = OnDeviceStage.Failed;
            _shell.ReportAuthRequired();
        }
        catch (StudioApiException ex)
        {
            Logger.Warn("on-device transcript was refused", ex);
            Stage = OnDeviceStage.Failed;
            ErrorKey = ex.Code switch
            {
                "claim_pending" => "studio.captions.device.pending",
                "invalid_transcript" => "studio.captions.device.invalid",
                _ => "studio.captions.device.failed",
            };
        }
        catch (Exception ex)
        {
            Logger.Warn("on-device transcription failed", ex);
            Stage = OnDeviceStage.Failed;
            ErrorKey = "studio.captions.device.failed";
        }
        finally
        {
            _onDevice?.Dispose();
            _onDevice = null;
        }
    }

    public void CancelOnDevice() => _onDevice?.Cancel();

    public void DeleteModel(string id)
    {
        try
        {
            Transcriber?.DeleteModel(id);
            RefreshModels();
        }
        catch (Exception ex)
        {
            Logger.Warn("model not deleted", ex);
        }
    }

    public async Task OpenDraftAsync(string language)
    {
        if (Video is not { } video) return;
        ErrorKey = null;
        try
        {
            var draft = await _captions.DraftAsync(video.Id, language, ChannelId);
            Editor = new CueEditorViewModel(_captions, video.Id, ChannelId, draft) { Confirm = Confirm };
            Editor.Closed += (_, _) =>
            {
                Editor = null;
                _ = LoadAsync();
            };
        }
        catch (AuthRequiredException)
        {
            _shell.ReportAuthRequired();
        }
        catch (StudioApiException ex)
        {
            Logger.Warn("draft did not open", ex);
            ErrorKey = "studio.captions.failed";
        }
    }

    public static string LanguageKey(string language) => "studio.captions.lang." + language;

    private sealed class SyncProgress : IProgress<double>
    {
        private readonly Action<double> _report;

        public SyncProgress(Action<double> report) => _report = report;

        public void Report(double value) => _report(Math.Max(0, Math.Min(1, value)));
    }
}

public enum OnDeviceStage { Idle, Downloading, Transcribing, Sending, Waiting, Done, Failed }
