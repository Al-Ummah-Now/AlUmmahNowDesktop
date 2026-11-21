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
        private static readonly (string Category, string En, string Ar)[] Fixtures =
        {
            ("quran_recitation", "Surah Al-Mulk, full recitation", "سورة الملك كاملة"),
            ("quran_recitation", "Surah Ar-Rahman", "سورة الرحمن"),
            ("quran_tafsir", "Reflections on Surah Al-Kahf, part 1", "تأملات في سورة الكهف، الجزء الأول"),
            ("quran_tafsir", "The meaning of Al-Fatiha", "معاني سورة الفاتحة"),
            ("hadith_studies", "Hadith of the week: intentions", "حديث الأسبوع: النيات"),
            ("hadith_studies", "Forty hadith, episode 2", "الأربعون النووية، الحلقة الثانية"),
            ("fiqh", "Q&A: prayer while travelling", "سؤال وجواب: الصلاة في السفر"),
            ("fiqh", "Fasting: common questions", "الصيام: أسئلة شائعة"),
            ("islamic_history", "Seerah: the early years in Makkah", "السيرة: السنوات الأولى في مكة"),
            ("islamic_history", "Al-Andalus: a short history", "الأندلس: تاريخ موجز"),
            ("islamic_nasheed", "Tala'a al-Badru 'Alayna", "طلع البدر علينا"),
            ("islamic_nasheed", "A nasheed for the morning", "نشيد الصباح"),
            ("ramadan", "Preparing for Ramadan", "الاستعداد لرمضان"),
            ("ramadan", "Iftar around the world", "الإفطار حول العالم"),
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
                ChannelName = ChannelName(i, language),
                ThumbnailUrl = Thumbnail(thumbnailFolder, i, language),
                // Never fetched: the screenshots stay on the home page.
                PlayUrl = "https://demo.invalid/" + (i + 1) + ".m3u8",
                DurationSeconds = 240 + i * 97,
                Views = 1200 + (Fixtures.Length - i) * 830,
                PublishedAt = start.AddHours(-11 * i),
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
