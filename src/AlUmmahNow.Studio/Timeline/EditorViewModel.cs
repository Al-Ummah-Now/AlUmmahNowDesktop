using System.Text.Json;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Engines;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlUmmahNow.Studio.Timeline;

/// <summary>
/// The Studio editor (E19 19.5): an edit list (Core's EDL project) on one video track. Add videos, then split, trim,
/// ripple delete, join and move clips; every edit is a Core <c>edl_apply_json</c> call so the rules match every app.
/// Undo and redo keep whole project snapshots. Export renders <c>edl_flatten_json</c> on the platform's media stack, and
/// the result can go straight into the upload queue.
/// </summary>
public sealed partial class EditorViewModel : ObservableObject
{
    /// <summary>Clips can't be cut or trimmed shorter than this.</summary>
    public const long MinClipMs = 100;

    private static readonly Logger Logger = Log.For("editor");

    private readonly IEdlEditing _edl;
    private readonly IMediaProbe _probe;
    private readonly ITimelineRenderer _renderer;
    private readonly Func<DateTimeOffset> _now;
    private readonly SnapshotHistory<string> _history;
    private string _saved;
    private CancellationTokenSource? _export;
    private int _nextId;

    [ObservableProperty] private FlattenedTimeline _timeline = new(0, Array.Empty<FlatTrack>());
    [ObservableProperty] private string? _selectedClipId;
    [ObservableProperty] private long _playheadMs;
    [ObservableProperty] private string? _errorKey;
    [ObservableProperty] private string? _errorDetail;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private double _exportProgress;
    [ObservableProperty] private string? _exportedPath;

