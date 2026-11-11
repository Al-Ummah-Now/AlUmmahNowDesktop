using System.Globalization;

namespace AlUmmahNow.Studio.Upload;

public static class Formats
{
    /// <summary>"12.3 MB" in the UI language's digits and separators.</summary>
    public static string Bytes(long bytes, CultureInfo culture)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return v.ToString(u == 0 ? "0" : "0.0", culture) + " " + units[u];
    }

    /// <summary>The local zone's IANA id (the API wants IANA, Windows reports its own ids).</summary>
    public static string LocalTimeZone()
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId) return local.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : "UTC";
    }

    /// <summary>Parses "yyyy-MM-dd HH:mm" in local time.</summary>
    public static DateTimeOffset? ParseLocal(string text) =>
        DateTime.TryParseExact(text.Trim(), new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)
            ? new DateTimeOffset(t)
            : null;

    public static string ToLocalInput(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
