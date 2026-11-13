using AlUmmahNow.Admin.Demo;
using AlUmmahNow.Admin.Media;
using AlUmmahNow.Admin.Review;
using Xunit;

namespace AlUmmahNow.Admin.Tests;

public class ReviewQueueTests
{
    private static ReviewItem Row(string id, int? dueMin, double? risk, int createdMin = 0) => new()
    {
        Id = id,
        SlaDueAt = dueMin == null ? null : new DateTimeOffset(2026, 11, 13, 15, 0, 0, TimeSpan.Zero).AddMinutes(dueMin.Value),
        Risk = risk,
        CreatedAt = new DateTimeOffset(2026, 11, 13, 14, 0, 0, TimeSpan.Zero).AddMinutes(createdMin),
    };

    [Fact]
    public void Action_needed_is_sorted_by_sla_deadline_then_risk()
    {
        var order = ReviewQueue.Order(new[]
        {
            Row("none-high", null, 0.99), Row("late-low", 30, 0.1), Row("soon-low", 5, 0.2), Row("soon-high", 5, 0.9), Row("overdue", -3, 0.0),
        }, ReviewTab.ActionNeeded).Select(i => i.Id);
        Assert.Equal(new[] { "overdue", "soon-high", "soon-low", "late-low", "none-high" }, order);
    }

    [Fact]
    public void Same_deadline_and_risk_falls_back_to_oldest_first()
    {
        var order = ReviewQueue.Order(new[] { Row("newer", 5, 0.5, 10), Row("older", 5, 0.5, 1) }, ReviewTab.ActionNeeded).Select(i => i.Id);
        Assert.Equal(new[] { "older", "newer" }, order);
    }

    [Fact]
    public async Task Search_filters_loaded_rows_at_once_and_suggests_titles_and_channels()
    {
        var time = new ManualTime();
        var service = new DemoReviewService(time: time);
        var queue = new ReviewQueue(service, time);
        await queue.RefreshAsync();
        Assert.Equal("rev-002", queue.Items[0].Id); // overdue first

        var pending = queue.SearchAsync("surah");
        Assert.Single(queue.Items);
        Assert.Equal("rev-003", queue.Items[0].Id);
        Assert.Contains("Qur'an recitation, Surah Al-Kahf", queue.Suggestions("sur"));
        Assert.Contains("Masjid An-Noor", queue.Suggestions("masj"));
        time.Advance(ReviewQueue.SearchDelay);
        await pending;
        Assert.Contains("queue action_needed q=surah", service.Calls);
    }

