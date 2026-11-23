using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AlUmmahNow.CoreBridge;
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
        Expired,
        Revoked,
    }

    /// <summary>
    /// Local encrypted download record. Never persists signed URLs (artwork is a local file path only).
    /// </summary>
    public sealed class OfflineDownloadRecord
    {
        public string UserId { get; set; } = "default";
        public string LicenseId { get; set; } = "";
        public string VideoId { get; set; } = "";
        public string Title { get; set; } = "";
        public string? TitleAr { get; set; }
        public string? ChannelName { get; set; }
        public string? ThumbnailPath { get; set; }
        public double? DurationSeconds { get; set; }
        public OfflineDownloadQuality Quality { get; set; } = OfflineDownloadQuality.Audio;
        public string FileName { get; set; } = "";
        public long Bytes { get; set; }
        public long ExpiresAtMs { get; set; }
        public long DownloadedAtMs { get; set; }
        public long LastAccessedAtMs { get; set; }
        public OfflineDownloadStatus Status { get; set; } = OfflineDownloadStatus.Ready;

        public bool IsPlayable(long effectiveNowMs) =>
            Status == OfflineDownloadStatus.Ready && ExpiresAtMs > effectiveNowMs;
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

    /// <summary>Releases server license slots (`DELETE /api/v1/downloads/:licenseId` and `revoke-device`).</summary>
    public interface ILicenseSlotReleaser
    {
        bool ReleaseLicense(string licenseId);
        bool RevokeDevice(string deviceId);
    }

    public sealed class HttpLicenseSlotReleaser : ILicenseSlotReleaser
    {
        private readonly HttpClient _http;
        private readonly string _apiBaseUrl;
        private readonly Func<string?> _bearerToken;

        public HttpLicenseSlotReleaser(HttpClient http, string apiBaseUrl, Func<string?> bearerToken)
        {
            _http = http;
            _apiBaseUrl = apiBaseUrl.TrimEnd('/');
            _bearerToken = bearerToken;
        }

        public bool ReleaseLicense(string licenseId)
        {
            var token = _bearerToken();
            if (string.IsNullOrWhiteSpace(token)) return false;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Delete, $"{_apiBaseUrl}/api/v1/downloads/{Uri.EscapeDataString(licenseId)}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var res = _http.SendAsync(req).GetAwaiter().GetResult();
                return res.IsSuccessStatusCode || (int)res.StatusCode == 404;
            }
            catch
            {
                return false;
            }
        }

        public bool RevokeDevice(string deviceId)
        {
            var token = _bearerToken();
            if (string.IsNullOrWhiteSpace(token)) return false;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{_apiBaseUrl}/api/v1/downloads/revoke-device")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { deviceId }), Encoding.UTF8, "application/json"),
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var res = _http.SendAsync(req).GetAwaiter().GetResult();
                return res.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
    }

    public interface IDownloadKeyVault
    {
        byte[] GetOrCreateKey();
    }

    /// <summary>
    /// OS-backed key vault delegating to <see cref="ISecureStore"/> (Linux `libsecret` via `LibSecretStore`
    /// or Windows DPAPI via `DpapiSecureStore`). Keys survive restarts and never touch disk in plaintext.
    /// </summary>
    public sealed class SecureStoreDownloadKeyVault : IDownloadKeyVault
    {
        public const string DefaultKeyName = "offline_download_aes256_key";
        private readonly ISecureStore _store;
        private readonly string _keyName;
        private readonly object _gate = new object();

        public SecureStoreDownloadKeyVault(ISecureStore store, string keyName = DefaultKeyName)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _keyName = keyName;
        }

        public byte[] GetOrCreateKey()
        {
            lock (_gate)
            {
                var existing = _store.Get(_keyName);
                if (existing != null && existing.Length == 32) return existing;
                var fresh = new byte[32];
                using var rng = RandomNumberGenerator.Create();
                rng.GetBytes(fresh);
                _store.Set(_keyName, fresh);
                return (byte[])fresh.Clone();
            }
        }
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
    /// Shared `AUNDL01` chunked AES-256-GCM binary format matching `AlUmmahNowCore` (`crates/aun-downloads`):
    /// - Header: `MAGIC("AUNDL01\0")` (8 B) + base nonce (8 B) + `u16 BE videoIdLen` + `videoId`
    ///   + `u16 BE licenseIdLen` + `licenseId`
    /// - Fixed 64 KiB plaintext chunks + 16 B GCM tag (`65552` B stride), `is_final` flag in AAD.
    /// - Opening <see cref="AunDl01DecryptStream"/> verifies header binding and authenticates the final
    ///   chunk immediately so truncated or swapped files fail before playback starts.
    /// </summary>
    public static class AunDl01Format
    {
        public static readonly byte[] Magic = { (byte)'A', (byte)'U', (byte)'N', (byte)'D', (byte)'L', (byte)'0', (byte)'1', 0 };
        public const int ChunkSize = 64 * 1024;
        public const int TagLen = 16;
        public const int ChunkStride = ChunkSize + TagLen;
        public const int BaseNonceLen = 8;
        internal const int MaxIdLen = 512;

        internal static void ValidateBinding(string videoId, string licenseId)
        {
            var vidLen = Encoding.UTF8.GetByteCount(videoId ?? "");
            var licLen = Encoding.UTF8.GetByteCount(licenseId ?? "");
            if (vidLen == 0 || vidLen > MaxIdLen) throw new InvalidDataException("Invalid video_id for AAD binding.");
            if (licLen == 0 || licLen > MaxIdLen) throw new InvalidDataException("Invalid license_id for AAD binding.");
        }

        internal static byte[] ChunkNonce(byte[] prefix, uint index)
        {
            var nonce = new byte[12];
            Buffer.BlockCopy(prefix, 0, nonce, 0, BaseNonceLen);
            nonce[8] = (byte)((index >> 24) & 0xFF);
            nonce[9] = (byte)((index >> 16) & 0xFF);
            nonce[10] = (byte)((index >> 8) & 0xFF);
            nonce[11] = (byte)(index & 0xFF);
            return nonce;
        }

        internal static byte[] BuildAad(string videoId, string licenseId, uint index, bool isFinal)
        {
            var vid = Encoding.UTF8.GetBytes(videoId);
            var lic = Encoding.UTF8.GetBytes(licenseId);
            var aad = new byte[Magic.Length + 2 + vid.Length + 2 + lic.Length + 4 + 1];
            var pos = 0;
            Buffer.BlockCopy(Magic, 0, aad, pos, Magic.Length); pos += Magic.Length;
            aad[pos++] = (byte)((vid.Length >> 8) & 0xFF);
            aad[pos++] = (byte)(vid.Length & 0xFF);
            Buffer.BlockCopy(vid, 0, aad, pos, vid.Length); pos += vid.Length;
            aad[pos++] = (byte)((lic.Length >> 8) & 0xFF);
            aad[pos++] = (byte)(lic.Length & 0xFF);
            Buffer.BlockCopy(lic, 0, aad, pos, lic.Length); pos += lic.Length;
            aad[pos++] = (byte)((index >> 24) & 0xFF);
            aad[pos++] = (byte)((index >> 16) & 0xFF);
            aad[pos++] = (byte)((index >> 8) & 0xFF);
            aad[pos++] = (byte)(index & 0xFF);
            aad[pos] = isFinal ? (byte)1 : (byte)0;
            return aad;
        }
    }

    /// <summary>Single-pass streaming `AUNDL01` encryptor holding at most ~64 KiB of plaintext in RAM.</summary>
    public sealed class AunDl01StreamWriter : IDisposable
    {
#if NET8_0_OR_GREATER
        private readonly AesGcm _aesGcm;
        private readonly byte[] _prefix = new byte[AunDl01Format.BaseNonceLen];
        private readonly string _videoId;
        private readonly string _licenseId;
        private readonly string _tmpPath;
        private readonly string _destPath;
        private readonly FileStream _fs;
        private readonly byte[] _pending = new byte[AunDl01Format.ChunkSize + 1];
        private int _pendingLen;
        private uint _chunkIndex;
        private bool _finished;

        public AunDl01StreamWriter(byte[] key, string destinationPath, string videoId, string licenseId)
        {
            if (key.Length != 32) throw new ArgumentException("AES-256 key must be 32 bytes.", nameof(key));
            AunDl01Format.ValidateBinding(videoId, licenseId);
            _aesGcm = new AesGcm(key, AunDl01Format.TagLen);
            RandomNumberGenerator.Fill(_prefix);
            _videoId = videoId;
            _licenseId = licenseId;
            _destPath = destinationPath;
            _tmpPath = destinationPath + ".aun.tmp";
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _fs = new FileStream(_tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var vid = Encoding.UTF8.GetBytes(videoId);
            var lic = Encoding.UTF8.GetBytes(licenseId);
            _fs.Write(AunDl01Format.Magic, 0, AunDl01Format.Magic.Length);
            _fs.Write(_prefix, 0, _prefix.Length);
            _fs.WriteByte((byte)((vid.Length >> 8) & 0xFF));
            _fs.WriteByte((byte)(vid.Length & 0xFF));
            _fs.Write(vid, 0, vid.Length);
            _fs.WriteByte((byte)((lic.Length >> 8) & 0xFF));
            _fs.WriteByte((byte)(lic.Length & 0xFF));
            _fs.Write(lic, 0, lic.Length);
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            if (_finished) throw new ObjectDisposedException(nameof(AunDl01StreamWriter));
            var pos = offset;
            var remaining = count;
            while (remaining > 0)
            {
                var take = Math.Min(remaining, _pending.Length - _pendingLen);
                Buffer.BlockCopy(buffer, pos, _pending, _pendingLen, take);
                _pendingLen += take;
                pos += take;
                remaining -= take;
                if (_pendingLen > AunDl01Format.ChunkSize)
                {
                    SealAndWriteChunk(AunDl01Format.ChunkSize, isFinal: false);
                    var leftover = _pendingLen - AunDl01Format.ChunkSize;
                    Buffer.BlockCopy(_pending, AunDl01Format.ChunkSize, _pending, 0, leftover);
                    _pendingLen = leftover;
                }
            }
        }

        public long Finish()
        {
            if (_finished) throw new ObjectDisposedException(nameof(AunDl01StreamWriter));
            SealAndWriteChunk(_pendingLen, isFinal: true);
            Array.Clear(_pending, 0, _pending.Length);
            _pendingLen = 0;
            _fs.Flush(true);
            _fs.Dispose();
            _aesGcm.Dispose();
            if (File.Exists(_destPath)) File.Delete(_destPath);
            File.Move(_tmpPath, _destPath);
            _finished = true;
            return new FileInfo(_destPath).Length;
        }

        private void SealAndWriteChunk(int len, bool isFinal)
        {
            var nonce = AunDl01Format.ChunkNonce(_prefix, _chunkIndex);
            var aad = AunDl01Format.BuildAad(_videoId, _licenseId, _chunkIndex, isFinal);
            var ct = new byte[len];
            var tag = new byte[AunDl01Format.TagLen];
            _aesGcm.Encrypt(nonce, _pending.AsSpan(0, len), ct, tag, aad);
            _fs.Write(ct, 0, ct.Length);
            _fs.Write(tag, 0, tag.Length);
            _chunkIndex++;
        }

        public void Dispose()
        {
            if (!_finished)
            {
                Array.Clear(_pending, 0, _pending.Length);
                try { _fs.Dispose(); } catch { }
                try { _aesGcm.Dispose(); } catch { }
                try { if (File.Exists(_tmpPath)) File.Delete(_tmpPath); } catch { }
                _finished = true;
            }
        }
#else
        public AunDl01StreamWriter(byte[] key, string destinationPath, string videoId, string licenseId) =>
            throw new PlatformNotSupportedException("Offline encrypted downloads require .NET 8+ (Windows/Linux Desktop).");
        public void Write(byte[] buffer, int offset, int count) => throw new PlatformNotSupportedException();
        public long Finish() => throw new PlatformNotSupportedException();
        public void Dispose() { }
#endif

        public static long EncryptStream(byte[] key, string videoId, string licenseId, Stream input, string destinationPath)
        {
            using var writer = new AunDl01StreamWriter(key, destinationPath, videoId, licenseId);
            var buf = new byte[AunDl01Format.ChunkSize];
            int n;
            while ((n = input.Read(buf, 0, buf.Length)) > 0)
            {
                writer.Write(buf, 0, n);
            }
            return writer.Finish();
        }
    }

    /// <summary>
    /// Seekable, O(1) random-access chunked decrypt-on-read <see cref="Stream"/> for `AUNDL01` files.
    /// Decrypts only the 64 KiB chunks needed for each read; zero plaintext temp files and zero
    /// whole-file memory allocations.
    /// </summary>
    public sealed class AunDl01DecryptStream : Stream
    {
#if NET8_0_OR_GREATER
        private readonly AesGcm _aesGcm;
        private readonly FileStream _fs;
        private readonly byte[] _prefix;
        private readonly string _videoId;
        private readonly string _licenseId;
        private readonly long _dataOffset;
        private readonly long _plainLen;
        private readonly uint _numChunks;
        private long _position;
        private long _cachedIndex = -1;
        private byte[] _cachedPlain = Array.Empty<byte>();

        private AunDl01DecryptStream(
            AesGcm aesGcm,
            FileStream fs,
            byte[] prefix,
            string videoId,
            string licenseId,
            long dataOffset,
            long plainLen,
            uint numChunks)
        {
            _aesGcm = aesGcm;
            _fs = fs;
            _prefix = prefix;
            _videoId = videoId;
            _licenseId = licenseId;
            _dataOffset = dataOffset;
            _plainLen = plainLen;
            _numChunks = numChunks;
        }

        public static AunDl01DecryptStream Open(byte[] key, string path, string expectedVideoId, string expectedLicenseId)
        {
            if (key.Length != 32) throw new ArgumentException("AES-256 key must be 32 bytes.", nameof(key));
            AunDl01Format.ValidateBinding(expectedVideoId, expectedLicenseId);
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var aesGcm = new AesGcm(key, AunDl01Format.TagLen);
            try
            {
                var fileLen = fs.Length;
                if (fileLen < 16) throw new InvalidDataException("Truncated AUNDL01 header.");
                var fixedHdr = new byte[16];
                ReadExact(fs, fixedHdr, 0, 16);
                if (!fixedHdr.AsSpan(0, 8).SequenceEqual(AunDl01Format.Magic))
                    throw new InvalidDataException("Invalid AUNDL01 magic.");
                var prefix = fixedHdr.AsSpan(8, 8).ToArray();

                var headerVideoId = ReadU16String(fs);
                var headerLicenseId = ReadU16String(fs);
                if (!string.Equals(headerVideoId, expectedVideoId, StringComparison.Ordinal) ||
                    !string.Equals(headerLicenseId, expectedLicenseId, StringComparison.Ordinal))
                {
                    throw new CryptographicException("File binding does not match expected videoId/licenseId.");
                }

                var dataOffset = fs.Position;
                if (fileLen < dataOffset + AunDl01Format.TagLen)
                    throw new InvalidDataException("Truncated AUNDL01 payload.");
                var payloadLen = fileLen - dataOffset;
                long stride = AunDl01Format.ChunkStride;
                var numChunks64 = ((payloadLen - 1L) / stride) + 1L;
                var lastFrameLen = payloadLen - (numChunks64 - 1L) * stride;
                if (lastFrameLen < AunDl01Format.TagLen || lastFrameLen > stride)
                    throw new InvalidDataException("Truncated final chunk.");
                if (numChunks64 > 1L && lastFrameLen == AunDl01Format.TagLen)
                    throw new InvalidDataException("Invalid empty trailing chunk.");
                var plainLen = (numChunks64 - 1L) * AunDl01Format.ChunkSize + (lastFrameLen - AunDl01Format.TagLen);

                var stream = new AunDl01DecryptStream(
                    aesGcm, fs, prefix, headerVideoId, headerLicenseId, dataOffset, plainLen, (uint)numChunks64);
                // Immediately authenticate final chunk so chunk-boundary truncation or wrong key fails on Open.
                stream.LoadChunk((uint)(numChunks64 - 1L));
                return stream;
            }
            catch
            {
                aesGcm.Dispose();
                fs.Dispose();
                throw;
            }
        }

        private byte[] LoadChunk(uint index)
        {
            if (_cachedIndex == index) return _cachedPlain;
            if (index >= _numChunks) throw new InvalidDataException("Chunk index out of bounds.");
            var isFinal = index == _numChunks - 1;
            var chunkPlainLen = isFinal
                ? (int)(_plainLen - (long)index * AunDl01Format.ChunkSize)
                : AunDl01Format.ChunkSize;
            var frameLen = chunkPlainLen + AunDl01Format.TagLen;
            _fs.Seek(_dataOffset + (long)index * AunDl01Format.ChunkStride, SeekOrigin.Begin);
            var frame = new byte[frameLen];
            ReadExact(_fs, frame, 0, frameLen);
            var plain = new byte[chunkPlainLen];
            var nonce = AunDl01Format.ChunkNonce(_prefix, index);
            var aad = AunDl01Format.BuildAad(_videoId, _licenseId, index, isFinal);
            _aesGcm.Decrypt(nonce, frame.AsSpan(0, chunkPlainLen), frame.AsSpan(chunkPlainLen, AunDl01Format.TagLen), plain, aad);
            Array.Clear(_cachedPlain, 0, _cachedPlain.Length);
            _cachedPlain = plain;
            _cachedIndex = index;
            return _cachedPlain;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _plainLen;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count <= 0 || _position >= _plainLen) return 0;
            var toRead = (int)Math.Min(count, _plainLen - _position);
            var written = 0;
            while (written < toRead)
            {
                var idx = (uint)(_position / AunDl01Format.ChunkSize);
                var inChunk = (int)(_position % AunDl01Format.ChunkSize);
                var chunk = LoadChunk(idx);
                var take = Math.Min(chunk.Length - inChunk, toRead - written);
                Buffer.BlockCopy(chunk, inChunk, buffer, offset + written, take);
                _position += take;
                written += take;
            }
            return written;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _plainLen + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (next < 0) throw new IOException("Cannot seek before beginning of stream.");
            _position = next;
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Array.Clear(_cachedPlain, 0, _cachedPlain.Length);
                _aesGcm.Dispose();
                _fs.Dispose();
            }
            base.Dispose(disposing);
        }

        private static void ReadExact(Stream s, byte[] buf, int offset, int count)
        {
            var read = 0;
            while (read < count)
            {
                var n = s.Read(buf, offset + read, count - read);
                if (n <= 0) throw new EndOfStreamException("Unexpected EOF in AUNDL01 stream.");
                read += n;
            }
        }

        private static string ReadU16String(Stream s)
        {
            var lenBuf = new byte[2];
            ReadExact(s, lenBuf, 0, 2);
            var len = (lenBuf[0] << 8) | lenBuf[1];
            if (len <= 0 || len > AunDl01Format.MaxIdLen) throw new InvalidDataException("Invalid AUNDL01 header id length.");
            var bytes = new byte[len];
            ReadExact(s, bytes, 0, len);
            return Encoding.UTF8.GetString(bytes);
        }
#else
        public static AunDl01DecryptStream Open(byte[] key, string path, string expectedVideoId, string expectedLicenseId) =>
            throw new PlatformNotSupportedException("Offline encrypted downloads require .NET 8+ (Windows/Linux Desktop).");
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new PlatformNotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new PlatformNotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new PlatformNotSupportedException();
        public override void SetLength(long value) => throw new PlatformNotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new PlatformNotSupportedException();
#endif
    }

    /// <summary>
    /// Encrypted local offline download store for Desktop (E14-S10):
    /// - Shared `AUNDL01` streaming encryption + seekable chunked decrypt-on-read stream.
    /// - Keyed by `(UserId, VideoId)`, forbids signed URL persistence, and uses OS key vaults.
    /// - Rollback-safe expiry using server time + monotonic clock + persisted high-water mark.
    /// - Releases server slots on delete/revocation/wipe and never silently evicts active downloads.
    /// </summary>
    public sealed class OfflineDownloadStore
    {
        private sealed class ManifestEnvelope
        {
            public int Version { get; set; } = 2;
            public long HighWaterNowMs { get; set; }
            public List<string> PendingSlotReleases { get; set; } = new List<string>();
            public List<OfflineDownloadRecord> Items { get; set; } = new List<OfflineDownloadRecord>();
        }

        private readonly string _rootDir;
        private readonly string _manifestPath;
        private readonly IDownloadKeyVault _keyVault;
        private readonly Func<long> _wallClockMs;
        private readonly Func<long> _monotonicClockMs;
        private readonly ILicenseSlotReleaser? _slotReleaser;
        private readonly string _deviceId;
        private readonly object _gate = new object();
        private readonly Dictionary<string, OfflineDownloadRecord> _records = new Dictionary<string, OfflineDownloadRecord>(StringComparer.Ordinal);
        private readonly HashSet<string> _pendingSlotReleases = new HashSet<string>(StringComparer.Ordinal);
        private long _highWaterNowMs;
        private (long ServerMs, long MonoMs)? _serverClockAnchor;

        public OfflineDownloadStore(
            string rootDir,
            IDownloadKeyVault keyVault,
            bool offlineDownloadsEnabled = false,
            Func<long>? wallClockMs = null,
            Func<long>? monotonicClockMs = null,
            ILicenseSlotReleaser? slotReleaser = null,
            string deviceId = "desktop-device")
        {
            _rootDir = rootDir;
            _manifestPath = Path.Combine(rootDir, "manifest.json");
            _keyVault = keyVault;
            OfflineDownloadsEnabled = offlineDownloadsEnabled;
            _wallClockMs = wallClockMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _monotonicClockMs = monotonicClockMs ?? DefaultMonotonicMs;
            _slotReleaser = slotReleaser;
            _deviceId = deviceId;
            Directory.CreateDirectory(_rootDir);
            LoadManifest();
        }

        private static long DefaultMonotonicMs()
        {
#if NET8_0_OR_GREATER
            return Environment.TickCount64;
#else
            return System.Diagnostics.Stopwatch.GetTimestamp() * 1000L / System.Diagnostics.Stopwatch.Frequency;
#endif
        }

        public bool OfflineDownloadsEnabled { get; set; }
        public OfflineDownloadSettings Settings { get; private set; } = new OfflineDownloadSettings();

        /// <summary>Wires `offlineDownloads` directly from `GET /api/me/features`.</summary>
        public void ApplyFeatures(UserFeatures features) =>
            OfflineDownloadsEnabled = features.IsOn(UserFeatures.OfflineDownloads);

        private static string RecordKey(string userId, string videoId) => $"{userId}\u001F{videoId}";

        public long ObserveServerTime(long serverTimeMs)
        {
            lock (_gate)
            {
                return ObserveServerTimeLocked(serverTimeMs);
            }
        }

        private long ObserveServerTimeLocked(long serverTimeMs)
        {
            _serverClockAnchor = (serverTimeMs, _monotonicClockMs());
            if (serverTimeMs > _highWaterNowMs)
            {
                _highWaterNowMs = serverTimeMs;
                PersistManifestLocked();
            }
            return _highWaterNowMs;
        }

        private long EffectiveNowMsLocked()
        {
            var effective = Math.Max(_wallClockMs(), _highWaterNowMs);
            if (_serverClockAnchor.HasValue)
            {
                var mono = _monotonicClockMs();
                if (mono >= _serverClockAnchor.Value.MonoMs)
                {
                    effective = Math.Max(effective, _serverClockAnchor.Value.ServerMs + (mono - _serverClockAnchor.Value.MonoMs));
                }
            }
            if (effective > _highWaterNowMs)
            {
                _highWaterNowMs = effective;
                PersistManifestLocked();
            }
            return effective;
        }

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
            string licenseId,
            string videoId,
            string title,
            OfflineDownloadQuality quality,
            long serverTimeMs,
            long expiresAtMs,
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
                ObserveServerTimeLocked(serverTimeMs);
                PurgeInactiveLocked();

                var cap = Settings.MaxStorageBytes;
                var used = _records.Values.Sum(r => r.Bytes);
                if (cap > 0 && expectedBytes > 0 && used + expectedBytes > cap)
                    throw new StorageBudgetExceededException(used, expectedBytes, cap);

                var safeName = SanitizeFileName($"{userId}_{licenseId}") + ".aun";
                var dest = Path.Combine(_rootDir, safeName);
                var encryptedBytes = AunDl01StreamWriter.EncryptStream(_keyVault.GetOrCreateKey(), videoId, licenseId, input, dest);
                if (cap > 0 && used + encryptedBytes > cap)
                {
                    DeleteQuietly(dest);
                    throw new StorageBudgetExceededException(used, encryptedBytes, cap);
                }

                var key = RecordKey(userId, videoId);
                if (_records.TryGetValue(key, out var prev))
                {
                    if (!string.Equals(prev.FileName, safeName, StringComparison.Ordinal))
                        DeleteQuietly(Path.Combine(_rootDir, prev.FileName));
                    if (!string.Equals(prev.LicenseId, licenseId, StringComparison.Ordinal))
                        ReleaseServerSlotLocked(prev.LicenseId);
                }
                var now = EffectiveNowMsLocked();
                var rec = new OfflineDownloadRecord
                {
                    UserId = userId,
                    LicenseId = licenseId,
                    VideoId = videoId,
                    Title = title,
                    TitleAr = titleAr,
                    ChannelName = channelName,
                    ThumbnailPath = thumbnailPath,
                    DurationSeconds = durationSeconds,
                    Quality = quality,
                    FileName = safeName,
                    Bytes = encryptedBytes,
                    ExpiresAtMs = expiresAtMs,
                    DownloadedAtMs = now,
                    LastAccessedAtMs = now,
                    Status = OfflineDownloadStatus.Ready,
                };
                _records[key] = rec;
                PersistManifestLocked();
                return rec;
            }
        }

        /// <summary>Opens a seekable chunked decrypt-on-read stream (zero plaintext temp files, zero full-file RAM).</summary>
        public AunDl01DecryptStream OpenDecryptStream(string userId, string videoId)
        {
            lock (_gate)
            {
                var key = RecordKey(userId, videoId);
                if (!_records.TryGetValue(key, out var rec))
                    throw new FileNotFoundException("Download not found.", videoId);
                var now = EffectiveNowMsLocked();
                if (!rec.IsPlayable(now))
                    throw new InvalidOperationException("Download license expired or revoked.");
                var stream = AunDl01DecryptStream.Open(
                    _keyVault.GetOrCreateKey(),
                    Path.Combine(_rootDir, rec.FileName),
                    rec.VideoId,
                    rec.LicenseId);
                rec.LastAccessedAtMs = now;
                PersistManifestLocked();
                return stream;
            }
        }

        public IReadOnlyList<OfflineDownloadRecord> PlayableDownloads(string userId)
        {
            if (!OfflineDownloadsEnabled) return Array.Empty<OfflineDownloadRecord>();
            lock (_gate)
            {
                var now = EffectiveNowMsLocked();
                return _records.Values
                    .Where(r => string.Equals(r.UserId, userId, StringComparison.Ordinal) && r.IsPlayable(now))
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

        public IReadOnlyCollection<string> PendingServerSlotReleases()
        {
            lock (_gate)
            {
                return _pendingSlotReleases.ToArray();
            }
        }

        public bool Remove(string userId, string videoId)
        {
            lock (_gate)
            {
                var key = RecordKey(userId, videoId);
                if (!_records.TryGetValue(key, out var rec)) return false;
                _records.Remove(key);
                DeleteQuietly(Path.Combine(_rootDir, rec.FileName));
                ReleaseServerSlotLocked(rec.LicenseId);
                PersistManifestLocked();
                return true;
            }
        }

        public int ApplyLicenseRefresh(IEnumerable<LicenseRefreshDecision> decisions, long serverTimeMs)
        {
            lock (_gate)
            {
                ObserveServerTimeLocked(serverTimeMs);
                var deleted = 0;
                foreach (var d in decisions)
                {
                    var match = _records.FirstOrDefault(kv => string.Equals(kv.Value.LicenseId, d.LicenseId, StringComparison.Ordinal));
                    if (match.Value == null) continue;
                    if (string.Equals(d.Status, "active", StringComparison.OrdinalIgnoreCase) && d.ExpiresAtMs.HasValue)
                    {
                        match.Value.ExpiresAtMs = d.ExpiresAtMs.Value;
                        match.Value.Status = OfflineDownloadStatus.Ready;
                    }
                    else
                    {
                        _records.Remove(match.Key);
                        DeleteQuietly(Path.Combine(_rootDir, match.Value.FileName));
                        ReleaseServerSlotLocked(match.Value.LicenseId);
                        deleted++;
                    }
                }
                FlushPendingSlotReleasesLocked();
                PersistManifestLocked();
                return deleted;
            }
        }

        public int FlushPendingSlotReleases()
        {
            lock (_gate)
            {
                var flushed = FlushPendingSlotReleasesLocked();
                if (flushed > 0) PersistManifestLocked();
                return flushed;
            }
        }

        public int ClearForUser(string userId)
        {
            lock (_gate)
            {
                var keys = _records.Where(kv => string.Equals(kv.Value.UserId, userId, StringComparison.Ordinal)).Select(kv => kv.Key).ToList();
                foreach (var key in keys)
                {
                    var rec = _records[key];
                    _records.Remove(key);
                    DeleteQuietly(Path.Combine(_rootDir, rec.FileName));
                    ReleaseServerSlotLocked(rec.LicenseId);
                }
                try { _slotReleaser?.RevokeDevice(_deviceId); } catch { }
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
                    ReleaseServerSlotLocked(rec.LicenseId);
                }
                _records.Clear();
                try { _slotReleaser?.RevokeDevice(_deviceId); } catch { }
                if (_pendingSlotReleases.Count == 0)
                {
                    DeleteQuietly(_manifestPath);
                }
                else
                {
                    PersistManifestLocked();
                }
            }
        }

        private void ReleaseServerSlotLocked(string licenseId)
        {
            if (_slotReleaser == null) return;
            var released = false;
            try { released = _slotReleaser.ReleaseLicense(licenseId); } catch { released = false; }
            if (!released) _pendingSlotReleases.Add(licenseId);
        }

        private int FlushPendingSlotReleasesLocked()
        {
            if (_slotReleaser == null || _pendingSlotReleases.Count == 0) return 0;
            var flushed = 0;
            foreach (var id in _pendingSlotReleases.ToList())
            {
                var ok = false;
                try { ok = _slotReleaser.ReleaseLicense(id); } catch { ok = false; }
                if (ok)
                {
                    _pendingSlotReleases.Remove(id);
                    flushed++;
                }
            }
            return flushed;
        }

        private void PurgeInactiveLocked()
        {
            var now = EffectiveNowMsLocked();
            var inactive = _records.Where(kv => !kv.Value.IsPlayable(now)).Select(kv => kv.Key).ToList();
            foreach (var key in inactive)
            {
                var rec = _records[key];
                _records.Remove(key);
                DeleteQuietly(Path.Combine(_rootDir, rec.FileName));
                ReleaseServerSlotLocked(rec.LicenseId);
            }
        }

        private void LoadManifest()
        {
            if (!File.Exists(_manifestPath)) return;
            try
            {
                var env = JsonSerializer.Deserialize<ManifestEnvelope>(File.ReadAllText(_manifestPath));
                if (env == null) return;
                _highWaterNowMs = env.HighWaterNowMs;
                foreach (var id in env.PendingSlotReleases) _pendingSlotReleases.Add(id);
                foreach (var rec in env.Items)
                {
                    if (File.Exists(Path.Combine(_rootDir, rec.FileName)))
                    {
                        _records[RecordKey(rec.UserId, rec.VideoId)] = rec;
                        _highWaterNowMs = Math.Max(_highWaterNowMs, rec.DownloadedAtMs);
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
            var env = new ManifestEnvelope
            {
                Version = 2,
                HighWaterNowMs = _highWaterNowMs,
                PendingSlotReleases = _pendingSlotReleases.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                Items = _records.Values.ToList(),
            };
            var tmp = _manifestPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(env));
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