    public EditorViewModel(IEdlEditing edl, IMediaProbe probe, ITimelineRenderer renderer, Func<DateTimeOffset>? now = null)
    {
        _edl = edl;
        _probe = probe;
        _renderer = renderer;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _saved = EdlProject.New("project-" + _now().ToUnixTimeMilliseconds(), "", _now());
        _history = new SnapshotHistory<string>(_saved);
        _history.Changed += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>The project JSON as it stands.</summary>
    public string Project => _history.Current;

    public bool CanUndo => _history.CanUndo;

    public bool CanRedo => _history.CanRedo;

    public bool IsDirty => _history.Current != _saved;

    public IReadOnlyList<FlatClip> Clips => Timeline.Tracks.FirstOrDefault(t => t.Id == EdlProject.VideoTrack)?.Clips ?? Array.Empty<FlatClip>();

    public FlatClip? Selected => Clips.FirstOrDefault(c => c.ClipId == SelectedClipId);

    public IReadOnlyDictionary<string, string> SourceNames { get; private set; } = new Dictionary<string, string>();

    /// <summary>The clip under the playhead, or the selected one.</summary>
    public FlatClip? Target => Selected ?? ClipAt(PlayheadMs);

    public bool CanSplit => ClipAt(PlayheadMs) is { } c && PlayheadMs - c.TimelineStartMs >= MinClipMs && End(c) - PlayheadMs >= MinClipMs;

    public bool CanTrimStart => Target is { } c && PlayheadMs > c.TimelineStartMs && End(c) - PlayheadMs >= MinClipMs;

    public bool CanTrimEnd => Target is { } c && PlayheadMs - c.TimelineStartMs >= MinClipMs && PlayheadMs < End(c);

    public bool CanJoin => Target is { } c && Next(c) is { } n && n.SourceId == c.SourceId && n.TimelineStartMs == End(c) && n.InMs == c.OutMs;

    public bool CanExport => Clips.Count > 0 && !IsExporting;

    public FlatClip? ClipAt(long ms) => Clips.FirstOrDefault(c => ms >= c.TimelineStartMs && ms < End(c));

    public static long End(FlatClip c) => c.TimelineStartMs + c.TimelineDurationMs;

    /// <summary>Adds each video at the end of the timeline. Files without a video track are skipped with a message.</summary>
    public async Task AddVideosAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorKey = null;
        try
        {
            var project = Project;
            var added = 0;
            foreach (var path in paths)
            {
                MediaSourceInfo info;
                try
                {
                    info = await _probe.ProbeAsync(path, cancellationToken).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is IOException)
                {
                    Fail("studio.editor.error.unreadable", Path.GetFileName(path));
                    continue;
                }

                if (!info.HasVideo || (info.DurationMs ?? 0) < MinClipMs)
                {
                    Fail("studio.editor.error.noVideo", Path.GetFileName(path));
                    continue;
                }

                var sourceId = NewId("s", project);
                var withSource = EdlProject.AddSource(project, sourceId, Path.GetFullPath(path), info);
                var next = TryApply(withSource, EdlOp.Append(EdlProject.VideoTrack, NewId("c", withSource), sourceId, 0, info.DurationMs!.Value));
                if (next == null) continue;
                project = next;
                added++;
            }

            // A skipped file's message stays up after the others are added.
            if (added > 0) Commit(project);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public bool Split()
    {
        if (!CanSplit || ClipAt(PlayheadMs) is not { } c) return false;
        var id = NewId("c", Project);
        return Edit(EdlOp.Split(c.ClipId, PlayheadMs, id), select: id);
    }

    /// <summary>Cuts the target clip's start back to the playhead; the rest of the clip stays where it is.</summary>
    public bool TrimStartToPlayhead()
    {
        if (!CanTrimStart || Target is not { } c) return false;
        var newIn = c.InMs + (long)Math.Round((PlayheadMs - c.TimelineStartMs) * c.Speed);
        return Edit(EdlOp.Trim(c.ClipId, newIn, c.OutMs), select: c.ClipId);
    }

    public bool TrimEndToPlayhead()
    {
        if (!CanTrimEnd || Target is not { } c) return false;
        var newOut = c.InMs + (long)Math.Round((PlayheadMs - c.TimelineStartMs) * c.Speed);
        return Edit(EdlOp.Trim(c.ClipId, c.InMs, newOut), select: c.ClipId);
    }

    /// <summary>Removes the target clip and closes the gap.</summary>
    public bool Delete()
    {
        if (Target is not { } c) return false;
        var ok = Edit(EdlOp.RippleDeleteClip(c.ClipId), select: null);
        if (ok) PlayheadMs = Math.Min(PlayheadMs, Timeline.DurationMs);
        return ok;
    }

    /// <summary>Joins the target clip with the next one when they continue the same video (the inverse of a split).</summary>
    public bool JoinWithNext()
    {
        if (!CanJoin || Target is not { } c || Next(c) is not { } n) return false;
        return Edit(EdlOp.Join(c.ClipId, n.ClipId), select: c.ClipId);
    }

    /// <summary>Moves the target clip so it starts at <paramref name="startMs"/>; Core refuses landing on another clip.</summary>
    public bool MoveTo(long startMs)
    {
        if (Target is not { } c) return false;
        return Edit(EdlOp.Move(c.ClipId, EdlProject.VideoTrack, Math.Max(0, startMs)), select: c.ClipId);
    }

    /// <summary>Swaps the target clip with its neighbour, keeping the timeline gapless (keyboard "move earlier/later").</summary>
    public bool MoveBy(int direction)
    {
        if (Target is not { } c) return false;
        var clips = Clips;
        var i = clips.ToList().FindIndex(x => x.ClipId == c.ClipId);
        var j = i + Math.Sign(direction);
        if (i < 0 || j < 0 || j >= clips.Count) return false;
        var other = clips[j];
        var (first, second) = direction < 0 ? (c, other) : (other, c);
        var start = Math.Min(c.TimelineStartMs, other.TimelineStartMs);
        // Park the later clip past the end, then place both: two Core moves, one undo step.
        var parked = TryApply(Project, EdlOp.Move(second.ClipId, EdlProject.VideoTrack, Timeline.DurationMs + 1));
        var placed = parked == null ? null : TryApply(parked, EdlOp.Move(first.ClipId, EdlProject.VideoTrack, start));
        var done = placed == null ? null : TryApply(placed, EdlOp.Move(second.ClipId, EdlProject.VideoTrack, start + first.TimelineDurationMs));
        if (done == null) return false;
        Commit(done);
        SelectedClipId = c.ClipId;
        return true;
    }

    public void Undo() => _history.Undo();

    public void Redo() => _history.Redo();

    public void Select(string? clipId) => SelectedClipId = clipId;

    public void Seek(long ms) => PlayheadMs = Math.Clamp(ms, 0, Timeline.DurationMs);

    /// <summary>Selects the clip at a timeline position (a click) and moves the playhead there.</summary>
    public void ClickAt(long ms)
    {
        Seek(ms);
        SelectedClipId = ClipAt(PlayheadMs)?.ClipId;
    }

    /// <summary>Starts a new, empty project.</summary>
    public void New()
    {
        _saved = EdlProject.New("project-" + _now().ToUnixTimeMilliseconds(), "", _now());
        _history.Reset(_saved);
        SelectedClipId = null;
        PlayheadMs = 0;
        ExportedPath = null;
    }

    /// <summary>Opens a saved project (older schemas are upgraded by Core).</summary>
    public async Task<bool> OpenAsync(string path)
    {
        try
        {
            var json = _edl.Load(await File.ReadAllTextAsync(path).ConfigureAwait(true));
            _edl.Flatten(json);
            // Sources must be local files on this computer (see EdlProject.IsLocalMediaPath).
            if (EdlProject.SourcePaths(json).FirstOrDefault(p => !EdlProject.IsLocalMediaPath(p)) is { } bad)
            {
                Fail("studio.editor.error.source", Path.GetFileName(bad));
                return false;
            }

            _saved = json;
            _history.Reset(json);
            SelectedClipId = null;
            PlayheadMs = 0;
            ErrorKey = null;
            return true;
        }
        catch (Exception ex) when (ex is EdlEditException || ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            Fail("studio.editor.error.open", Path.GetFileName(path));
            return false;
        }
    }

    public async Task<bool> SaveAsync(string path)
    {
        try
        {
            var json = EdlProject.Touch(Project, _now());
            await File.WriteAllTextAsync(path, json).ConfigureAwait(true);
            _history.Amend(json);
            _saved = json;
            OnPropertyChanged(nameof(IsDirty));
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            Fail("studio.editor.error.save", Path.GetFileName(path));
            return false;
        }
    }

    /// <summary>Renders the timeline to <paramref name="output"/>. Returns the file, or null when it failed or was cancelled.</summary>
    public async Task<string?> ExportAsync(string output)
    {
        if (!CanExport) return null;
        _export = new CancellationTokenSource();
        IsExporting = true;
        ExportProgress = 0;
        ExportedPath = null;
        ErrorKey = null;
        try
        {
            var timeline = _edl.Flatten(Project);
            await _renderer.RenderAsync(timeline, EdlProject.Settings(Project), output, new Progress<double>(p => ExportProgress = p), _export.Token).ConfigureAwait(true);
            ExportProgress = 1;
            ExportedPath = output;
            return output;
        }
        catch (OperationCanceledException)
        {
            TryDelete(output);
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error("export failed", ex);
            TryDelete(output);
            Fail("studio.editor.error.export", ex.Message);
            return null;
        }
        finally
        {
            IsExporting = false;
            _export.Dispose();
            _export = null;
        }
    }

    public void CancelExport() => _export?.Cancel();

    partial void OnSelectedClipIdChanged(string? value) => RaiseCommands();

    partial void OnPlayheadMsChanged(long value) => RaiseCommands();

    partial void OnIsExportingChanged(bool value) => OnPropertyChanged(nameof(CanExport));

    private bool Edit(string op, string? select)
    {
        var next = TryApply(Project, op);
        if (next == null) return false;
        Commit(next);
        SelectedClipId = select != null && Clips.Any(c => c.ClipId == select) ? select : null;
        ErrorKey = null;
        return true;
    }

    private string? TryApply(string project, string op)
    {
        try
        {
            return _edl.Apply(project, op);
        }
        catch (EdlEditException ex)
        {
            Fail(ex.Kind switch
            {
                EdlErrorKind.NotFound => "studio.editor.error.notFound",
                EdlErrorKind.Conflict => "studio.editor.error.overlap",
                _ => "studio.editor.error.refused",
            }, null);
            Logger.Info("edit refused: " + ex.Message);
            return null;
        }
    }

    private void Commit(string project) => _history.Record(project);

    private void Fail(string key, string? detail)
    {
        ErrorDetail = detail;
        ErrorKey = key;
    }

    private void Refresh()
    {
        try
        {
            Timeline = _edl.Flatten(Project);
            SourceNames = EdlProject.SourceNames(Project);
        }
        catch (EdlEditException ex)
        {
            Logger.Error("flatten failed", ex);
            Timeline = new FlattenedTimeline(0, Array.Empty<FlatTrack>());
        }

        if (SelectedClipId != null && Clips.All(c => c.ClipId != SelectedClipId)) SelectedClipId = null;
        if (PlayheadMs > Timeline.DurationMs) PlayheadMs = Timeline.DurationMs;
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(Clips));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanExport));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(CanSplit));
        OnPropertyChanged(nameof(CanTrimStart));
        OnPropertyChanged(nameof(CanTrimEnd));
        OnPropertyChanged(nameof(CanJoin));
    }

    private FlatClip? Next(FlatClip c)
    {
        var clips = Clips;
        var i = clips.ToList().FindIndex(x => x.ClipId == c.ClipId);
        return i >= 0 && i + 1 < clips.Count ? clips[i + 1] : null;
    }

    /// <summary>A clip or source id not used in the project yet.</summary>
    private string NewId(string prefix, string project)
    {
        string id;
        do
        {
            id = prefix + (++_nextId);
        }
        while (project.Contains("\"" + id + "\"", StringComparison.Ordinal));
        return id;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
