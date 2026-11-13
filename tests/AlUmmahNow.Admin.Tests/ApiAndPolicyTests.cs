using System.Net;
using AlUmmahNow.Admin.Auth;
using AlUmmahNow.Admin.Media;
using AlUmmahNow.Admin.Push;
using AlUmmahNow.Admin.Review;
using AlUmmahNow.Admin.Security;
using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Logging;
using Xunit;

namespace AlUmmahNow.Admin.Tests;

public class AdminApiReviewServiceTests
{
    private static readonly Uri Admin = new("https://admin-api.qa.alummahnow.net");
    private static readonly Uri Videos = new("https://videos.api.qa.alummahnow.net");

    private static AdminApiReviewService Make(StubHandler handler) =>
        new(Admin, Videos, _ => Task.FromResult<string?>("token-1"), "AlUmmahNowAdminWindows/test", PrivateHttp.Create(handler: handler));

    [Fact]
    public async Task Queue_reads_rows_with_numeric_flags_and_sends_the_tab_and_search()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK,
            """{"result":{"items":[{"id":"r1","subject_id":"v1","status":"in_review","title":"T","risk":0.7,"nsfw":1,"minor":0,"hold_cause":"nsfw","sla_due_at":"2026-11-13T15:04:00Z","version":3}],"nextCursor":"c2"}}"""));
        var page = await Make(handler).QueueAsync(ReviewTab.ActionNeeded, " talk ", null);
        var row = Assert.Single(page.Items);
        Assert.True(row.Nsfw);
        Assert.False(row.Minor);
        Assert.Equal(HoldCause.Nsfw, row.HoldCause);
        Assert.Equal(3, row.Version);
        Assert.Equal("c2", page.NextCursor);
        var request = handler.Seen[0].Request;
        Assert.Equal("/api/review/queue", request.RequestUri!.AbsolutePath);
        Assert.Contains("tab=action_needed", request.RequestUri.Query);
        Assert.Contains("q=talk", request.RequestUri.Query);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.True(request.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task Decision_sends_reason_note_and_version()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, """{"result":{"review":{"id":"r1","status":"rejected","undo_until":"2026-11-13T15:00:10Z"}}}"""));
        var item = await Make(handler).DecideAsync("r1", ReviewDecision.Reject, "hate", "  see 0:42  ", 3);
        Assert.Equal("rejected", item.Status);
        Assert.Equal("""{"decision":"reject","reasonCode":"hate","note":"see 0:42","version":3}""", handler.Seen[0].Body);
    }

    [Theory]
    [InlineData(409, """{"code":"already_decided","decision":"approve","decidedByName":"Yusuf"}""", typeof(AlreadyDecidedException))]
    [InlineData(409, """{"error":"This review is approved"}""", typeof(AlreadyDecidedException))]
    [InlineData(409, """{"code":"claimed","assignedToName":"Yusuf"}""", typeof(ClaimedByOtherException))]
    [InlineData(403, """{"code":"step_up_required"}""", typeof(StepUpRequiredException))]
    [InlineData(403, """{"code":"senior_only"}""", typeof(SeniorOnlyException))]
    [InlineData(410, """{"code":"undo_expired"}""", typeof(UndoExpiredException))]
    [InlineData(401, "{}", typeof(SignInRequiredException))]
    public async Task Refusals_become_typed_errors(int status, string body, Type expected)
    {
        var handler = new StubHandler((_, _) => StubHandler.Json((HttpStatusCode)status, body));
        var ex = await Assert.ThrowsAnyAsync<ReviewException>(() => Make(handler).DecideAsync("r1", ReviewDecision.Approve, null, null, 1));
        Assert.IsType(expected, ex);
    }

    [Fact]
    public void Conflict_without_a_code_reads_the_status_word_from_older_servers()
    {
        var ex = Assert.IsType<AlreadyDecidedException>(AdminApiReviewService.Failure(409, """{"error":"This review is rejected"}"""));
        Assert.Equal("reject", ex.Decision);
    }

    [Fact]
    public async Task Non_staff_profile_is_refused()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, """{"result":{"userId":"u1","roles":["user"]}}"""));
        await Assert.ThrowsAsync<NotStaffException>(() => Make(handler).ProfileAsync());
        var reviewer = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, """{"result":{"userId":"u1","displayName":"A","roles":["Senior Reviewer"]}}"""));
        var profile = await Make(reviewer).ProfileAsync();
        Assert.True(profile.IsSeniorReviewer);
    }

    [Fact]
    public async Task Detail_drops_a_stream_that_is_not_tls()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK,
            """{"result":{"review":{"id":"r1","status":"in_review"},"findings":[],"stream":{"url":"http://cdn.example/v.m3u8?sig=abc"}}}"""));
        var detail = await Make(handler).DetailAsync("r1");
        Assert.Null(detail.Stream);
    }

    [Fact]
    public async Task Logs_never_carry_urls_ids_tokens_or_signatures()
    {
        var sink = new ListSink();
        Log.AddSink(sink);
        try
        {
            var handler = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.Conflict, """{"code":"weird"}"""));
            await Assert.ThrowsAnyAsync<ReviewException>(() => Make(handler).MediaAsync("rev-secret-id", "frames/x.jpg?sig=zzz"));
            var text = string.Join("\n", sink.Lines);
            Assert.Contains("/api/review/:id/media", text);
            Assert.DoesNotContain("rev-secret-id", text);
            Assert.DoesNotContain("sig=", text);
            Assert.DoesNotContain("token-1", text);
            Assert.Equal("ReviewStream(preview=False)", new ReviewStream { Url = new Uri("https://cdn.example/v.m3u8?sig=abc") }.ToString());
        }
        finally
        {
            Log.RemoveSink(sink);
        }
    }

    [Fact]
    public void Plain_http_is_refused_except_loopback()
    {
        Assert.Throws<ArgumentException>(() => new AdminApiReviewService(new Uri("http://admin-api.example"), Videos, _ => Task.FromResult<string?>("t"), "ua"));
        Assert.True(PrivateHttp.IsAllowed(new Uri("http://127.0.0.1:8787/")));
        Assert.Equal("https://cdn.example", PrivateHttp.Describe(new Uri("https://cdn.example/a/b.m3u8?sig=1")));
    }

    [Fact]
    public async Task Push_registration_is_the_admin_app_on_wns()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, "{}"));
        await Make(handler).RegisterAsync("install-1", "https://db5p.notify.windows.com/w/?token=abc", "ar-SA", "0.4.0");
        var (request, body) = handler.Seen[0];
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("https://videos.api.qa.alummahnow.net/api/v1/devices/install-1", request.RequestUri!.AbsoluteUri);
        Assert.Contains("\"app\":\"admin\"", body);
        Assert.Contains("\"provider\":\"wns\"", body);
        Assert.Contains("\"locale\":\"ar\"", body);
        Assert.Throws<ArgumentException>(() => AdminPushRegistration.Body("https://evil.example/notify.windows.com", "en", "1"));
    }

    private sealed class ListSink : ILogSink
    {
        public List<string> Lines { get; } = new();

        public void Write(LogEntry entry)
        {
            lock (Lines) Lines.Add(entry.ToString());
        }
    }
}

