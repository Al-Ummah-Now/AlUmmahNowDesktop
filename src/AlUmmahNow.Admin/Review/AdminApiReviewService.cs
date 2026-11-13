using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AlUmmahNow.Admin.Media;
using AlUmmahNow.CoreBridge.Logging;

namespace AlUmmahNow.Admin.Review;

/// <summary>
/// admin-api's review routes (and api-videos' device registry) as the signed-in reviewer. Built
/// on #1280 as it is, tolerant of the fields #1297 adds. URLs, bodies and tokens are never
/// logged: a media request names the review, a stream URL carries a signature.
/// </summary>
public sealed class AdminApiReviewService : IReviewService, IPushRegistry
{
    private static readonly Logger Logger = Log.For("review");

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly Uri _adminApi;
    private readonly Uri _videosApi;
    private readonly Func<CancellationToken, Task<string?>> _token;
    private readonly HttpClient _http;
    private readonly string _userAgent;
    private readonly Func<StaffProfile?>? _fallbackAccount;

    public AdminApiReviewService(Uri adminApi, Uri videosApi, Func<CancellationToken, Task<string?>> token, string userAgent,
        HttpClient? http = null, Func<StaffProfile?>? fallbackAccount = null)
    {
        if (!PrivateHttp.IsAllowed(adminApi) || !PrivateHttp.IsAllowed(videosApi)) throw new ArgumentException("TLS only");
        _adminApi = adminApi;
        _videosApi = videosApi;
        _token = token;
        _userAgent = userAgent;
        _http = http ?? PrivateHttp.Create();
        _fallbackAccount = fallbackAccount;
    }

