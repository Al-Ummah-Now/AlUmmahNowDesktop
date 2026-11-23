using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AlUmmahNow.CoreBridge.Studio;

namespace AlUmmahNow.App.Media
{
    public enum OfflineDownloadQuality
    {
        Audio,
        Q360p,
        Q480p,
        Q720p,
        Q1080p,
    }

    public enum OfflineDownloadStatus
    {
        Ready,
        Removed,
    }

    /// <summary>
    /// Plain-file local offline download record (E14-D1: no encryption, no DRM, no license expiry).
    /// Never persists signed URLs (artwork is stored as a local file path only).
    /// </summary>
    public sealed class OfflineDownloadRecord
    {
        public string UserId { get; set; } = "default";
        public string VideoId { get; set; } = "";
        public string Title { get; set; } = "";
        public string? TitleAr { get; set; }
        public string? ChannelName { get; set; }
        public string? ThumbnailPath { get; set; }
        public double? DurationSeconds { get; set; }
        public OfflineDownloadQuality Quality { get; set; } = OfflineDownloadQuality.Audio;
        public string FileName { get; set; } = "";
        public long Bytes { get; set; }
        public long DownloadedAtMs { get; set; }
        public long LastAccessedAtMs { get; set; }
        public OfflineDownloadStatus Status { get; set; } = OfflineDownloadStatus.Ready;

        public bool IsPlayable => Status == OfflineDownloadStatus.Ready;
    }

    public sealed class OfflineDownloadSettings
    {
        public const long DefaultMaxStorageBytes = 2L * 1024L * 1024L * 1024L;

        public bool WifiOnly { get; set; } = true;
        public long MaxStorageBytes { get; set; } = DefaultMaxStorageBytes;
        public int? MaxVideos { get; set; }
        public OfflineDownloadQuality DefaultQuality { get; set; } = OfflineDownloadQuality.Audio;
        public bool AutoDownloadEnabled { get; set; }
    }

    /// <summary>Server hygiene answer from `POST /api/v1/downloads/sync`.</summary>
    public sealed class AvailabilitySyncDecision
    {
        public AvailabilitySyncDecision(string videoId, bool available, string? reason = null)
        {
            VideoId = videoId;
            Available = available;
            Reason = reason;
        }

        public string VideoId { get; }
        public bool Available { get; }
        public string? Reason { get; }
    }

    public sealed class StorageBudgetExceededException : IOException
    {
        public StorageBudgetExceededException(long usedBytes, long incomingBytes, long maxStorageBytes)
            : base($"storage_budget_exceeded: used={usedBytes} incoming={incomingBytes} cap={maxStorageBytes}")
        {
            UsedBytes = usedBytes;
            IncomingBytes = incomingBytes;
            MaxStorageBytes = maxStorageBytes;
        }

        public long UsedBytes { get; }
        public long IncomingBytes { get; }
        public long MaxStorageBytes { get; }
    }

    public sealed class MaxVideosExceededException : InvalidOperationException
    {
        public MaxVideosExceededException(int activeCount, int maxVideos)
            : base($"max_videos_exceeded: active={activeCount} cap={maxVideos}")
        {
            ActiveCount = activeCount;
            MaxVideos = maxVideos;
        }

        public int ActiveCount { get; }
        public int MaxVideos { get; }
    }

    public enum DownloadTransferAllowance
    {
        Allowed,
        BlockedFeatureOff,
        BlockedOffline,
        BlockedWifiOnly,
    }

    public static class OfflineDownloadPolicy
    {
        public static DownloadTransferAllowance CanDownload(
            bool featureEnabled,
            OfflineDownloadSettings settings,
            bool isConnected,
            bool isUnmetered)
        {
            if (!featureEnabled) return DownloadTransferAllowance.BlockedFeatureOff;
            if (!isConnected) return DownloadTransferAllowance.BlockedOffline;
            if (settings.WifiOnly && !isUnmetered) return DownloadTransferAllowance.BlockedWifiOnly;
            return DownloadTransferAllowance.Allowed;
        }
    }

    /// <summary>
    /// Plain-file local offline download store for Desktop (E14-S10, E14-D1):
    /// - Saves plain progressive `audio.m4a` / `video.mp4` files played directly by Windows Media Foundation / GStreamer.
    /// - No encryption, DRM, OS key storage, or license expiry.
    /// - Keyed by `(UserId, VideoId)` and forbids signed URL persistence.
    /// - Never silently evicts active downloads; deletes copies on availability sync when videos go private or are deleted.
    /// </summary>
    public sealed class OfflineDownloadStore
    {
        private sealed class ManifestEnvelope
        {
            public int Version { get; set; } = 3;
            public List<OfflineDownloadRecord> Items { get; set; } = new List<OfflineDownloadRecord>();
        }

        private readonly string _rootDir;
        private readonly string _manifestPath;
        private readonly Func<long> _clockMs;
        private readonly object _gate = new object();
        private readonly Dictionary<string, OfflineDownloadRecord> _records = new Dictionary<string, OfflineDownloadRecord>(StringComparer.Ordinal);

