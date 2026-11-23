using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
        Expired,
        Revoked,
    }

    public sealed class OfflineDownloadRecord
    {
        public string LicenseId { get; set; } = "";
        public string VideoId { get; set; } = "";
        public string Title { get; set; } = "";
        public string? TitleAr { get; set; }
        public string? ChannelName { get; set; }
        public double? DurationSeconds { get; set; }
        public OfflineDownloadQuality Quality { get; set; } = OfflineDownloadQuality.Audio;
        public string FileName { get; set; } = "";
        public long Bytes { get; set; }
        public long ExpiresAtMs { get; set; }
        public long DownloadedAtMs { get; set; }
        public long LastAccessedAtMs { get; set; }
        public OfflineDownloadStatus Status { get; set; } = OfflineDownloadStatus.Ready;

        public bool IsPlayable(long nowMs) => Status == OfflineDownloadStatus.Ready && ExpiresAtMs > nowMs;
    }

    public sealed class OfflineDownloadSettings
    {
        public const long DefaultMaxStorageBytes = 2L * 1024L * 1024L * 1024L;

        public bool WifiOnly { get; set; } = true;
        public long MaxStorageBytes { get; set; } = DefaultMaxStorageBytes;
        public OfflineDownloadQuality DefaultQuality { get; set; } = OfflineDownloadQuality.Audio;
        public bool AutoDownloadEnabled { get; set; }
    }

    public sealed class LicenseRefreshDecision
    {
        public LicenseRefreshDecision(string licenseId, string status, long? expiresAtMs = null, string? reason = null)
        {
            LicenseId = licenseId;
            Status = status;
            ExpiresAtMs = expiresAtMs;
            Reason = reason;
        }

        public string LicenseId { get; }
        public string Status { get; }
        public long? ExpiresAtMs { get; }
        public string? Reason { get; }
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

    public interface IDownloadKeyVault
    {
        byte[] GetOrCreateKey();
    }

    public sealed class InMemoryDownloadKeyVault : IDownloadKeyVault
    {
        private readonly byte[] _key;

        public InMemoryDownloadKeyVault(byte[]? key = null)
        {
            if (key != null)
            {
                if (key.Length != 32) throw new ArgumentException("AES-256 key must be 32 bytes.", nameof(key));
                _key = (byte[])key.Clone();
            }
            else
            {
                _key = new byte[32];
                using var rng = RandomNumberGenerator.Create();
                rng.GetBytes(_key);
            }
        }

        public byte[] GetOrCreateKey() => (byte[])_key.Clone();
    }

    /// <summary>
    /// Chunked authenticated encryption at rest for offline downloads (64 KiB chunks, per-chunk random nonce,
    /// per-chunk AAD/MAC binding videoId + licenseId + chunkIndex).
    /// Uses AES-256-GCM on .NET 8+ (Windows/Linux desktop) and Encrypt-then-MAC AES-256-CBC + HMAC-SHA256 on netstandard2.0.
    /// </summary>
    public static class ChunkedDownloadCipher
    {
        private const int ChunkBytes = 64 * 1024;
        private static readonly byte[] MagicGcm = Encoding.ASCII.GetBytes("AUN1");
        private static readonly byte[] MagicCbcHmac = Encoding.ASCII.GetBytes("AUN2");

        public static long Encrypt(byte[] key, string videoId, string licenseId, byte[] plaintext, string destinationPath)
        {
            if (key.Length != 32) throw new ArgumentException("Key must be 32 bytes.", nameof(key));
            var tmp = destinationPath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
#if NET8_0_OR_GREATER
                fs.Write(MagicGcm, 0, MagicGcm.Length);
                using var aesGcm = new AesGcm(key, 16);
                var offset = 0;
                var chunkIndex = 0;
                do
                {
                    var len = Math.Min(ChunkBytes, plaintext.Length - offset);
                    var nonce = new byte[12];
                    RandomNumberGenerator.Fill(nonce);
                    var ciphertext = new byte[len];
                    var tag = new byte[16];
                    var aad = Encoding.UTF8.GetBytes($"{videoId}|{licenseId}|{chunkIndex}");
                    aesGcm.Encrypt(nonce, plaintext.AsSpan(offset, len), ciphertext, tag, aad);
                    WriteInt32BigEndian(fs, len + 16);
                    fs.Write(nonce, 0, nonce.Length);
                    fs.Write(ciphertext, 0, ciphertext.Length);
                    fs.Write(tag, 0, tag.Length);
                    offset += len;
                    chunkIndex++;
                } while (offset < plaintext.Length);
#else
                fs.Write(MagicCbcHmac, 0, MagicCbcHmac.Length);
                var offset = 0;
                var chunkIndex = 0;
                using var rng = RandomNumberGenerator.Create();
                do
                {
                    var len = Math.Min(ChunkBytes, plaintext.Length - offset);
                    var iv = new byte[16];
                    rng.GetBytes(iv);
                    byte[] ciphertext;
                    using (var aes = Aes.Create())
                    {
                        aes.Key = key;
                        aes.IV = iv;
                        aes.Mode = CipherMode.CBC;
                        aes.Padding = PaddingMode.PKCS7;
                        using var ms = new MemoryStream();
                        using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                        {
                            cs.Write(plaintext, offset, len);
                            cs.FlushFinalBlock();
                        }
                        ciphertext = ms.ToArray();
                    }
                    var aad = Encoding.UTF8.GetBytes($"{videoId}|{licenseId}|{chunkIndex}");
                    var mac = ComputeHmac(key, aad, iv, ciphertext);
                    WriteInt32BigEndian(fs, ciphertext.Length);
                    fs.Write(iv, 0, iv.Length);
                    fs.Write(ciphertext, 0, ciphertext.Length);
                    fs.Write(mac, 0, mac.Length);
                    offset += len;
                    chunkIndex++;
                } while (offset < plaintext.Length);
#endif
            }

            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            File.Move(tmp, destinationPath);
            return new FileInfo(destinationPath).Length;
        }

        public static byte[] Decrypt(byte[] key, string videoId, string licenseId, string sourcePath)
        {
            if (key.Length != 32) throw new ArgumentException("Key must be 32 bytes.", nameof(key));
            var raw = File.ReadAllBytes(sourcePath);
            if (raw.Length < 4) throw new InvalidDataException("Invalid encrypted download header.");

#if NET8_0_OR_GREATER
            if (raw.AsSpan(0, 4).SequenceEqual(MagicGcm))
            {
                using var aesGcm = new AesGcm(key, 16);
                using var output = new MemoryStream();
                var cursor = 4;
                var chunkIndex = 0;
                while (cursor < raw.Length)
                {
                    if (cursor + 4 + 12 > raw.Length) throw new InvalidDataException("Truncated encrypted download.");
                    var combinedLen = ReadInt32BigEndian(raw, cursor);
                    cursor += 4;
                    if (combinedLen < 16 || cursor + 12 + combinedLen > raw.Length)
                        throw new InvalidDataException("Invalid encrypted chunk length.");
                    var nonce = raw.AsSpan(cursor, 12);
                    cursor += 12;
                    var ctLen = combinedLen - 16;
                    var ct = raw.AsSpan(cursor, ctLen);
                    var tag = raw.AsSpan(cursor + ctLen, 16);
                    cursor += combinedLen;
                    var plain = new byte[ctLen];
                    var aad = Encoding.UTF8.GetBytes($"{videoId}|{licenseId}|{chunkIndex}");
                    aesGcm.Decrypt(nonce, ct, tag, plain, aad);
                    output.Write(plain, 0, plain.Length);
                    chunkIndex++;
                }
                return output.ToArray();
            }
#endif
            if (raw[0] == MagicCbcHmac[0] && raw[1] == MagicCbcHmac[1] && raw[2] == MagicCbcHmac[2] && raw[3] == MagicCbcHmac[3])
            {
                using var output = new MemoryStream();
                var cursor = 4;
                var chunkIndex = 0;
                while (cursor < raw.Length)
                {
                    if (cursor + 4 + 16 + 32 > raw.Length) throw new InvalidDataException("Truncated encrypted download.");
                    var ctLen = ReadInt32BigEndian(raw, cursor);
                    cursor += 4;
                    if (ctLen <= 0 || cursor + 16 + ctLen + 32 > raw.Length)
                        throw new InvalidDataException("Invalid encrypted chunk length.");
                    var iv = new byte[16];
                    Buffer.BlockCopy(raw, cursor, iv, 0, 16);
                    cursor += 16;
                    var ct = new byte[ctLen];
                    Buffer.BlockCopy(raw, cursor, ct, 0, ctLen);
                    cursor += ctLen;
                    var mac = new byte[32];
                    Buffer.BlockCopy(raw, cursor, mac, 0, 32);
                    cursor += 32;
                    var aad = Encoding.UTF8.GetBytes($"{videoId}|{licenseId}|{chunkIndex}");
                    var expected = ComputeHmac(key, aad, iv, ct);
                    if (!FixedTimeEquals(mac, expected)) throw new CryptographicException("Authentication tag mismatch.");
                    using var aes = Aes.Create();
                    aes.Key = key;
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    using var ms = new MemoryStream(ct);
                    using var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read);
                    cs.CopyTo(output);
                    chunkIndex++;
                }
                return output.ToArray();
            }

            throw new InvalidDataException("Unsupported encrypted download format.");
        }

        private static byte[] ComputeHmac(byte[] key, byte[] aad, byte[] iv, byte[] ciphertext)
        {
            using var hmac = new HMACSHA256(key);
            using var ms = new MemoryStream(aad.Length + iv.Length + ciphertext.Length);
            ms.Write(aad, 0, aad.Length);
            ms.Write(iv, 0, iv.Length);
            ms.Write(ciphertext, 0, ciphertext.Length);
            return hmac.ComputeHash(ms.ToArray());
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static void WriteInt32BigEndian(Stream s, int value)
        {
            s.WriteByte((byte)((value >> 24) & 0xFF));
            s.WriteByte((byte)((value >> 16) & 0xFF));
            s.WriteByte((byte)((value >> 8) & 0xFF));
            s.WriteByte((byte)(value & 0xFF));
        }

        private static int ReadInt32BigEndian(byte[] buf, int offset) =>
            (buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3];
    }

    /// <summary>
    /// Encrypted local offline download store for Desktop (E14-S10).
    /// Gated by the server's `offlineDownloads` switch (defaults OFF).
    /// </summary>
    public sealed class OfflineDownloadStore
    {
        private readonly string _rootDir;
        private readonly string _manifestPath;
        private readonly IDownloadKeyVault _keyVault;
        private readonly Func<long> _clockMs;
        private readonly object _gate = new object();
        private readonly Dictionary<string, OfflineDownloadRecord> _records = new Dictionary<string, OfflineDownloadRecord>();

        public OfflineDownloadStore(
            string rootDir,
            IDownloadKeyVault keyVault,
            bool offlineDownloadsEnabled = false,
            Func<long>? clockMs = null)
        {
            _rootDir = rootDir;
            _manifestPath = Path.Combine(rootDir, "manifest.json");
            _keyVault = keyVault;
            OfflineDownloadsEnabled = offlineDownloadsEnabled;
            _clockMs = clockMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Directory.CreateDirectory(_rootDir);
            LoadManifest();
        }

        public bool OfflineDownloadsEnabled { get; set; }
        public OfflineDownloadSettings Settings { get; private set; } = new OfflineDownloadSettings();

        public void UpdateSettings(OfflineDownloadSettings settings)
        {
            lock (_gate)
            {
                Settings = settings;
                EvictToCapLocked();
                PersistManifestLocked();
            }
        }

        public OfflineDownloadRecord SaveEncrypted(
            string licenseId,
            string videoId,
            string title,
            OfflineDownloadQuality quality,
            long expiresAtMs,
            byte[] plaintext,
            string? titleAr = null,
            string? channelName = null,
            double? durationSeconds = null)
        {
            if (!OfflineDownloadsEnabled) throw new InvalidOperationException("offline_downloads_feature_off");
            lock (_gate)
            {
                var safeName = SanitizeFileName(licenseId) + ".aun";
                var dest = Path.Combine(_rootDir, safeName);
                var encryptedBytes = ChunkedDownloadCipher.Encrypt(_keyVault.GetOrCreateKey(), videoId, licenseId, plaintext, dest);
                var now = _clockMs();
                var rec = new OfflineDownloadRecord
                {
                    LicenseId = licenseId,
                    VideoId = videoId,
                    Title = title,
                    TitleAr = titleAr,
                    ChannelName = channelName,
                    DurationSeconds = durationSeconds,
                    Quality = quality,
                    FileName = safeName,
                    Bytes = encryptedBytes,
                    ExpiresAtMs = expiresAtMs,
                    DownloadedAtMs = now,
                    LastAccessedAtMs = now,
                    Status = OfflineDownloadStatus.Ready,
                };
                _records[licenseId] = rec;
                EvictToCapLocked();
                PersistManifestLocked();
                return rec;
            }
        }

        public byte[] ReadDecrypted(string licenseId)
        {
            lock (_gate)
            {
                if (!_records.TryGetValue(licenseId, out var rec))
                    throw new FileNotFoundException("Download not found.", licenseId);
                var now = _clockMs();
                if (!rec.IsPlayable(now))
                    throw new InvalidOperationException("Download license expired or revoked.");
                var plain = ChunkedDownloadCipher.Decrypt(
                    _keyVault.GetOrCreateKey(),
                    rec.VideoId,
                    rec.LicenseId,
                    Path.Combine(_rootDir, rec.FileName));
                rec.LastAccessedAtMs = now;
                PersistManifestLocked();
                return plain;
            }
        }

        public IReadOnlyList<OfflineDownloadRecord> PlayableDownloads()
        {
            if (!OfflineDownloadsEnabled) return Array.Empty<OfflineDownloadRecord>();
            lock (_gate)
            {
                var now = _clockMs();
                return _records.Values
                    .Where(r => r.IsPlayable(now))
                    .OrderByDescending(r => r.DownloadedAtMs)
                    .ToList();
            }
        }

        public IReadOnlyList<OfflineDownloadRecord> AllDownloads()
        {
            lock (_gate)
            {
                return _records.Values.OrderByDescending(r => r.DownloadedAtMs).ToList();
            }
        }

        public long TotalUsedBytes()
        {
            lock (_gate)
            {
                return _records.Values.Sum(r => r.Bytes);
            }
        }

        public bool Remove(string licenseId)
        {
            lock (_gate)
            {
                if (!_records.TryGetValue(licenseId, out var rec)) return false;
                _records.Remove(licenseId);
                DeleteQuietly(Path.Combine(_rootDir, rec.FileName));
                PersistManifestLocked();
                return true;
            }
        }

        /// <summary>
        /// Applies batch license refresh decisions from `POST /api/v1/downloads/refresh`.
        /// Revoked or expired items have their encrypted files deleted immediately.
        /// </summary>
        public int ApplyLicenseRefresh(IEnumerable<LicenseRefreshDecision> decisions)
        {
            lock (_gate)
            {
                var deleted = 0;
                foreach (var d in decisions)
                {
                    if (!_records.TryGetValue(d.LicenseId, out var existing)) continue;
                    if (string.Equals(d.Status, "active", StringComparison.OrdinalIgnoreCase) && d.ExpiresAtMs.HasValue)
                    {
                        existing.ExpiresAtMs = d.ExpiresAtMs.Value;
                        existing.Status = OfflineDownloadStatus.Ready;
                    }
                    else
                    {
                        _records.Remove(d.LicenseId);
                        DeleteQuietly(Path.Combine(_rootDir, existing.FileName));
                        deleted++;
                    }
                }
                PersistManifestLocked();
                return deleted;
            }
        }

        /// <summary>Wipes every local encrypted download and manifest on sign-out or account deletion.</summary>
        public void WipeAll()
        {
            lock (_gate)
            {
                foreach (var rec in _records.Values)
                {
                    DeleteQuietly(Path.Combine(_rootDir, rec.FileName));
                }
                _records.Clear();
                DeleteQuietly(_manifestPath);
            }
        }

        private void EvictToCapLocked()
        {
            var cap = Settings.MaxStorageBytes;
            if (cap <= 0) return;
            var used = _records.Values.Sum(r => r.Bytes);
            if (used <= cap) return;
            foreach (var victim in _records.Values.OrderBy(r => r.LastAccessedAtMs).ToList())
            {
                if (used <= cap) break;
                _records.Remove(victim.LicenseId);
                DeleteQuietly(Path.Combine(_rootDir, victim.FileName));
                used -= victim.Bytes;
            }
        }

        private void LoadManifest()
        {
            if (!File.Exists(_manifestPath)) return;
            try
            {
                var list = JsonSerializer.Deserialize<List<OfflineDownloadRecord>>(File.ReadAllText(_manifestPath));
                if (list == null) return;
                foreach (var rec in list)
                {
                    if (File.Exists(Path.Combine(_rootDir, rec.FileName)))
                    {
                        _records[rec.LicenseId] = rec;
                    }
                }
            }
            catch
            {
                // Corrupt manifest is ignored cleanly.
            }
        }

        private void PersistManifestLocked()
        {
            var tmp = _manifestPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_records.Values.ToList()));
            if (File.Exists(_manifestPath)) File.Delete(_manifestPath);
            File.Move(tmp, _manifestPath);
        }

        private static string SanitizeFileName(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
            }
            return sb.ToString();
        }

        private static void DeleteQuietly(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
