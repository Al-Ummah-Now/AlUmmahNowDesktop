using System.Text.RegularExpressions;
using Xunit;

namespace AlUmmahNow.App.Tests;

/// <summary>
/// store/microsoft/: Microsoft Store listing limits, en-us / ar-sa parity, copy tone, and the source
/// files cited by privacy.md.
/// </summary>
public class StoreListingTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Store = Path.Combine(Root, "store", "microsoft");
    private static readonly string[] Apps = { "viewer", "studio" };
    private static readonly string[] Languages = { "en-us", "ar-sa" };

    private static readonly string[] Files =
        { "title.txt", "description.txt", "short-description.txt", "features.txt", "keywords.txt", "release-notes.txt" };

    // Partner Center limits for MSIX app listings.
    private static readonly Dictionary<string, int> MaxChars = new()
    {
        ["title.txt"] = 256,
        ["description.txt"] = 10_000,
        ["short-description.txt"] = 1_000,
        ["release-notes.txt"] = 1_500,
    };

    private const int MaxFeatures = 20, MaxFeatureChars = 200;
    private const int MaxKeywords = 7, MaxKeywordChars = 30, MaxKeywordWords = 21;

    private static readonly string[] BannedEnglish = { "lesson", "lecture", "course", "library", "beneficial knowledge" };
    private static readonly string[] BannedArabic = { "درس", "دروس", "محاضر", "مكتبة", "العلم النافع", "فيديو" };

    public static IEnumerable<object[]> Listings() =>
        from app in Apps from lang in Languages select new object[] { app, lang };

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AlUmmahNow.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static string Read(string app, string lang, string file) => File.ReadAllText(Path.Combine(Store, app, lang, file)).Trim();

    private static List<string> Lines(string app, string lang, string file) =>
        Read(app, lang, file).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    [Fact]
    public void EveryAppHasExactlyTheTwoLanguages()
    {
        foreach (var app in Apps)
        {
            var found = Directory.GetDirectories(Path.Combine(Store, app)).Select(Path.GetFileName).OrderBy(n => n).ToArray();
            Assert.Equal(Languages.OrderBy(n => n).ToArray(), found);
        }
    }

    [Theory]
    [MemberData(nameof(Listings))]
    public void FilesExistAndAreWithinLimits(string app, string lang)
    {
        var present = Directory.GetFiles(Path.Combine(Store, app, lang)).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(Files.OrderBy(n => n).ToArray(), present);
        foreach (var (file, max) in MaxChars)
        {
            var text = Read(app, lang, file);
            Assert.False(string.IsNullOrWhiteSpace(text), $"{app}/{lang}/{file} is empty");
            Assert.True(text.Length <= max, $"{app}/{lang}/{file}: {text.Length} > {max}");
        }

        var features = Lines(app, lang, "features.txt");
        Assert.InRange(features.Count, 1, MaxFeatures);
        Assert.All(features, f => Assert.True(f.Length <= MaxFeatureChars, $"{app}/{lang} feature too long: {f}"));

        var keywords = Lines(app, lang, "keywords.txt");
        Assert.InRange(keywords.Count, 1, MaxKeywords);
        Assert.All(keywords, k => Assert.True(k.Length <= MaxKeywordChars, $"{app}/{lang} search term too long: {k}"));
        Assert.True(keywords.Sum(k => k.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length) <= MaxKeywordWords, $"{app}/{lang}: more than {MaxKeywordWords} words in search terms");
        Assert.Equal(keywords.Count, keywords.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("studio")]
    public void EnglishAndArabicMatch(string app)
    {
        foreach (var file in new[] { "features.txt", "keywords.txt" })
            Assert.Equal(Lines(app, "en-us", file).Count, Lines(app, "ar-sa", file).Count);
        // Same bullet structure in the description.
        int Bullets(string lang) => Read(app, lang, "description.txt").Split('\n').Count(l => l.TrimStart().StartsWith("•", StringComparison.Ordinal));
        Assert.Equal(Bullets("en-us"), Bullets("ar-sa"));
        Assert.Matches(new Regex(@"\p{IsArabic}"), Read(app, "ar-sa", "description.txt"));
        Assert.DoesNotMatch(new Regex(@"\p{IsArabic}"), Read(app, "en-us", "description.txt"));
    }

    [Theory]
    [MemberData(nameof(Listings))]
    public void CopyToneWords(string app, string lang)
    {
        foreach (var file in Files)
        {
            var text = Read(app, lang, file);
            foreach (var word in lang == "en-us" ? BannedEnglish : BannedArabic)
                Assert.False(text.Contains(word, StringComparison.OrdinalIgnoreCase), $"{app}/{lang}/{file} uses \"{word}\" (CLAUDE.md copy tone)");
        }
    }

    [Fact]
    public void PrivacyAnswersCiteFilesThatExist()
    {
        var text = File.ReadAllText(Path.Combine(Store, "privacy.md"));
        var cited = Regex.Matches(text, @"`([A-Za-z0-9_.\-/]+\.(?:cs|props|appxmanifest|md))`").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(cited);
        Assert.All(cited, path => Assert.True(File.Exists(Path.Combine(Root, path)), $"privacy.md cites missing file {path}"));
    }

    [Fact]
    public void ManifestsDeclareTheListingLanguages()
    {
        foreach (var project in new[] { "AlUmmahNow.Windows", "AlUmmahNow.Studio.Windows" })
        {
            var manifest = File.ReadAllText(Path.Combine(Root, "src", project, "Package.appxmanifest"));
            foreach (var lang in Languages) Assert.Contains($"<Resource Language=\"{lang}\" />", manifest);
        }
    }
}
