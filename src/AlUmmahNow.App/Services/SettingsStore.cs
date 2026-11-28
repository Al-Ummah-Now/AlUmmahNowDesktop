using System;
using System.Collections.Generic;
using System.IO;
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
        private static readonly object ProcessLocksGate = new object();
        private static readonly Dictionary<string, ProcessConsentLockEntry> ProcessLocks =
            new Dictionary<string, ProcessConsentLockEntry>(
                Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

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
        /// Reads the standalone confirmed telemetry consent generation token, or <c>null</c> when disabled,
        /// unconfirmed/pending, missing, or unreadable. Each opt-in writes a fresh unique token so another
        /// running instance can detect an intervening opt-out + re-enable.
        /// </summary>
        public string? LoadTelemetryConsentToken()
        {
            try
            {
                if (!File.Exists(_telemetryConsentPath)) return null;
                using var stream = new FileStream(
                    _telemetryConsentPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var first = reader.ReadLine()?.Trim();
                if (!IsValidEnabledToken(first)) return null;
                return HasPendingPrefixOnNextLine(reader) ? null : first;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads the consent generation token for an in-progress opt-in claim (<paramref name="pendingClaimToken"/>).
        /// Accepts a confirmed sidecar or an unconfirmed sidecar only when <paramref name="pendingClaimToken"/> is
        /// an active pending claim in that generation.
        /// </summary>
        public string? LoadTelemetryConsentTokenForClaim(string? pendingClaimToken)
        {
            if (pendingClaimToken is not { Length: > 0 }) return LoadTelemetryConsentToken();
            ReadConsentSidecarUnlocked(out var token, out var pendingClaims);
            if (token is not { Length: > 0 }) return null;
            if (pendingClaims == null) return token;
            if (!string.Equals(ExtractBaseGenerationToken(token), ExtractBaseGenerationToken(pendingClaimToken), StringComparison.Ordinal))
            {
                return null;
            }
            foreach (var claim in pendingClaims)
            {
                if (string.Equals(claim, pendingClaimToken, StringComparison.Ordinal)) return token;
            }
            return null;
        }

        private static bool IsValidEnabledToken(string? raw) =>
            string.Equals(raw, "enabled", StringComparison.Ordinal) ||
            (raw != null && raw.StartsWith("enabled:", StringComparison.Ordinal) && raw.Length > "enabled:".Length);

        private static bool HasPendingPrefixOnNextLine(StreamReader reader)
        {
            int ch;
            while ((ch = reader.Read()) >= 0 && (ch == '\r' || ch == '\n' || ch == ' ' || ch == '\t'))
            {
            }
            if (ch < 0) return false;
            const string prefix = "pending:";
            if (ch != prefix[0]) return false;
            for (var i = 1; i < prefix.Length; i++)
            {
                if (reader.Read() != prefix[i]) return false;
            }
            return true;
        }

        private void ReadConsentSidecarUnlocked(out string? token, out List<string>? pendingClaims)
        {
            token = null;
            pendingClaims = null;
            try
            {
                if (!File.Exists(_telemetryConsentPath)) return;
                string rawText;
                using (var stream = new FileStream(
                    _telemetryConsentPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    rawText = reader.ReadToEnd();
                }
                var lines = rawText
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return;
                var first = lines[0].Trim();
                if (!IsValidEnabledToken(first)) return;
                token = first;
                if (lines.Length > 1)
                {
                    var second = lines[1].Trim();
                    if (second.StartsWith("pending:", StringComparison.Ordinal))
                    {
                        pendingClaims = new List<string>();
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        var parts = second.Substring("pending:".Length)
                            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var part in parts)
                        {
                            var claim = part.Trim();
                            if (claim.Length > 0 && seen.Add(claim))
                            {
                                pendingClaims.Add(claim);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                token = null;
                pendingClaims = null;
            }
        }

        private void WriteConsentSidecarUnlocked(string token, List<string>? pendingClaims)
        {
            var directory = Path.GetDirectoryName(_telemetryConsentPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            CleanupOrphanConsentTempFilesUnlocked();
            var content = pendingClaims != null && pendingClaims.Count > 0
                ? token + "\npending:" + string.Join(",", pendingClaims)
                : token;
            var temp = _telemetryConsentPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, content);
                ReplaceFile(temp, _telemetryConsentPath);
            }
            catch
            {
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }

        private void CleanupOrphanConsentTempFilesUnlocked()
        {
            var directory = Path.GetDirectoryName(_telemetryConsentPath);
            var fileName = Path.GetFileName(_telemetryConsentPath);
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName)) return;
            try
            {
                foreach (var tmp in Directory.EnumerateFiles(directory, fileName + ".*.tmp"))
                {
                    try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException || ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// Clears the shared telemetry consent sidecar ONLY if it still represents
        /// <paramref name="expectedConfirmedToken"/> (or is already absent), so a failed startup
        /// cannot erase a concurrent opt-in or an in-progress pending claim from another instance.
        /// </summary>
        public bool ClearTelemetryConsentIfTokenMatches(string? expectedConfirmedToken)
        {
            using var crossProcessLock = TryAcquireConsentLock(
                createDirectory: false,
                out var directoryMissing,
                timeoutMs: 10000,
                serializeMutation: true);
            if (directoryMissing) return true;
            if (crossProcessLock == null) return false;
            ReadConsentSidecarUnlocked(out var current, out var pendingClaims);
            if (current is not { Length: > 0 })
            {
                CleanupOrphanConsentTempFilesUnlocked();
                return true;
            }
            if (pendingClaims != null) return false;
            if (expectedConfirmedToken is not { Length: > 0 }) return false;
            if (!string.Equals(current, expectedConfirmedToken, StringComparison.Ordinal))
            {
                return false;
            }
            return ClearConsentSidecarUnlocked();
        }

        /// <summary>
        /// Reads the confirmed standalone telemetry consent state. Missing, unreadable, unrecognized,
        /// or unconfirmed pending-only files default to off so an interrupted opt-in never activates on restart.
        /// </summary>
        public bool LoadTelemetryEnabled()
        {
            try
            {
                if (!File.Exists(_telemetryConsentPath)) return false;
                using var stream = new FileStream(
                    _telemetryConsentPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var first = reader.ReadLine()?.Trim();
                if (!IsValidEnabledToken(first)) return false;
                return !HasPendingPrefixOnNextLine(reader);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Outcome of saving a telemetry opt-out across processes.</summary>
        public enum TelemetryOptOutResult
        {
            Saved,
            PendingOtherProcessRevocation,
            SaveFailed,
        }

        private readonly object _optOutStateGate = new();
        private string? _failedOptOutToken;
        private string? _lastKnownEnabledBaseToken;
        private bool _pendingOtherProcessRevocation;

        private bool TryConfirmOptedOutOnDisk()
        {
            try
            {
                using var stream = new FileStream(
                    _telemetryConsentPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var firstLine = reader.ReadLine()?.Trim();
                if (firstLine is not { Length: > 0 }) return true;
                if (!string.Equals(firstLine, "enabled", StringComparison.OrdinalIgnoreCase) &&
                    !firstLine.StartsWith("enabled:", StringComparison.Ordinal))
                {
                    return true;
                }
                var secondLine = reader.ReadLine()?.Trim();
                return secondLine != null && secondLine.StartsWith("pending:", StringComparison.Ordinal);
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Returns true when an earlier opt-out or rollback in this process failed to update the shared
        /// consent sidecar and the same stale opt-in generation is still present on disk (or disk state
        /// cannot be verified due to transient permission/I/O errors). Automatically clears only when
        /// disk confirms opt-out or a newer opt-in generation.
        /// </summary>
        public bool IsTelemetryDisableSaveFailed()
        {
            string? failedToken;
            lock (_optOutStateGate) failedToken = _failedOptOutToken;
            if (failedToken == null) return false;
            var current = LoadTelemetryConsentToken();
            if (current == null)
            {
                if (TryConfirmOptedOutOnDisk())
                {
                    lock (_optOutStateGate)
                    {
                        if (_failedOptOutToken == failedToken) _failedOptOutToken = null;
                    }
                    return false;
                }
                return true;
            }
            var currentBase = ExtractBaseGenerationToken(current);
            if (failedToken != "__failed__" && !string.Equals(currentBase, failedToken, StringComparison.Ordinal))
            {
                lock (_optOutStateGate)
                {
                    if (_failedOptOutToken == failedToken) _failedOptOutToken = null;
                }
                return false;
            }
            return true;
        }

        /// <summary>
        /// Returns true when an earlier opt-out in this process succeeded on disk while a sibling process
        /// still held an active flush guard, and that guard is still active with telemetry still opted out.
        /// </summary>
        public bool IsTelemetryPendingOtherRevocation()
        {
            lock (_optOutStateGate)
            {
                if (!_pendingOtherProcessRevocation) return false;
            }
            if (LoadTelemetryEnabled() || !HasActiveFlushGuards())
            {
                lock (_optOutStateGate) _pendingOtherProcessRevocation = false;
                return false;
            }
            return true;
        }

        private void RecordDisableSaveFailed()
        {
            var current = LoadTelemetryConsentToken();
            lock (_optOutStateGate)
            {
                var token = current is { Length: > 0 }
                    ? ExtractBaseGenerationToken(current)
                    : (_lastKnownEnabledBaseToken ?? "__failed__");
                _failedOptOutToken = token;
                _pendingOtherProcessRevocation = false;
            }
        }

        private void RecordKnownEnabledToken(string? token)
        {
            if (token is not { Length: > 0 }) return;
            var baseToken = ExtractBaseGenerationToken(token);
            lock (_optOutStateGate) _lastKnownEnabledBaseToken = baseToken;
        }

        private void RecordOptOutResult(TelemetryOptOutResult result)
        {
            if (result == TelemetryOptOutResult.SaveFailed)
            {
                RecordDisableSaveFailed();
                return;
            }
            lock (_optOutStateGate)
            {
                _failedOptOutToken = null;
                _pendingOtherProcessRevocation = result == TelemetryOptOutResult.PendingOtherProcessRevocation;
            }
        }

        private void ClearOptOutTracking()
        {
            lock (_optOutStateGate)
            {
                _failedOptOutToken = null;
                _pendingOtherProcessRevocation = false;
            }
        }

        /// <summary>
        /// Clears the shared telemetry consent sidecar and waits briefly for any sibling process's
        /// active flush monitor to observe revocation. Distinguishes a true save failure from a saved
        /// opt-out whose sibling flush revocation wait timed out.
        /// </summary>
        public TelemetryOptOutResult SaveTelemetryOptOut()
        {
            using var crossProcessLock = TryAcquireConsentLock(
                createDirectory: false,
                out var directoryMissing,
                timeoutMs: 10000,
                serializeMutation: true);
            TelemetryOptOutResult result;
            if (directoryMissing)
            {
                result = TelemetryOptOutResult.Saved;
            }
            else if (crossProcessLock == null)
            {
                result = TelemetryOptOutResult.SaveFailed;
            }
            else
            {
                result = ClearConsentSidecarUnlockedResult();
            }
            RecordOptOutResult(result);
            return result;
        }

        /// <summary>
        /// Atomically updates the standalone telemetry consent state without touching <c>settings.json</c>.
        /// Returns <c>true</c> only when the on-disk state was confirmed updated to <paramref name="enabled"/>.
        /// When opting out, falls back to overwriting the sidecar with <c>disabled</c> if deletion fails;
        /// if both delete and overwrite fail (for example due to an exclusive handle or read-only volume),
        /// returns <c>false</c> rather than treating an unreadable file as opted out.
        /// </summary>
        public bool SaveTelemetryEnabled(bool enabled) =>
            enabled
                ? TrySaveTelemetryEnabledInternal(true, confirmImmediately: true, out _)
                : SaveTelemetryOptOut() == TelemetryOptOutResult.Saved;

        /// <summary>
        /// Atomically updates the standalone telemetry consent state under a cross-process lock without
        /// touching <c>settings.json</c>. When enabling, <paramref name="writtenToken"/> receives the
        /// unique claim token written so a failed enable can roll back via <see cref="RollbackTelemetryEnabled"/>
        /// without clobbering a confirmed or concurrent opt-in, while clearing the sidecar if all concurrent
        /// unconfirmed opt-in attempts roll back. Call <see cref="ConfirmTelemetryEnabled"/> once native
        /// consent activation succeeds.
        /// </summary>
        public bool TrySaveTelemetryEnabled(bool enabled, out string? writtenToken) =>
            TrySaveTelemetryEnabledInternal(enabled, confirmImmediately: false, out writtenToken);

        private bool TrySaveTelemetryEnabledInternal(bool enabled, bool confirmImmediately, out string? writtenToken)
        {
            writtenToken = null;
            using var crossProcessLock = TryAcquireConsentLock(
                createDirectory: enabled,
                out var directoryMissing,
                timeoutMs: enabled ? 2000 : 10000,
                serializeMutation: true);
            if (!enabled && directoryMissing)
            {
                return true;
            }
            if (crossProcessLock == null)
            {
                return false;
            }
            if (!enabled)
            {
                return ClearConsentSidecarUnlocked();
            }

            try
            {
                ReadConsentSidecarUnlocked(out var existing, out var pendingClaims);
                string token;
                List<string>? nextPending = null;
                if (existing is { Length: > 0 })
                {
                    var baseGen = ExtractBaseGenerationToken(existing);
                    token = baseGen + ":" + Guid.NewGuid().ToString("N");
                    if (!confirmImmediately && pendingClaims != null)
                    {
                        nextPending = new List<string>(pendingClaims) { token };
                    }
                }
                else
                {
                    token = "enabled:" + Guid.NewGuid().ToString("N");
                    if (!confirmImmediately)
                    {
                        nextPending = new List<string> { token };
                    }
                }
                WriteConsentSidecarUnlocked(token, nextPending);
                writtenToken = token;
                if (confirmImmediately)
                {
                    RecordKnownEnabledToken(token);
                    ClearOptOutTracking();
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Marks a pending opt-in claim as confirmed after native consent activation succeeds.
        /// Never resurrects consent if another process already opted out or started a different generation.
        /// </summary>
        public bool ConfirmTelemetryEnabled(string? expectedToken)
        {
            if (expectedToken is not { Length: > 0 }) return false;
            using var crossProcessLock = TryAcquireConsentLock(
                createDirectory: false,
                out var directoryMissing,
                serializeMutation: true);
            if (directoryMissing || crossProcessLock == null) return false;
            ReadConsentSidecarUnlocked(out var current, out var pendingClaims);
            if (current is not { Length: > 0 }) return false;
            if (!string.Equals(ExtractBaseGenerationToken(current), ExtractBaseGenerationToken(expectedToken), StringComparison.Ordinal))
            {
                return false;
            }
            if (pendingClaims == null)
            {
                RecordKnownEnabledToken(current);
                ClearOptOutTracking();
                return true;
            }
            try
            {
                WriteConsentSidecarUnlocked(current, pendingClaims: null);
                RecordKnownEnabledToken(current);
                ClearOptOutTracking();
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Rolls back a failed opt-in claim. If the generation is still unconfirmed, removes this claim from
        /// the pending set and deletes the sidecar once no unconfirmed opt-in claims remain. If the generation
        /// is already confirmed (or another process started a newer generation), leaves it untouched.
        /// Returns <c>false</c> when <paramref name="expectedToken"/> is missing or the cross-process lock cannot be acquired.
        /// </summary>
        public bool RollbackTelemetryEnabled(string? expectedToken)
        {
            if (expectedToken is not { Length: > 0 })
            {
                RecordDisableSaveFailed();
                return false;
            }
            using var crossProcessLock = TryAcquireConsentLock(
                createDirectory: false,
                out var directoryMissing,
                serializeMutation: true);
            if (directoryMissing) return true;
            if (crossProcessLock == null)
            {
                RecordDisableSaveFailed();
                return false;
            }
            ReadConsentSidecarUnlocked(out var current, out var pendingClaims);
            if (current is not { Length: > 0 }) return true;
            if (pendingClaims != null)
            {
                if (!pendingClaims.Remove(expectedToken))
                {
                    return true;
                }
                if (pendingClaims.Count == 0)
                {
                    var optOutResult = ClearConsentSidecarUnlockedResult();
                    RecordOptOutResult(optOutResult);
                    return optOutResult != TelemetryOptOutResult.SaveFailed;
                }
                try
                {
                    var nextLine1 = string.Equals(current, expectedToken, StringComparison.Ordinal)
                        ? pendingClaims[pendingClaims.Count - 1]
                        : current;
                    WriteConsentSidecarUnlocked(nextLine1, pendingClaims);
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    RecordDisableSaveFailed();
                    return false;
                }
            }
            // Once the generation is confirmed (no pending line), a stale rollback never erases consent.
            return true;
        }

        private static string ExtractBaseGenerationToken(string token)
        {
            if (!token.StartsWith("enabled:", StringComparison.Ordinal))
            {
                return "enabled:legacy";
            }
            var secondColon = token.IndexOf(':', "enabled:".Length);
            return secondColon >= 0 ? token.Substring(0, secondColon) : token;
        }

        private static bool IsJoinedGenerationToken(string token) =>
            token.StartsWith("enabled:", StringComparison.Ordinal) &&
            token.IndexOf(':', "enabled:".Length) >= 0;

        private bool ClearConsentSidecarUnlocked() =>
            ClearConsentSidecarUnlockedResult() == TelemetryOptOutResult.Saved;

        private TelemetryOptOutResult ClearConsentSidecarUnlockedResult()
        {
            bool cleared;
            try
            {
                // Do NOT pre-check with File.Exists: it returns false when directory search permission is denied,
                // which would falsely report opt-out success while the "enabled" marker still exists on disk.
                File.Delete(_telemetryConsentPath);
                cleared = true;
            }
            catch (DirectoryNotFoundException)
            {
                return TelemetryOptOutResult.Saved;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                try
                {
                    using (var stream = new FileStream(
                        _telemetryConsentPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete))
                    using (var writer = new StreamWriter(stream))
                    {
                        writer.Write("disabled");
                    }
                    cleared = true;
                }
                catch (Exception overwriteEx) when (overwriteEx is IOException || overwriteEx is UnauthorizedAccessException)
                {
                    return TelemetryOptOutResult.SaveFailed;
                }
            }
            CleanupOrphanConsentTempFilesUnlocked();
            if (cleared)
            {
                return WaitForActiveFlushRevocationsUnlocked(timeoutMs: 1500)
                    ? TelemetryOptOutResult.Saved
                    : TelemetryOptOutResult.PendingOtherProcessRevocation;
            }
            return TelemetryOptOutResult.SaveFailed;
        }

        /// <summary>
        /// Acquires a per-flush guard file in the shared config directory so a concurrent opt-out in
        /// another process can wait briefly for this process's flush monitor to revoke native consent
        /// before opt-out returns (without waiting for slow HTTP I/O to finish).
        /// </summary>
        public IDisposable? TryAcquireActiveFlushGuard()
        {
            try
            {
                var directory = Path.GetDirectoryName(_telemetryConsentPath);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;
                var guardPath = Path.Combine(directory, "telemetry-flush-active." + Guid.NewGuid().ToString("N") + ".lock");
                var stream = new FileStream(guardPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                return new ActiveFlushGuardLease(stream, guardPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// Returns true if another process currently holds an active flush guard in the shared config directory.
        /// Cleans up unlocked leftover guard files from exited processes.
        /// </summary>
        public bool HasActiveFlushGuards()
        {
            var directory = Path.GetDirectoryName(_telemetryConsentPath);
            if (string.IsNullOrEmpty(directory)) return false;
            string[] guards;
            try
            {
                guards = Directory.GetFiles(directory, "telemetry-flush-active.*.lock");
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return true;
            }
            if (guards.Length == 0) return false;
            var anyLive = false;
            foreach (var guardPath in guards)
            {
                try
                {
                    using (new FileStream(guardPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                    }
                    try { File.Delete(guardPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                catch (FileNotFoundException)
                {
                }
                catch (DirectoryNotFoundException)
                {
                    return false;
                }
                catch (IOException)
                {
                    anyLive = true;
                }
                catch (UnauthorizedAccessException)
                {
                    anyLive = true;
                }
            }
            return anyLive;
        }

        private bool WaitForActiveFlushRevocationsUnlocked(int timeoutMs)
        {
            var directory = Path.GetDirectoryName(_telemetryConsentPath);
            if (string.IsNullOrEmpty(directory)) return true;
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                string[] guards;
                try
                {
                    guards = Directory.GetFiles(directory, "telemetry-flush-active.*.lock");
                }
                catch (DirectoryNotFoundException)
                {
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    return false;
                }
                if (guards.Length == 0) return true;
                var anyLive = false;
                foreach (var guardPath in guards)
                {
                    try
                    {
                        using (new FileStream(guardPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        {
                        }
                        try { File.Delete(guardPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                    catch (FileNotFoundException)
                    {
                    }
                    catch (DirectoryNotFoundException)
                    {
                        return true;
                    }
                    catch (IOException)
                    {
                        anyLive = true;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        anyLive = true;
                    }
                }
                if (!anyLive) return true;
                if (DateTime.UtcNow >= deadline) return false;
                Thread.Sleep(5);
            }
        }

        private sealed class ActiveFlushGuardLease : IDisposable
        {
            private FileStream? _stream;
            private readonly string _path;

            public ActiveFlushGuardLease(FileStream stream, string path)
            {
                _stream = stream;
                _path = path;
            }

            public void Dispose()
            {
                var stream = Interlocked.Exchange(ref _stream, null);
                if (stream == null) return;
                try { stream.Dispose(); } catch (IOException) { }
                try { File.Delete(_path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>
        /// Acquires the cross-process telemetry consent lock without creating the config directory.
        /// Returns <c>null</c> if the lock cannot be acquired (or the directory does not exist); callers
        /// must fail closed and skip dispatching telemetry flushes when <c>null</c> is returned.
        /// </summary>
        public IDisposable? TryAcquireTelemetryConsentLock() =>
            TryAcquireConsentLock(createDirectory: false, out _, serializeMutation: false);

        private IDisposable? TryAcquireConsentLock(
            bool createDirectory,
            out bool directoryMissing,
            int timeoutMs = 2000,
            bool serializeMutation = false)
        {
            directoryMissing = false;
            string lockPath;
            try
            {
                var directory = Path.GetDirectoryName(_telemetryConsentPath);
                if (createDirectory && !string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                // Keep the lock file in the same shared config directory as the consent sidecar so
                // processes with different TMPDIR values still coordinate on the exact same lock file.
                lockPath = Path.GetFullPath(_telemetryConsentPath + ".lock");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }

            ProcessConsentLockEntry entry;
            lock (ProcessLocksGate)
            {
                if (!ProcessLocks.TryGetValue(lockPath, out entry!))
                {
                    entry = new ProcessConsentLockEntry();
                    ProcessLocks[lockPath] = entry;
                }
                entry.Waiters++;
            }

            ProcessConsentLockLease? lease = null;
            try
            {
                lock (entry.Gate)
                {
                    if (entry.RefCount > 0)
                    {
                        Volatile.Write(ref entry.RefCount, entry.RefCount + 1);
                        lease = new ProcessConsentLockLease(lockPath, entry);
                    }
                    else
                    {
                        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                        while (true)
                        {
                            try
                            {
                                entry.Stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                                Volatile.Write(ref entry.RefCount, 1);
                                lease = new ProcessConsentLockLease(lockPath, entry);
                                break;
                            }
                            catch (DirectoryNotFoundException) when (!createDirectory)
                            {
                                directoryMissing = true;
                                return null;
                            }
                            catch (IOException) when (DateTime.UtcNow < deadline)
                            {
                                Thread.Sleep(10);
                            }
                            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                            {
                                return null;
                            }
                        }
                    }
                }
            }
            finally
            {
                lock (ProcessLocksGate)
                {
                    entry.Waiters--;
                    if (Volatile.Read(ref entry.RefCount) == 0 && entry.Waiters == 0 &&
                        ProcessLocks.TryGetValue(lockPath, out var current) && ReferenceEquals(current, entry))
                    {
                        ProcessLocks.Remove(lockPath);
                    }
                }
            }

            if (lease == null || !serializeMutation)
            {
                return lease;
            }

            // Serialize same-process sidecar mutations (save / compare-and-delete rollback) separately from
            // flush leases so rollback is atomic while opt-out never blocks on an in-flight flush.
            try
            {
                Monitor.Enter(entry.MutationGate);
                return new SidecarMutationLease(lease, entry.MutationGate);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        private sealed class ProcessConsentLockEntry
        {
            public readonly object Gate = new object();
            public readonly object MutationGate = new object();
            public FileStream? Stream;
            public int RefCount;
            public int Waiters;
        }

        private sealed class SidecarMutationLease : IDisposable
        {
            private IDisposable? _processLease;
            private object? _mutationGate;

            public SidecarMutationLease(IDisposable processLease, object mutationGate)
            {
                _processLease = processLease;
                _mutationGate = mutationGate;
            }

            public void Dispose()
            {
                var gate = Interlocked.Exchange(ref _mutationGate, null);
                var lease = Interlocked.Exchange(ref _processLease, null);
                try
                {
                    if (gate != null) Monitor.Exit(gate);
                }
                finally
                {
                    lease?.Dispose();
                }
            }
        }

        private sealed class ProcessConsentLockLease : IDisposable
        {
            private readonly string _lockPath;
            private ProcessConsentLockEntry? _entry;

            public ProcessConsentLockLease(string lockPath, ProcessConsentLockEntry entry)
            {
                _lockPath = lockPath;
                _entry = entry;
            }

            public void Dispose()
            {
                var entry = Interlocked.Exchange(ref _entry, null);
                if (entry == null) return;
                FileStream? toDispose = null;
                lock (entry.Gate)
                {
                    if (entry.RefCount > 0)
                    {
                        var remaining = entry.RefCount - 1;
                        Volatile.Write(ref entry.RefCount, remaining);
                        if (remaining == 0)
                        {
                            toDispose = entry.Stream;
                            entry.Stream = null;
                        }
                    }
                }
                toDispose?.Dispose();
                lock (ProcessLocksGate)
                {
                    if (Volatile.Read(ref entry.RefCount) == 0 && entry.Waiters == 0 &&
                        ProcessLocks.TryGetValue(_lockPath, out var current) && ReferenceEquals(current, entry))
                    {
                        ProcessLocks.Remove(_lockPath);
                    }
                }
            }
        }

        private static void ReplaceFile(string temp, string destination)
        {
#if NETSTANDARD2_0
            try
            {
                File.Replace(temp, destination, null);
            }
            catch (FileNotFoundException)
            {
                try
                {
                    File.Move(temp, destination);
                }
                catch (IOException) when (File.Exists(destination))
                {
                    File.Replace(temp, destination, null);
                }
            }
#else
            File.Move(temp, destination, overwrite: true);
#endif
        }
    }
}
