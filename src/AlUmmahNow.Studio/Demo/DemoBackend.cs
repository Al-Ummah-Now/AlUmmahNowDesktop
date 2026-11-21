using System.Text.Json;
using AlUmmahNow.CoreBridge.Passkeys;
using AlUmmahNow.CoreBridge.Studio;

namespace AlUmmahNow.Studio.Demo;

/// <summary>
/// CI and screenshot mode (<c>AUN_DEMO=1</c>): two signed-in accounts and canned channel data, no
/// network. Never used in a normal launch.
/// </summary>
public sealed class DemoAccounts : IStudioAccounts
{
    private readonly List<StudioAccount> _accounts = new()
    {
        new StudioAccount("demo-1", "amina@example.com", "Amina Studio", null, null, false),
        new StudioAccount("demo-2", "omar@example.com", "Omar", null, null, false),
    };

    private string? _active = "demo-1";

    public event EventHandler? Changed;

    public IReadOnlyList<StudioAccount> Accounts => _accounts.ToList();
    public StudioAccount? Active => _accounts.FirstOrDefault(a => a.UserId == _active);

    public void Reload() { }

    public Task<StudioAccount> SignInAsync(SignInMode mode, CancellationToken cancellationToken = default)
    {
        _active = _accounts[0].UserId;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(_accounts[0]);
    }

    public bool PasskeysAvailable => true;

    public Task<StudioAccount> SignInWithPasskeyAsync(CancellationToken cancellationToken = default) => SignInAsync(SignInMode.Default, cancellationToken);

    public Task<SavedPasskey> AddPasskeyAsync(Func<CancellationToken, Task<bool>>? confirmSignInAgain = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SavedPasskey("demo-passkey", "Windows Hello", DateTimeOffset.UtcNow));

    public void Switch(string userId)
    {
        _active = userId;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task SignOutAsync(string userId)
    {
        _accounts.RemoveAll(a => a.UserId == userId);
        if (_active == userId) _active = _accounts.FirstOrDefault()?.UserId;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public void SetActiveChannel(string? channelId) { }
}

/// <summary>Canned api-videos answers for <see cref="DemoAccounts"/>, with edits kept in memory.</summary>
public sealed class DemoTransport : IApiTransport
{
    private readonly object _gate = new();
    private readonly List<Dictionary<string, object?>> _videos;
    private readonly List<(Dictionary<string, object?> Video, DateTimeOffset At, string Status)> _deleted = new();
    private readonly HashSet<string> _off;
    private readonly string? _thumbs;
    private readonly string _language;

    /// <param name="featuresOff">
    /// Features answered as off (CI and screenshots: <c>AUN_DEMO_FEATURES_OFF=upload,analytics</c>): a
    /// <see cref="UserFeatures"/> key is false in GET /api/me/features; <c>analytics</c> answers 404 <c>feature_off</c>.
    /// </param>
    /// <param name="thumbnailFolder">
    /// Title-card thumbnails (<c>AUN_DEMO_THUMBS</c>, tests/fixtures/studio-thumbs): <c>thumb-N.jpg</c> in English and
    /// <c>thumb-N.ar.jpg</c> in Arabic, one per video. Without it the cards show their night tile.
    /// </param>
    /// <param name="language">The UI language, which picks the title card's language.</param>
    public DemoTransport(IEnumerable<string>? featuresOff = null, string? thumbnailFolder = null, string language = "ar")
    {
        _off = new HashSet<string>(featuresOff ?? Array.Empty<string>(), StringComparer.Ordinal);
        _thumbs = thumbnailFolder;
        _language = language;
        // A hand-made library, like a real channel's: original title plus the creator's per-language titles (the API's
        // titleLocalized), the category the subject belongs to, and uneven views, lengths and upload days. The second
        // video was just uploaded and is still processing, so it has no views yet.
        var videos = new[]
        {
            new DemoVideo("تأملات في سورة الكهف — الجزء الأول", "Reflections on Surah Al-Kahf, part one", "تأملات في سورة الكهف — الجزء الأول",
                "quran_tafsir", "ar", true, 3412, 186, 27, 2675, "2026-11-17T18:40:00Z", new[] { "تفسير", "سورة الكهف" }),
            new DemoVideo("Seerah: the early years in Makkah", "Seerah: the early years in Makkah", "السيرة: السنوات الأولى في مكة",
                "islamic_history", "en", true, 0, 0, 0, 3390, "2026-11-20T21:05:00Z", new[] { "seerah", "makkah" }),
            new DemoVideo("أحكام الصيام للمبتدئين", "Fasting rulings for beginners", "أحكام الصيام للمبتدئين",
                "fiqh", "ar", false, 1046, 58, 9, 1291, "2026-11-14T06:15:00Z", new[] { "صيام", "فقه" }),
            new DemoVideo("Arabic grammar, part 3", "Arabic grammar, part 3", "النحو العربي، الجزء الثالث",
                "islamic_lectures", "en", true, 587, 31, 4, 2214, "2026-11-12T16:30:00Z", new[] { "arabic", "nahw" }),
            new DemoVideo("خطبة الجمعة: الصبر", "Friday khutbah: patience", "خطبة الجمعة: الصبر",
                "khutbah", "ar", true, 7935, 402, 61, 1688, "2026-11-06T13:10:00Z", new[] { "خطبة", "الصبر" }),
            new DemoVideo("Hadith of the week: intentions", "Hadith of the week: intentions", "حديث الأسبوع: النيات",
                "hadith_studies", "en", false, 248, 12, 2, 486, "2026-11-04T09:20:00Z", new[] { "hadith", "niyyah" }),
            new DemoVideo("قصص الأنبياء: يونس عليه السلام", "Stories of the prophets: Yunus", "قصص الأنبياء: يونس عليه السلام",
                "islamic_history", "ar", true, 2193, 97, 14, 1462, "2026-10-29T19:45:00Z", new[] { "قصص الأنبياء", "يونس" }),
            new DemoVideo("Q&A: prayer while travelling", "Q&A: prayer while travelling", "سؤال وجواب: الصلاة في السفر",
                "fiqh", "en", true, 1608, 73, 22, 903, "2026-10-23T11:00:00Z", new[] { "salah", "travel" }),
        };
        _videos = videos.Select((v, i) => new Dictionary<string, object?>
        {
            ["id"] = "demo-v" + (i + 1),
            ["title"] = v.Title,
            ["titleLocalized"] = new Dictionary<string, string> { ["en"] = v.En, ["ar"] = v.Ar },
            ["description"] = "",
            ["channelId"] = "demo-c1",
            ["isPublic"] = v.IsPublic,
            ["uploadStatus"] = v.Views == 0 ? "processing" : "ready",
            ["category"] = v.Category,
            ["language"] = v.Language,
            // Real-looking tags (never "demo": the frames show them in the edit dialog).
            ["tags"] = v.Tags,
            ["views"] = v.Views,
            ["likesCount"] = v.Likes,
            ["commentsCount"] = v.Comments,
            ["duration"] = v.Seconds,
            ["thumbnailUrl"] = Thumbnail(i),
            ["dateCreated"] = v.Created,
        }).ToList();
    }

    public Task<string> SendAsync(ApiRequest request, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Answer(request));
        }
    }

