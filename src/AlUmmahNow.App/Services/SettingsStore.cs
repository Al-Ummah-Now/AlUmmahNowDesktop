using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AlUmmahNow.App.Services
{
    /// <summary>
    /// Light or Dark (owner rule: no "System" choice). <see cref="System"/> only remains so settings saved
    /// by older versions still load; every app reads it as Dark.
    /// </summary>
    public enum ThemePreference { System, Dark, Light }

    public sealed class AppSettings
    {
        public string Language { get; set; } = "ar";
        public ThemePreference Theme { get; set; } = ThemePreference.Dark;
        public string? WatchFolder { get; set; }

        /// <summary>Studio search history, newest first (local only).</summary>
        public List<string>? RecentSearches { get; set; }

        /// <summary>Studio: each signed-in account's resolved name (user id → name), so the account menu shows names, not emails.</summary>
        public Dictionary<string, string>? AccountNames { get; set; }

        /// <summary>Send diagnostics and crash reports. Off until the person turns it on.</summary>
        public bool TelemetryEnabled { get; set; }
    }

    /// <summary>Stores settings as JSON. A corrupt or missing file yields defaults.</summary>
    public sealed class SettingsStore
    {
        private readonly string _path;

        public SettingsStore(string path) => _path = path;

        public AppSettings Load()
        {
            try
            {
                return File.Exists(_path)
                    ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings()
                    : new AppSettings();
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                return new AppSettings();
            }
        }

        public void Save(AppSettings settings)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings));
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(temp, _path);
        }
    }
}
