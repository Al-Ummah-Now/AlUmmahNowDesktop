using System.IO.Compression;
using AlUmmahNow.Admin.Review;

namespace AlUmmahNow.Admin.Demo;

/// <summary>
/// Canned review queue for CI scenarios and screenshots (AUN_DEMO=1): nothing leaves the
/// machine. Mirrors admin-api's rules closely enough to drive the UI: claims, version
/// conflicts, the 10-second undo, reason codes and senior-only minor items.
/// </summary>
public sealed class DemoReviewService : IReviewService, IPushRegistry
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly List<ReviewItem> _items;
    private readonly bool _senior;
    private readonly Uri? _stream;

    public DemoReviewService(bool senior = true, Uri? stream = null, TimeProvider? time = null, string? displayName = "Amina Reviewer")
    {
        _time = time ?? TimeProvider.System;
        _senior = senior;
        _stream = stream;
        DisplayName = displayName;
        var now = _time.GetUtcNow();
        _items = new List<ReviewItem>
        {
            Item("rev-001", "Friday khutbah: patience in hardship", "Masjid An-Noor", 0.42, now.AddMinutes(4), cause: "risk", category: "hate"),
            Item("rev-002", "Untitled upload 0412", "New Channel 88", 0.91, now.AddMinutes(-2), nsfw: true, cause: "nsfw", category: "sexual"),
            Item("rev-003", "Qur'an recitation, Surah Al-Kahf", "Tilawah Daily", 0.18, now.AddMinutes(25), cause: "check_failed", category: "religious_misattribution"),
            Item("rev-004", "Kids' nasheed", "Little Ummah", 0.67, now.AddMinutes(9), minor: true, cause: "escalation", category: "child_safety", escalate: true),
            Item("rev-005", "Charity drive update", "Sadaqah Now", 0.55, now.AddMinutes(9), cause: "appeal", category: "spam_scam"),
            Item("rev-006", "Street interview about Ramadan", "Ummah Voices", 0.33, null, cause: "budget", category: "privacy"),
        };
        _items.Add(Decided("rev-090", "Hajj vlog day 3", "Travel Deen", "approve", "Yusuf Admin", now.AddHours(-1)));
        _items.Add(Decided("rev-091", "Fake giveaway", "Win Big", "reject", "Amina Reviewer", now.AddHours(-3)));
        // A second reviewer decides rev-005 while it is open here: the app shows the conflict.
        ConflictOn = "rev-005";
    }

    public string? DisplayName { get; }

    /// <summary>Decisions on this id answer 409 "Already approved by Yusuf Admin".</summary>
    public string? ConflictOn { get; set; }

    public List<string> Calls { get; } = new();

    private static ReviewItem Item(string id, string title, string channel, double risk, DateTimeOffset? due, bool nsfw = false, bool minor = false,
        string? cause = null, string? category = null, bool escalate = false) => new()
    {
        Id = id, SubjectId = "vid-" + id.Substring(4), Title = title, ChannelId = "ch-" + channel.Length, ChannelName = channel,
        ChannelTrust = risk > 0.6 ? "new" : "trusted", Risk = risk, Nsfw = nsfw, Minor = minor, HoldCauseWire = cause, TopCategory = category,
        Escalate = escalate, SlaDueAt = due, CreatedAt = (due ?? DateTimeOffset.UtcNow).AddMinutes(-30), Version = 1, Status = "in_review",
    };

    private static ReviewItem Decided(string id, string title, string channel, string decision, string by, DateTimeOffset at)
    {
        var i = Item(id, title, channel, 0.2, null);
        i.Status = decision == "approve" ? "approved" : "rejected";
        i.Decision = decision;
        i.DecidedByName = by;
        i.DecidedAt = at;
        return i;
    }

    private void Record(string call)
    {
        lock (_gate) Calls.Add(call);
    }

    public Task<StaffProfile> ProfileAsync(CancellationToken ct = default) =>
        Task.FromResult(new StaffProfile("demo-reviewer", DisplayName, new[] { _senior ? "senior_reviewer" : "content_reviewer" }));

    public Task<ReviewPage> QueueAsync(ReviewTab tab, string? query, string? cursor, CancellationToken ct = default)
    {
        Record("queue " + tab.Wire() + (string.IsNullOrEmpty(query) ? "" : " q=" + query));
        lock (_gate)
        {
            var rows = _items
                .Where(i => _senior || !i.Minor)
                .Where(i => tab == ReviewTab.ActionNeeded ? i.IsOpen : !i.IsOpen)
                .Where(i => ReviewQueue.Matches(i, query ?? ""))
                .Select(i => i.Copy())
                .ToList();
            return Task.FromResult(new ReviewPage(rows));
        }
    }

    public Task<ReviewDetail> DetailAsync(string id, CancellationToken ct = default)
    {
        Record("detail " + id);
        ReviewItem item;
        lock (_gate) item = (_items.FirstOrDefault(i => i.Id == id && (_senior || !i.Minor)) ?? throw new ReviewApiException(404, "not_found")).Copy();
        var findings = new List<ReviewFinding>
        {
            new() { Id = id + "-f1", Source = "frame", Category = item.TopCategory ?? "other", Score = item.Risk ?? 0.5, AtMs = 42_000, MediaKey = "frame-42000", Model = "classifier", Rationale = "Flagged frame" },
            new() { Id = id + "-f2", Source = "transcript", Category = "misleading_metadata", Score = 0.31, AtMs = 95_000, Text = "…the description claims a different speaker…" },
            new() { Id = id + "-f3", Source = "frame", Category = item.TopCategory ?? "other", Score = 0.22, AtMs = 120_000, MediaKey = "frame-120000" },
        };
        var manifest = Enumerable.Range(0, 6).Select(n => new ReviewFrame("frame-" + (n * 30_000), n * 30_000, false));
        var history = new List<ReviewHistoryEntry>
        {
            new() { Id = "old-1", Status = "approved", Decision = "approve", DecidedAt = _time.GetUtcNow().AddDays(-12), DecidedByName = "Yusuf Admin" },
            new() { Id = "old-2", Status = "rejected", Decision = "reject", ReasonCode = "spam_scam", DecidedAt = _time.GetUtcNow().AddDays(-40), DecidedByName = "Amina Reviewer" },
        };
        var subject = new ReviewSubject { Id = item.SubjectId, Title = item.Title ?? "", Description = "Uploaded from the Studio app.", Tags = new List<string> { "community", "ramadan" }, ChannelId = item.ChannelId, Duration = 754 };
        var stream = _stream == null || !item.IsOpen ? null : new ReviewStream { Url = _stream, ExpiresAt = _time.GetUtcNow().AddMinutes(10), Preview = item.IsSensitive };
        return Task.FromResult(new ReviewDetail(item, findings, history, subject, ReviewDetail.OrderFrames(findings, manifest), stream));
    }

    public Task ClaimAsync(string id, CancellationToken ct = default)
    {
        Record("claim " + id);
        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Id == id) ?? throw new ReviewApiException(404, "not_found");
            item.AssignedTo = "demo-reviewer";
            item.AssignedToName = DisplayName;
        }

        return Task.CompletedTask;
    }

    public Task RevealAsync(string id, string target, string installId, CancellationToken ct = default)
    {
        Record("reveal " + id + " " + target);
        return Task.CompletedTask;
    }

    public Task<ReviewItem> DecideAsync(string id, ReviewDecision decision, string? reasonCode, string? note, int? version, CancellationToken ct = default)
    {
        Record("decide " + id + " " + Wire.Of(decision) + (reasonCode == null ? "" : " " + reasonCode));
        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Id == id) ?? throw new ReviewApiException(404, "not_found");
            var now = _time.GetUtcNow();
            if (decision == ReviewDecision.Undo)
            {
                if (item.UndoUntil == null || now > item.UndoUntil) throw new UndoExpiredException();
                item.Status = "in_review";
                item.Decision = null;
                item.DecidedAt = null;
                item.DecidedByName = null;
                item.UndoUntil = null;
                item.Version = (item.Version ?? 1) + 1;
                return Task.FromResult(item.Copy());
            }

            if (id == ConflictOn) throw new AlreadyDecidedException("approve", "Yusuf Admin", now.AddSeconds(-20));
            if (!item.IsOpen) throw new AlreadyDecidedException(item.Decision, item.DecidedByName, item.DecidedAt);
            if (version != null && version != item.Version) throw new AlreadyDecidedException(item.Decision, item.DecidedByName, item.DecidedAt);
            if (decision is ReviewDecision.Reject or ReviewDecision.RequestChanges && !ReviewReasons.IsKnown(reasonCode)) throw new ReviewApiException(400, "reason_required");
            item.Status = decision switch
            {
                ReviewDecision.Approve => "approved",
                ReviewDecision.Reject => "rejected",
                ReviewDecision.RequestChanges => "changes_requested",
                _ => "in_review",
            };
            item.Escalate = decision == ReviewDecision.Escalate || item.Escalate;
            item.Decision = Wire.Of(decision);
            item.DecidedAt = now;
            item.DecidedByName = DisplayName;
            item.UndoUntil = now.AddSeconds(10);
            item.Version = (item.Version ?? 1) + 1;
            return Task.FromResult(item.Copy());
        }
    }

    public Task<byte[]> MediaAsync(string id, string key, CancellationToken ct = default)
    {
        Record("media " + id + " " + key);
        var seed = (key.GetHashCode() & 0x7fffffff) % 200;
        return Task.FromResult(SolidPng(64, 36, (byte)(30 + seed / 2), (byte)(60 + seed / 3), (byte)(90 + seed / 4)));
    }

    public Task RecordCaptureAsync(CaptureEvent captureEvent, string? reviewId, string installId, CancellationToken ct = default)
    {
        Record("capture " + captureEvent);
        return Task.CompletedTask;
    }

    public Task RegisterAsync(string installId, string channelUri, string locale, string appVersion, CancellationToken ct = default)
    {
        Record("push register");
        return Task.CompletedTask;
    }

    public Task UnregisterAsync(string installId, CancellationToken ct = default)
    {
        Record("push unregister");
        return Task.CompletedTask;
    }

    /// <summary>A tiny PNG built in memory (frames in demo mode), so no image files ship or land on disk.</summary>
    public static byte[] SolidPng(int width, int height, byte r, byte g, byte b)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width; x++)
            {
                var shade = (byte)((x + y) % 16 * 4);
                raw.WriteByte((byte)Math.Min(255, r + shade));
                raw.WriteByte((byte)Math.Min(255, g + shade));
                raw.WriteByte((byte)Math.Min(255, b + shade));
            }
        }

        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true)) raw.WriteTo(zs);
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        WriteBig(ihdr, 0, width);
        WriteBig(ihdr, 4, height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", z.ToArray());
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void WriteBig(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBig(len, 0, data.Length);
        s.Write(len);
        var typed = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(typed, 0);
        data.CopyTo(typed, 4);
        s.Write(typed);
        var crc = new byte[4];
        WriteBig(crc, 0, (int)Crc32(typed));
        s.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var d in data)
        {
            crc ^= d;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
