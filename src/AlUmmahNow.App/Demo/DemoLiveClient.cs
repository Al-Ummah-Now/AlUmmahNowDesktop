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
    /// Live streams for review frames and the automation scenarios (<c>AUN_DEMO_LIVE=1</c>): no network, never in a normal
    /// launch. The stream and the recording play the committed fixture (<c>AUN_FIXTURE_MEDIA</c>).
    /// </summary>
    public sealed class DemoLiveClient : ILiveClient
    {
        public const string LiveId = "demo-live", PremiereId = "demo-premiere", SoonId = "demo-soon", RecordingId = "demo-recording";

        private readonly bool _arabic;
        private readonly string? _media;
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;

        public DemoLiveClient(string? mediaFolder, string language)
        {
            _arabic = language == "ar";
            _media = string.IsNullOrEmpty(mediaFolder) ? null : new Uri(Path.Combine(Path.GetFullPath(mediaFolder), "watch.mp4")).AbsoluteUri;
        }

        private string T(string en, string ar) => _arabic ? ar : en;

        private LiveStream Stream(string id) => new LiveStream
        {
            Id = id,
            Title = id switch
            {
                LiveId => T("Questions after Maghrib", "أسئلة بعد المغرب"),
                PremiereId => T("Tafsir of Surah Al-Kahf", "تفسير سورة الكهف"),
                SoonId => T("Friday sermon", "خطبة الجمعة"),
                _ => T("Questions after Maghrib, last week", "أسئلة بعد المغرب، الأسبوع الماضي"),
            },
            Description = id == LiveId ? T("Weekly. Questions are answered at the end.", "كل أسبوع. تُجاب الأسئلة في الآخر.") : "",
            Status = id == LiveId ? LiveStatus.Live : id == RecordingId ? LiveStatus.Ended : LiveStatus.Scheduled,
            Kind = id == PremiereId ? LiveKind.Premiere : LiveKind.Live,
            EventType = Event(id),
            ChannelName = T("Noor Learning", "نور للتعلّم"),
            ScheduledStart = Start(id),
            Viewers = id == LiveId ? 128 : (long?)null,
        };

        private static LiveEventType? Event(string id) => id == LiveId ? LiveEventType.Halqa : id == SoonId ? LiveEventType.Jumuah : (LiveEventType?)null;

        private DateTimeOffset? Start(string id) => id == PremiereId ? _start.AddMinutes(133) : id == SoonId ? _start.AddHours(9) : (DateTimeOffset?)null;

        public Task<LiveFeatures> GetFeaturesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LiveFeatures { Premieres = true, Reminders = true, Events = true });

        public Task<IReadOnlyList<LiveStream>> ListAsync(LiveStatus status, LiveEventType? eventType = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LiveStream>>(new[] { LiveId, PremiereId, SoonId }.Select(Stream)
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
                ChatEnabled = id != RecordingId,
                ChatReplay = id == RecordingId,
                ReminderEnabled = stream.Status == LiveStatus.Scheduled,
            });
        }

        public Task<LiveChatPage> GetChatAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(id == LiveId
                ? new LiveChatPage(Messages, new LiveChatMessage { Id = "0", AuthorName = T("Noor Learning", "نور للتعلّم"), Text = T("Please keep to today’s subject.", "نرجو الالتزام بموضوع اليوم.") })
                : new LiveChatPage(Array.Empty<LiveChatMessage>()));

        public Task<LiveChatPage> GetChatReplayAsync(string id, long fromMs, long toMs, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LiveChatPage(Messages.Where(m => m.OffsetMs >= fromMs && m.OffsetMs < toMs).ToList()));

        public Task<long?> BeatAsync(string id, string viewerId, bool leaving, CancellationToken cancellationToken = default) => Task.FromResult<long?>(128);

        public Task SetReminderAsync(string id, bool on, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private IReadOnlyList<LiveChatMessage> Messages => new[]
        {
            Message("1", 0, T("Yusuf Rahman", "يوسف الرحمن"), T("Assalamu alaikum from Toronto.", "السلام عليكم من تورنتو.")),
            Message("2", 600, T("Noor Learning", "نور للتعلّم"), T("Wa alaikum assalam. Welcome, everyone.", "وعليكم السلام ورحمة الله. أهلًا بكم جميعًا."), LiveChatRole.Owner),
            Message("3", 1_200, T("Khadija Haddad", "خديجة حداد"), T("Is there a recording after?", "هل يبقى التسجيل بعد ذلك؟")),
            Message("4", 1_800, T("Omar Farooq", "عمر الفاروق"), T("Yes, it stays on the channel with the chat beside it.", "نعم، يبقى في القناة ومعه المحادثة."), LiveChatRole.Moderator),
            Message("5", 2_400, T("Bilal Ahmed", "بلال أحمد"), T("The sound is clear now, jazakum Allahu khayran.", "الصوت واضح الآن، جزاكم الله خيرًا.")),
        };

        private static LiveChatMessage Message(string id, long offset, string author, string text, LiveChatRole? role = null) =>
            new LiveChatMessage { Id = id, OffsetMs = offset, AuthorName = author, Text = text, Role = role };
    }
}
