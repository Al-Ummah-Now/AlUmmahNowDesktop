using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Media;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Studio;
using Xunit;

namespace AlUmmahNow.App.Tests;

public sealed class OfflineAndListenTests
{
    private sealed class FakePlayer : IMediaPlayer
    {
        public PlayerState State { get; private set; } = PlayerState.Idle;
        public double Position { get; private set; }
        public double Duration { get; set; } = 600;
        public double Rate { get; set; } = 1.0;
        public double Volume { get; set; } = 1.0;
        public bool Muted { get; set; }
        public string? LastOpenedUrl { get; private set; }
        public int PauseCount { get; private set; }

        public event EventHandler? StateChanged { add { } remove { } }
        public event EventHandler? PositionChanged { add { } remove { } }

        public void Open(string url, double startSeconds = 0, bool autoplay = true)
        {
            LastOpenedUrl = url;
            Position = startSeconds;
            State = autoplay ? PlayerState.Playing : PlayerState.Paused;
        }

        public void Play() => State = PlayerState.Playing;
        public void Pause()
        {
            PauseCount++;
            State = PlayerState.Paused;
        }

        public void Seek(double seconds) => Position = seconds;
        public void Stop() => State = PlayerState.Idle;
    }

    private sealed class FakeSecureStore : ISecureStore
    {
        private readonly Dictionary<string, byte[]> _map = new(StringComparer.Ordinal);
        public byte[]? Get(string key) => _map.TryGetValue(key, out var v) ? (byte[])v.Clone() : null;
        public void Set(string key, byte[] value) => _map[key] = (byte[])value.Clone();
        public void Delete(string key) => _map.Remove(key);
        public string[] List(string prefix) => _map.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }

    private sealed class RecordingSlotReleaser : ILicenseSlotReleaser
    {
        public List<string> Released { get; } = new();
        public List<string> RevokedDevices { get; } = new();
        public bool ReleaseLicense(string licenseId) { Released.Add(licenseId); return true; }
        public bool RevokeDevice(string deviceId) { RevokedDevices.Add(deviceId); return true; }
    }

    [Fact]
    public void AunDl01_StreamingEncrypt_RandomAccessSeek_Binding_AndTruncationDetection()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-dl-cipher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new FakeSecureStore();
            var vault = new SecureStoreDownloadKeyVault(store);
            var key = vault.GetOrCreateKey();
            // Persists across instances (same key after restart).
            Assert.Equal(key, new SecureStoreDownloadKeyVault(store).GetOrCreateKey());

            var payload = new byte[AunDl01Format.ChunkSize + 5000];
            for (var i = 0; i < payload.Length; i++) payload[i] = (byte)((i * 29) & 0xFF);
            var encPath = Path.Combine(dir, "clip.aun");

            using (var input = new MemoryStream(payload))
            {
                AunDl01StreamWriter.EncryptStream(key, "vid-1", "lic-1", input, encPath);
            }

            // O(1) random access across chunk boundary without reading whole file into memory.
            using (var stream = AunDl01DecryptStream.Open(key, encPath, "vid-1", "lic-1"))
            {
                Assert.Equal(payload.Length, stream.Length);
                var start = AunDl01Format.ChunkSize - 50;
                stream.Seek(start, SeekOrigin.Begin);
                var buf = new byte[120];
                var n = stream.Read(buf, 0, buf.Length);
                Assert.Equal(120, n);
                Assert.Equal(payload.AsSpan(start, 120).ToArray(), buf);
            }

            // Cross-video and cross-license binding rejection on Open.
            Assert.ThrowsAny<CryptographicException>(() => AunDl01DecryptStream.Open(key, encPath, "vid-other", "lic-1"));
            Assert.ThrowsAny<CryptographicException>(() => AunDl01DecryptStream.Open(key, encPath, "vid-1", "lic-other"));