public class PolicyTests
{
    [Fact]
    public void Hello_on_open_and_after_five_minutes_hidden()
    {
        var time = new ManualTime();
        var policy = new LockPolicy(time);
        Assert.True(policy.IsLocked);
        policy.Unlocked();
        policy.Hidden();
        time.Advance(TimeSpan.FromMinutes(4));
        Assert.False(policy.Shown());
        policy.Hidden();
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(policy.Shown());
    }

    [Fact]
    public void Step_up_asks_for_a_sign_in_from_the_last_fifteen_minutes()
    {
        var url = StepUp.WithMaxAge(new Uri("https://auth.qa.alummahnow.net/authorize?client_id=c&state=s&code_challenge=x&prompt=select_account"));
        Assert.Contains("prompt=login", url.Query);
        Assert.Contains("max_age=900", url.Query);
        Assert.DoesNotContain("select_account", url.Query);
        Assert.Contains("code_challenge=x", url.Query);
    }

    [Theory]
    [InlineData("alummahnow-admin://auth/callback?code=1&state=2", true)]
    [InlineData("alummahnow-admin://evil/callback?code=1", false)]
    [InlineData("alummahnow-studio://auth/callback?code=1", false)]
    [InlineData(null, false)]
    public void Only_the_exact_callback_completes_sign_in(string? uri, bool ok) => Assert.Equal(ok, AdminRedirect.IsCallback(uri));

