using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AlUmmahNow.CoreBridge;

namespace AlUmmahNow.App.Demo
{
    /// <summary>
    /// Live streams for review frames and the automation scenarios (<c>AUN_DEMO_LIVE=1</c>; <c>off</c>: with every Epic 17
    /// switch off and nothing on air): no network, never in a normal launch. The stream and the recording play the committed fixture (<c>AUN_FIXTURE_MEDIA</c>),
    /// and have its title and channel.
    /// </summary>
    public sealed class DemoLiveClient : ILiveClient
    {
        public const string LiveId = "demo-live", PremiereId = "demo-premiere", SoonId = "demo-soon", RecordingId = "demo-recording",
            EndedId = "demo-ended", CanceledId = "demo-canceled", PremiereEndedId = "demo-premiere-ended", PremiereCanceledId = "demo-premiere-canceled";

        private readonly bool _arabic;
        private readonly bool _off;
        private readonly HashSet<string> _reminders = new();
        private readonly bool _remindFails;
        private readonly string? _media;
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;

        /// <param name="off">Every switch off, and nothing on air.</param>
        /// <param name="remindFails">A reminder cannot be set.</param>
        public DemoLiveClient(string? mediaFolder, string language, bool off = false, bool remindFails = false)
        {
            _off = off;
            _remindFails = remindFails;
            _arabic = language == "ar";
            _media = string.IsNullOrEmpty(mediaFolder) ? null : new Uri(Path.Combine(Path.GetFullPath(mediaFolder), "watch.mp4")).AbsoluteUri;
        }

        private string T(string en, string ar) => _arabic ? ar : en;

        private LiveStream Stream(string id) => new LiveStream
        {
            Id = id,
            Title = id switch
            {
                LiveId => T("Reflections on Surah Al-Kahf, part 1", "تأملات في سورة الكهف، الجزء الأول"),
                PremiereId or PremiereEndedId or PremiereCanceledId => T("Reflections on Surah Al-Kahf, part 2", "تأملات في سورة الكهف، الجزء الثاني"),
                SoonId or CanceledId => T("Friday sermon", "خطبة الجمعة"),
                _ => T("Reflections on Surah Al-Kahf, part 1", "تأملات في سورة الكهف، الجزء الأول"),
            },
            Description = id == LiveId ? T("Weekly. Questions are answered at the end.", "كل أسبوع. تُجاب الأسئلة في الآخر.") : "",
            Status = id switch
            {
                LiveId => LiveStatus.Live,
                RecordingId or EndedId or PremiereEndedId => LiveStatus.Ended,
                CanceledId or PremiereCanceledId => LiveStatus.Cancelled,
                _ => LiveStatus.Scheduled,
            },
            Kind = id is PremiereId or PremiereEndedId or PremiereCanceledId ? LiveKind.Premiere : LiveKind.Live,
            EventType = Event(id),
            ChannelName = T("Masjid Live", "المسجد مباشر"),
            ScheduledStart = Start(id),
            Viewers = id == LiveId ? 128 : (long?)null,
        };

        private LiveEventType? Event(string id) => _off ? (LiveEventType?)null : id == LiveId ? LiveEventType.Halqa : id == SoonId ? LiveEventType.Jumuah : (LiveEventType?)null;

        private DateTimeOffset? Start(string id) => id == PremiereId ? _start.AddMinutes(133) : id == SoonId ? _start.AddHours(9) : (DateTimeOffset?)null;

        public Task<LiveFeatures> GetFeaturesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_off ? new LiveFeatures() : new LiveFeatures { Premieres = true, Reminders = true, Events = true });

        public Task<IReadOnlyList<LiveStream>> ListAsync(LiveStatus status, LiveEventType? eventType = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LiveStream>>((_off ? Array.Empty<string>() : new[] { LiveId, PremiereId, SoonId }).Select(Stream)
                .Where(s => s.Status == status && (eventType == null || s.EventType == eventType)).ToList());

        public Task<LiveStream> GetStreamAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(Stream(id));

        public Task<LiveState> GetStateAsync(string id, CancellationToken cancellationToken = default)
        {
            var stream = Stream(id);
            return Task.FromResult(new LiveState
            {
                Status = stream.Status,
                Kind = stream.Kind,
                EventType = stream.EventType,
                PlaybackUrl = id == LiveId ? _media : null,
                VodUrl = id == RecordingId ? _media : null,
                ScheduledStart = stream.ScheduledStart,
                Viewers = stream.Viewers,
                ChatEnabled = !_off && (stream.Status == LiveStatus.Live || stream.Status == LiveStatus.Scheduled),
                ChatReplay = !_off && id == RecordingId,
                ReminderEnabled = !_off && stream.Status == LiveStatus.Scheduled,
                ReminderSet = _reminders.Contains(id),
            });
        }

        public Task<LiveChatPage> GetChatAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(id == LiveId
                ? new LiveChatPage(Messages, new LiveChatMessage { Id = "0", AuthorName = T("Masjid Live", "المسجد مباشر"), Text = T("Please keep to today’s subject.", "نرجو الالتزام بموضوع اليوم.") })
                : new LiveChatPage(Array.Empty<LiveChatMessage>()));

        public Task<LiveChatPage> GetChatReplayAsync(string id, long fromMs, long toMs, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LiveChatPage(Messages.Where(m => m.OffsetMs >= fromMs && m.OffsetMs < toMs).ToList()));

        public Task<long?> BeatAsync(string id, string viewerId, bool leaving, CancellationToken cancellationToken = default) => Task.FromResult<long?>(128);

        public Task SetReminderAsync(string id, bool on, CancellationToken cancellationToken = default)
        {
            if (_remindFails) return Task.FromException(new CatalogException("demo: not set"));
            if (on) _reminders.Add(id);
            else _reminders.Remove(id);
            return Task.CompletedTask;
        }

        private IReadOnlyList<LiveChatMessage> Messages => new[]
        {
            Message("1", 0, T("Yusuf Rahman", "يوسف الرحمن"), T("Assalamu alaikum from Toronto.", "السلام عليكم من تورنتو.")),
            Message("2", 600, T("Masjid Live", "المسجد مباشر"), T("Wa alaikum assalam. Welcome, everyone.", "وعليكم السلام ورحمة الله. أهلًا بكم جميعًا."), LiveChatRole.Owner),
            Message("3", 1_200, T("Khadija Haddad", "خديجة حداد"), T("Is there a recording after?", "هل يبقى التسجيل بعد ذلك؟")),
            Message("4", 1_800, T("Omar Farooq", "عمر الفاروق"), T("Yes, it stays on the channel with the chat beside it.", "نعم، يبقى في القناة ومعه المحادثة."), LiveChatRole.Moderator),
            Message("5", 2_400, T("Bilal Ahmed", "بلال أحمد"), T("The sound is clear now, jazakum Allahu khayran.", "الصوت واضح الآن، جزاكم الله خيرًا.")),
        };

        private static LiveChatMessage Message(string id, long offset, string author, string text, LiveChatRole? role = null) =>
            new LiveChatMessage { Id = id, OffsetMs = offset, AuthorName = author, Text = text, Role = role };
    }
}
