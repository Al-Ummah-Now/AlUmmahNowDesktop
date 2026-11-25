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
}
