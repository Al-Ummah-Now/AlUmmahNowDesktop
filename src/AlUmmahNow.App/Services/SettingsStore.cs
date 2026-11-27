using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

        /// <summary>
        /// Opt-in anonymous diagnostics and crash reporting (off by default).
        /// Stored in a dedicated sidecar file via <see cref="SettingsStore.SaveTelemetryEnabled"/> so unrelated
        /// theme/language saves from another running instance can never overwrite an opt-out.
        /// </summary>
        [JsonIgnore]
        public bool TelemetryEnabled { get; set; }
    }

    /// <summary>Stores settings as JSON. A corrupt or missing file yields defaults.</summary>
    public sealed class SettingsStore
    {
        private readonly string _path;
        private readonly string _telemetryConsentPath;

        public SettingsStore(string path)
        {
            _path = path;
            var dir = Path.GetDirectoryName(path);
            _telemetryConsentPath = string.IsNullOrEmpty(dir)
                ? "telemetry-consent"
                : Path.Combine(dir, "telemetry-consent");
        }

        public AppSettings Load()
        {
            AppSettings settings;
            try
            {
                settings = File.Exists(_path)
                    ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings()
                    : new AppSettings();
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                settings = new AppSettings();
            }
            settings.TelemetryEnabled = LoadTelemetryEnabled();
            return settings;
        }

        public void Save(AppSettings settings)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings));
            File.Move(temp, _path, overwrite: true);
        }

        /// <summary>
        /// Reads the standalone telemetry consent state. Missing, unreadable, or unrecognized files default to off.
        /// </summary>
        public bool LoadTelemetryEnabled()
        {
            try
            {
                if (!File.Exists(_telemetryConsentPath)) return false;
                return string.Equals(File.ReadAllText(_telemetryConsentPath).Trim(), "enabled", StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Atomically updates the standalone telemetry consent state without touching <c>settings.json</c>.
        /// </summary>
        public void SaveTelemetryEnabled(bool enabled)
        {
            if (!enabled)
            {
                try
                {
                    if (File.Exists(_telemetryConsentPath)) File.Delete(_telemetryConsentPath);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
                return;
            }
            var directory = Path.GetDirectoryName(_telemetryConsentPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temp = _telemetryConsentPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, "enabled");
            File.Move(temp, _telemetryConsentPath, overwrite: true);
        }
    }
}
