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

        /// <summary>Axis labels: 950, 1.2K, 3M ("١٫٢ ألف" in Arabic).</summary>
        public string Compact(double value)
        {
            var abs = Math.Abs(value);
            if (abs >= 1_000_000) return Format("num.million", Decimal(Math.Round(value / 1_000_000, 1)));
            if (abs >= 1_000) return Format("num.thousand", Decimal(Math.Round(value / 1_000, 1)));
            return Decimal(value);
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

        /// <summary>A file size: "12.3 MB" / "١٢٫٣ م.ب".</summary>
        public string Bytes(long bytes)
        {
            string[] units = { "unit.b", "unit.kb", "unit.mb", "unit.gb", "unit.tb" };
            double v = Math.Max(0, bytes);
            var u = 0;
            while (v >= 1024 && u < units.Length - 1)
            {
                v /= 1024;
                u++;
            }

            return Format(units[u], Digits(v.ToString(u == 0 ? "0" : "0.0", CultureInfo.InvariantCulture)));
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
        /// Arabic zero «٠»; English keeps " · ". Empty parts are skipped.
        /// </summary>
        public string Separator => _language == "ar" ? " \u2022 " : " \u00B7 ";

        public string Join(IEnumerable<string?> parts) => string.Join(Separator, parts.Where(p => !string.IsNullOrEmpty(p)));

        public string Join(params string?[] parts) => Join((IEnumerable<string?>)parts);

        /// <summary>Wraps text in Unicode first-strong isolates so embedded runs cannot reorder neighbours.</summary>
        public static string Isolate(string text) => "\u2068" + text + "\u2069";

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
