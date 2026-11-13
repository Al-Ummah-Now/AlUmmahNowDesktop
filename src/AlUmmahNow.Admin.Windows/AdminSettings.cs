using System.Text.Json;
using AlUmmahNow.CoreBridge.Logging;

namespace AlUmmahNow.Admin.WindowsApp;

/// <summary>
/// The only thing the Admin app writes besides its DPAPI-protected tokens and its log: theme,
/// language and the install id push registration uses. No queue rows, no media, no URLs.
/// </summary>
internal sealed class AdminSettings
{
    /// <summary>"light" or "dark" (no "system" option, wiki AUN-ADMIN-NATIVE).</summary>
    public string Theme { get; set; } = "light";

    /// <summary>English by default.</summary>
    public string Language { get; set; } = "en";

    public string InstallId { get; set; } = "";

    public static AdminSettings Load(string path)
    {
        AdminSettings? s = null;
        try
        {
            if (File.Exists(path)) s = JsonSerializer.Deserialize<AdminSettings>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException)
        {
            Log.For("settings").Warn("settings unreadable, using defaults: " + ex.GetType().Name);
        }

        s ??= new AdminSettings();
        if (s.Theme is not ("light" or "dark")) s.Theme = "light";
        if (s.Language is not ("en" or "ar")) s.Language = "en";
        if (string.IsNullOrEmpty(s.InstallId)) s.InstallId = "win-" + Guid.NewGuid().ToString("N");
        return s;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this));
    }
}
