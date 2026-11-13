using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlUmmahNow.Admin.Review;

// The admin-api review contract (AlUmmahNow docs/specs/VIDEO-REVIEW.md §6). Field names match the
// Apple and Android Admin apps: rows arrive in snake_case, fields the server adds later are
// optional, so older servers still decode. Every response is wrapped in { "result": … }.

/// <summary>Who is signed in, from admin-api <c>GET /api/me</c>. The server checks the role on every call.</summary>
public sealed class StaffProfile
{
    public StaffProfile(string userId, string? displayName, IReadOnlyList<string> roles, IReadOnlyList<string>? permissions = null)
    {
        UserId = userId;
        DisplayName = displayName;
        Roles = roles;
        Permissions = permissions ?? Array.Empty<string>();
    }

    public string UserId { get; }
    public string? DisplayName { get; }
    public IReadOnlyList<string> Roles { get; }
    public IReadOnlyList<string> Permissions { get; }

    /// <summary>Role names as the server reads them: case, spaces and dashes do not matter.</summary>
    public static string Normalize(string role)
    {
        var id = role.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return id == "superadmin" ? "super_admin" : id;
    }

    private static readonly string[] ReviewerRoles = { "super_admin", "admin", "moderator", "content_reviewer", "senior_reviewer" };
    private static readonly string[] SeniorRoles = { "super_admin", "admin", "senior_reviewer" };

    /// <summary>May work the review queue (<c>isReviewer</c> in shared-types roles.ts).</summary>
    public bool IsReviewer =>
        Roles.Select(Normalize).Intersect(ReviewerRoles).Any() || Permissions.Any(p => p.StartsWith("review:", StringComparison.Ordinal));

    /// <summary>Escalations and minor items (VR7, AD13). Admins count.</summary>
    public bool IsSeniorReviewer => Roles.Select(Normalize).Intersect(SeniorRoles).Any();
}

/// <summary>The queue tabs.</summary>
public enum ReviewTab { ActionNeeded, History }

public static class ReviewTabs
{
    public static string Wire(this ReviewTab tab) => tab == ReviewTab.ActionNeeded ? "action_needed" : "history";
}

/// <summary>Why a video waits for a person (<c>VideoReviewHoldCause</c>).</summary>
public enum HoldCause { Nsfw, Risk, Escalation, Appeal, Budget, CheckFailed }

/// <summary>A decision a reviewer sends. RequestChanges keeps the video private and asks the creator to edit.</summary>
public enum ReviewDecision { Approve, Reject, Escalate, RequestChanges, Undo }

/// <summary>The reasons a reviewer may give (<c>REVIEW_REASON_CODES</c>). en/ar text: <c>admin.reason.&lt;code&gt;</c>.</summary>
public static class ReviewReasons
{
    public static readonly IReadOnlyList<string> Codes = new[]
    {
        "sexual", "child_safety", "graphic_violence", "self_harm", "hate", "violent_extremism", "harassment",
        "spam_scam", "impersonation", "privacy", "copyright", "misleading_metadata", "religious_misattribution", "other",
    };

    public static bool IsKnown(string? code) => code != null && Codes.Contains(code);
}

public static class Wire
{
    public static string Of(ReviewDecision d) => d switch
    {
        ReviewDecision.Approve => "approve",
        ReviewDecision.Reject => "reject",
        ReviewDecision.Escalate => "escalate",
        ReviewDecision.RequestChanges => "request_changes",
        _ => "undo",
    };

    public static HoldCause? Cause(string? value) => value switch
    {
        "nsfw" => HoldCause.Nsfw,
        "risk" => HoldCause.Risk,
        "escalation" => HoldCause.Escalation,
        "appeal" => HoldCause.Appeal,
        "budget" => HoldCause.Budget,
        "check_failed" => HoldCause.CheckFailed,
        _ => null,
    };

    public static string Of(HoldCause c) => c switch
    {
        HoldCause.Nsfw => "nsfw",
        HoldCause.Risk => "risk",
        HoldCause.Escalation => "escalation",
        HoldCause.Appeal => "appeal",
        HoldCause.Budget => "budget",
        _ => "check_failed",
    };
}

/// <summary>One row of the queue, text only: no media, so it may be shown offline.</summary>
public sealed class ReviewItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("subject_id")] public string SubjectId { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "in_review";
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("channel_id")] public string? ChannelId { get; set; }
    [JsonPropertyName("channel_name")] public string? ChannelName { get; set; }

    /// <summary>The channel's trust level as admin-web shows it (<c>trusted</c>, <c>new</c>, <c>warned</c>).</summary>
    [JsonPropertyName("channel_trust")] public string? ChannelTrust { get; set; }

    [JsonPropertyName("risk")] public double? Risk { get; set; }
    [JsonPropertyName("nsfw"), JsonConverter(typeof(FlagConverter))] public bool Nsfw { get; set; }
    [JsonPropertyName("escalate"), JsonConverter(typeof(FlagConverter))] public bool Escalate { get; set; }

    /// <summary>"Minor present": the server only ever sends these to senior reviewers (AD13).</summary>
    [JsonPropertyName("minor"), JsonConverter(typeof(FlagConverter))] public bool Minor { get; set; }

    [JsonPropertyName("hold_cause")] public string? HoldCauseWire { get; set; }
    [JsonPropertyName("top_category")] public string? TopCategory { get; set; }
    [JsonPropertyName("sla_due_at")] public DateTimeOffset? SlaDueAt { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; set; }
    [JsonPropertyName("assigned_to")] public string? AssignedTo { get; set; }
    [JsonPropertyName("assigned_to_name")] public string? AssignedToName { get; set; }
    [JsonPropertyName("decision")] public string? Decision { get; set; }
    [JsonPropertyName("decided_at")] public DateTimeOffset? DecidedAt { get; set; }
    [JsonPropertyName("decided_by")] public string? DecidedBy { get; set; }
    [JsonPropertyName("decided_by_name")] public string? DecidedByName { get; set; }
    [JsonPropertyName("undo_until")] public DateTimeOffset? UndoUntil { get; set; }

    /// <summary>Decisions send it: a second reviewer gets a conflict, not a double decision.</summary>
    [JsonPropertyName("version")] public int? Version { get; set; }

    [JsonIgnore] public HoldCause? HoldCause => Wire.Cause(HoldCauseWire);

    /// <summary>Blurred until a per-item, audited "Show" (NSFW or minor present).</summary>
    [JsonIgnore] public bool IsSensitive => Nsfw || Minor || HoldCause == Review.HoldCause.Nsfw;

    [JsonIgnore] public bool IsOpen => Status == "in_review" || Status == "appealed";

    public ReviewItem Copy() => (ReviewItem)MemberwiseClone();
}