    public Task<IReadOnlyList<Suggestion>> SuggestAsync(string text, string? channelId, int limit, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<Suggestion> found = _videos
                .Where(v => Matches(v, text.Trim()))
                .Take(limit)
                .Select(v => new Suggestion((string)v["title"]!, SuggestionKind.Video, (string)v["id"]!, null))
                .ToList();
            return Task.FromResult(found);
        }
    }

    /// <summary>As the API's search: the title, the per-language titles and the tags.</summary>
    private static bool Matches(Dictionary<string, object?> video, string text)
    {
        if (text.Length == 0) return true;
        var fields = new List<string> { (string)video["title"]! };
        if (video["titleLocalized"] is Dictionary<string, string> localized) fields.AddRange(localized.Values);
        if (video["tags"] is IEnumerable<string> tags) fields.AddRange(tags);
        return fields.Any(f => f.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>Parses <c>AUN_DEMO_FEATURES_OFF</c> ("upload,analytics").</summary>
    public static IReadOnlyList<string> FeaturesOff(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private string Answer(ApiRequest r)
    {
        switch (r.OperationId)
        {
            case "GET " + FeaturesClient.Route:
            {
                var keys = new[]
                {
                    UserFeatures.Upload, UserFeatures.CreateChannel, UserFeatures.Live, UserFeatures.CaptionsTranscribe, UserFeatures.CaptionsTranslate,
                    UserFeatures.InlineTranslate, UserFeatures.ViewerTranslatedTitles, UserFeatures.QuranEditions, UserFeatures.QuranText, UserFeatures.AudioTracks,
                    UserFeatures.SiteDomains, UserFeatures.SocialAutopost, UserFeatures.YoutubeImport, UserFeatures.AiCategorySuggest, UserFeatures.BulkSchedule,
                };
                return Json(new { version = 1, features = keys.ToDictionary(k => k, k => !_off.Contains(k)) });
            }

            case "get_ChannelAccessList":
                return Json(new
                {
                    success = true,
                    data = new object[]
                    {
                        new { id = "demo-c1", userId = "demo-1", name = "قناة النور", handle = "alnoor", isMain = true, role = "owner", capabilities = Array.Empty<string>() },
                        new { id = "demo-c2", userId = "demo-9", name = _language == "ar" ? "فريق إعلام المسجد" : "Masjid Media Team", handle = "masjidmedia", isMain = false, role = "editor", capabilities = new[] { "video.upload", "video.edit" } },
                    },
                });
            case "get_VideosList":
            {
                var q = r.Query.TryGetValue("q", out var text) ? text : null;
                var filter = r.Query.TryGetValue("filter", out var f) ? f : "all";
                var channel = r.Query.TryGetValue("channelId", out var c) ? c : "";
                var rows = _videos.Where(v => (string)v["channelId"]! == channel)
                    .Where(v => string.IsNullOrWhiteSpace(q) || Matches(v, q.Trim()))
                    .Where(v => filter == "all" || (filter == "public") == (bool)v["isPublic"]!)
                    .ToList();
                return Json(new { data = rows });
            }

            case "put_VideoUpdate":
            {
                var v = _videos.FirstOrDefault(x => (string)x["id"]! == r.PathParams["id"]);
                if (v != null && r.BodyJson != null)
                {
                    using var doc = JsonDocument.Parse(r.BodyJson);
                    foreach (var p in doc.RootElement.EnumerateObject()) v[p.Name] = Plain(p.Value);
                }

                return "{\"success\":true}";
            }

            case "post_VideoBulkUpdate":
            {
                using var doc = JsonDocument.Parse(r.BodyJson ?? "{}");
                var ids = doc.RootElement.GetProperty("videoIds").EnumerateArray().Select(x => x.GetString()).ToHashSet();
                var changes = doc.RootElement.GetProperty("changes");
                foreach (var v in _videos.Where(x => ids.Contains((string)x["id"]!)))
                {
                    if (changes.TryGetProperty("isPublic", out var p)) v["isPublic"] = p.GetBoolean();
                    if (changes.TryGetProperty("category", out var cat)) v["category"] = cat.ValueKind == JsonValueKind.Null ? null : cat.GetString();
                }

                return "{\"success\":true}";
            }

            case "post_VideoBulkDelete":
            case "POST /api/videos/bulk-restore":
            case "POST /api/videos/bulk-purge":
            {
                using var doc = JsonDocument.Parse(r.BodyJson ?? "{}");
                var ids = doc.RootElement.GetProperty("videoIds").EnumerateArray().Select(x => x.GetString()!).ToList();
                foreach (var id in ids)
                {
                    if (r.OperationId == "post_VideoBulkDelete" && _videos.FirstOrDefault(v => (string)v["id"]! == id) is { } v)
                    {
                        _videos.Remove(v);
                        _deleted.Add((v, DateTimeOffset.UtcNow, "deleted"));
                    }
                    else if (_deleted.FindIndex(d => (string)d.Video["id"]! == id) is var i and >= 0)
                    {
                        if (r.OperationId.EndsWith("restore", StringComparison.Ordinal))
                        {
                            _videos.Add(_deleted[i].Video);
                            _deleted.RemoveAt(i);
                        }
                        else
                        {
                            _deleted[i] = (_deleted[i].Video, _deleted[i].At, "queued");
                        }
                    }
                }

                return Json(new { success = true, results = ids.Select(id => new { videoId = id, success = true }) });
            }

            case "GET /api/channels/{id}/content/deleted":
                return Json(new
                {
                    success = true,
                    result = new
                    {
                        items = _deleted.Select(d => new { id = d.Video["id"], title = d.Video["title"], deletedAt = d.At.ToString("O"), purgeAfter = d.At.AddDays(30).ToString("O"), status = d.Status }),
                        cursor = (string?)null,
                    },
                });
            case "get_PlaylistsList":
            {
                // Names as the creator gave them in the frame's language, like the channel names.
                var ar = _language == "ar";
                return Json(new
                {
                    data = new[]
                    {
                        new { id = "demo-p1", title = ar ? "تفسير القرآن" : "Quran tafsir", videos = new[] { "demo-v1" }, isPublic = true },
                        new { id = "demo-p2", title = ar ? "خطب الجمعة" : "Friday khutbahs", videos = new[] { "demo-v5" }, isPublic = true },
                    },
                });
            }
            case "get_AnalyticsChannelGet":
            {
                if (_off.Contains("analytics")) throw StudioApiException.FromServer("get_AnalyticsChannelGet not found", 404, "feature_off");
                var start = DateTime.UtcNow.Date.AddDays(-27);
                long Sum(string key) => _videos.Sum(v => Convert.ToInt64(v[key], System.Globalization.CultureInfo.InvariantCulture));
                return Json(new
                {
                    // Lifetime totals are the library's own numbers, so nothing on screen contradicts the video list.
                    totalViews = Sum("views"),
                    totalLikes = Sum("likesCount"),
                    totalComments = Sum("commentsCount"),
                    totalSubscribers = 212,
                    topPerformingVideos = _videos.OrderByDescending(v => Convert.ToInt64(v["views"], System.Globalization.CultureInfo.InvariantCulture)).Take(5)
                        .Select(v => new { videoId = v["id"], title = v["title"], titleLocalized = v["titleLocalized"], views = v["views"], thumbnailUrl = v["thumbnailUrl"] }),
                    recentMetrics = DemoSeries.Days(start, 28, Sum("views"), Sum("likesCount"), Sum("commentsCount"), subscribers: 46)
                        .Select(d => new { date = d.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), views = d.Views, likes = d.Likes, comments = d.Comments, subscribers = d.Subscribers, watchTime = d.WatchSeconds }),
                });
            }

            case "get_AnalyticsVideoGet":
                return Json(new { views = 1200, likes = 40, comments = 8, watchTime = 54000, avgViewDuration = 312 });
            default:
                return "{\"success\":true}";
        }
    }

    private string? Thumbnail(int index)
    {
        if (string.IsNullOrEmpty(_thumbs)) return null;
        var localized = Path.Combine(_thumbs, $"thumb-{index + 1}.{_language}.jpg");
        var english = Path.Combine(_thumbs, $"thumb-{index + 1}.jpg");
        var path = _language != "en" && File.Exists(localized) ? localized : english;
        return File.Exists(path) ? new Uri(Path.GetFullPath(path)).AbsoluteUri : null;
    }

    private sealed record DemoVideo(string Title, string En, string Ar, string Category, string Language, bool IsPublic, long Views, long Likes,
        long Comments, int Seconds, string Created, string[] Tags);

    private static object? Plain(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.Array => e.EnumerateArray().Select(x => x.GetString()).ToArray(),
        _ => null,
    };

    private static string Json(object value) => JsonSerializer.Serialize(value);
}

/// <summary>
/// The demo channel's last <c>n</c> days: a weekly rhythm (Friday highest, Tuesday lowest), seeded day-to-day noise and two
/// spikes, the way a real channel moves. The period totals are fixed shares of the library's lifetime numbers, so the
/// Views card never exceeds what the videos add up to.
/// </summary>
public static class DemoSeries
{
    public sealed record DemoDay(DateTime Date, long Views, long Likes, long Comments, long Subscribers, long WatchSeconds);

    public static IReadOnlyList<DemoDay> Days(DateTime start, int n, long libraryViews, long libraryLikes, long libraryComments, long subscribers)
    {
        var random = new Random(1447);
        double Weekday(DayOfWeek d) => d switch
        {
            DayOfWeek.Friday => 1.45, DayOfWeek.Saturday => 1.2, DayOfWeek.Thursday => 1.1, DayOfWeek.Sunday => 1.0,
            DayOfWeek.Wednesday => 0.9, DayOfWeek.Monday => 0.85, _ => 0.78,
        };
        var dates = Enumerable.Range(0, n).Select(i => start.AddDays(i)).ToList();
        // A slow climb across the period, plus a new-upload spike on day 6 and a shared-link spike on day 19.
        var weights = dates.Select((d, i) => Weekday(d.DayOfWeek) * (0.85 + 0.3 * i / Math.Max(1, n - 1)) * (0.82 + 0.36 * random.NextDouble())
            * (i == 6 ? 2.1 : i == 19 ? 1.7 : i == 7 ? 1.35 : 1.0)).ToList();
        var views = Split((long)Math.Round(libraryViews * 0.82), weights);
        var likes = Split((long)Math.Round(libraryLikes * 0.85), weights.Select(w => w * (0.7 + 0.6 * random.NextDouble())).ToList());
        var comments = Split((long)Math.Round(libraryComments * 0.85), weights.Select(w => w * (0.5 + random.NextDouble())).ToList());
        var subs = Split(subscribers, weights.Select(w => w * (0.6 + 0.8 * random.NextDouble())).ToList());
        return dates.Select((d, i) => new DemoDay(d, views[i], likes[i], comments[i], subs[i], (long)Math.Round(views[i] * (255 + 70 * random.NextDouble())))).ToList();
    }

    /// <summary>Splits <paramref name="total"/> in proportion to <paramref name="weights"/> (largest remainder), so the parts add up exactly.</summary>
    public static long[] Split(long total, IReadOnlyList<double> weights)
    {
        var sum = weights.Sum();
        var exact = weights.Select(w => total * w / sum).ToArray();
        var parts = exact.Select(x => (long)Math.Floor(x)).ToArray();
        var left = total - parts.Sum();
        foreach (var i in Enumerable.Range(0, parts.Length).OrderByDescending(i => exact[i] - parts[i]).Take((int)left)) parts[i]++;
        return parts;
    }
}
