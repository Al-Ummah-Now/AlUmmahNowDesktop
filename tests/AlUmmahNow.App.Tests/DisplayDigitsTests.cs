using System.Globalization;
using System.Text.RegularExpressions;
using AlUmmahNow.App.I18n;
using Xunit;

namespace AlUmmahNow.App.Tests;

/// <summary>
/// Owner decision 2026-11-15: Arabic shows Eastern Arabic (Hindi) numerals ٠–٩ everywhere, whatever the
/// machine's region settings; English keeps Arabic numerals 0–9.
/// </summary>
public class DisplayDigitsTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 15, 14, 30, 0, TimeSpan.Zero);

    private static IEnumerable<string> All(Localizer l) => new[]
    {
        l.Number(1_234_567), l.Number(-42), l.Decimal(12.5), l.Percent(0.25), l.Percent(0.125, 1), l.Compact(1200),
        l.Compact(3_400_000), l.ShortDate(new DateTime(2026, 11, 11)), l.Date(Day), l.DateTime(Day), l.Duration(3725),
        l.Duration(TimeSpan.FromMilliseconds(15_000)), l.Speed(1.5), l.Quality(1080, 0), l.Quality(0, 800_000),
        l.Bytes(12_900_000), l.Bytes(512), l.Money(12.69m, "USD"), l.Money(1500m, "EUR"),
        l.Views(1234), l.Format("studio.analytics.days", 28), l.Format("studio.upload.progress", l.Bytes(1024), l.Bytes(2048)),
        l.Format("studio.videos.deleteBody", 30), l.Format("admin.stepup.body", 15), l.Format("passkey.error.limit", 10),
    };

    [Theory]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    [InlineData("ar-MA")]
    [InlineData("fr-FR")]
    public void Arabic_has_no_ascii_digits_whatever_the_machine_culture(string machine)
    {
        var saved = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(machine);
            foreach (var text in All(new Localizer("ar")))
            {
                Assert.DoesNotMatch("[0-9]", text);
                Assert.Matches("[٠-٩]", text);
                Assert.DoesNotContain("US$", text);
                Assert.DoesNotContain("$", text);
            }
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = saved;
        }
    }

    [Fact]
    public void Arabic_uses_arabic_separators_and_currency_names()
    {
        var ar = new Localizer("ar");
        Assert.Equal("١٢٣٤٥٦٧", ar.Number(1_234_567));
        Assert.Equal("١٢٫٥", ar.Decimal(12.5));
        Assert.Equal("٢٥٪", ar.Percent(0.25));
        Assert.Equal("١٢٫٦٩ دولار أمريكي", ar.Money(12.69m, "USD"));
        Assert.Equal("١٢٫٣ م.ب", ar.Bytes(12_900_000));
        Assert.Equal("⁨٢٠٢٦-١١-١٥⁩", ar.Date(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 15)))));
        Assert.Equal("⁨١:٠٢:٠٥⁩", ar.Duration(3725));
        Assert.Equal("١٫٥x", ar.Speed(1.5));
        Assert.Equal("١٫٢ ألف مشاهدة", ar.Views(1234));
    }

    [Fact]
    public void English_is_unchanged()
    {
        var en = new Localizer("en");
        Assert.Equal("1,234,567", en.Number(1_234_567));
        Assert.Equal("12.5", en.Decimal(12.5));
        Assert.Equal("25%", en.Percent(0.25));
        Assert.Equal("$12.69", en.Money(12.69m, "USD"));
        Assert.Equal("EUR 1,500.00", en.Money(1500m, "EUR"));
        Assert.Equal("12.3 MB", en.Bytes(12_900_000));
        Assert.Equal("512 byte", en.Bytes(512));
        Assert.Equal("⁨1:02:05⁩", en.Duration(3725));
        Assert.Equal("1.5x", en.Speed(1.5));
        Assert.Equal("1080p", en.Quality(1080, 0));
        Assert.Equal("800 kbps", en.Quality(0, 800_000));
        Assert.Equal("1.2K views", en.Views(1234));
        Assert.Equal("Last 28 days", en.Format("studio.analytics.days", 28));
        foreach (var text in All(en)) Assert.DoesNotMatch("[٠-٩۰-۹]", text);
    }

    [Fact]
    public void Text_arguments_are_left_as_given()
    {
        var ar = new Localizer("ar");
        Assert.Equal("عنوان clip-2026.mp4", ar.Format("studio.upload.titleFor", "clip-2026.mp4"));
    }

    [Fact]
    public void Arabic_joins_with_a_bullet_because_the_middle_dot_reads_as_zero()
    {
        Assert.Equal("\u2068عام\u2069، \u2068١٫٢ ألف مشاهدة\u2069", new Localizer("ar").Join("عام", null, "", new Localizer("ar").Views(1200)));
        Assert.Equal("\u2068Public\u2069 · \u20681.2K views\u2069", new Localizer("en").Join("Public", null, "", new Localizer("en").Views(1200)));
    }

    [Fact]
    public void MixedDirectionPartsStayIsolated()
    {
        // An Arabic channel name in an English line keeps its order and does not pull the number after it into its run.
        Assert.Equal("\u2068الأمة الآن\u2069 · \u20681.5K views\u2069", new Localizer("en").Join("الأمة الآن", "1.5K views"));
        // And an English name in an Arabic line.
        Assert.Equal("\u2068Al Ummah Now\u2069، \u2068١٫٥ ألف مشاهدة\u2069", new Localizer("ar").Join("Al Ummah Now", "١٫٥ ألف مشاهدة"));
    }

    [Fact]
    public void Zero_stats_read_as_a_word_in_arabic()
    {
        Assert.Equal("لا يوجد", new Localizer("ar").Stat(0));
        Assert.Equal("٣١٣٫٥", new Localizer("ar").Stat(313.5, decimals: true));
        Assert.Equal("0", new Localizer("en").Stat(0));
        // Counts from 1,000 are compact (owner 2026-11-21); Arabic numbers are never grouped.
        Assert.Equal("15K", new Localizer("en").Stat(15046));
        Assert.Equal("4.8K", new Localizer("en").Stat(4887));
        Assert.Equal("٤٫٨ ألف", new Localizer("ar").Stat(4887));
        Assert.Equal("٨٨٧", new Localizer("ar").Stat(887));
        Assert.Equal("١٫٢ م", new Localizer("ar").Stat(1_234_567));
        Assert.Equal("١٢٣٤٫٥", new Localizer("ar").Decimal(1234.5));
        Assert.Equal("4,887", new Localizer("en").Number(4887));
        Assert.Equal("٤٨٨٧", new Localizer("ar").Number(4887));
    }

    [Fact]
    public void Title_pattern_examples_use_the_ui_digits()
    {
        Assert.Equal("استخدم {filename} و{n} (١، ٢، ٣…) و{date} لتكوين كل عنوان.", new Localizer("ar").Format("studio.upload.titlePatternHint", 1, 2, 3));
        Assert.Equal("Use {filename}, {n} (1, 2, 3…) and {date} to build each title.", new Localizer("en").Format("studio.upload.titlePatternHint", 1, 2, 3));
    }

    [Fact]
    public void Arabic_strings_have_no_ascii_digits_or_middle_dots()
    {
        foreach (var (key, value) in new Localizer().Bundle("ar"))
        {
            var text = Regex.Replace(value, @"\{\d+\}", "");
            Assert.False(Regex.IsMatch(text, "[0-9]"), key + ": " + value);
            Assert.False(text.Contains('·'), key + ": " + value);
        }
    }

    [Theory]
    [InlineData("٢٠٢٦-١١-١٥ ١٤:٣٠", "2026-11-15 14:30")]
    [InlineData("۱۲۳", "123")]
    [InlineData("١٢٫٥", "12.5")]
    [InlineData("abc 42", "abc 42")]
    [InlineData("", "")]
    public void Typed_digits_are_normalised(string typed, string expected) =>
        Assert.Equal(expected, Localizer.NormalizeDigits(typed));

    [Theory]
    [InlineData("ar", "٢٠٢٦-١١-١٧ ٠٩:٠٠")]
    [InlineData("en", "2026-11-17 09:00")]
    public void Prefilled_input_uses_the_ui_digits_and_reads_back(string language, string shown)
    {
        var l = new Localizer(language);
        Assert.Equal(shown, l.InputDigits("2026-11-17 09:00"));
        Assert.Equal("2026-11-17 09:00", Localizer.NormalizeDigits(l.InputDigits("2026-11-17 09:00")));
    }

    /// <summary>Allowed outside the Localizer: wire, file, log and media-pipeline formats, never shown to people.</summary>
    private static readonly Dictionary<string, string> NotDisplay = new()
    {
        ["src/AlUmmahNow.App/I18n/Localizer.cs"] = "the shared display formatter",
        ["src/AlUmmahNow.CoreBridge/Logging/Log.cs"] = "log timestamps",
        ["src/AlUmmahNow.CoreBridge/Studio/StudioApiClient.cs"] = "API query values and ISO dates",
        ["src/AlUmmahNow.CoreBridge/Studio/StudioHttpClient.cs"] = "log message",
        ["src/AlUmmahNow.CoreBridge/Studio/StudioServices.cs"] = "ISO dates on the wire",
        ["src/AlUmmahNow.CoreBridge/ShortLinks.cs"] = "short-link URL query values on the wire",
        ["src/AlUmmahNow.CoreBridge/Temporary/HttpCatalogClient.cs"] = "API query values",
        ["src/AlUmmahNow.CoreBridge/Temporary/HttpNotificationsAndEventsClient.cs"] = "API query values",
        ["src/AlUmmahNow.Linux/MprisSession.cs"] = "D-Bus introspection XML",
        ["src/AlUmmahNow.Linux/Program.cs"] = "automation log line",
        ["src/AlUmmahNow.Windows/Program.cs"] = "automation log line",
        ["src/AlUmmahNow.Studio/Demo/DemoBackend.cs"] = "demo API JSON",
        ["src/AlUmmahNow.App/Theme/fanous/native/FanousThemes.cs"] = "generated by the shared native theme registry (colour hex, not display)",
        ["src/AlUmmahNow.Studio/Encode/Encoding.cs"] = "GStreamer pipeline",
        ["src/AlUmmahNow.Studio/Encode/RenditionEncoding.cs"] = "GStreamer pipeline",
        ["src/AlUmmahNow.Studio/Engines/CsvWriter.cs"] = "CSV export",
        ["src/AlUmmahNow.Studio/Upload/Formats.cs"] = "schedule text box value (an input, kept as typed)",
        ["src/AlUmmahNow.Studio/Upload/UploadPlanner.cs"] = "video titles people edit and the server stores",
    };

    private static readonly Regex Bypass = new(
        @"ToString\((?!""N""\)|""X8""\))\s*""|ToString\(\s*(System\.Globalization\.)?CultureInfo|(?i:string)\.Format\(|new (System\.Globalization\.)?CultureInfo\(|CultureInfo\.(GetCultureInfo|CurrentCulture|CurrentUICulture)|To(Short|Long)(Date|Time)String|StringFormat=|\{[A-Za-z_.]+:(N|P|C|F|D|0|#)|""[^""]*·",
        RegexOptions.Compiled);

    [Fact]
    public void Display_formatting_goes_through_the_localizer()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "AlUmmahNow.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!.FullName, "src"), "*.*", SearchOption.AllDirectories))
        {
            if (!(file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".xaml", StringComparison.Ordinal))) continue;
            var relative = Path.GetRelativePath(root.FullName, file).Replace('\\', '/');
            if (relative.Contains("/bin/") || relative.Contains("/obj/") || NotDisplay.ContainsKey(relative)) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (Bypass.IsMatch(lines[i])) offenders.Add(relative + ":" + (i + 1) + ": " + lines[i].Trim());
            }
        }

        Assert.True(offenders.Count == 0,
            "Format numbers, dates and times for display with Localizer (Number, Decimal, Percent, Date, DateTime, Duration, Bytes, Money, Format), and join values with Localizer.Join, not \" · \":\n"
            + string.Join("\n", offenders));
    }
}