        public OfflineDownloadStore(
            string rootDir,
            bool offlineDownloadsEnabled = false,
            Func<long>? clockMs = null)
        {
            _rootDir = rootDir;
            _manifestPath = Path.Combine(rootDir, "manifest.json");
            OfflineDownloadsEnabled = offlineDownloadsEnabled;
            _clockMs = clockMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Directory.CreateDirectory(_rootDir);
            LoadManifest();
        }

        public bool OfflineDownloadsEnabled { get; set; }
        public OfflineDownloadSettings Settings { get; private set; } = new OfflineDownloadSettings();

        /// <summary>Wires `offlineDownloads` directly from `GET /api/me/features`.</summary>
        public void ApplyFeatures(UserFeatures features) =>
            OfflineDownloadsEnabled = features.IsOn(UserFeatures.OfflineDownloads);

        private static string RecordKey(string userId, string videoId) => $"{userId}\u001F{videoId}";

        public void UpdateSettings(OfflineDownloadSettings settings)
        {
            lock (_gate)
            {
                Settings = settings;
                PurgeInactiveLocked();
                PersistManifestLocked();
            }
        }

        public OfflineDownloadRecord SaveFromStream(
            string userId,
            string videoId,
            string title,
            OfflineDownloadQuality quality,
            Stream input,
            long expectedBytes = 0,
            string? titleAr = null,
            string? channelName = null,
            string? thumbnailPath = null,
            double? durationSeconds = null)
        {
            if (!OfflineDownloadsEnabled) throw new InvalidOperationException("offline_downloads_feature_off");
            if (!string.IsNullOrEmpty(thumbnailPath) &&
                (thumbnailPath!.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 thumbnailPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("signed_url_persistence_forbidden", nameof(thumbnailPath));
            }

            lock (_gate)
            {
                PurgeInactiveLocked();

                var key = RecordKey(userId, videoId);
                var replacing = _records.TryGetValue(key, out var existing);
                if (!replacing && Settings.MaxVideos.HasValue)
                {
                    var activeCount = _records.Values.Count(r => string.Equals(r.UserId, userId, StringComparison.Ordinal) && r.IsPlayable);
                    if (activeCount >= Settings.MaxVideos.Value)
                        throw new MaxVideosExceededException(activeCount, Settings.MaxVideos.Value);
                }

                var cap = Settings.MaxStorageBytes;
                var used = _records.Values.Sum(r => r.Bytes) - (existing?.Bytes ?? 0);
                if (cap > 0 && expectedBytes > 0 && used + expectedBytes > cap)
                    throw new StorageBudgetExceededException(used, expectedBytes, cap);

                var ext = quality == OfflineDownloadQuality.Audio ? ".m4a" : ".mp4";
                var safeName = SanitizeFileName($"{userId}_{videoId}") + ext;
                var dest = Path.Combine(_rootDir, safeName);
                var tmp = dest + ".tmp";

                long writtenBytes;
                try
                {
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        input.CopyTo(fs);
                        fs.Flush(true);
                    }
                    writtenBytes = new FileInfo(tmp).Length;
                    if (cap > 0 && used + writtenBytes > cap)
                    {
                        DeleteQuietly(tmp);
                        throw new StorageBudgetExceededException(used, writtenBytes, cap);
                    }
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(tmp, dest);
                }
                catch
                {
                    DeleteQuietly(tmp);
                    throw;
                }

                if (existing != null && !string.Equals(existing.FileName, safeName, StringComparison.Ordinal))
                {
                    DeleteQuietly(Path.Combine(_rootDir, existing.FileName));
                }

                var now = _clockMs();
                var rec = new OfflineDownloadRecord
                {
                    UserId = userId,
                    VideoId = videoId,
                    Title = title,
                    TitleAr = titleAr,
                    ChannelName = channelName,
                    ThumbnailPath = thumbnailPath,
                    DurationSeconds = durationSeconds,
                    Quality = quality,
                    FileName = safeName,
                    Bytes = writtenBytes,
                    DownloadedAtMs = now,
                    LastAccessedAtMs = now,
                    Status = OfflineDownloadStatus.Ready,
                };
                _records[key] = rec;
                PersistManifestLocked();
                return rec;
            }
        }

        /// <summary>Returns the plain local file path for direct playback by the desktop media player.</summary>
        public string GetPlayableFilePath(string userId, string videoId)
        {
            lock (_gate)
            {
                var key = RecordKey(userId, videoId);
                if (!_records.TryGetValue(key, out var rec) || !rec.IsPlayable)
                    throw new FileNotFoundException($"Offline download not found for {videoId}");
                var fullPath = Path.Combine(_rootDir, rec.FileName);
                if (!File.Exists(fullPath))
                    throw new FileNotFoundException($"Offline media file missing for {videoId}", fullPath);
                rec.LastAccessedAtMs = _clockMs();
                PersistManifestLocked();
                return fullPath;
            }
        }

