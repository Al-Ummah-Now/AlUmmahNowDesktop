using AlUmmahNow.Admin.Media;
using AlUmmahNow.CoreBridge.Logging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlUmmahNow.Admin.Review;

/// <summary>What happened to a decision, for the toast and the screen reader.</summary>
public enum DecisionResultKind { Done, Undone, Conflict, ClaimedByOther, Offline, ReasonRequired, UndoExpired, Failed, StepUpCancelled }

public sealed class DecisionOutcome
{
    public DecisionOutcome(DecisionResultKind kind, ReviewItem? item = null, ReviewException? error = null)
    {
        Kind = kind;
        Item = item;
        Error = error;
    }

    public DecisionResultKind Kind { get; }
    public ReviewItem? Item { get; }
    public ReviewException? Error { get; }
    public AlreadyDecidedException? Conflict => Error as AlreadyDecidedException;
}

/// <summary>A decision that can still be reversed: the 10-second undo toast.</summary>
public sealed class PendingUndo
{
    public PendingUndo(string reviewId, ReviewDecision decision, DateTimeOffset until)
    {
        ReviewId = reviewId;
        Decision = decision;
        Until = until;
    }

    public string ReviewId { get; }
    public ReviewDecision Decision { get; }
    public DateTimeOffset Until { get; }
}

/// <summary>
/// The open review: claims it (3 minutes, renewed while open), decides with the version it read,
/// and keeps the 10-second undo. Decisions take effect at once on the server; undo asks the
/// server to reverse it (the server sends the creator a correction). Nothing here is cached.
/// </summary>
public sealed class ReviewSession : ObservableObject, IDisposable
{
    public static readonly TimeSpan ClaimRenewEvery = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(10);

    private static readonly Logger Logger = Log.For("review");

    private readonly IReviewService _service;
    private readonly ReviewQueue? _queue;
    private readonly TimeProvider _time;
    private readonly Func<CancellationToken, Task<bool>>? _stepUp;
    private readonly RevealPolicy _reveal;
    private readonly string _installId;
    private ITimer? _renew;
    private ReviewDetail? _detail;
    private bool _isLoading;
    private bool _isBusy;
    private ReviewException? _claimError;
    private ReviewException? _loadError;
    private PendingUndo? _undo;

    public ReviewSession(IReviewService service, ReviewQueue? queue, RevealPolicy reveal, string installId,
        Func<CancellationToken, Task<bool>>? stepUp = null, TimeProvider? time = null)
    {
        _service = service;
        _queue = queue;
        _reveal = reveal;
        _installId = installId;
        _stepUp = stepUp;
        _time = time ?? TimeProvider.System;
    }

    public ReviewDetail? Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    /// <summary>A decision is in flight: the buttons wait, so a double press is one decision.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>Someone else holds the claim, or claiming failed. The item is read-only meanwhile.</summary>
    public ReviewException? ClaimError
    {
        get => _claimError;
        private set => SetProperty(ref _claimError, value);
    }

    public ReviewException? LoadError
    {
        get => _loadError;
        private set => SetProperty(ref _loadError, value);
    }

    public PendingUndo? Undo
    {
        get => _undo;
        private set => SetProperty(ref _undo, value);
    }

    public RevealPolicy Reveal => _reveal;

    public bool CanDecide => Detail is { } d && d.Item.IsOpen && ClaimError == null && !IsBusy;