    [Theory]
    [InlineData("open:rev-1", PushAction.Open, "rev-1")]
    [InlineData("claim:rev-1", PushAction.Claim, "rev-1")]
    [InlineData("/moderation/review/rev-1", PushAction.Open, "rev-1")]
    public void Toast_arguments_open_or_claim(string args, PushAction action, string id)
    {
        var a = PushActivation.Parse(args)!;
        Assert.Equal(action, a.Action);
        Assert.Equal(id, a.ReviewId);
    }

    [Theory]
    [InlineData("open:../../x")]
    [InlineData("claim:")]
    [InlineData("delete:rev-1")]
    [InlineData("https://evil.example")]
    public void Other_toast_arguments_are_ignored(string args) => Assert.Null(PushActivation.Parse(args));

    [Fact]
    public void Clear_raw_notification_names_the_toast_to_remove()
    {
        var clear = PushClear.Parse("""{"kind":"review_clear","reviewId":"rev-1","tag":"0123456789abcdef","group":"review"}""")!;
        Assert.Equal("0123456789abcdef", clear.Tag);
        Assert.Equal("review", clear.Group);
        Assert.Equal(16, PushClear.Parse("""{"kind":"review_clear","reviewId":"rev-1"}""")!.Tag.Length);
        Assert.Null(PushClear.Parse("""{"kind":"notification"}"""));
        Assert.Null(PushClear.Parse("not json"));
    }

    [Fact]
    public void Push_is_off_without_a_real_package_identity()
    {
        Assert.False(new PushConfig(null, packaged: true).CanRegister);
        Assert.False(new PushConfig("AlUmmahNow.Admin.Dev", packaged: true).CanRegister);
        Assert.False(new PushConfig("AlUmmahNow.Admin", packaged: false).CanRegister);
        Assert.True(new PushConfig("AlUmmahNow.Admin", packaged: true).CanRegister);
    }

    [Theory]
    [InlineData("A", false, false, ReviewCommand.Approve)]
    [InlineData("R", false, false, ReviewCommand.Reject)]
    [InlineData("E", false, false, ReviewCommand.Escalate)]
    [InlineData("C", false, false, ReviewCommand.RequestChanges)]
    [InlineData("Z", true, false, ReviewCommand.Undo)]
    [InlineData("J", false, false, ReviewCommand.Next)]
    [InlineData("Number191", false, true, ReviewCommand.Help)]
    [InlineData("Q", false, false, ReviewCommand.None)]
    public void Keyboard_shortcuts(string key, bool ctrl, bool shift, ReviewCommand expected) => Assert.Equal(expected, ReviewShortcuts.For(key, ctrl, shift));

    [Fact]
    public void Every_admin_string_and_reason_exists_in_english_and_arabic()
    {
        var l = new Localizer("en");
        var en = l.Bundle("en");
        var ar = l.Bundle("ar");
        foreach (var key in en.Keys.Where(k => k.StartsWith("admin.", StringComparison.Ordinal))) Assert.True(ar.ContainsKey(key), key);
        foreach (var code in ReviewReasons.Codes) Assert.True(en.ContainsKey("admin.reason." + code), code);
        foreach (var (key, _) in ReviewShortcuts.Help) Assert.True(en.ContainsKey(key), key);
        // «مرئي» for video in Arabic, never «فيديو».
        Assert.DoesNotContain(ar.Where(kv => kv.Key.StartsWith("admin.", StringComparison.Ordinal)), kv => kv.Value.Contains("فيديو"));
    }
}
