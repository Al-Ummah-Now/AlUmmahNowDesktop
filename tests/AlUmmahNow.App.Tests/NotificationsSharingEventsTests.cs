using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.ViewModels;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.CoreBridge.Temporary;
using Xunit;

namespace AlUmmahNow.App.Tests;

public class NotificationsSharingEventsTests
{
    private const string WebHost = "videos.qa.alummahnow.net";
    private const string ShortHost = "qa.alummahnow.link";

    [Fact]
    public void Short_links_build_and_parse_across_all_supported_routes()
    {
        var videoUrl = ShortLinks.Video("vid_123", ShortHost, startSeconds: 95, playlistId: "pl_1", playlistIndex: 2, lang: "ar");
        Assert.Equal("https://qa.alummahnow.link/v/vid_123?t=95&list=pl_1&index=2&lang=ar", videoUrl);
        var parsed = ShortLinks.Parse(videoUrl, WebHost, ShortHost);
        Assert.NotNull(parsed);
        Assert.Equal(AppLinkKind.Video, parsed!.Kind);
        Assert.Equal("vid_123", parsed.IdOrHandle);
        Assert.Equal("ar", parsed.Lang);
        Assert.Equal(95L, parsed.StartSeconds);
        Assert.Equal("pl_1", parsed.PlaylistId);
        Assert.Equal(2, parsed.PlaylistIndex);

        var liveUrl = ShortLinks.Live("str_7", ShortHost, lang: "ar");
        Assert.Equal("https://qa.alummahnow.link/e/str_7?lang=ar", liveUrl);
        Assert.Equal(AppLinkKind.Live, ShortLinks.Parse(liveUrl, WebHost, ShortHost)!.Kind);

        var handleUrl = ShortLinks.ChannelHandle("@الأمة", ShortHost, lang: "ar");
        var handleParsed = ShortLinks.Parse(handleUrl, WebHost, ShortHost);
        Assert.Equal(AppLinkKind.ChannelHandle, handleParsed!.Kind);
        Assert.Equal("الأمة", handleParsed.IdOrHandle);

        Assert.Equal(AppLinkKind.Short, ShortLinks.Parse(ShortLinks.Short("sh_1", ShortHost), WebHost, ShortHost)!.Kind);
        Assert.Equal(AppLinkKind.ChannelId, ShortLinks.Parse(ShortLinks.ChannelId("ch_1", ShortHost), WebHost, ShortHost)!.Kind);
        Assert.Equal(AppLinkKind.Post, ShortLinks.Parse(ShortLinks.Post("po_1", ShortHost), WebHost, ShortHost)!.Kind);
        Assert.Equal(AppLinkKind.Playlist, ShortLinks.Parse(ShortLinks.Playlist("pl_1", ShortHost), WebHost, ShortHost)!.Kind);

        Assert.Null(ShortLinks.Video("bad/id", ShortHost));
        Assert.Null(ShortLinks.Parse("https://evil.example/v/vid_123", WebHost, ShortHost));
    }

    [Fact]
    public async Task WatchViewModel_builds_short_share_link_with_optional_position_and_locale()
    {
        var c = new FakeCatalog();
        c.Videos.Add(FakeCatalog.Ready("vid_42"));
        var p = new FakePlayer();
        var s = new FakeSession();
        var vm = new WatchViewModel(c, p, s, new Localizer("ar"));
        await vm.LoadAsync("vid_42");
        p.Tick(83);

        Assert.Equal("https://alummahnow.link/v/vid_42?lang=ar", vm.ShareUrl());
        Assert.Equal("https://alummahnow.link/v/vid_42?t=83&lang=ar", vm.ShareUrl(atCurrentPosition: true));
        Assert.Contains("١:٢٣", vm.ShareAtPositionLabel);
    }

    [Fact]
    public void Localizer_formats_capped_badges_and_quiet_window_times_in_both_languages()
    {
        var ar = new Localizer("ar");
        var en = new Localizer("en");

        Assert.Equal("", ar.BadgeCap9(0));
        Assert.Equal("٤", ar.BadgeCap9(4));
        Assert.Equal("٩+", ar.BadgeCap9(14));
        Assert.Equal("9+", en.BadgeCap9(14));

        Assert.Equal("\u2068١٠:٠٠ م\u2069", ar.MinuteOfDay(1320));
        Assert.Equal("\u206810:00 PM\u2069", en.MinuteOfDay(1320));
        Assert.Equal("\u20686:05 AM\u2069", en.MinuteOfDay(365));
        Assert.Equal("\u206812:00 AM\u2069", en.MinuteOfDay(0));
        Assert.Equal("\u2068١٢:٣٠ م\u2069", ar.MinuteOfDay(750));
        Assert.Equal("\u206811:59 PM\u2069", en.MinuteOfDay(-1));
    }

