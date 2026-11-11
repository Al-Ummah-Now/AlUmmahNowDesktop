namespace AlUmmahNow.Studio.Upload;

/// <summary>The server's category and language keys; labels come from i18n (studio.category.*, studio.language.*).</summary>
public static class StudioCatalog
{
    public static readonly IReadOnlyList<string> Categories = new[]
    {
        "quran_recitation", "quran_tafsir", "hadith_studies", "islamic_history", "fiqh", "aqeedah", "dawah", "khutbah",
        "islamic_lectures", "islamic_nasheed", "islamic_kids", "islamic_news", "hajj_umrah", "ramadan", "islamic_lifestyle",
    };

    public static readonly IReadOnlyList<string> Languages = new[] { "ar", "en", "ur", "fr" };
}
