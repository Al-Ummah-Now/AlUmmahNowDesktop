using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlUmmahNow.App.I18n
{
    /// <summary>
    /// Holds the active language and is the one display formatter of the apps: every number, count, date,
    /// time, duration, percentage, file size and amount of money shown to people goes through it.
    /// Arabic always shows Eastern Arabic (Hindi) numerals ٠–٩ with the Arabic separators ٫ ٬ ٪, whatever
    /// the operating system's region or digit settings (owner decision 2026-11-15). Text is formatted with
    /// the invariant culture and the digits are then mapped, so the output is the same on every machine.
    /// English keeps Arabic numerals 0–9. Ids, URLs, version numbers, CSV/ISO wire values and text that
    /// people type stay as typed; <see cref="NormalizeDigits"/> turns typed ٠–٩ back into 0–9.
    /// </summary>
    public sealed class Localizer : ObservableObject
    {
        public static readonly IReadOnlyList<string> SupportedLanguages = new[] { "ar", "en" };

        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _bundles = new();
        private string _language;

        public Localizer(string language = "ar")
        {
            foreach (var code in SupportedLanguages) _bundles[code] = Load(code);
            _language = Normalize(language);
        }

        public event EventHandler? LanguageChanged;

        public string Language
        {
            get => _language;
            set
            {
                if (SetProperty(ref _language, Normalize(value)))
                {
                    OnPropertyChanged(nameof(IsRtl));
                    LanguageChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public bool IsRtl => _language == "ar";

        public IReadOnlyDictionary<string, string> Bundle(string language) => _bundles[language];

        public string this[string key] =>
            _bundles[_language].TryGetValue(key, out var value) ? value
            : _bundles["en"].TryGetValue(key, out var fallback) ? fallback
            : key;

        /// <summary>
        /// The message for <paramref name="key"/> with its {0}… placeholders filled. Number arguments are
        /// formatted with <see cref="Number(long)"/> or <see cref="Decimal"/>; text arguments are used as given.
        /// </summary>
        public string Format(string key, params object[] args)
        {
            var shown = new object[args.Length];
            for (var i = 0; i < args.Length; i++) shown[i] = Shown(args[i]);
            return string.Format(CultureInfo.InvariantCulture, this[key], shown);
        }

        private object Shown(object arg) => arg switch
        {
            int v => Number(v),
            long v => Number(v),
            short v => Number(v),
            uint v => Number(v),
            double v => Decimal(v),
            float v => Decimal(v),
            decimal v => Decimal((double)v),
            _ => arg,
        };

        /// <summary>A whole number with grouping: "1,234" / "١٬٢٣٤".</summary>
        public string Number(long value) => Digits(value.ToString("N0", CultureInfo.InvariantCulture));

        /// <summary>A number with at most one decimal ("1.5", "١٫٥" in Arabic).</summary>
        public string Decimal(double value) => Digits(value.ToString("#,0.#", CultureInfo.InvariantCulture));

        /// <summary>
        /// A headline total for a stat tile. Zero is a word in Arabic («لا يوجد»): the Eastern Arabic zero «٠»
        /// is a single dot, which on its own in a tile reads as an empty value.
        /// </summary>
        public string Stat(double value, bool decimals = false) =>
            value == 0 ? this["stat.zero"] : decimals ? Decimal(value) : Number((long)Math.Round(value));

        /// <summary>A share of 1 as a percentage: 0.25 → "25%" / "٢٥٪".</summary>
        public string Percent(double fraction, int decimals = 0)
        {
            var format = decimals <= 0 ? "#,0" : "#,0." + new string('#', decimals);
            return Digits((fraction * 100).ToString(format, CultureInfo.InvariantCulture) + "%");
        }

        /// <summary>
        /// Axis labels and other compact numbers without a noun: 950, 1.5K, 3M. Truncated to one decimal, never
        /// rounded up, so 1,999 is "1.9K" and never "2K". Arabic: exactly 1 is the bare scale word («ألف», «مليون»),
        /// exactly 2 the dual with no digit («ألفان», «مليونان»; a digit is never followed by the dual), a whole 3–10
        /// the plural («٣ آلاف», «٣ ملايين»), and 11+ or any decimal the singular («١١ ألف», «١٫٥ ألف»).
        /// </summary>
        public string Compact(double value)
        {
            var abs = Math.Abs(value);
            if (abs < 1_000) return Decimal(value);
            var sign = value < 0 ? "-" : "";
            var (key, number) = Scaled((long)Math.Floor(abs));
            return sign + Format("num." + key, number);
        }

        /// <summary>
        /// A view count with its noun. Under 1,000 the noun takes the CLDR plural of the full count: «مشاهدة واحدة»,
        /// «مشاهدتان», «٣ مشاهدات», «١١ مشاهدة» / "1 view", "3 views". From 1,000 the count is compact and the noun
        /// is counted by the scale word, so it stays singular: exactly 1 «ألف مشاهدة», exactly 2 the construct form
        /// with no digit «ألفا مشاهدة», a whole 3–10 «٣ آلاف مشاهدة», 11+ or any decimal «١٫٣ ألف مشاهدة» /
        /// "1K views", "1.3K views".
        /// </summary>
        public string Views(long count)
        {
            if (count < 0) count = 0;
            if (count < 1_000) return Format("views." + PluralCategory(count), Number(count));
            var (key, number) = Scaled(count);
            return key.EndsWith("_one", StringComparison.Ordinal) || key.EndsWith("_two", StringComparison.Ordinal)
                ? Format("views." + key, number)
                : Format("views.compact", Format("num." + key, number));
        }

        /// <summary>
        /// A count of 1,000 or more truncated to one decimal: the scale key ("thousand", "million_one",
        /// "billion_two", "thousand_plural") and the number shown.
        /// </summary>
        private (string Key, string Number) Scaled(long n)
        {
            string scale;
            long unit;
            if (n >= 1_000_000_000) { scale = "billion"; unit = 1_000_000_000; }
            else if (n >= 1_000_000) { scale = "million"; unit = 1_000_000; }
            else { scale = "thousand"; unit = 1_000; }
            var tenths = n / (unit / 10);
            var shown = tenths % 10 == 0 ? tenths / 10 : -1;
            var form = shown == 1 ? "_one" : shown == 2 ? "_two" : shown >= 3 && shown <= 10 ? "_plural" : "";
            return (scale + form, Decimal(tenths / 10.0));
        }

        /// <summary>The CLDR plural category of a whole number in the active language.</summary>
        private string PluralCategory(long n)
        {
            if (_language != "ar") return n == 1 ? "one" : "other";
            if (n == 0) return "zero";
            if (n == 1) return "one";
            if (n == 2) return "two";
            var mod = n % 100;
            if (mod >= 3 && mod <= 10) return "few";
            if (mod >= 11 && mod <= 99) return "many";
            return "other";
        }

        /// <summary>A day for chart labels: "11 Nov" / "١١ نوفمبر".</summary>
        public string ShortDate(DateTime date)
        {
            var month = date.ToString("MMM", _language == "ar" ? Arabic : CultureInfo.InvariantCulture);
            return Digits(date.Day.ToString(CultureInfo.InvariantCulture)) + " " + month;
        }

        /// <summary>A calendar day in local time: "2026-11-15" / "٢٠٢٦-١١-١٥".</summary>
        public string Date(DateTimeOffset value) =>
            Isolate(Digits(value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

        /// <summary>A day and time in local time: "2026-11-15 14:30" / "٢٠٢٦-١١-١٥ ١٤:٣٠".</summary>
        public string DateTime(DateTimeOffset value) =>
            Isolate(Digits(value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));

        private static readonly CultureInfo Arabic = ArabicMonths();

        /// <summary>Arabic with Gregorian month names (the default "ar" culture uses the Hijri calendar).</summary>
        private static CultureInfo ArabicMonths()
        {
            var culture = (CultureInfo)CultureInfo.GetCultureInfo("ar-EG").Clone();
            culture.DateTimeFormat.Calendar = new GregorianCalendar();
            return culture;
        }

        /// <summary>Formats seconds as m:ss or h:mm:ss, isolated so it stays intact in RTL text.</summary>
        public string Duration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
            var t = TimeSpan.FromSeconds(Math.Floor(seconds));
            var text = t.TotalHours >= 1
                ? ((int)t.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + t.ToString("mm\\:ss", CultureInfo.InvariantCulture)
                : ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":" + t.ToString("ss", CultureInfo.InvariantCulture);
            return Isolate(Digits(text));
        }

        /// <summary>A timecode (player position, frame time), same shape as <see cref="Duration"/>.</summary>
        public string Duration(TimeSpan value) => Duration(value.TotalSeconds);

        /// <summary>A playback speed: "1.5x" / "١٫٥x".</summary>
        public string Speed(double rate) => Digits(rate.ToString("0.##", CultureInfo.InvariantCulture)) + "x";

        /// <summary>A rendition label: "1080p" / "١٠٨٠p", or the bitrate when the height is unknown.</summary>
        public string Quality(int height, long bandwidth) => height > 0
            ? Digits(height.ToString(CultureInfo.InvariantCulture)) + "p"
            : Format("unit.kbps", bandwidth / 1000);

        /// <summary>
        /// A file size, as the website's <c>formatBytes</c> (shared-types locale-format): 1024 steps, the
        /// Intl short unit ("1.5 MB" / «١٫٥ م.ب»), at most one decimal and none on whole bytes. With
        /// <paramref name="decimals"/>, exactly that many on every unit, as the web upload page's file rows
        /// ("1.43 MB" / «١٫٤٣ م.ب»). The same text on every platform, never the OS's own size format.
        /// </summary>
        public string Bytes(long bytes, int? decimals = null)
        {
            string[] units = { "unit.b", "unit.kb", "unit.mb", "unit.gb", "unit.tb" };
            double v = Math.Max(0, bytes);
            var u = 0;
            while (v >= 1024 && u < units.Length - 1)
            {
                v /= 1024;
                u++;
            }

            var format = decimals is { } d && d > 0 ? "#,0." + new string('0', d) : decimals != null || u == 0 ? "#,0" : "#,0.#";
            return Format(units[u], Digits(v.ToString(format, CultureInfo.InvariantCulture)));
        }

        /// <summary>
        /// An amount of money. Arabic puts the Arabic currency name after the amount ("١٢٫٦٩ دولار أمريكي"),
        /// never «US$»; English uses the symbol ("$12.69") or the code for other currencies ("EUR 12.69").
        /// </summary>
        public string Money(decimal amount, string currency)
        {
            var code = (currency ?? "").ToUpperInvariant();
            var number = Math.Abs(amount).ToString("#,0.00", CultureInfo.InvariantCulture);
            var sign = amount < 0 ? "-" : "";
            if (_language == "ar")
            {
                var name = this["currency." + code];
                return sign + Digits(number) + " " + (name == "currency." + code ? code : name);
            }

            return code == "USD" ? sign + "$" + number : sign + code + " " + number;
        }

        /// <summary>
        /// Joins values on one line. Arabic uses " • " because the middle dot «·» reads as the Eastern
        /// Arabic zero «٠»; English keeps " · ". Empty parts are skipped. Each part is a direction isolate,
        /// so an Arabic name in an English line (or the reverse) keeps its own order and cannot pull a
        /// neighbouring number into its run ("1.5 · الأمة الآنK views").
        /// </summary>
        public string Separator => _language == "ar" ? " \u2022 " : " \u00B7 ";

        public string Join(IEnumerable<string?> parts) =>
            string.Join(Separator, parts.Where(p => !string.IsNullOrEmpty(p)).Select(p => Isolate(p!)));

        public string Join(params string?[] parts) => Join((IEnumerable<string?>)parts);

        /// <summary>Wraps text in Unicode first-strong isolates so embedded runs cannot reorder neighbours.</summary>
        public static string Isolate(string text) => "\u2068" + text + "\u2069";

        /// <summary>
        /// Prefilled input text ("2026-11-17 09:00") in the UI's digits: ٠–٩ in Arabic. No isolates, so the
        /// field's text reads back through <see cref="NormalizeDigits"/>.
        /// </summary>
        public string InputDigits(string text) => Digits(text);

        /// <summary>Typed Eastern Arabic ٠–٩ and Persian ۰–۹ digits become 0–9 (for parsing input).</summary>
        public static string NormalizeDigits(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var sb = new StringBuilder(text!.Length);
            foreach (var c in text)
            {
                if (c >= '\u0660' && c <= '\u0669') sb.Append((char)('0' + (c - '\u0660')));
                else if (c >= '\u06F0' && c <= '\u06F9') sb.Append((char)('0' + (c - '\u06F0')));
                else if (c == '\u066B') sb.Append('.');
                else if (c == '\u066C') sb.Append(',');
                else sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>Maps invariant-formatted number text to the language's digits and separators.</summary>
        private string Digits(string text)
        {
            if (_language != "ar") return text;
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                sb.Append(c switch
                {
                    >= '0' and <= '9' => (char)('\u0660' + (c - '0')),
                    ',' => '\u066C',
                    '.' => '\u066B',
                    '%' => '\u066A',
                    _ => c,
                });
            }

            return sb.ToString();
        }

        private static string Normalize(string language) =>
            language != null && language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "ar";

        private static IReadOnlyDictionary<string, string> Load(string code)
        {
            using var stream = typeof(Localizer).Assembly.GetManifestResourceStream("i18n." + code + ".json")
                ?? throw new InvalidOperationException("Missing bundle " + code);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            var map = new Dictionary<string, string>();
            foreach (var entry in doc.RootElement.EnumerateObject()) map[entry.Name] = entry.Value.GetString() ?? "";
            return map;
        }
    }
}
