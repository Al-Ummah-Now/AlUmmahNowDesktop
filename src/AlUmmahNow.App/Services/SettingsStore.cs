using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

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
        public bool SaveTelemetryEnabled(bool enabled) => TrySaveTelemetryEnabled(enabled, out _);

        /// <summary>
        /// Atomically updates the standalone telemetry consent state under a cross-process lock without
        /// touching <c>settings.json</c>. When enabling, <paramref name="writtenToken"/> receives the
        /// unique generation token written so a failed enable can roll back via <see cref="RollbackTelemetryEnabled"/>
        /// without clobbering a newer opt-in from another process.
        /// </summary>
        public bool TrySaveTelemetryEnabled(bool enabled, out string? writtenToken)
        {
            writtenToken = null;
            using var crossProcessLock = TryAcquireConsentLock();
            if (enabled && crossProcessLock == null)
            {
                return false;
            }
            if (!enabled)
            {
                return ClearConsentSidecarUnlocked();
            }

            try
            {
                var directory = Path.GetDirectoryName(_telemetryConsentPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var token = "enabled:" + Guid.NewGuid().ToString("N");
                var temp = _telemetryConsentPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, token);
                ReplaceFile(temp, _telemetryConsentPath);
                writtenToken = token;
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Rolls back a failed opt-in only if the on-disk consent token still matches <paramref name="expectedToken"/>.
        /// If another process already wrote a newer generation token, leaves the newer opt-in untouched and returns <c>true</c>.
        /// </summary>
        public bool RollbackTelemetryEnabled(string? expectedToken)
        {
            if (string.IsNullOrEmpty(expectedToken)) return SaveTelemetryEnabled(false);
            using var crossProcessLock = TryAcquireConsentLock();
            if (crossProcessLock == null) return false;
            var current = LoadTelemetryConsentToken();
            if (!string.Equals(current, expectedToken, StringComparison.Ordinal))
            {
                return true;
            }
            return ClearConsentSidecarUnlocked();
        }

        private bool ClearConsentSidecarUnlocked()
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

        private IDisposable? TryAcquireConsentLock()
        {
            try
            {
                var fullPath = Path.GetFullPath(_telemetryConsentPath);
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)))[..16];
                var lockPath = Path.Combine(Path.GetTempPath(), $"aun-telemetry-consent-{hash}.lock");
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (true)
                {
                    try
                    {
                        return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    }
                    catch (IOException) when (DateTime.UtcNow < deadline)
                    {
                        Thread.Sleep(10);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
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