    [Fact]
    public void Quiet_hours_evaluate_overnight_and_prayer_windows()
    {
        var qh = new NotificationQuietHours(
            enabled: true,
            timezone: "Asia/Riyadh",
            startMinute: 1320,
            endMinute: 360,
            prayerQuietEnabled: true,
            prayerWindows: new[] { new PrayerQuietWindow(735, 760) });
        Assert.True(qh.ContainsMinute(1380));
        Assert.True(qh.ContainsMinute(120));
        Assert.False(qh.ContainsMinute(600));
        Assert.True(qh.ContainsMinute(745));
    }

    [Fact]
    public async Task Notifications_and_events_view_models_and_http_clients_work_end_to_end()
    {
        var responses = new Queue<string>(new[]
        {
            // 1) OpenInboxAsync when quietHours switch is OFF -> skips GetQuietHoursAsync
            """{"version":1,"features":{"quietHours":false}}""",
            """{"result":{"items":[{"id":"n1","category":"subscribers","notificationType":"subscription","notificationKey":"newSubscriber","params":{},"readAt":null,"createdAt":1795450000000},{"id":"n2","category":"account","notificationType":"system","notificationKey":"tierRequestApproved","params":{"tier_en":"Creator","tier_ar":"صانع محتوى"},"readAt":null,"createdAt":1795450001000}],"unreadCount":2,"unseenCount":2,"menuOpen":"seen"}}""",
            """{"result":{"unreadCount":2,"unseenCount":0,"menuOpen":"seen"}}""",
            // 2) OpenInboxAsync when quietHours switch is ON -> fetches quietHours; prayerWindows starts empty so IsPrayerQuietVisible is false
            """{"version":1,"features":{"quietHours":true}}""",
            """{"result":{"items":[{"id":"n3","category":"live","notificationType":"stream","notificationKey":"liveStarted","params":{"channelName":"المنار","videoTitle":"الحلقة الأسبوعية"},"readAt":null,"createdAt":1795450002000}],"unreadCount":1,"unseenCount":1,"menuOpen":"seen"}}""",
            """{"result":{"unreadCount":1,"unseenCount":0,"menuOpen":"seen"}}""",
            """{"result":{"quietHours":{"enabled":true,"timezone":"Asia/Riyadh","startMinute":1320,"endMinute":360,"prayerQuietEnabled":false,"prayerWindows":[]}}}""",
            // 3) SyncPrayerWindowsAsync -> supplies local windows so IsPrayerQuietVisible turns true, then SetPrayerQuietAsync(true)
            """{"result":{"quietHours":{"enabled":true,"timezone":"Asia/Riyadh","startMinute":1320,"endMinute":360,"prayerQuietEnabled":false,"prayerWindows":[{"startMinute":735,"endMinute":760}]}}}""",
            """{"result":{"quietHours":{"enabled":true,"timezone":"Asia/Riyadh","startMinute":1320,"endMinute":360,"prayerQuietEnabled":true,"prayerWindows":[{"startMinute":735,"endMinute":760}]}}}""",
            // 4) EventsCalendarViewModel feature gate off -> on
            """{"version":1,"features":{"eventCalendar":false}}""",
            """{"version":1,"features":{"eventCalendar":true}}""",
            """{"result":{"items":[{"streamId":"str_1","channelId":"ch_1","channelName":"Al-Manar","channelNameAr":"المنار","title":"Weekly Halqa","kind":"live","eventType":"halqa","status":"scheduled","scheduledStartAt":"2026-11-28T19:00:00Z","watchPath":"/live/str_1"}]}}""",
            """{"success":true}""",
        });
        var paths = new List<string>();
        using var http = new HttpClient(new StubHandler((req) =>
        {
            paths.Add(req.Method.Method + " " + req.RequestUri!.PathAndQuery);
            return responses.Dequeue();
        }));
        var baseUri = new Uri("https://videos.api.qa.alummahnow.net");
        var notifsClient = new HttpNotificationsClient(http, baseUri, _ => Task.FromResult<string?>("tok"));
        var eventsClient = new HttpEventsClient(http, baseUri, _ => Task.FromResult<string?>("tok"));
        var pushRegistrar = new HttpAppPushRegistrar(http, baseUri, _ => Task.FromResult<string?>("tok"));

        var localizer = new Localizer("ar");
        var notifsVm = new NotificationsViewModel(notifsClient, localizer);

        await notifsVm.OpenInboxAsync();
        Assert.False(notifsVm.IsQuietHoursEnabled);
        Assert.False(notifsVm.IsPrayerQuietVisible);
        Assert.Equal("مشترك جديد", notifsVm.TitleFor(notifsVm.Items[0]));
        Assert.Equal("أصبحت لديك فئة صانع محتوى", notifsVm.TitleFor(notifsVm.Items[1]));
        localizer.Language = "en";
        Assert.Equal("New subscriber", notifsVm.TitleFor(notifsVm.Items[0]));
        Assert.Equal("You now have the Creator tier", notifsVm.TitleFor(notifsVm.Items[1]));
        localizer.Language = "ar";

        await notifsVm.OpenInboxAsync();
        Assert.True(notifsVm.IsQuietHoursEnabled);
        Assert.False(notifsVm.IsPrayerQuietVisible);
        Assert.Equal("المنار تبث مباشرة: الحلقة الأسبوعية", notifsVm.TitleFor(notifsVm.Items[0]));
        Assert.False(notifsVm.HasBadge);

        // Prayer toggle is ignored until local windows exist
        await notifsVm.SetPrayerQuietAsync(true);
        Assert.False(notifsVm.QuietHours.PrayerQuietEnabled);

        await notifsVm.SyncPrayerWindowsAsync(new[] { new PrayerQuietWindow(735, 760) }, "Asia/Riyadh");
        Assert.True(notifsVm.IsPrayerQuietVisible);
        await notifsVm.SetPrayerQuietAsync(true);
        Assert.True(notifsVm.QuietHours.PrayerQuietEnabled);

        var eventsVm = new EventsCalendarViewModel(eventsClient, localizer);
        await eventsVm.RefreshAsync();
        Assert.False(eventsVm.IsFeatureEnabled);
        Assert.Empty(eventsVm.Items);

        await eventsVm.RefreshAsync();
        Assert.True(eventsVm.IsFeatureEnabled);
        Assert.Single(eventsVm.Items);
        var starts = new DateTimeOffset(2026, 11, 28, 19, 0, 0, TimeSpan.Zero);
        Assert.Equal(localizer.Join("المنار", localizer.DateTime(starts)), eventsVm.SubtitleFor(eventsVm.Items[0]));
        Assert.DoesNotMatch("[0-9]", eventsVm.SubtitleFor(eventsVm.Items[0]));
        // Without a time, or with one that cannot be read, the channel alone
        foreach (var unknown in new[] { null, "soon" })
        {
            var ev = new CalendarEvent("s", "c", "Al-Manar", "المنار", null, "T", null, "live", "halqa", "scheduled", unknown, null, "/live/s");
            Assert.Equal(localizer.Join("المنار"), eventsVm.SubtitleFor(ev));
        }

        await pushRegistrar.RegisterAsync("viewer", "inst_12345678", "https://wns.example/ch", "ar", "0.1.0");
        Assert.Contains("PUT /api/devices/inst_12345678", paths);

        Assert.False(new PushConfig("AlUmmahNow.Dev", packaged: true).CanRegister);
        Assert.True(new PushConfig("AlUmmahNow.Viewer", packaged: true).CanRegister);
    }

