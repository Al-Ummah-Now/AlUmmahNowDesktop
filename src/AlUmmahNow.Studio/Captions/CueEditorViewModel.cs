using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Engines;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlUmmahNow.Studio.Captions;

/// <summary>Edits one caption draft: cue text, start and end, split and merge, delete; undo, save, publish, delete.</summary>
public sealed partial class CueEditorViewModel : ObservableObject
{
    private static readonly Logger Logger = Log.For("captions");

    private readonly ICaptionsApi _captions;
    private readonly string _videoId;
    private readonly string? _channelId;
    private readonly SnapshotHistory<IReadOnlyList<CaptionCue>> _history;
    private IReadOnlyList<CaptionCue> _saved;
    private (int Cue, int Segment)? _typing;

    [ObservableProperty] private CaptionDraftSummary _summary;
    [ObservableProperty] private int _selected = -1;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string? _errorKey;
    [ObservableProperty] private string? _noticeKey;

    public CueEditorViewModel(ICaptionsApi captions, string videoId, string? channelId, CaptionDraft draft)
    {
        _captions = captions;
        _videoId = videoId;
        _channelId = channelId;
        _summary = draft.Summary;
        _saved = draft.Cues;
        _history = new SnapshotHistory<IReadOnlyList<CaptionCue>>(draft.Cues, 200, new CueListComparer());
        _history.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(Cues));
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        };
        if (draft.Cues.Count > 0) _selected = 0;
    }

    public event EventHandler? Closed;

    public Func<string, object[], Task<bool>> Confirm { get; set; } = (_, _) => Task.FromResult(false);

    public string Language => Summary.Language;

    /// <summary>Arabic drafts are edited right to left.</summary>
    public bool IsRtl => Language == "ar";

    public IReadOnlyList<CaptionCue> Cues => _history.Current;

    public bool IsDirty => !new CueListComparer().Equals(_history.Current, _saved);

    public bool CanUndo => _history.CanUndo;

    public bool CanRedo => _history.CanRedo;

    /// <summary>Typing in the same segment keeps one undo step.</summary>
    public void SetText(int cue, int segment, string text)
    {
        var next = CueEditing.SetText(Cues, cue, segment, text);
        if (_typing == (cue, segment)) _history.Amend(next);
        else _history.Record(next);
        _typing = (cue, segment);
    }

    public bool Split(int cue, int segment, int offset) => Apply(CueEditing.Split(Cues, cue, segment, offset), cue);

    public bool MergeWithNext(int cue) => Apply(CueEditing.MergeWithNext(Cues, cue), cue);

    public bool Nudge(int cue, CueEditing.Edge edge, int deltaMs) => Apply(CueEditing.Nudge(Cues, cue, edge, deltaMs), cue);

    public bool SetTimes(int cue, int startMs, int endMs)
    {
        var ok = Apply(CueEditing.SetTimes(Cues, cue, startMs, endMs), cue);
        if (!ok) ErrorKey = "studio.captions.editor.badTimes";
        return ok;
    }

    public void Delete(int cue)
    {
        Apply(CueEditing.Delete(Cues, cue), Math.Min(cue, Cues.Count - 2));
    }

    public void Undo()
    {
        _typing = null;
        _history.Undo();
        Selected = Math.Min(Selected, Cues.Count - 1);
    }

    public void Redo()
    {
        _typing = null;
        _history.Redo();
        Selected = Math.Min(Selected, Cues.Count - 1);
    }

    private bool Apply(IReadOnlyList<CaptionCue>? next, int select)
    {
        _typing = null;
        if (next == null) return false;
        ErrorKey = null;
        _history.Record(next);
        Selected = Math.Max(-1, Math.Min(select, Cues.Count - 1));
        return true;
    }

    public async Task<bool> SaveAsync()
    {
        if (!IsDirty) return true;
        IsSaving = true;
        ErrorKey = null;
        try
        {
            var cues = Cues;
            Summary = await _captions.SaveDraftAsync(_videoId, Language, Summary.Version, cues, _channelId);
            _saved = cues;
            _typing = null;
            OnPropertyChanged(nameof(IsDirty));
            NoticeKey = "studio.captions.editor.saved";
            return true;
        }
        catch (StudioApiException ex)
        {
            Logger.Warn("draft not saved", ex);
            ErrorKey = ex.Code == "draft_conflict" || ex.Status == 409 ? "studio.captions.editor.conflict"
                : ex.Status == 400 ? "studio.captions.editor.invalid"
                : "studio.captions.editor.saveFailed";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>Saves, then publishes the draft as the video's captions. Asks before replacing an uploaded track.</summary>
    public async Task<bool> PublishAsync()
    {
        if (!await SaveAsync()) return false;
        IsSaving = true;
        try
        {
            try
            {
                Summary = await _captions.PublishDraftAsync(_videoId, Language, Summary.Version, false, _channelId);
            }
            catch (StudioApiException ex) when (ex.Code == "replaces_upload")
            {
                IsSaving = false;
                if (!await Confirm("studio.captions.editor.replacesUpload", Array.Empty<object>())) return false;
                IsSaving = true;
                Summary = await _captions.PublishDraftAsync(_videoId, Language, Summary.Version, true, _channelId);
            }

            NoticeKey = "studio.captions.editor.published";
            return true;
        }
        catch (StudioApiException ex)
        {
            Logger.Warn("draft not published", ex);
            ErrorKey = ex.Code == "draft_conflict" ? "studio.captions.editor.conflict" : "studio.captions.editor.publishFailed";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    public async Task DeleteDraftAsync()
    {
        if (!await Confirm("studio.captions.editor.deleteConfirm", new object[] { CaptionsViewModel.LanguageKey(Language) })) return;
        try
        {
            await _captions.DeleteDraftAsync(_videoId, Language, _channelId);
            Closed?.Invoke(this, EventArgs.Empty);
        }
        catch (StudioApiException ex)
        {
            Logger.Warn("draft not deleted", ex);
            ErrorKey = "studio.captions.editor.deleteFailed";
        }
    }

    /// <summary>Closes the editor; asks first when there are unsaved changes.</summary>
    public async Task CloseAsync()
    {
        if (IsDirty && !await Confirm("studio.captions.editor.discard", Array.Empty<object>())) return;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"0:01.500" for a cue time.</summary>
    public static string Time(int ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}" : $"{t.Minutes}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    /// <summary>Reads "m:ss.mmm", "h:mm:ss.mmm" or plain seconds; null when it isn't a time.</summary>
    public static int? ParseTime(string text)
    {
        var parts = (text ?? "").Trim().Split(':');
        if (parts.Length is < 1 or > 3) return null;
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) || v < 0) return null;
            total = (total * 60) + v;
        }

        return (int)Math.Round(total * 1000);
    }

    private sealed class CueListComparer : IEqualityComparer<IReadOnlyList<CaptionCue>>
    {
        public bool Equals(IReadOnlyList<CaptionCue>? x, IReadOnlyList<CaptionCue>? y) => ReferenceEquals(x, y) || (x != null && y != null && x.SequenceEqual(y));

        public int GetHashCode(IReadOnlyList<CaptionCue> obj) => obj.Count;
    }
}
