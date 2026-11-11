using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlUmmahNow.App.I18n
{
    /// <summary>Holds the active language and formats text, numbers and durations for it.</summary>
    public sealed class Localizer : ObservableObject
    {
        public static readonly IReadOnlyList<string> SupportedLanguages = new[] { "ar", "en" };

        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _bundles = new();
        private string _language;
        private bool _easternNumerals;

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

        public bool EasternNumerals
        {
            get => _easternNumerals;
            set
            {
                if (SetProperty(ref _easternNumerals, value)) LanguageChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public IReadOnlyDictionary<string, string> Bundle(string language) => _bundles[language];

        public string this[string key] =>
            _bundles[_language].TryGetValue(key, out var value) ? value
            : _bundles["en"].TryGetValue(key, out var fallback) ? fallback
            : key;

        public string Format(string key, params object[] args) =>
            string.Format(CultureInfo.InvariantCulture, this[key], args);

        public string Number(long value) => Digits(value.ToString("N0", CultureInfo.InvariantCulture));

        /// <summary>A number with at most one decimal ("1.5", "١٫٥" with Eastern numerals).</summary>
        public string Decimal(double value)
        {
            var text = value.ToString("#,0.#", CultureInfo.InvariantCulture);
            if (_easternNumerals && _language == "ar") text = text.Replace('.', '٫');
            return Digits(text);
        }

        /// <summary>Axis labels: 950, 1.2K, 3M ("١٫٢ ألف" in Arabic).</summary>
        public string Compact(double value)
        {
            var abs = Math.Abs(value);
            if (abs >= 1_000_000) return Format("num.million", Decimal(Math.Round(value / 1_000_000, 1)));
            if (abs >= 1_000) return Format("num.thousand", Decimal(Math.Round(value / 1_000, 1)));
            return Decimal(value);
        }

        /// <summary>A day for chart labels: "11 Nov" / "11 نوفمبر".</summary>
        public string ShortDate(DateTime date)
        {
            var month = date.ToString("MMM", _language == "ar" ? Arabic : CultureInfo.InvariantCulture);
            return Digits(date.Day.ToString(CultureInfo.InvariantCulture)) + " " + month;
        }

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

        /// <summary>Wraps text in Unicode first-strong isolates so embedded runs cannot reorder neighbours.</summary>
        public static string Isolate(string text) => "⁨" + text + "⁩";

        private string Digits(string text)
        {
            if (!_easternNumerals || _language != "ar") return text;
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                sb.Append(c >= '0' && c <= '9' ? (char)('٠' + (c - '0')) : c == ',' ? '٬' : c);
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
