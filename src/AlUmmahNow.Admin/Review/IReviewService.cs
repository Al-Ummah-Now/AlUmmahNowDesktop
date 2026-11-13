namespace AlUmmahNow.Admin.Review;

/// <summary>
/// The review queue on admin-api (AlUmmahNow #1280, gaps in #1297). Every call acts as the
/// signed-in reviewer; the server checks the role each time.
/// </summary>
public interface IReviewService
{
    Task<StaffProfile> ProfileAsync(CancellationToken ct = default);

    Task<ReviewPage> QueueAsync(ReviewTab tab, string? query, string? cursor, CancellationToken ct = default);

    Task<ReviewDetail> DetailAsync(string id, CancellationToken ct = default);

    /// <summary>Claims (or renews the claim on) an item for 3 minutes.</summary>
    Task ClaimAsync(string id, CancellationToken ct = default);

    /// <summary>Audited reveal of one blurred item (<paramref name="target"/>: <c>item</c>, a frame key, <c>player</c>).</summary>
    Task RevealAsync(string id, string target, string installId, CancellationToken ct = default);

    Task<ReviewItem> DecideAsync(string id, ReviewDecision decision, string? reasonCode, string? note, int? version, CancellationToken ct = default);

    /// <summary>A frame or thumbnail of the review, as bytes in memory. Never written to disk.</summary>
    Task<byte[]> MediaAsync(string id, string key, CancellationToken ct = default);

    Task RecordCaptureAsync(CaptureEvent captureEvent, string? reviewId, string installId, CancellationToken ct = default);
}

/// <summary>Push registration for the Admin app (<c>PUT /api/v1/devices/:installId</c>, <c>app: "admin"</c>).</summary>
public interface IPushRegistry
{
    Task RegisterAsync(string installId, string channelUri, string locale, string appVersion, CancellationToken ct = default);

    Task UnregisterAsync(string installId, CancellationToken ct = default);
}

/// <summary>Review refusals the app explains in its own words.</summary>
public class ReviewException : Exception
{
    public ReviewException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Another reviewer decided first (409): "Already approved by Name 20 s ago".</summary>
public sealed class AlreadyDecidedException : ReviewException
{
    public AlreadyDecidedException(string? decision, string? byName, DateTimeOffset? at) : base("Already decided")
    {
        Decision = decision;
        ByName = byName;
        At = at;
    }

    public string? Decision { get; }
    public string? ByName { get; }
    public DateTimeOffset? At { get; }
}

public sealed class ClaimedByOtherException : ReviewException
{
    public ClaimedByOtherException(string? name) : base("Claimed by another reviewer") => Name = name;

    public string? Name { get; }
}

/// <summary>The server wants a sign-in within the last 15 minutes (<c>step_up_required</c>).</summary>
public sealed class StepUpRequiredException : ReviewException
{
    public StepUpRequiredException() : base("A recent sign-in is required") { }
}

public sealed class UndoExpiredException : ReviewException
{
    public UndoExpiredException() : base("The undo window has passed") { }
}

public sealed class NotStaffException : ReviewException
{
    public NotStaffException() : base("Not a reviewer") { }
}

public sealed class SeniorOnlyException : ReviewException
{
    public SeniorOnlyException() : base("Senior reviewers only") { }
}

public sealed class OfflineException : ReviewException
{
    public OfflineException(Exception? inner = null) : base("Offline", inner) { }
}

public sealed class SignInRequiredException : ReviewException
{
    public SignInRequiredException() : base("Sign in again") { }
}

/// <summary>Any other refusal, with the server's code (e.g. <c>reason_required</c>, <c>different_reviewer</c>, <c>media_gone</c>).</summary>
public sealed class ReviewApiException : ReviewException
{
    public ReviewApiException(int status, string? code) : base("admin-api answered " + status + (code == null ? "" : " " + code))
    {
        Status = status;
        Code = code;
    }

    public int Status { get; }
    public string? Code { get; }
}
