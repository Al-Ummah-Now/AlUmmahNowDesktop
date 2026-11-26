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
    /// Arabic always shows Eastern Arabic (Hindi) numerals ٠–٩ with «٫» and «٪» and no grouping, whatever
    /// the operating system's region or digit settings (owner decisions 2026-11-15 and 2026-11-21). Text is formatted with
    /// the invariant culture and the digits are then mapped, so the output is the same on every machine.
    /// English keeps Arabic numerals 0–9. Ids, URLs, version numbers, CSV/ISO wire values and text that
    /// people type stay as typed; <see cref="NormalizeDigits"/> turns typed ٠–٩ back into 0–9.
    /// </summary>
    public sealed class Localizer : ObservableObject
    {
        public static readonly IReadOnlyList<string> SupportedLanguages = new[] { "ar", "en" };

        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _bundles = new();
        private readonly TimeZoneInfo? _zone;
        private string _language;

        /// <param name="zone">The time zone of days and times; the device's when null.</param>
        public Localizer(string language = "ar", TimeZoneInfo? zone = null)
        {
            _zone = zone;
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

        /// <summary>A whole number: grouped in English ("1,234"), never grouped in Arabic ("١٢٣٤").</summary>
        public string Number(long value) => Figure(value.ToString("N0", CultureInfo.InvariantCulture));

        /// <summary>A number with at most one decimal ("1.5", "١٫٥" in Arabic).</summary>
        public string Decimal(double value) => Figure(value.ToString("#,0.#", CultureInfo.InvariantCulture));

        /// <summary>
        /// A headline total for a stat tile. Zero is a word in Arabic («لا يوجد»): the Eastern Arabic zero «٠»
        /// is a single dot, which on its own in a tile reads as an empty value. A count of 1,000 or more is compact,
        /// like every large count ("4.8K", «٤٫٨ ألف»; owner 2026-11-21); a measure with decimals (watch hours) stays exact.
        /// </summary>
        public string Stat(double value, bool decimals = false) =>
            value == 0 ? this["stat.zero"]
            : decimals ? Decimal(value)
            : Math.Abs(value) >= 1_000 ? Compact(Math.Round(value))
            : Number((long)Math.Round(value));

        /// <summary>A share of 1 as a percentage: 0.25 → "25%" / "٢٥٪".</summary>
        public string Percent(double fraction, int decimals = 0)
        {
            var format = decimals <= 0 ? "#,0" : "#,0." + new string('#', decimals);
            return Figure((fraction * 100).ToString(format, CultureInfo.InvariantCulture) + "%");
        }

        /// <summary>
        /// Axis labels and other compact numbers without a noun: 950, 1.5K, 3M. Truncated to one decimal, never
        /// rounded up, so 1,999 is "1.9K" and never "2K". Arabic thousands and billions: exactly 1 is the bare scale
        /// word («ألف», «مليار»), exactly 2 the dual with no digit («ألفان», «ملياران»), a whole 3–10 the plural
        /// («٣ آلاف»), a whole number ending in 11–99 the accusative («١٢ ألفًا», «١٢٠ ألفًا»), and anything else
        /// (100, any decimal) the singular («١٠٠ ألف», «١٫٥ ألف»). Millions are always «{n} م»
        /// («١ م», «١٫٢ م»; owner 2026-11-21).
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
        /// with no digit «ألفا مشاهدة», a whole 3–10 «٣ آلاف مشاهدة», anything else the singular before the noun
        /// («١٢ ألف مشاهدة», «١٫٣ ألف مشاهدة»); millions
        /// «١٫٢ م مشاهدة» / "1K views", "1.3K views", "1.2M views".
        /// </summary>
        public string Views(long count)
        {
            if (count < 0) count = 0;
            if (count < 1_000) return Format("views." + PluralCategory(count), Number(count));
            var (key, number) = Scaled(count);
            if (key.EndsWith("_one", StringComparison.Ordinal) || key.EndsWith("_two", StringComparison.Ordinal))
                return Format("views." + key, number);
            // Before a noun a whole 11–99 is the singular («١٢ ألف مشاهدة»), not the standalone accusative.
            return Format("views.compact", Format("num." + key.Replace("_tamyiz", ""), number));
        }

        /// <summary>
        /// A count of 1,000 or more truncated to one decimal: the scale key ("thousand", "million_one",
        /// "billion_two", "thousand_plural", "thousand_tamyiz") and the number shown.
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
            var form = shown == 1 ? "_one"
                : shown == 2 ? "_two"
                : shown >= 3 && shown <= 10 ? "_plural"
                : shown > 0 && shown % 100 >= 11 && shown % 100 <= 99 && scale != "million" ? "_tamyiz"
                : "";
            return (scale + form, Decimal(tenths / 10.0));
        }

        /// <summary>The message for <paramref name="key"/> in the form the count takes (<c>.one</c>, <c>.few</c>, …).</summary>
        public string Plural(string key, long count) => Format(key + "." + PluralCategory(count), count);

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

        /// <summary>A day for chart labels, as on the web: "Nov 11" / «١١ نوفمبر».</summary>
        public string ShortDate(DateTime date)
        {
            var day = Digits(date.Day.ToString(CultureInfo.InvariantCulture));
            return _language == "ar" ? day + " " + date.ToString("MMM", Arabic) : date.ToString("MMM", CultureInfo.InvariantCulture) + " " + day;
        }

        /// <summary>
        /// A calendar day in local time: "Nov 5, 2026" / «٠٥‏/١١‏/٢٠٢٦» (right-to-left marks before each slash). The web's and
        /// Android's medium date, written out here so it is the same on every machine.
        /// </summary>
        public string Date(DateTimeOffset value) => Isolate(Day(Local(value)));

        /// <summary>A day and time in local time: "Nov 5, 2026, 2:30 PM" / «٠٥‏/١١‏/٢٠٢٦، ٢:٣٠ م» (medium date, short time).</summary>
        public string DateTime(DateTimeOffset value)
        {
            var at = Local(value);
            return Isolate(Day(at) + (_language == "ar" ? "، " : ", ") + ShortTime(at.Hour, at.Minute));
        }

        private string ShortTime(int hour, int minute)
        {
            var time = Digits(((hour + 11) % 12 + 1).ToString(CultureInfo.InvariantCulture) + ":" + minute.ToString("00", CultureInfo.InvariantCulture));
            return time + " " + DayHalf(hour >= 12);
        }

        /// <summary>"AM", "PM" / «ص», «م».</summary>
        public string DayHalf(bool afternoon) => _language == "ar" ? (afternoon ? "م" : "ص") : (afternoon ? "PM" : "AM");

        /// <summary>The minutes of a time: "05" / «٠٥».</summary>
        public string Minute(int minute) => Digits(minute.ToString("00", CultureInfo.InvariantCulture));

        /// <summary>A month of a calendar: "November 2026" / «نوفمبر ٢٠٢٦».</summary>
        public string MonthYear(DateTime month) =>
            month.ToString("MMMM", _language == "ar" ? Arabic : CultureInfo.InvariantCulture) + " " + Digits(month.Year.ToString(CultureInfo.InvariantCulture));

        /// <summary>A day of a calendar: "7" / «٧».</summary>
        public string DayOfMonth(DateTime day) => Digits(day.Day.ToString(CultureInfo.InvariantCulture));

        /// <summary>A day for screen readers: "Saturday, November 7, 2026" / «السبت، ٧ نوفمبر ٢٠٢٦».</summary>
        public string LongDay(DateTime day) => _language == "ar"
            ? Weekday(day.DayOfWeek) + "، " + DayOfMonth(day) + " " + MonthYear(day)
            : Weekday(day.DayOfWeek) + ", " + day.ToString("MMMM", CultureInfo.InvariantCulture) + " " + DayOfMonth(day) + ", " + Digits(day.Year.ToString(CultureInfo.InvariantCulture));

        public string Weekday(DayOfWeek day) => _language == "ar"
            ? new[] { "الأحد", "الاثنين", "الثلاثاء", "الأربعاء", "الخميس", "الجمعة", "السبت" }[(int)day]
            : day.ToString();

        /// <summary>The head of a calendar's column: "Sat" / «س».</summary>
        public string WeekdayLetter(DayOfWeek day) => _language == "ar" ? "حنثرخجس".Substring((int)day, 1) : day.ToString().Substring(0, 3);

        private DateTimeOffset Local(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, _zone ?? TimeZoneInfo.Local);

        private string Day(DateTimeOffset at) =>
            Digits(at.ToString(_language == "ar" ? "dd'\u200F/'MM'\u200F/'yyyy" : "MMM d, yyyy", CultureInfo.InvariantCulture));

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

        /// <summary>Capped notification badge count: empty when zero, "1".."9" / "١".."٩", or "9+" / "٩+".</summary>
        public string BadgeCap9(int count) =>
            count <= 0 ? ""
            : count > 9 ? Digits("9") + "+"
            : Digits(count.ToString(CultureInfo.InvariantCulture));

        /// <summary>Minutes from midnight (0..1439) as the short time of the dates: "10:00 PM" / «١٠:٠٠ م», isolated.</summary>
        public string MinuteOfDay(int minute)
        {
            var clamped = ((minute % 1440) + 1440) % 1440;
            return Isolate(ShortTime(clamped / 60, clamped % 60));
        }

        /// <summary>A playback speed, as in the web player's menu: "1.5×" / «١٫٥×»; normal speed is the word.</summary>
        public string Speed(double rate) => Math.Abs(rate - 1) < 0.001
            ? this["player.speed.normal"]
            : Digits(rate.ToString("0.##", CultureInfo.InvariantCulture)) + "×";

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
            return Format(units[u], Figure(v.ToString(format, CultureInfo.InvariantCulture)));
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
                return sign + Figure(number) + " " + (name == "currency." + code ? code : name);
            }

            return code == "USD" ? sign + "$" + number : sign + code + " " + number;
        }

        /// <summary>
        /// Joins values on one line. Arabic uses the Arabic comma ("، "): the Eastern Arabic zero «٠» is a dot,
        /// so any dot separator («·» or «•») beside a number reads as one more zero («٤:٠٠ •» as «٤:٠٠٠»).
        /// English keeps " · ". Empty parts are skipped. Each part is a direction isolate,
        /// so an Arabic name in an English line (or the reverse) keeps its own order and cannot pull a
        /// neighbouring number into its run ("1.5 · الأمة الآنK views").
        /// </summary>
        public string Separator => _language == "ar" ? "\u060C " : " \u00B7 ";

        public string Join(IEnumerable<string?> parts) =>
            string.Join(Separator, parts.Where(p => !string.IsNullOrEmpty(p)).Select(p => Isolate(p!)));

        public string Join(params string?[] parts) => Join((IEnumerable<string?>)parts);

        /// <summary>The same for text that may take more than one line: a line never begins with the separator.</summary>
        public string JoinWrapped(params string?[] parts) =>
            string.Join(WrappedSeparator, parts.Where(p => !string.IsNullOrEmpty(p)).Select(p => Isolate(p!)));

        private string WrappedSeparator => IsRtl ? Separator : "\u00A0\u00B7 ";

        /// <summary>Wraps text in Unicode first-strong isolates so embedded runs cannot reorder neighbours.</summary>
        public static string Isolate(string text) => "\u2068" + text + "\u2069";

        /// <summary>Keeps left-to-right text such as Windows paths together in either language.</summary>
        public static string IsolateLtr(string text) => "\u2066" + text + "\u2069";

        /// <summary>Joins a filename and wrapped row metadata in the current UI direction.</summary>
        public string JoinFileName(string fileName, params string?[] details)
        {
            if (fileName.Length == 0) return JoinWrapped(details);

            // An LTR embedding keeps the extension on the right even in Windows text renderers
            // that ignore isolates. The LTR isolate keeps the field independent of its neighbours.
            var extensionStart = fileName.LastIndexOf('.');
            int suffixStart = extensionStart > 0 ? extensionStart : fileName.Length;
            int digitsStart = suffixStart;
            while (digitsStart > 0 && fileName[digitsStart - 1] >= '0' && fileName[digitsStart - 1] <= '9')
                digitsStart--;

            // A strong LTR mark before a numeric suffix prevents the preceding Arabic letters
            // from turning its digits into an RTL number run. A second mark anchors the dot.
            var displayName = fileName.Insert(suffixStart, "\u200E");
            if (digitsStart < suffixStart)
                displayName = displayName.Insert(digitsStart, "\u200E");

            var name = IsolateLtr("\u202A" + displayName + "\u202C");
            var metadata = JoinWrapped(details);
            // GTK can infer the paragraph direction from its text.
            var direction = IsRtl ? "\u200F" : "\u200E";
            return direction + name + (metadata.Length == 0 ? "" : WrappedSeparator + metadata);
        }

        /// <summary>
        /// A version for display, isolated: "1.4.2-preview.3" / «١.٤.٢-preview.٣». It is no decimal number, so its dots
        /// stay dots (wiki AUN-APP-UPDATES §12). With letters it is embedded left to right as well: Windows' text does not
        /// know isolates, and put the parts in another order.
        /// </summary>
        public string Version(string version)
        {
            var shown = Digits(version).Replace('\u066B', '.');
            return Isolate(version.Contains('-') ? "\u202A" + shown + "\u202C" : shown);
        }

        /// <summary>Typed Eastern Arabic ٠–٩ and Persian ۰–۹ digits become 0–9 (for parsing input).</summary>
        public static string NormalizeDigits(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var sb = new StringBuilder(text!.Length);
            foreach (var c in text)
            {
                // Marks of direction are of the field, not of what was typed
                if (c == '\u200E' || c == '\u200F' || (c >= '\u202A' && c <= '\u202E') || (c >= '\u2066' && c <= '\u2069')) continue;
                if (c >= '\u0660' && c <= '\u0669') sb.Append((char)('0' + (c - '\u0660')));
                else if (c >= '\u06F0' && c <= '\u06F9') sb.Append((char)('0' + (c - '\u06F0')));
                else if (c == '\u066B') sb.Append('.');
                else if (c == '\u066C') sb.Append(',');
                else sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>
        /// A formatted number in the language's digits. Arabic numbers are never grouped (owner decision 2026-11-21, which
        /// replaces 2026-11-15's «٬»): in most Arabic UI fonts «٬» and the decimal mark «٫» look alike, so «٫» is the only
        /// comma-like mark («٤٨٨٧», «١٢٣٤٫٥»). English keeps "4,887".
        /// </summary>
        private string Figure(string invariant) => Digits(_language == "ar" ? invariant.Replace(",", "") : invariant);

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
