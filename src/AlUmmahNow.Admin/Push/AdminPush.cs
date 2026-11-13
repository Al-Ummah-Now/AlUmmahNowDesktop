using System.Text.Json;

namespace AlUmmahNow.Admin.Push;

/// <summary>What a toast or deep link asks the app to do.</summary>
public enum PushAction { Open, Claim }

public sealed class PushActivation
{
    public PushActivation(PushAction action, string reviewId)
    {
        Action = action;
        ReviewId = reviewId;
    }

    public PushAction Action { get; }
    public string ReviewId { get; }

    /// <summary>
    /// The WNS toast (AlUmmahNow #1286 <c>reviewToastXml</c>): buttons send <c>open:&lt;id&gt;</c> and
    /// <c>claim:&lt;id&gt;</c>, the toast body launches <c>/moderation/review/&lt;id&gt;</c>. Anything else is ignored.
    /// </summary>
    public static PushActivation? Parse(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return null;
        var a = arguments.Trim();
        if (a.StartsWith("open:", StringComparison.Ordinal)) return Make(PushAction.Open, a.Substring(5));
        if (a.StartsWith("claim:", StringComparison.Ordinal)) return Make(PushAction.Claim, a.Substring(6));
        const string path = "/moderation/review/";
        if (a.StartsWith(path, StringComparison.Ordinal)) return Make(PushAction.Open, a.Substring(path.Length));
        return null;
    }

    private static PushActivation? Make(PushAction action, string id) => IsReviewId(id) ? new PushActivation(action, id) : null;

    /// <summary>Review ids are short opaque tokens; nothing with a path, query or scheme.</summary>
    public static bool IsReviewId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>The raw WNS notification that removes an alert claimed or decided elsewhere.</summary>
public sealed class PushClear
{
    public PushClear(string reviewId, string tag, string group)
    {
        ReviewId = reviewId;
        Tag = tag;
        Group = group;
    }

    public string ReviewId { get; }
    public string Tag { get; }
    public string Group { get; }

    public const string ReviewGroup = "review";

    /// <summary><c>{"kind":"review_clear","reviewId":"…","tag":"…","group":"review"}</c>; null for anything else.</summary>
    public static PushClear? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "review_clear") return null;
            var id = root.TryGetProperty("reviewId", out var r) ? r.GetString() : null;
            if (id == null || !PushActivation.IsReviewId(id)) return null;
            var tag = root.TryGetProperty("tag", out var t) ? t.GetString() : null;
            var group = root.TryGetProperty("group", out var g) ? g.GetString() : null;
            return new PushClear(id, string.IsNullOrEmpty(tag) ? Tag16(id) : tag!, string.IsNullOrEmpty(group) ? ReviewGroup : group!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The server's toast tag: first 16 hex digits of SHA-256(review id) (WNS tags are ≤ 16 chars).</summary>
    public static string Tag16(string reviewId)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(reviewId));
        return Convert.ToHexString(hash).ToLowerInvariant().Substring(0, 16);
    }
}

public enum PushState { NotConfigured, Off, Registering, Registered, Failed }

/// <summary>
/// Whether this build can receive pushes. WNS needs the app's package identity (Partner Center
/// Package SID + secret on the server, the matching identity in the signed MSIX); until the owner
/// supplies them the build ships with push off and says so.
/// </summary>
public sealed class PushConfig
{
    public PushConfig(string? packageIdentity, bool packaged)
    {
        PackageIdentity = packageIdentity;
        Packaged = packaged;
    }

    /// <summary>The Partner Center identity name stamped into the MSIX at release; empty in dev builds.</summary>
    public string? PackageIdentity { get; }

    /// <summary>Running from an MSIX (WNS channels need package identity).</summary>
    public bool Packaged { get; }

    public bool CanRegister => Packaged && !string.IsNullOrWhiteSpace(PackageIdentity) && !PackageIdentity!.EndsWith(".Dev", StringComparison.Ordinal);
}
