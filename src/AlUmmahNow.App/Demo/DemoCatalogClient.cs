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
    /// Fixture catalog for CI screenshots (<c>AUN_DEMO=1</c>): canned videos in every home rail, no
    /// network. Never used in a normal launch. Thumbnails are title cards in the UI language
    /// (<c>thumb-1.jpg</c>… in English, <c>thumb-1.ar.jpg</c>… in Arabic, one per video), or else <c>thumb-1.png</c>,
    /// <c>thumb-2.png</c>… in an optional folder (the store screenshot workflow draws them); without
    /// it the cards show their placeholder.
    /// </summary>
    public sealed class DemoCatalogClient : ICatalogClient
    {
        // Category, titles, then a hand-made life for each video, as a real catalog has: the channel that posted it, its
        // length in seconds, its views and how many hours ago it went up. Nothing follows a formula, so no rail repeats
        // a pattern (lengths, views, channels or upload times).
        private static readonly (string Category, string En, string Ar, int Channel, int Seconds, long Views, int HoursAgo)[] Fixtures =
        {
            ("quran_recitation", "Surah Al-Mulk, full recitation", "سورة الملك كاملة", 1, 1834, 48210, 5),
            ("quran_recitation", "Surah Ar-Rahman", "سورة الرحمن", 2, 1412, 126400, 52),
            ("quran_tafsir", "Reflections on Surah Al-Kahf, part 1", "تأملات في سورة الكهف، الجزء الأول", 0, 2675, 8730, 20),
            ("quran_tafsir", "The meaning of Al-Fatiha", "معاني سورة الفاتحة", 0, 1148, 31950, 98),
            ("hadith_studies", "Hadith of the week: intentions", "حديث الأسبوع: النيات", 1, 486, 5410, 9),
            ("hadith_studies", "Forty hadith, episode 2", "الأربعون النووية، الحلقة الثانية", 2, 1567, 2980, 140),
            ("fiqh", "Q&A: prayer while travelling", "سؤال وجواب: الصلاة في السفر", 2, 903, 17620, 31),
            ("fiqh", "Fasting: common questions", "الصيام: أسئلة شائعة", 0, 1291, 64300, 210),
            ("islamic_history", "Seerah: the early years in Makkah", "السيرة: السنوات الأولى في مكة", 3, 3390, 22840, 15),
            ("islamic_history", "Al-Andalus: a short history", "الأندلس: تاريخ موجز", 3, 2218, 9640, 76),
            ("islamic_nasheed", "Tala'a al-Badru 'Alayna", "طلع البدر علينا", 1, 264, 241500, 400),
            ("islamic_nasheed", "A nasheed for the morning", "نشيد الصباح", 0, 197, 13280, 44),
            ("ramadan", "Preparing for Ramadan", "الاستعداد لرمضان", 1, 1735, 37010, 26),
            ("ramadan", "Iftar around the world", "الإفطار حول العالم", 3, 742, 92450, 300),
            // Four per category, so every category rail fills its row.
            ("quran_recitation", "Surah Yasin, full recitation", "سورة يس كاملة", 2, 1306, 71830, 64),
            ("quran_recitation", "Surah Al-Waqi'ah", "سورة الواقعة", 1, 947, 19470, 118),
            ("quran_tafsir", "Reflections on Surah Yusuf", "تأملات في سورة يوسف", 3, 2954, 6120, 36),
            ("quran_tafsir", "Ayat al-Kursi explained", "شرح آية الكرسي", 1, 611, 154300, 180),
            ("hadith_studies", "Hadith of the week: mercy", "حديث الأسبوع: الرحمة", 0, 437, 3860, 58),
            ("hadith_studies", "Forty hadith, episode 3", "الأربعون النووية، الحلقة الثالثة", 2, 1622, 2310, 260),
            ("fiqh", "Zakat: who pays and how", "الزكاة: على من تجب وكيف تُخرج", 0, 1478, 11240, 88),
            ("fiqh", "Wudu step by step", "الوضوء خطوة بخطوة", 1, 382, 208700, 520),
            ("islamic_history", "The House of Wisdom in Baghdad", "بيت الحكمة في بغداد", 3, 1863, 15760, 12),
            ("islamic_history", "Salah al-Din and Jerusalem", "صلاح الدين والقدس", 3, 2489, 43190, 130),
            ("islamic_nasheed", "A nasheed for the evening", "نشيد المساء", 0, 231, 7450, 70),
            ("islamic_nasheed", "Mawlay salli wa sallim", "مولاي صلّ وسلّم", 2, 318, 86900, 230),
            ("ramadan", "Laylat al-Qadr reminders", "تذكير بليلة القدر", 0, 865, 25980, 3),
            ("ramadan", "Suhoor ideas for busy days", "أفكار للسحور في الأيام المزدحمة", 2, 529, 4170, 160),
        };

        // Each channel's name as its creator gives it in each language (an Arabic page never shows a Latin name).
        private static readonly (string En, string Ar)[] Channels =
        {
            ("Al Ummah Now", "الأمة الآن"), ("Noor Channel", "قناة النور"), ("Masjid Live", "المسجد مباشر"), ("Seerah Stories", "قصص السيرة"),
        };

        /// <summary>A demo channel's name in <paramref name="language"/>.</summary>
        public static string ChannelName(int index, string language) =>
            language == "ar" ? Channels[index % Channels.Length].Ar : Channels[index % Channels.Length].En;

        private readonly IReadOnlyList<Video> _videos;

        public DemoCatalogClient(string? thumbnailFolder = null, string language = "ar")
        {
            var start = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
            _videos = Fixtures.Select((f, i) => new Video
            {
                Id = "demo-v" + (i + 1),
                Title = new LocalizedText(f.En, new Dictionary<string, string> { ["en"] = f.En, ["ar"] = f.Ar }),
                Description = new LocalizedText(""),
                ChannelName = ChannelName(f.Channel, language),
                ThumbnailUrl = Thumbnail(thumbnailFolder, i, language),
                // Never fetched: the screenshots stay on the home page.
                PlayUrl = "https://demo.invalid/" + (i + 1) + ".m3u8",
                DurationSeconds = f.Seconds,
                Views = f.Views,
                PublishedAt = start.AddHours(-f.HoursAgo),
                Category = f.Category,
                IsPublic = true,
                UploadStatus = "ready",
            }).ToList();
        }

        private static string? Thumbnail(string? folder, int index, string language)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            // A title card per video in the UI language (thumb-N.ar.jpg / thumb-N.jpg, tests/fixtures/viewer-thumbs)
            // when present; else the store workflow's eight drawn backgrounds (thumb-1.png … thumb-8.png), reused.
            var localized = Path.Combine(folder, "thumb-" + (index + 1) + "." + language + ".jpg");
            if (language != "en" && File.Exists(localized)) return new Uri(Path.GetFullPath(localized)).AbsoluteUri;
            var card = Path.Combine(folder, "thumb-" + (index + 1) + ".jpg");
            if (File.Exists(card)) return new Uri(Path.GetFullPath(card)).AbsoluteUri;
            var path = Path.Combine(folder, "thumb-" + (index % 8 + 1) + ".png");
            return File.Exists(path) ? new Uri(Path.GetFullPath(path)).AbsoluteUri : null;
        }

        public Task<VideoPage> ListVideosAsync(VideoQuery query, CancellationToken cancellationToken = default)
        {
            IEnumerable<Video> items = _videos;
            if (query.Category != null) items = items.Where(v => v.Category == query.Category);
            items = query.Sort switch
            {
                VideoSort.Views => items.OrderByDescending(v => v.Views),
                VideoSort.Title => items.OrderBy(v => v.Title.Fallback, StringComparer.Ordinal),
                _ => items.OrderByDescending(v => v.PublishedAt),
            };
            return Task.FromResult(new VideoPage(items.Take(query.Limit).ToList(), null));
        }

        public Task<Video> GetVideoAsync(string id, CancellationToken cancellationToken = default) =>
            _videos.FirstOrDefault(v => v.Id == id) is { } video
                ? Task.FromResult(video)
                : Task.FromException<Video>(new CatalogException("no demo video " + id));

        public Task<IReadOnlyList<SubtitleTrack>> GetSubtitleTracksAsync(string videoId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SubtitleTrack>>(Array.Empty<SubtitleTrack>());

        public Task<string> GetTextAsync(string url, CancellationToken cancellationToken = default) =>
            Task.FromException<string>(new CatalogException("demo catalog has no media"));
    }
}