    [Fact]
    public async Task NotificationsViewModel_survives_features_failure_and_rolls_back_failed_quiet_save()
    {
        var step = 0;
        using var http = new HttpClient(new StatusStubHandler((_) =>
        {
            step++;
            return step switch
            {
                // 1) OpenInboxAsync: features call fails with 503, but inbox list + markSeen still succeed
                1 => (HttpStatusCode.ServiceUnavailable, """{"error":{"code":"service_unavailable"}}"""),
                2 => (HttpStatusCode.OK, """{"result":{"items":[{"id":"n1","category":"subscribers","notificationType":"subscription","notificationKey":"newSubscriber","params":{},"readAt":null,"createdAt":1795450000000}],"unreadCount":1,"unseenCount":1,"menuOpen":"seen"}}"""),
                3 => (HttpStatusCode.OK, """{"result":{"unreadCount":1,"unseenCount":0,"menuOpen":"seen"}}"""),
                // 2) OpenInboxAsync with quietHours ON, followed by failed SetDailyQuietAsync(false) -> rolls back to true
                4 => (HttpStatusCode.OK, """{"version":1,"features":{"quietHours":true}}"""),
                5 => (HttpStatusCode.OK, """{"result":{"items":[],"unreadCount":0,"unseenCount":0,"menuOpen":"seen"}}"""),
                6 => (HttpStatusCode.OK, """{"result":{"quietHours":{"enabled":true,"timezone":"UTC","startMinute":1320,"endMinute":360,"prayerQuietEnabled":false,"prayerWindows":[]}}}"""),
                7 => (HttpStatusCode.InternalServerError, """{"error":{"code":"internal_error"}}"""),
                _ => throw new InvalidOperationException("Unexpected call " + step),
            };
        }));
        var client = new HttpNotificationsClient(http, new Uri("https://videos.api.qa.alummahnow.net"), _ => Task.FromResult<string?>("tok"));
        var vm = new NotificationsViewModel(client, new Localizer("en"));

        await vm.OpenInboxAsync();
        Assert.Equal(LoadState.Content, vm.State);
        Assert.Single(vm.Items);
        Assert.False(vm.IsQuietHoursEnabled);

        await vm.OpenInboxAsync();
        Assert.True(vm.IsQuietHoursEnabled);
        Assert.True(vm.QuietHours.Enabled);
        Assert.Equal(360, vm.QuietHours.EndMinute);

        await vm.SetDailyQuietAsync(false);
        Assert.True(vm.QuietHours.Enabled);
    }

    private sealed class StatusStubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> _respond;
        public StatusStubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = _respond(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string> _respond;
        public StubHandler(Func<HttpRequestMessage, string> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_respond(request), Encoding.UTF8, "application/json"),
            });
    }
}
