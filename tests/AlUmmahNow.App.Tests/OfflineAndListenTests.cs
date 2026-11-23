using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Media;
using Xunit;

namespace AlUmmahNow.App.Tests;

public sealed class OfflineAndListenTests
{
    [Fact]
    public void ListenMode_EnforcesFeatureGate_SleepTimer_AndQueue()
    {
        var now = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        var controller = new ListenModeController(listenModeEnabled: false, clock: () => now);
        var player = new FakePlayer();
        var session = new FakeSession();

        // OFF blocks audio-only mode
        controller.SetAudioOnly(true, player, session);
        Assert.False(controller.IsAudioOnly);

        // Enable switch and queue items
        controller.SetFeatureEnabled(true);
        var e1 = new ListenQueueEntry("v1", "First", "Channel A", "https://media/v1/master.m3u8", "https://media/v1/audio.m4a", durationSeconds: 600);
        var e2 = new ListenQueueEntry("v2", "Second", "Channel A", "https://media/v2/master.m3u8");
        var e3 = new ListenQueueEntry("v3", "Third", "Channel A", "https://media/v3/master.m3u8");

        controller.SetNowPlaying(e1);
        player.Open(e1.PlayUrl, 45, true);
        player.Rate = 1.5;

        controller.SetAudioOnly(true, player, session);
        Assert.True(controller.IsAudioOnly);
        Assert.Equal("https://media/v1/audio.m4a", player.Opened[^1].Url);
        Assert.Equal(45, player.Position);
        Assert.Equal(1.5, player.Rate);

        // Sleep timer (15 min) pauses player on expiry
        controller.SetSleepTimer(SleepTimerPreset.FromMinutes(15));
        Assert.False(controller.TickSleepTimer(player));
        now = now.AddMinutes(15);
        Assert.True(controller.TickSleepTimer(player));
        Assert.Equal(PlayerState.Paused, player.State);

        // End of episode pauses playback
        player.Play();
        controller.SetSleepTimer(SleepTimerPreset.EndOfEpisode);
        Assert.True(controller.OnEpisodeEnded(player));
        Assert.Equal(PlayerState.Paused, player.State);

        // Queue operations
        controller.AddToEnd(e2);
        controller.PlayNext(e3);
        Assert.Equal(new[] { "v1", "v3", "v2" }, new[] { controller.Queue[0].VideoId, controller.Queue[1].VideoId, controller.Queue[2].VideoId });
        controller.MoveQueueItem(2, 1);
        Assert.Equal("v2", controller.Advance()?.VideoId);
        controller.ClearUpNext();
        Assert.Single(controller.Queue);
    }

    [Fact]
    public void ChunkedCipher_RoundTrips_AndRejectsWrongKeyOrTamper()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-desktop-cipher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var vaultA = new InMemoryDownloadKeyVault();
            var vaultB = new InMemoryDownloadKeyVault();
            var payload = new byte[150_000];
            for (var i = 0; i < payload.Length; i++) payload[i] = (byte)((i * 37) % 251);

            var file = Path.Combine(dir, "lecture.aun");
            ChunkedDownloadCipher.Encrypt(vaultA.GetOrCreateKey(), "v1", "lic1", payload, file);
            Assert.NotEqual(payload, File.ReadAllBytes(file));

            var plain = ChunkedDownloadCipher.Decrypt(vaultA.GetOrCreateKey(), "v1", "lic1", file);
            Assert.Equal(payload, plain);

            Assert.ThrowsAny<CryptographicException>(() => ChunkedDownloadCipher.Decrypt(vaultB.GetOrCreateKey(), "v1", "lic1", file));
            Assert.ThrowsAny<CryptographicException>(() => ChunkedDownloadCipher.Decrypt(vaultA.GetOrCreateKey(), "v2", "lic1", file));

            var tampered = File.ReadAllBytes(file);
            tampered[tampered.Length - 1] ^= 0xFF;
            File.WriteAllBytes(file, tampered);
            Assert.ThrowsAny<CryptographicException>(() => ChunkedDownloadCipher.Decrypt(vaultA.GetOrCreateKey(), "v1", "lic1", file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void OfflineDownloadStore_EnforcesGate_WifiPolicy_RefreshRevocation_AndWipe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-desktop-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            long nowMs = 10_000;
            var store = new OfflineDownloadStore(dir, new InMemoryDownloadKeyVault(), offlineDownloadsEnabled: false, clockMs: () => nowMs);

            Assert.Equal(
                DownloadTransferAllowance.BlockedFeatureOff,
                OfflineDownloadPolicy.CanDownload(false, new OfflineDownloadSettings(), isConnected: true, isUnmetered: true));
            Assert.Equal(
                DownloadTransferAllowance.BlockedWifiOnly,
                OfflineDownloadPolicy.CanDownload(true, new OfflineDownloadSettings { WifiOnly = true }, isConnected: true, isUnmetered: false));

            Assert.Throws<InvalidOperationException>(() =>
                store.SaveEncrypted("lic-1", "v1", "Surah", OfflineDownloadQuality.Audio, nowMs + 60_000, Encoding.UTF8.GetBytes("a")));

            store.OfflineDownloadsEnabled = true;
            var r1 = store.SaveEncrypted("lic-1", "v1", "Surah Al-Kahf", OfflineDownloadQuality.Audio, nowMs + 60_000, Encoding.UTF8.GetBytes("audio-1"), titleAr: "سورة الكهف");
            var r2 = store.SaveEncrypted("lic-2", "v2", "Fiqh", OfflineDownloadQuality.Q480p, nowMs + 60_000, Encoding.UTF8.GetBytes("video-2"));

            Assert.Equal("audio-1", Encoding.UTF8.GetString(store.ReadDecrypted(r1.LicenseId)));
            Assert.Equal(2, store.PlayableDownloads().Count);

            var deleted = store.ApplyLicenseRefresh(new[]
            {
                new LicenseRefreshDecision("lic-1", "active", expiresAtMs: nowMs + 120_000),
                new LicenseRefreshDecision("lic-2", "revoked", reason: "creator_disabled"),
            });
            Assert.Equal(1, deleted);
            Assert.False(File.Exists(Path.Combine(dir, r2.FileName)));
            Assert.Single(store.PlayableDownloads());

            store.WipeAll();
            Assert.Empty(store.AllDownloads());
            Assert.Equal(0, store.TotalUsedBytes());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Localizer_ProvidesListenAndDownloadStringsInEnglishAndArabic()
    {
        var en = new Localizer("en");
        var ar = new Localizer("ar");
        Assert.Equal("Listen", en["player.listen"]);
        Assert.Equal("استماع", ar["player.listen"]);
        Assert.Equal("Downloads", en["downloads.title"]);
        Assert.Equal("التنزيلات", ar["downloads.title"]);
        Assert.Contains("مرئيات", ar["downloads.empty"]);
    }
}
