using System.Text.Json;
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

    public DemoTransport()
    {
        string[] titles =
        {
            "تفسير سورة الكهف — الدرس الأول", "Seerah: the early years in Makkah", "أحكام الصيام للمبتدئين", "Arabic grammar, lesson 3",
            "خطبة الجمعة: الصبر", "Hadith of the week: intentions", "قصص الأنبياء: يونس عليه السلام", "Q&A: prayer while travelling",
        };
        _videos = titles.Select((t, i) => new Dictionary<string, object?>
        {
            ["id"] = "demo-v" + (i + 1),
            ["title"] = t,
            ["description"] = "",
            ["channelId"] = "demo-c1",
            ["isPublic"] = i % 3 != 2,
            ["uploadStatus"] = i == 1 ? "processing" : "ready",
            ["category"] = i % 2 == 0 ? "quran_tafsir" : "islamic_lectures",
            ["language"] = i % 2 == 0 ? "ar" : "en",
            ["tags"] = new[] { "demo" },
            ["views"] = 1200 - (i * 130),
            ["likesCount"] = 40 - (i * 4),
            ["commentsCount"] = 8 - i,
            ["duration"] = 600 + (i * 95),
            ["dateCreated"] = new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero).AddDays(-i * 3).ToString("O"),
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
                .Where(v => ((string)v["title"]!).IndexOf(text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(limit)
                .Select(v => new Suggestion((string)v["title"]!, SuggestionKind.Video, (string)v["id"]!, null))
                .ToList();
            return Task.FromResult(found);
        }
    }

    private string Answer(ApiRequest r)
    {
        switch (r.OperationId)
        {
            case "get_ChannelAccessList":
                return Json(new
                {
                    success = true,
                    data = new object[]
                    {
                        new { id = "demo-c1", userId = "demo-1", name = "دروس النور", handle = "alnoor", isMain = true, role = "owner", capabilities = Array.Empty<string>() },
                        new { id = "demo-c2", userId = "demo-9", name = "Masjid Media Team", handle = "masjidmedia", isMain = false, role = "editor", capabilities = new[] { "video.upload", "video.edit" } },
                    },
                });
            case "get_VideosList":
            {
                var q = r.Query.TryGetValue("q", out var text) ? text : null;
                var filter = r.Query.TryGetValue("filter", out var f) ? f : "all";
                var channel = r.Query.TryGetValue("channelId", out var c) ? c : "";
                var rows = _videos.Where(v => (string)v["channelId"]! == channel)
                    .Where(v => q == null || ((string)v["title"]!).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
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

            case "delete_VideoDelete":
                _videos.RemoveAll(x => (string)x["id"]! == r.PathParams["id"]);
                return "{\"success\":true}";
            case "get_PlaylistsList":
                return Json(new { data = new[] { new { id = "demo-p1", title = "تفسير القرآن", videos = new[] { "demo-v1" }, isPublic = true }, new { id = "demo-p2", title = "Weekly lessons", videos = Array.Empty<string>(), isPublic = true } } });
            case "get_AnalyticsChannelGet":
            {
                var start = DateTime.UtcNow.Date.AddDays(-27);
                return Json(new
                {
                    totalViews = 18240,
                    totalLikes = 912,
                    totalComments = 143,
                    totalSubscribers = 1310,
                    topPerformingVideos = _videos.Take(5).Select(v => new { videoId = v["id"], title = v["title"], views = v["views"] }),
                    recentMetrics = Enumerable.Range(0, 28).Select(i => new { date = start.AddDays(i).ToString("yyyy-MM-dd"), views = 400 + ((i * 37) % 260), watchTime = 36000 + ((i * 1700) % 9000) }),
                });
            }

            case "get_AnalyticsVideoGet":
                return Json(new { views = 1200, likes = 40, comments = 8, watchTime = 54000, avgViewDuration = 312 });
            default:
                return "{\"success\":true}";
        }
    }

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
