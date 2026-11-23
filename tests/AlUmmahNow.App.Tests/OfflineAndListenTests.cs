using System;
using System.Collections.Generic;
using System.IO;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Media;
using AlUmmahNow.CoreBridge.Studio;
using Xunit;

namespace AlUmmahNow.App.Tests
{
    public sealed class OfflineAndListenTests
    {
        private sealed class FakePlayer : IMediaPlayer
        {
            public string? LoadedUrl { get; private set; }
            public PlayerState State { get; private set; } = PlayerState.Idle;
            public double Position { get; private set; }
            public double Duration => 600;
            public double Rate { get; set; } = 1.0;

            public event EventHandler? StateChanged { add { } remove { } }
            public event EventHandler? PositionChanged { add { } remove { } }

            public void Open(string url, double startPosition, bool autoPlay)
            {
                LoadedUrl = url;
                Position = startPosition;
                State = autoPlay ? PlayerState.Playing : PlayerState.Paused;
            }

            public void Play() => State = PlayerState.Playing;
            public void Pause() => State = PlayerState.Paused;
            public void Seek(double position) => Position = position;
            public void Stop() => State = PlayerState.Idle;
        }

        [Fact]
        public void ListenMode_GatedByFeatureSwitch_PreservesPositionAndFormatsArabicDigits()
        {
            var loc = new Localizer("ar");
            var player = new FakePlayer();
            player.Open("https://media.example/pub/vid-1/r1/master.m3u8", startPosition: 45, autoPlay: true);
            player.Rate = 1.5;

            var listen = new ListenModeController(listenModeEnabled: false);
            var entry = new ListenQueueEntry(
                videoId: "vid-1",
                title: "Surah Al-Kahf",
                channelName: "Recitations",
                playUrl: "https://media.example/pub/vid-1/r1/master.m3u8",
                audioUrl: "https://media.example/pub/vid-1/r1/audio.m4a",
                durationSeconds: 600);
            listen.SetNowPlaying(entry);

            listen.SetAudioOnly(true, player);
            Assert.False(listen.IsAudioOnly);

            listen.ApplyFeatures(new UserFeatures(new HashSet<string> { UserFeatures.ListenMode }));
            listen.SetAudioOnly(true, player);
            Assert.True(listen.IsAudioOnly);
            Assert.Equal("https://media.example/pub/vid-1/r1/audio.m4a", player.LoadedUrl);
            Assert.Equal(45, player.Position);
            Assert.Equal(1.5, player.Rate);
            Assert.Equal(PlayerState.Playing, player.State);

            var label = ListenModeController.FormatSleepTimerLabel(loc, SleepTimerPreset.FromMinutes(15));
            Assert.Equal("١٥ دقيقة", label);
        }

        [Fact]
        public void PlainDownloadStore_EnforcesFeatureSwitch_Caps_MultiAccount_AndAvailabilitySync()
        {
            var dir = Path.Combine(Path.GetTempPath(), "aun-dl-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var store = new OfflineDownloadStore(dir, offlineDownloadsEnabled: false);
                store.UpdateSettings(new OfflineDownloadSettings { WifiOnly = true, MaxStorageBytes = 5_000, MaxVideos = 2 });

                var payload = new byte[1_500];
                new Random(42).NextBytes(payload);

                // Feature OFF blocks saves
                Assert.Throws<InvalidOperationException>(() =>
                    store.SaveFromStream("user-a", "vid-1", "Lecture", OfflineDownloadQuality.Audio, new MemoryStream(payload)));

                store.ApplyFeatures(new UserFeatures(new HashSet<string> { UserFeatures.OfflineDownloads }));

                // Signed / remote thumbnail URLs forbidden
                Assert.Throws<ArgumentException>(() =>
                    store.SaveFromStream(
                        "user-a", "vid-1", "Lecture", OfflineDownloadQuality.Audio, new MemoryStream(payload),
                        thumbnailPath: "https://cdn.example/signed.jpg?sig=abc"));

                // Saves plain .m4a file directly playable via path
                var saved = store.SaveFromStream(
                    "user-a", "vid-1", "Lecture", OfflineDownloadQuality.Audio, new MemoryStream(payload),
                    thumbnailPath: "/local/thumbs/vid-1.jpg");
                Assert.EndsWith(".m4a", saved.FileName);
                var playablePath = store.GetPlayableFilePath("user-a", "vid-1");
                Assert.Equal(payload, File.ReadAllBytes(playablePath));

                // Multi-account isolation
                store.SaveFromStream("user-b", "vid-1", "Lecture", OfflineDownloadQuality.Audio, new MemoryStream(payload));
                Assert.Single(store.PlayableDownloads("user-a"));
                Assert.Single(store.PlayableDownloads("user-b"));

                // Storage budget refusal without silently evicting active downloads
                var big = new byte[4_000];
                Assert.Throws<StorageBudgetExceededException>(() =>
                    store.SaveFromStream("user-a", "vid-big", "Big", OfflineDownloadQuality.Q480p, new MemoryStream(big), expectedBytes: big.Length));
                Assert.Single(store.PlayableDownloads("user-a"));

                // Tier maxVideos cap enforced
                store.UpdateSettings(new OfflineDownloadSettings { WifiOnly = true, MaxStorageBytes = 50_000, MaxVideos = 1 });
                Assert.Throws<MaxVideosExceededException>(() =>
                    store.SaveFromStream("user-a", "vid-2", "Second", OfflineDownloadQuality.Audio, new MemoryStream(payload)));

                // Availability sync deletes local files for private/deleted/disabled videos
                var deleted = store.ApplyAvailabilitySync(new[]
                {
                    new AvailabilitySyncDecision("vid-1", available: false, reason: "video_deleted"),
                });
                Assert.Equal(2, deleted);
                Assert.False(File.Exists(playablePath));
                Assert.Empty(store.PlayableDownloads("user-a"));
                Assert.Empty(store.PlayableDownloads("user-b"));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }
    }
}