public sealed class ReviewPage
{
    public ReviewPage(IReadOnlyList<ReviewItem> items, string? nextCursor = null)
    {
        Items = items;
        NextCursor = nextCursor;
    }

    public IReadOnlyList<ReviewItem> Items { get; }
    public string? NextCursor { get; }
}

/// <summary>One flagged observation (<c>content_review_findings</c>).</summary>
public sealed class ReviewFinding
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("score")] public double Score { get; set; }
    [JsonPropertyName("at_ms")] public int? AtMs { get; set; }
    [JsonPropertyName("end_ms")] public int? EndMs { get; set; }

    /// <summary>A frame or thumbnail of the review, read through <c>GET /api/review/:id/media?key=</c>. Never a stored URL.</summary>
    [JsonPropertyName("media_key")] public string? MediaKey { get; set; }

    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("quran_ref")] public string? QuranRef { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("rationale")] public string? Rationale { get; set; }
}

/// <summary>A frame of the strip: flagged frames first, then the rest of the manifest in time order.</summary>
public sealed class ReviewFrame
{
    public ReviewFrame(string key, int atMs, bool flagged, string? category = null)
    {
        Key = key;
        AtMs = atMs;
        Flagged = flagged;
        Category = category;
    }

    public string Key { get; }
    public int AtMs { get; }
    public bool Flagged { get; }
    public string? Category { get; }
}

/// <summary>Earlier reviews of the same video and the channel's record.</summary>
public sealed class ReviewHistoryEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("decision")] public string? Decision { get; set; }
    [JsonPropertyName("reason_code")] public string? ReasonCode { get; set; }
    [JsonPropertyName("decided_at")] public DateTimeOffset? DecidedAt { get; set; }
    [JsonPropertyName("decided_by_name")] public string? DecidedByName { get; set; }
}

public sealed class ReviewSubject
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("channelId")] public string? ChannelId { get; set; }
    [JsonPropertyName("duration")] public double? Duration { get; set; }
}

/// <summary>
/// A short-lived, per-reviewer stream. Held in memory for the open item only: never stored,
/// logged or pushed. <see cref="ToString"/> never prints the URL.
/// </summary>
public sealed class ReviewStream
{
    [JsonPropertyName("url")] public Uri? Url { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>A low-resolution preview stream (blurred items before a reveal).</summary>
    [JsonPropertyName("preview")] public bool Preview { get; set; }

    public override string ToString() => "ReviewStream(preview=" + Preview + ")";
}

/// <summary><c>GET /api/review/:id</c>.</summary>
public sealed class ReviewDetail
{
    public ReviewDetail(ReviewItem item, IReadOnlyList<ReviewFinding> findings, IReadOnlyList<ReviewHistoryEntry>? history = null,
        ReviewSubject? subject = null, IReadOnlyList<ReviewFrame>? frames = null, ReviewStream? stream = null)
    {
        Item = item;
        Findings = findings;
        History = history ?? Array.Empty<ReviewHistoryEntry>();
        Subject = subject;
        Frames = frames ?? Array.Empty<ReviewFrame>();
        Stream = stream;
    }

    public ReviewItem Item { get; }
    public IReadOnlyList<ReviewFinding> Findings { get; }
    public IReadOnlyList<ReviewHistoryEntry> History { get; }
    public ReviewSubject? Subject { get; }
    public IReadOnlyList<ReviewFrame> Frames { get; }
    public ReviewStream? Stream { get; }

    /// <summary>Flagged frames first (highest score first), then every other frame in time order.</summary>
    public static IReadOnlyList<ReviewFrame> OrderFrames(IEnumerable<ReviewFinding> findings, IEnumerable<ReviewFrame> manifest)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<ReviewFrame>();
        foreach (var f in findings.Where(f => f.MediaKey != null && (f.Source == "frame" || f.Source == "thumbnail")).OrderByDescending(f => f.Score))
        {
            if (seen.Add(f.MediaKey!)) output.Add(new ReviewFrame(f.MediaKey!, f.AtMs ?? 0, true, f.Category));
        }

        foreach (var frame in manifest.OrderBy(m => m.AtMs))
        {
            if (seen.Add(frame.Key)) output.Add(new ReviewFrame(frame.Key, frame.AtMs, false));
        }

        return output;
    }
}

/// <summary>Something on the device the audit log records (AD8 capture rules).</summary>
public enum CaptureEvent { Screenshot, RecordingStarted, RecordingStopped }

/// <summary>D1 returns booleans as 0/1; newer rows send true/false.</summary>
public sealed class FlagConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.True => true,
        JsonTokenType.False => false,
        JsonTokenType.Number => reader.GetDouble() != 0,
        JsonTokenType.String => reader.GetString() is "1" or "true",
        _ => false,
    };

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
}
