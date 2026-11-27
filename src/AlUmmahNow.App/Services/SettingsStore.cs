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
            ReplaceFile(temp, _path);
        }

        /// <summary>
        /// Reads the standalone telemetry consent generation token, or <c>null</c> when disabled, missing, or unreadable.
        /// Each opt-in writes a fresh unique token so another running instance can detect an intervening opt-out + re-enable.
        /// </summary>
        public string? LoadTelemetryConsentToken()
        {
            try
            {
                if (!File.Exists(_telemetryConsentPath)) return null;
                var raw = File.ReadAllText(_telemetryConsentPath).Trim();
                if (string.Equals(raw, "enabled", StringComparison.Ordinal) ||
                    (raw.StartsWith("enabled:", StringComparison.Ordinal) && raw.Length > "enabled:".Length))
                {
                    return raw;
                }
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads the standalone telemetry consent state. Missing, unreadable, or unrecognized files default to off.
        /// </summary>
        public bool LoadTelemetryEnabled() => LoadTelemetryConsentToken() != null;

        /// <summary>
        /// Atomically updates the standalone telemetry consent state without touching <c>settings.json</c>.
        /// Returns <c>true</c> only when the on-disk state was confirmed updated to <paramref name="enabled"/>.
        /// When opting out, falls back to overwriting the sidecar with <c>disabled</c> if deletion fails;
        /// if both delete and overwrite fail (for example due to an exclusive handle or read-only volume),
        /// returns <c>false</c> rather than treating an unreadable file as opted out.
        /// </summary>
        public bool SaveTelemetryEnabled(bool enabled)
        {
            if (!enabled)
            {
                try
                {
                    // Do NOT pre-check with File.Exists: it returns false when directory search permission is denied,
                    // which would falsely report opt-out success while the "enabled" marker still exists on disk.
                    File.Delete(_telemetryConsentPath);
                    return true;
                }
                catch (DirectoryNotFoundException)
                {
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    try
                    {
                        File.WriteAllText(_telemetryConsentPath, "disabled");
                        return true;
                    }
                    catch (Exception overwriteEx) when (overwriteEx is IOException || overwriteEx is UnauthorizedAccessException)
                    {
                        return false;
                    }
                }
            }

            try
            {
                var directory = Path.GetDirectoryName(_telemetryConsentPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var temp = _telemetryConsentPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, "enabled:" + Guid.NewGuid().ToString("N"));
                ReplaceFile(temp, _telemetryConsentPath);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static void ReplaceFile(string temp, string destination)
        {
            if (File.Exists(destination))
            {
                File.Replace(temp, destination, null);
            }
            else
            {
                File.Move(temp, destination);
            }
        }
    }
}
