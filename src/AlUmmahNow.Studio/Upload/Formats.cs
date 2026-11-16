using System.Globalization;

namespace AlUmmahNow.Studio.Upload;

public static class Formats
{
    /// <summary>The local zone's IANA id (the API wants IANA, Windows reports its own ids).</summary>
    public static string LocalTimeZone()
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId) return local.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : "UTC";
    }

    /// <summary>Parses "yyyy-MM-dd HH:mm" in local time (callers pass typed text through Localizer.NormalizeDigits).</summary>
    public static DateTimeOffset? ParseLocal(string text) =>
        DateTime.TryParseExact(text.Trim(), new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)
            ? new DateTimeOffset(t)
            : null;

    /// <summary>The schedule text box value: an input, so it keeps Arabic numerals 0–9 as typed.</summary>
    public static string ToLocalInput(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