    [Fact]
    public async Task Typing_quickly_sends_one_search()
    {
        var time = new ManualTime();
        var service = new DemoReviewService(time: time);
        var queue = new ReviewQueue(service, time);
        var a = queue.SearchAsync("c");
        var b = queue.SearchAsync("ch");
        var c = queue.SearchAsync("cha");
        time.Advance(ReviewQueue.SearchDelay);
        await Task.WhenAll(a, b, c);
        Assert.Equal(new[] { "queue action_needed q=cha" }, service.Calls.Where(x => x.StartsWith("queue", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Offline_keeps_the_last_text_rows()
    {
        var service = new FlakyService(new DemoReviewService());
        var queue = new ReviewQueue(service);
        await queue.RefreshAsync();
        var count = queue.Items.Count;
        service.Offline = true;
        await queue.RefreshAsync();
        Assert.True(queue.IsOffline);
        Assert.Equal(count, queue.Items.Count);
        service.Offline = false;
        await queue.RefreshAsync();
        Assert.False(queue.IsOffline);
    }

    [Fact]
    public async Task Juniors_never_see_minor_items()
    {
        var queue = new ReviewQueue(new DemoReviewService(senior: false));
        await queue.RefreshAsync();
        Assert.DoesNotContain(queue.Items, i => i.Minor);
        var seniorQueue = new ReviewQueue(new DemoReviewService(senior: true));
        await seniorQueue.RefreshAsync();
        Assert.Contains(seniorQueue.Items, i => i.Minor);
    }
}

public class ReviewSessionTests
{
    private static (ReviewSession Session, ReviewQueue Queue, DemoReviewService Service, ManualTime Time) Make(Func<CancellationToken, Task<bool>>? stepUp = null, IReviewService? wrap = null)
    {
        var time = new ManualTime();
        var service = new DemoReviewService(time: time);
        var queue = new ReviewQueue(wrap ?? service, time);
        var session = new ReviewSession(wrap ?? service, queue, new RevealPolicy(time), "install-1", stepUp, time);
        return (session, queue, service, time);
    }

    [Fact]
    public async Task Opening_claims_and_renews_while_open()
    {
        var (session, _, service, time) = Make();
        await session.OpenAsync("rev-001");
        Assert.True(session.CanDecide);
        Assert.Equal(1, service.Calls.Count(c => c == "claim rev-001"));
        time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(3, service.Calls.Count(c => c == "claim rev-001"));
        session.Close();
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(3, service.Calls.Count(c => c == "claim rev-001"));
    }

    [Fact]
    public async Task Approve_takes_effect_at_once_and_moves_the_row_to_history()
    {
        var (session, queue, _, _) = Make();
        await queue.RefreshAsync();
        await session.OpenAsync("rev-001");
        var outcome = await session.DecideAsync(ReviewDecision.Approve);
        Assert.Equal(DecisionResultKind.Done, outcome.Kind);
        Assert.Equal("approved", outcome.Item!.Status);
        Assert.DoesNotContain(queue.Items, i => i.Id == "rev-001");
        Assert.NotNull(session.Undo);
        Assert.False(session.CanDecide);
    }

    [Fact]
    public async Task Undo_within_ten_seconds_reopens_and_reclaims()
    {
        var (session, queue, service, time) = Make();
        await queue.RefreshAsync();
        await session.OpenAsync("rev-001");
        await session.DecideAsync(ReviewDecision.Approve);
        time.Advance(TimeSpan.FromSeconds(9));
        var undone = await session.UndoAsync();
        Assert.Equal(DecisionResultKind.Undone, undone.Kind);
        Assert.True(undone.Item!.IsOpen);
        Assert.Contains(queue.Items, i => i.Id == "rev-001");
        Assert.Contains("decide rev-001 undo", service.Calls);
        Assert.True(session.CanDecide);
    }

    [Fact]
    public async Task Undo_after_ten_seconds_is_refused_without_calling_the_server()
    {
        var (session, _, service, time) = Make();
        await session.OpenAsync("rev-001");
        await session.DecideAsync(ReviewDecision.Approve);
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(DecisionResultKind.UndoExpired, (await session.UndoAsync()).Kind);
        Assert.DoesNotContain("decide rev-001 undo", service.Calls);
    }

    [Fact]
    public async Task Reject_and_request_changes_need_a_reason()
    {
        var (session, _, service, _) = Make();
        await session.OpenAsync("rev-001");
        Assert.Equal(DecisionResultKind.ReasonRequired, (await session.DecideAsync(ReviewDecision.Reject)).Kind);
        Assert.Equal(DecisionResultKind.ReasonRequired, (await session.DecideAsync(ReviewDecision.RequestChanges, "made_up")).Kind);
        Assert.DoesNotContain(service.Calls, c => c.StartsWith("decide", StringComparison.Ordinal));
        Assert.Equal(DecisionResultKind.Done, (await session.DecideAsync(ReviewDecision.Reject, "hate", "note")).Kind);
        Assert.Contains("decide rev-001 reject hate", service.Calls);
    }

    [Fact]
    public async Task A_second_reviewer_gets_already_approved_by_name()
    {
        var (session, _, _, _) = Make();
        await session.OpenAsync("rev-005");
        var outcome = await session.DecideAsync(ReviewDecision.Reject, "spam_scam");
        Assert.Equal(DecisionResultKind.Conflict, outcome.Kind);
        Assert.Equal("approve", outcome.Conflict!.Decision);
        Assert.Equal("Yusuf Admin", outcome.Conflict.ByName);
    }

    [Fact]
    public async Task No_decisions_offline()
    {
        var time = new ManualTime();
        var flaky = new FlakyService(new DemoReviewService(time: time));
        var queue = new ReviewQueue(flaky, time);
        var session = new ReviewSession(flaky, queue, new RevealPolicy(time), "i", null, time);
        await queue.RefreshAsync();
        await session.OpenAsync("rev-001");
        flaky.Offline = true;
        await queue.RefreshAsync();
        Assert.Equal(DecisionResultKind.Offline, (await session.DecideAsync(ReviewDecision.Approve)).Kind);
        Assert.Equal(0, flaky.Decisions);
    }

    [Fact]
    public async Task Step_up_reopens_sign_in_then_retries_once()
    {
        var steps = 0;
        var time = new ManualTime();
        var inner = new DemoReviewService(time: time);
        var service = new FlakyService(inner) { StepUpOnce = true };
        var session = new ReviewSession(service, null, new RevealPolicy(time), "i", _ => { steps++; return Task.FromResult(true); }, time);
        await session.OpenAsync("rev-001");
        Assert.Equal(DecisionResultKind.Done, (await session.DecideAsync(ReviewDecision.Approve)).Kind);
        Assert.Equal(1, steps);
        Assert.Equal(2, service.Decisions);
    }

    [Fact]
    public async Task Cancelled_step_up_decides_nothing()
    {
        var time = new ManualTime();
        var service = new FlakyService(new DemoReviewService(time: time)) { StepUpOnce = true };
        var session = new ReviewSession(service, null, new RevealPolicy(time), "i", _ => Task.FromResult(false), time);
        await session.OpenAsync("rev-001");
        Assert.Equal(DecisionResultKind.StepUpCancelled, (await session.DecideAsync(ReviewDecision.Approve)).Kind);
        Assert.Equal(1, service.Decisions);
    }

    [Fact]
    public async Task Show_is_audited_first_and_reblurs_on_leave_minimise_and_idle()
    {
        var (session, _, service, time) = Make();
        await session.OpenAsync("rev-002");
        var item = session.Detail!.Item;
        Assert.True(item.IsSensitive);
        Assert.False(session.Reveal.IsShown(item.Id, item.IsSensitive));
        Assert.True(await session.RevealAsync());
        Assert.Contains("reveal rev-002 item", service.Calls);
        Assert.True(session.Reveal.IsShown(item.Id, true));

        time.Advance(TimeSpan.FromSeconds(59));
        session.Reveal.Tick();
        Assert.True(session.Reveal.IsShown(item.Id, true));
        time.Advance(TimeSpan.FromSeconds(1));
        session.Reveal.Tick();
        Assert.False(session.Reveal.IsShown(item.Id, true));

        await session.RevealAsync();
        session.Reveal.Hide();
        Assert.False(session.Reveal.IsShown(item.Id, true));

        await session.RevealAsync();
        session.Close();
        Assert.False(session.Reveal.IsShown(item.Id, true));
    }

    [Fact]
    public async Task Failed_audit_keeps_the_item_blurred()
    {
        var time = new ManualTime();
        var service = new FlakyService(new DemoReviewService(time: time)) { FailReveal = true };
        var session = new ReviewSession(service, null, new RevealPolicy(time), "i", null, time);
        await session.OpenAsync("rev-002");
        Assert.False(await session.RevealAsync());
        Assert.False(session.Reveal.IsShown("rev-002", true));
    }

    [Fact]
    public void Flagged_frames_come_first_by_score_then_the_rest_in_time_order()
    {
        var frames = ReviewDetail.OrderFrames(
            new[]
            {
                new ReviewFinding { Source = "frame", MediaKey = "f60", Score = 0.4, AtMs = 60_000 },
                new ReviewFinding { Source = "frame", MediaKey = "f90", Score = 0.9, AtMs = 90_000 },
                new ReviewFinding { Source = "transcript", MediaKey = "t", Score = 1 },
            },
            new[] { new ReviewFrame("f90", 90_000, false), new ReviewFrame("f30", 30_000, false), new ReviewFrame("f0", 0, false) });
        Assert.Equal(new[] { "f90", "f60", "f0", "f30" }, frames.Select(f => f.Key));
        Assert.Equal(new[] { true, true, false, false }, frames.Select(f => f.Flagged));
    }
}

/// <summary>Wraps a service to go offline, ask for step-up once, or fail the reveal audit.</summary>
internal sealed class FlakyService : IReviewService
{
    private readonly IReviewService _inner;

    public FlakyService(IReviewService inner) => _inner = inner;

    public bool Offline { get; set; }
    public bool StepUpOnce { get; set; }
    public bool FailReveal { get; set; }
    public int Decisions { get; private set; }

    private void Check()
    {
        if (Offline) throw new OfflineException();
    }

    public Task<StaffProfile> ProfileAsync(CancellationToken ct = default) { Check(); return _inner.ProfileAsync(ct); }
    public Task<ReviewPage> QueueAsync(ReviewTab tab, string? query, string? cursor, CancellationToken ct = default) { Check(); return _inner.QueueAsync(tab, query, cursor, ct); }
    public Task<ReviewDetail> DetailAsync(string id, CancellationToken ct = default) { Check(); return _inner.DetailAsync(id, ct); }
    public Task ClaimAsync(string id, CancellationToken ct = default) { Check(); return _inner.ClaimAsync(id, ct); }

    public Task RevealAsync(string id, string target, string installId, CancellationToken ct = default)
    {
        Check();
        if (FailReveal) throw new ReviewApiException(500, null);
        return _inner.RevealAsync(id, target, installId, ct);
    }

    public Task<ReviewItem> DecideAsync(string id, ReviewDecision decision, string? reasonCode, string? note, int? version, CancellationToken ct = default)
    {
        Decisions++;
        Check();
        if (StepUpOnce)
        {
            StepUpOnce = false;
            throw new StepUpRequiredException();
        }

        return _inner.DecideAsync(id, decision, reasonCode, note, version, ct);
    }

    public Task<byte[]> MediaAsync(string id, string key, CancellationToken ct = default) { Check(); return _inner.MediaAsync(id, key, ct); }
    public Task RecordCaptureAsync(CaptureEvent captureEvent, string? reviewId, string installId, CancellationToken ct = default) => _inner.RecordCaptureAsync(captureEvent, reviewId, installId, ct);
}