            // Truncation at exact chunk boundary must fail final-chunk AAD check on Open.
            var raw = File.ReadAllBytes(encPath);
            var hdrLen = 16 + 2 + "vid-1".Length + 2 + "lic-1".Length;
            var truncPath = Path.Combine(dir, "trunc.aun");
            File.WriteAllBytes(truncPath, raw.AsSpan(0, hdrLen + AunDl01Format.ChunkStride).ToArray());
            Assert.ThrowsAny<Exception>(() => AunDl01DecryptStream.Open(key, truncPath, "vid-1", "lic-1"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Store_IsolatesAccounts_BlocksClockRollback_ForbidsSignedUrls_ReleasesSlots_NeverEvictsActive()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-dl-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            long wall = 10_000;
            long mono = 1_000;
            var releaser = new RecordingSlotReleaser();
            var store = new OfflineDownloadStore(
                dir,
                new SecureStoreDownloadKeyVault(new FakeSecureStore()),
                offlineDownloadsEnabled: false,
                wallClockMs: () => wall,
                monotonicClockMs: () => mono,
                slotReleaser: releaser);

            // Default-off switch blocks downloads
            using (var ms = new MemoryStream(new byte[300]))
            {
                Assert.Throws<InvalidOperationException>(() =>
                    store.SaveFromStream("u1", "lic-off", "v1", "Off", OfflineDownloadQuality.Audio, 10_000, 20_000, ms));
            }

            // Wired from /api/me/features
            store.ApplyFeatures(new UserFeatures(new[] { UserFeatures.OfflineDownloads }));
            Assert.True(store.OfflineDownloadsEnabled);

            // Signed URLs strictly forbidden
            using (var ms = new MemoryStream(new byte[10]))
            {
                Assert.Throws<ArgumentException>(() =>
                    store.SaveFromStream("u1", "lic-url", "v1", "Bad", OfflineDownloadQuality.Audio, 10_000, 20_000, ms,
                        thumbnailPath: "https://media.alummahnow.net/priv/tok/thumb.jpg"));
            }

            store.UpdateSettings(new OfflineDownloadSettings { MaxStorageBytes = 500 });
            using (var ms = new MemoryStream(new byte[300]))
            {
                store.SaveFromStream("u1", "lic-1", "v1", "Keep", OfflineDownloadQuality.Audio, 10_000, 20_000, ms);
            }

            // Second download over cap must throw instead of silently evicting active user download!
            using (var ms = new MemoryStream(new byte[300]))
            {
                Assert.Throws<StorageBudgetExceededException>(() =>
                    store.SaveFromStream("u2", "lic-2", "v1", "Too big", OfflineDownloadQuality.Audio, 10_000, 90_000, ms));
            }
            Assert.Single(store.PlayableDownloads("u1"));

            // Wall clock rolled backward while monotonic advances past expiry -> NOT playable!
            wall = 1_000;
            mono = 15_000; // anchor 10_000 + 14_000 = 24_000 > 20_000
            Assert.Empty(store.PlayableDownloads("u1"));
            Assert.Throws<InvalidOperationException>(() => store.OpenDecryptStream("u1", "v1"));

            // Remove frees server license slot.
            Assert.True(store.Remove("u1", "v1"));
            Assert.Equal(new[] { "lic-1" }, releaser.Released);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ListenMode_SwapsToAudioUrl_WiresFeatures_AndFormatsArabicNumerals()
    {
        var now = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        var player = new FakePlayer();
        var ctrl = new ListenModeController(listenModeEnabled: false, clock: () => now);
        var e1 = new ListenQueueEntry("v1", "Tafsir 1", "Al-Huda", "https://cdn/v1/master.m3u8", "https://cdn/v1/audio.m4a", durationSeconds: 600);
        ctrl.SetNowPlaying(e1);
        player.Open(e1.PlayUrl, 45, true);
        player.Rate = 1.5;

        // Off by default
        ctrl.SetAudioOnly(true, player);
        Assert.False(ctrl.IsAudioOnly);

        // Wired from /api/me/features
        ctrl.ApplyFeatures(new UserFeatures(new[] { UserFeatures.ListenMode }));
        ctrl.SetAudioOnly(true, player);
        Assert.True(ctrl.IsAudioOnly);
        Assert.Equal("https://cdn/v1/audio.m4a", player.LastOpenedUrl);
        Assert.Equal(45, player.Position);
        Assert.Equal(1.5, player.Rate);

        // Arabic sleep timer label uses Eastern Arabic numerals (١٥ دقيقة, never 15 دقيقة)
        var ar = new Localizer("ar");
        var en = new Localizer("en");
        Assert.Equal("١٥ دقيقة", ListenModeController.FormatSleepTimerLabel(ar, SleepTimerPreset.FromMinutes(15)));
        Assert.Equal("15 min", ListenModeController.FormatSleepTimerLabel(en, SleepTimerPreset.FromMinutes(15)));
        Assert.Contains("مرئيات", ar["downloads.empty"]);
        Assert.Contains("،", ar["downloads.expired"]);
    }
}
