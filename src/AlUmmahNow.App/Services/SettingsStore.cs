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
        /// Reads the standalone telemetry consent generation token, or <c>null</c> when disabled, missing, or unreadable.
        /// Each opt-in writes a fresh unique token so another running instance can detect an intervening opt-out + re-enable.
        /// </summary>
        public string? LoadTelemetryConsentToken()
        {
            ReadConsentSidecarUnlocked(out var token, out _);
            return token;
        }

        private static bool IsValidEnabledToken(string? raw) =>
            string.Equals(raw, "enabled", StringComparison.Ordinal) ||
            (raw != null && raw.StartsWith("enabled:", StringComparison.Ordinal) && raw.Length > "enabled:".Length);

        private void ReadConsentSidecarUnlocked(out string? token, out List<string>? pendingClaims)
        {
            token = null;
            pendingClaims = null;
            try
            {
                if (!File.Exists(_telemetryConsentPath)) return;
                var lines = File.ReadAllText(_telemetryConsentPath)
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
                        var parts = second.Substring("pending:".Length)
                            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var part in parts)
                        {
                            var claim = part.Trim();
                            if (claim.Length > 0 && !pendingClaims.Contains(claim))
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
            var content = pendingClaims != null && pendingClaims.Count > 0
                ? token + "\npending:" + string.Join(",", pendingClaims)
                : token;
            var temp = _telemetryConsentPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, content);
            ReplaceFile(temp, _telemetryConsentPath);
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
        public bool SaveTelemetryEnabled(bool enabled) =>
            TrySaveTelemetryEnabledInternal(enabled, confirmImmediately: true, out _);

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
            if (pendingClaims == null) return true;
            try
            {
                WriteConsentSidecarUnlocked(current, pendingClaims: null);
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
            if (expectedToken is not { Length: > 0 }) return false;
            using var crossProcessLock = TryAcquireConsentLock(
                createDirectory: false,
                out var directoryMissing,
                serializeMutation: true);
            if (directoryMissing) return true;
            if (crossProcessLock == null) return false;
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
                    return ClearConsentSidecarUnlocked();
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
                    return false;
                }
            }
            if (!string.Equals(current, expectedToken, StringComparison.Ordinal))
            {
                return true;
            }
            if (IsJoinedGenerationToken(expectedToken))
            {
                return true;
            }
            return ClearConsentSidecarUnlocked();
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