    // ---------- transport ----------

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri root, string path, IDictionary<string, string?>? query, object? body, CancellationToken ct)
    {
        var builder = new StringBuilder(root.AbsoluteUri.TrimEnd('/')).Append(path);
        if (query != null)
        {
            var sep = '?';
            foreach (var kv in query)
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                builder.Append(sep).Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value));
                sep = '&';
            }
        }

        var uri = new Uri(builder.ToString());
        if (!PrivateHttp.IsAllowed(uri)) throw new ReviewApiException(0, "insecure_url");
        var token = await _token(ct).ConfigureAwait(false) ?? throw new SignInRequiredException();
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(_userAgent);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException || ex.InnerException is IOException || ex.StatusCode == null)
        {
            throw new OfflineException(ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new OfflineException(ex);
        }

        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var failure = Failure((int)response.StatusCode, text);
            Logger.Warn("admin-api " + method + " " + Template(path) + " answered " + (int)response.StatusCode + " " + ((failure as ReviewApiException)?.Code ?? failure.GetType().Name));
            throw failure;
        }
    }

    /// <summary>"/api/review/:id/claim" for logs: ids are fine to keep out.</summary>
    internal static string Template(string path)
    {
        var parts = path.Split('/');
        if (parts.Length > 3 && parts[1] == "api" && parts[2] == "review" && parts[3] is not ("queue" or "stats" or "alerts" or "settings" or "audit")) parts[3] = ":id";
        if (parts.Length > 4 && parts[2] == "v1" && parts[3] == "devices") parts[4] = ":installId";
        return string.Join("/", parts);
    }

    private sealed class ErrorBody
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("decision")] public string? Decision { get; set; }
        [JsonPropertyName("decidedByName")] public string? DecidedByName { get; set; }
        [JsonPropertyName("decidedAt")] public DateTimeOffset? DecidedAt { get; set; }
        [JsonPropertyName("assignedToName")] public string? AssignedToName { get; set; }
    }

    internal static Exception Failure(int status, string text)
    {
        ErrorBody? body = null;
        try
        {
            body = JsonSerializer.Deserialize<ErrorBody>(text, Json);
        }
        catch (JsonException)
        {
        }

        var code = body?.Code;
        if (status == 401) return new SignInRequiredException();
        switch (code)
        {
            case "step_up_required": return new StepUpRequiredException();
            case "reviewer_only":
            case "staff_only": return new NotStaffException();
            case "senior_only": return new SeniorOnlyException();
            case "undo_expired":
            case "nothing_to_undo": return new UndoExpiredException();
            case "claimed": return new ClaimedByOtherException(body?.AssignedToName);
            case "already_decided":
            case "not_open": return new AlreadyDecidedException(body?.Decision ?? StatusWord(body?.Error), body?.DecidedByName, body?.DecidedAt);
        }

        if (status == 409 && code is null or "videos_refused") return new AlreadyDecidedException(body?.Decision ?? StatusWord(body?.Error), body?.DecidedByName, body?.DecidedAt);
        return new ReviewApiException(status, code);
    }

    /// <summary>#1280 says "This review is approved": the decision word, until #1297 sends it.</summary>
    private static string? StatusWord(string? error)
    {
        if (error == null) return null;
        foreach (var (word, decision) in new[] { ("approved", "approve"), ("rejected", "reject"), ("changes_requested", "request_changes") })
        {
            if (error.EndsWith(" " + word, StringComparison.Ordinal)) return decision;
        }

        return null;
    }

    private async Task<T> ResultAsync<T>(HttpMethod method, string path, IDictionary<string, string?>? query, object? body, CancellationToken ct)
    {
        using var response = await SendAsync(method, _adminApi, path, query, body, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            var node = JsonNode.Parse(text)?["result"] ?? throw new ReviewApiException((int)response.StatusCode, "no_result");
            return node.Deserialize<T>(Json) ?? throw new ReviewApiException((int)response.StatusCode, "no_result");
        }
        catch (JsonException)
        {
            throw new ReviewApiException((int)response.StatusCode, "invalid_json");
        }
    }

    private static string Id(string id) => Uri.EscapeDataString(id);

    // ---------- review ----------

    private sealed class MeResult
    {
        [JsonPropertyName("userId")] public string? UserId { get; set; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("roles")] public List<string>? Roles { get; set; }
        [JsonPropertyName("permissions")] public List<string>? Permissions { get; set; }
    }

    public async Task<StaffProfile> ProfileAsync(CancellationToken ct = default)
    {
        try
        {
            var me = await ResultAsync<MeResult>(HttpMethod.Get, "/api/me", null, null, ct).ConfigureAwait(false);
            if (me.UserId == null) throw new ReviewApiException(200, "no_user");
            var profile = new StaffProfile(me.UserId, me.DisplayName, me.Roles ?? new List<string>(), me.Permissions);
            if (!profile.IsReviewer) throw new NotStaffException();
            return profile;
        }
        catch (ReviewApiException ex) when (ex.Status == 404)
        {
            // No GET /api/me yet (#1297): the queue answers whether this account is a reviewer.
            await QueueAsync(ReviewTab.ActionNeeded, null, null, ct).ConfigureAwait(false);
            var account = _fallbackAccount?.Invoke();
            return new StaffProfile(account?.UserId ?? "", account?.DisplayName, new[] { "content_reviewer" });
        }
        catch (ReviewApiException ex) when (ex.Status == 403)
        {
            throw new NotStaffException();
        }
    }

    private sealed class QueueResult
    {
        [JsonPropertyName("items")] public List<ReviewItem>? Items { get; set; }
        [JsonPropertyName("nextCursor")] public string? NextCursor { get; set; }
    }

    public async Task<ReviewPage> QueueAsync(ReviewTab tab, string? query, string? cursor, CancellationToken ct = default)
    {
        var q = new Dictionary<string, string?> { ["tab"] = tab.Wire(), ["limit"] = "50", ["cursor"] = cursor, ["q"] = string.IsNullOrWhiteSpace(query) ? null : query!.Trim() };
        try
        {
            var page = await ResultAsync<QueueResult>(HttpMethod.Get, "/api/review/queue", q, null, ct).ConfigureAwait(false);
            return new ReviewPage(page.Items ?? new List<ReviewItem>(), page.NextCursor);
        }
        catch (ReviewApiException ex) when (ex.Status == 403)
        {
            throw new NotStaffException();
        }
    }

    private sealed class DetailResult
    {
        [JsonPropertyName("review")] public ReviewItem? Review { get; set; }
        [JsonPropertyName("findings")] public List<ReviewFinding>? Findings { get; set; }
        [JsonPropertyName("history")] public List<ReviewHistoryEntry>? History { get; set; }
        [JsonPropertyName("subject")] public ReviewSubject? Subject { get; set; }
        [JsonPropertyName("manifest")] public Manifest? Manifest { get; set; }
        [JsonPropertyName("manifestKey")] public string? ManifestKey { get; set; }
        [JsonPropertyName("stream")] public ReviewStream? Stream { get; set; }
    }

    internal sealed class Manifest
    {
        [JsonPropertyName("frames")] public List<ManifestFrame>? Frames { get; set; }
    }

    internal sealed class ManifestFrame
    {
        [JsonPropertyName("key")] public string Key { get; set; } = "";
        [JsonPropertyName("atMs")] public int AtMs { get; set; }
    }

    public async Task<ReviewDetail> DetailAsync(string id, CancellationToken ct = default)
    {
        var result = await ResultAsync<DetailResult>(HttpMethod.Get, "/api/review/" + Id(id), null, null, ct).ConfigureAwait(false);
        var item = result.Review ?? throw new ReviewApiException(200, "no_review");
        if (item.Title == null && result.Subject != null) item.Title = result.Subject.Title;
        var manifest = result.Manifest;
        if (manifest == null && result.ManifestKey is { Length: > 0 } key)
        {
            // #1280 returns the key only: the manifest is a JSON file of the review, read in memory.
            try
            {
                manifest = JsonSerializer.Deserialize<Manifest>(await MediaAsync(id, key, ct).ConfigureAwait(false), Json);
            }
            catch (Exception ex) when (ex is JsonException || ex is ReviewApiException)
            {
                Logger.Warn("review manifest unreadable: " + ex.GetType().Name);
            }
        }

        var findings = result.Findings ?? new List<ReviewFinding>();
        var frames = (manifest?.Frames ?? new List<ManifestFrame>()).Select(f => new ReviewFrame(f.Key, f.AtMs, false));
        var stream = result.Stream?.Url is { } url && PrivateHttp.IsAllowed(url) ? result.Stream : null;
        return new ReviewDetail(item, findings, (result.History ?? new List<ReviewHistoryEntry>()).Where(h => h.Id != id).ToList(),
            result.Subject, ReviewDetail.OrderFrames(findings, frames), stream);
    }

    public async Task ClaimAsync(string id, CancellationToken ct = default) =>
        (await SendAsync(HttpMethod.Post, _adminApi, "/api/review/" + Id(id) + "/claim", null, new { }, ct).ConfigureAwait(false)).Dispose();

    public async Task RevealAsync(string id, string target, string installId, CancellationToken ct = default) =>
        (await SendAsync(HttpMethod.Post, _adminApi, "/api/review/" + Id(id) + "/reveal", null, new { target, installId }, ct).ConfigureAwait(false)).Dispose();

    private sealed class DecisionResult
    {
        [JsonPropertyName("review")] public ReviewItem? Review { get; set; }
    }

    public async Task<ReviewItem> DecideAsync(string id, ReviewDecision decision, string? reasonCode, string? note, int? version, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object> { ["decision"] = Wire.Of(decision) };
        if (reasonCode != null) body["reasonCode"] = reasonCode;
        if (!string.IsNullOrWhiteSpace(note)) body["note"] = note!.Trim().Length > 1000 ? note.Trim().Substring(0, 1000) : note.Trim();
        if (version != null) body["version"] = version.Value;
        var result = await ResultAsync<DecisionResult>(HttpMethod.Post, "/api/review/" + Id(id) + "/decision", null, body, ct).ConfigureAwait(false);
        return result.Review ?? throw new ReviewApiException(200, "no_review");
    }

    /// <summary>Bytes in memory, from a <c>private, no-store</c> response. Capped at 8 MB (frames are ≤ 512 px).</summary>
    public async Task<byte[]> MediaAsync(string id, string key, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, _adminApi, "/api/review/" + Id(id) + "/media", new Dictionary<string, string?> { ["key"] = key }, null, ct).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaxMediaBytes) throw new ReviewApiException(413, "too_large");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length > MaxMediaBytes) throw new ReviewApiException(413, "too_large");
        return bytes;
    }

    public const int MaxMediaBytes = 8 * 1024 * 1024;

    public async Task RecordCaptureAsync(CaptureEvent captureEvent, string? reviewId, string installId, CancellationToken ct = default)
    {
        var name = captureEvent switch
        {
            CaptureEvent.Screenshot => "screenshot",
            CaptureEvent.RecordingStarted => "recording_started",
            _ => "recording_stopped",
        };
        try
        {
            (await SendAsync(HttpMethod.Post, _adminApi, "/api/review/audit/capture", null, new { @event = name, reviewId, installId }, ct).ConfigureAwait(false)).Dispose();
        }
        catch (ReviewApiException ex) when (ex.Status == 404)
        {
            // Not on admin-api yet (#1297).
            Logger.Info("capture audit route not available");
        }
    }

    // ---------- push devices (api-videos) ----------

    public async Task RegisterAsync(string installId, string channelUri, string locale, string appVersion, CancellationToken ct = default)
    {
        var body = AdminPushRegistration.Body(channelUri, locale, appVersion);
        (await SendAsync(HttpMethod.Put, _videosApi, "/api/v1/devices/" + Id(installId), null, body, ct).ConfigureAwait(false)).Dispose();
    }

    public async Task UnregisterAsync(string installId, CancellationToken ct = default)
    {
        try
        {
            (await SendAsync(HttpMethod.Delete, _videosApi, "/api/v1/devices/" + Id(installId), null, null, ct).ConfigureAwait(false)).Dispose();
        }
        catch (ReviewApiException ex) when (ex.Status == 404)
        {
        }
    }
}

/// <summary>The device registration body (api-videos <c>DeviceRegistrationSchema</c>, #1264).</summary>
public static class AdminPushRegistration
{
    public static Dictionary<string, object> Body(string channelUri, string locale, string appVersion)
    {
        if (!IsWnsChannel(channelUri)) throw new ArgumentException("Not a WNS channel URI", nameof(channelUri));
        return new Dictionary<string, object>
        {
            ["app"] = "admin",
            ["platform"] = "windows",
            ["provider"] = "wns",
            ["token"] = channelUri,
            ["locale"] = locale.StartsWith("ar", StringComparison.OrdinalIgnoreCase) ? "ar" : "en",
            ["appVersion"] = appVersion.Length > 32 ? appVersion.Substring(0, 32) : appVersion,
        };
    }

    /// <summary>The server accepts only https URLs on *.notify.windows.com, no port or credentials.</summary>
    public static bool IsWnsChannel(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && string.IsNullOrEmpty(u.UserInfo)
        && (u.Host == "notify.windows.com" || u.Host.EndsWith(".notify.windows.com", StringComparison.OrdinalIgnoreCase));
}