    public async Task OpenAsync(string id, CancellationToken ct = default)
    {
        Close();
        IsLoading = true;
        LoadError = null;
        try
        {
            Detail = await _service.DetailAsync(id, ct).ConfigureAwait(true);
            _reveal.Enter(id);
            if (Detail.Item.IsOpen) await ClaimAsync(ct).ConfigureAwait(true);
        }
        catch (ReviewException ex)
        {
            LoadError = ex;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(CanDecide));
        }
    }

    private async Task ClaimAsync(CancellationToken ct = default)
    {
        if (Detail == null) return;
        try
        {
            await _service.ClaimAsync(Detail.Item.Id, ct).ConfigureAwait(true);
            ClaimError = null;
            _renew ??= _time.CreateTimer(_ => _ = RenewAsync(), null, ClaimRenewEvery, ClaimRenewEvery);
        }
        catch (ReviewException ex)
        {
            ClaimError = ex;
        }

        OnPropertyChanged(nameof(CanDecide));
    }

    private async Task RenewAsync()
    {
        var detail = Detail;
        if (detail == null || !detail.Item.IsOpen) return;
        try
        {
            await _service.ClaimAsync(detail.Item.Id).ConfigureAwait(false);
        }
        catch (OfflineException)
        {
            // Next tick tries again; the claim lasts 3 minutes.
        }
        catch (ReviewException ex)
        {
            Logger.Warn("claim renewal refused: " + ex.GetType().Name);
            ClaimError = ex;
        }
    }

    /// <summary>The audited per-item "Show": the reveal is recorded first, and nothing is shown if that fails.</summary>
    public async Task<bool> RevealAsync(string target = "item")
    {
        // The id is pinned before the await: a reveal recorded for one item never shows another.
        var id = Detail?.Item.Id;
        if (id == null) return false;
        try
        {
            await _service.RevealAsync(id, target, _installId).ConfigureAwait(true);
            if (Detail?.Item.Id != id) return false;
            _reveal.Reveal(id);
            return true;
        }
        catch (ReviewException ex)
        {
            Logger.Warn("reveal not recorded, item stays blurred: " + ex.GetType().Name);
            return false;
        }
    }

    public async Task<DecisionOutcome> DecideAsync(ReviewDecision decision, string? reasonCode = null, string? note = null)
    {
        var detail = Detail;
        if (detail == null || decision == ReviewDecision.Undo) return new DecisionOutcome(DecisionResultKind.Failed);
        if (decision is ReviewDecision.Reject or ReviewDecision.RequestChanges && !ReviewReasons.IsKnown(reasonCode))
            return new DecisionOutcome(DecisionResultKind.ReasonRequired);
        if (_queue?.IsOffline == true) return new DecisionOutcome(DecisionResultKind.Offline);
        IsBusy = true;
        OnPropertyChanged(nameof(CanDecide));
        try
        {
            var item = await WithStepUpAsync(() => _service.DecideAsync(detail.Item.Id, decision, reasonCode, note, detail.Item.Version)).ConfigureAwait(true);
            Applied(detail, item);
            var until = item.UndoUntil ?? _time.GetUtcNow() + UndoWindow;
            Undo = new PendingUndo(item.Id, decision, until);
            Stop();
            return new DecisionOutcome(DecisionResultKind.Done, item);
        }
        catch (ReviewException ex)
        {
            return Failed(ex);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanDecide));
        }
    }

    /// <summary>Reverses the last decision while the toast is up.</summary>
    public async Task<DecisionOutcome> UndoAsync()
    {
        var undo = Undo;
        if (undo == null || _time.GetUtcNow() > undo.Until) return new DecisionOutcome(DecisionResultKind.UndoExpired);
        IsBusy = true;
        try
        {
            var item = await WithStepUpAsync(() => _service.DecideAsync(undo.ReviewId, ReviewDecision.Undo, null, null, null)).ConfigureAwait(true);
            Undo = null;
            if (Detail?.Item.Id == item.Id) Applied(Detail, item);
            else _queue?.Apply(item);
            if (Detail?.Item.Id == item.Id && item.IsOpen) await ClaimAsync().ConfigureAwait(true);
            return new DecisionOutcome(DecisionResultKind.Undone, item);
        }
        catch (ReviewException ex)
        {
            Undo = null;
            return Failed(ex);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanDecide));
        }
    }

    /// <summary>The toast timed out or was dismissed.</summary>
    public void ExpireUndo()
    {
        if (Undo != null && _time.GetUtcNow() >= Undo.Until) Undo = null;
    }

    private void Applied(ReviewDetail detail, ReviewItem item)
    {
        Detail = new ReviewDetail(item, detail.Findings, detail.History, detail.Subject, detail.Frames, item.IsOpen ? detail.Stream : null);
        _queue?.Apply(item);
    }

    private async Task<T> WithStepUpAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(true);
        }
        catch (StepUpRequiredException) when (_stepUp != null)
        {
            if (!await _stepUp(CancellationToken.None).ConfigureAwait(true)) throw new StepUpCancelledException();
            return await call().ConfigureAwait(true);
        }
    }

    private DecisionOutcome Failed(ReviewException ex)
    {
        var kind = ex switch
        {
            AlreadyDecidedException => DecisionResultKind.Conflict,
            ClaimedByOtherException => DecisionResultKind.ClaimedByOther,
            OfflineException => DecisionResultKind.Offline,
            UndoExpiredException => DecisionResultKind.UndoExpired,
            StepUpCancelledException => DecisionResultKind.StepUpCancelled,
            ReviewApiException { Code: "reason_required" } => DecisionResultKind.ReasonRequired,
            _ => DecisionResultKind.Failed,
        };
        if (kind == DecisionResultKind.Conflict && Detail != null)
        {
            // The row is decided: refresh so the reviewer sees who decided and moves on.
            _ = _queue?.RefreshAsync();
        }

        return new DecisionOutcome(kind, null, ex);
    }

    private void Stop()
    {
        _renew?.Dispose();
        _renew = null;
    }

    /// <summary>Leaves the item: claim renewal stops, the stream URL is dropped, everything re-blurs.</summary>
    public void Close()
    {
        Stop();
        if (Detail != null) _reveal.Leave();
        Detail = null;
        ClaimError = null;
        OnPropertyChanged(nameof(CanDecide));
    }

    public void Dispose() => Close();
}

public sealed class StepUpCancelledException : ReviewException
{
    public StepUpCancelledException() : base("Step-up sign-in cancelled") { }
}