        public IReadOnlyList<OfflineDownloadRecord> PlayableDownloads(string userId)
        {
            if (!OfflineDownloadsEnabled) return Array.Empty<OfflineDownloadRecord>();
            lock (_gate)
            {
                return _records.Values
                    .Where(r => string.Equals(r.UserId, userId, StringComparison.Ordinal) && r.IsPlayable)
                    .OrderByDescending(r => r.DownloadedAtMs)
                    .ToList();
            }
        }

        public IReadOnlyList<OfflineDownloadRecord> AllDownloads(string? userId = null)
        {
            lock (_gate)
            {
                return _records.Values
                    .Where(r => userId == null || string.Equals(r.UserId, userId, StringComparison.Ordinal))
                    .OrderByDescending(r => r.DownloadedAtMs)
                    .ToList();
            }
        }

        public long TotalUsedBytes()
        {
            lock (_gate)
            {
                return _records.Values.Sum(r => r.Bytes);
            }
        }

        public bool Remove(string userId, string videoId)
        {
            lock (_gate)
            {
                var key = RecordKey(userId, videoId);
                if (!_records.TryGetValue(key, out var removed)) return false;
                _records.Remove(key);
                DeleteQuietly(Path.Combine(_rootDir, removed.FileName));
                if (!string.IsNullOrEmpty(removed.ThumbnailPath)) DeleteQuietly(removed.ThumbnailPath!);
                PersistManifestLocked();
                return true;
            }
        }

        /// <summary>
        /// Applies hygiene availability sync decisions from `POST /api/v1/downloads/sync`.
        /// Deletes local copies for videos that went private, were deleted, or had downloads disabled.
        /// </summary>
        public int ApplyAvailabilitySync(IEnumerable<AvailabilitySyncDecision> decisions)
        {
            lock (_gate)
            {
                var deleted = 0;
                foreach (var decision in decisions)
                {
                    if (decision.Available) continue;
                    var matching = _records
                        .Where(kv => string.Equals(kv.Value.VideoId, decision.VideoId, StringComparison.Ordinal))
                        .Select(kv => kv.Key)
                        .ToList();
                    foreach (var key in matching)
                    {
                        var rec = _records[key];
                        _records.Remove(key);
                        DeleteQuietly(Path.Combine(_rootDir, rec.FileName));
                        if (!string.IsNullOrEmpty(rec.ThumbnailPath)) DeleteQuietly(rec.ThumbnailPath!);
                        deleted++;
                    }
                }
                if (deleted > 0) PersistManifestLocked();
                return deleted;
            }
        }

        public int ClearForUser(string userId)
        {
            lock (_gate)
            {
                var keys = _records
                    .Where(kv => string.Equals(kv.Value.UserId, userId, StringComparison.Ordinal))
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var key in keys)
                {
                    var rec = _records[key];
                    _records.Remove(key);
                    DeleteQuietly(Path.Combine(_rootDir, rec.FileName));
                    if (!string.IsNullOrEmpty(rec.ThumbnailPath)) DeleteQuietly(rec.ThumbnailPath!);
                }
                PersistManifestLocked();
                return keys.Count;
            }
        }

        public void WipeAll()
        {
            lock (_gate)
            {
                foreach (var rec in _records.Values)
                {
                    DeleteQuietly(Path.Combine(_rootDir, rec.FileName));
                    if (!string.IsNullOrEmpty(rec.ThumbnailPath)) DeleteQuietly(rec.ThumbnailPath!);
                }
                _records.Clear();
                DeleteQuietly(_manifestPath);
            }
        }

        private void PurgeInactiveLocked()
        {
            var keys = _records.Where(kv => !kv.Value.IsPlayable).Select(kv => kv.Key).ToList();
            foreach (var key in keys)
            {
                var rec = _records[key];
                _records.Remove(key);
                DeleteQuietly(Path.Combine(_rootDir, rec.FileName));
            }
        }

        private void LoadManifest()
        {
            if (!File.Exists(_manifestPath)) return;
            try
            {
                var json = File.ReadAllText(_manifestPath);
                var env = JsonSerializer.Deserialize<ManifestEnvelope>(json);
                if (env?.Items == null) return;
                foreach (var rec in env.Items)
                {
                    if (File.Exists(Path.Combine(_rootDir, rec.FileName)))
                    {
                        _records[RecordKey(rec.UserId, rec.VideoId)] = rec;
                    }
                }
            }
            catch
            {
                // Corrupt manifest starts clean rather than crashing startup.
            }
        }

        private void PersistManifestLocked()
        {
            var env = new ManifestEnvelope
            {
                Version = 3,
                Items = _records.Values.ToList(),
            };
            var tmp = _manifestPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(env));
            if (File.Exists(_manifestPath)) File.Delete(_manifestPath);
            File.Move(tmp, _manifestPath);
        }

        private static string SanitizeFileName(string raw)
        {
            var chars = raw.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray();
            return new string(chars);
        }

        private static void DeleteQuietly(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Ignore best-effort file cleanup errors.
            }
        }
    }
}
