using System;
using System.Collections.Generic;
using System.Linq;

namespace AlUmmahNow.App.Media
{
    public enum SleepTimerKind
    {
        Off,
        Minutes,
        EndOfEpisode,
    }

    public sealed class SleepTimerPreset : IEquatable<SleepTimerPreset>
    {
        public static readonly SleepTimerPreset Off = new SleepTimerPreset(SleepTimerKind.Off, 0);
        public static readonly SleepTimerPreset EndOfEpisode = new SleepTimerPreset(SleepTimerKind.EndOfEpisode, 0);

        public static readonly IReadOnlyList<SleepTimerPreset> Standard = new[]
        {
            Off,
            FromMinutes(15),
            FromMinutes(30),
            FromMinutes(45),
            FromMinutes(60),
            EndOfEpisode,
        };

        public SleepTimerPreset(SleepTimerKind kind, int minutes)
        {
            Kind = kind;
            Minutes = minutes;
        }

        public SleepTimerKind Kind { get; }
        public int Minutes { get; }

        public static SleepTimerPreset FromMinutes(int minutes) =>
            new SleepTimerPreset(SleepTimerKind.Minutes, Math.Max(1, minutes));

        public bool Equals(SleepTimerPreset? other) =>
            other != null && Kind == other.Kind && Minutes == other.Minutes;

        public override bool Equals(object? obj) => Equals(obj as SleepTimerPreset);
        public override int GetHashCode() => ((int)Kind * 397) ^ Minutes;
    }

    public sealed class ListenQueueEntry
    {
        public ListenQueueEntry(
            string videoId,
            string title,
            string? channelName,
            string playUrl,
            string? audioUrl = null,
            string? thumbnailUrl = null,
            double durationSeconds = 0)
        {
            VideoId = videoId;
            Title = title;
            ChannelName = channelName;
            PlayUrl = playUrl;
            AudioUrl = audioUrl;
            ThumbnailUrl = thumbnailUrl;
            DurationSeconds = durationSeconds;
        }

        public string VideoId { get; }
        public string Title { get; }
        public string? ChannelName { get; }
        public string PlayUrl { get; }
        public string? AudioUrl { get; }
        public string? ThumbnailUrl { get; }
        public double DurationSeconds { get; }
    }

    /// <summary>
    /// Listen mode, sleep timer, and up-next queue controller for Windows &amp; Linux Desktop (E14-S10).
    /// Gated by the server's `listenMode` feature switch (defaults OFF).
    /// </summary>
    public sealed class ListenModeController
    {
        private readonly Func<DateTimeOffset> _clock;
        private readonly List<ListenQueueEntry> _queue = new List<ListenQueueEntry>();

        public ListenModeController(bool listenModeEnabled = false, Func<DateTimeOffset>? clock = null)
        {
            ListenModeEnabled = listenModeEnabled;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public bool ListenModeEnabled { get; private set; }
        public bool IsAudioOnly { get; private set; }
        public SleepTimerPreset SleepTimer { get; private set; } = SleepTimerPreset.Off;
        public DateTimeOffset? SleepTimerExpiresAt { get; private set; }
        public int CurrentIndex { get; private set; } = -1;

        public IReadOnlyList<ListenQueueEntry> Queue => _queue;
        public ListenQueueEntry? CurrentEntry =>
            CurrentIndex >= 0 && CurrentIndex < _queue.Count ? _queue[CurrentIndex] : null;
        public IReadOnlyList<ListenQueueEntry> UpNext =>
            CurrentIndex >= 0 && CurrentIndex < _queue.Count
                ? _queue.Skip(CurrentIndex + 1).ToList()
                : _queue.ToList();

        public void SetFeatureEnabled(bool enabled)
        {
            ListenModeEnabled = enabled;
            if (!enabled)
            {
                IsAudioOnly = false;
                CancelSleepTimer();
            }
        }

        /// <summary>
        /// Toggles Listen (audio-only) mode, preserving current playback position, speed, and play state.
        /// </summary>
        public void SetAudioOnly(bool audioOnly, IMediaPlayer? player = null, IMediaSession? session = null)
        {
            if (audioOnly && !ListenModeEnabled) return;
            if (IsAudioOnly == audioOnly) return;
            IsAudioOnly = audioOnly;

            var entry = CurrentEntry;
            if (player != null && entry != null && !string.IsNullOrEmpty(entry.AudioUrl))
            {
                var pos = player.Position;
                var rate = player.Rate;
                var wasPlaying = player.State == PlayerState.Playing;
                var targetUrl = audioOnly ? entry.AudioUrl! : entry.PlayUrl;
                player.Open(targetUrl, pos, wasPlaying);
                player.Rate = rate;
                session?.SetMetadata(entry.Title, entry.ChannelName ?? "", entry.ThumbnailUrl, entry.DurationSeconds);
                session?.SetPlayback(player.State, pos, rate);
            }
        }

        public void SetSleepTimer(SleepTimerPreset preset)
        {
            SleepTimer = preset;
            SleepTimerExpiresAt = preset.Kind == SleepTimerKind.Minutes
                ? _clock().AddMinutes(preset.Minutes)
                : (DateTimeOffset?)null;
        }

        public void CancelSleepTimer() => SetSleepTimer(SleepTimerPreset.Off);

        public bool TickSleepTimer(IMediaPlayer? player = null)
        {
            if (SleepTimer.Kind != SleepTimerKind.Minutes || SleepTimerExpiresAt == null) return false;
            if (_clock() < SleepTimerExpiresAt.Value) return false;
            CancelSleepTimer();
            player?.Pause();
            return true;
        }

        public bool OnEpisodeEnded(IMediaPlayer? player = null)
        {
            if (SleepTimer.Kind != SleepTimerKind.EndOfEpisode) return false;
            CancelSleepTimer();
            player?.Pause();
            return true;
        }

        public void SetNowPlaying(ListenQueueEntry entry)
        {
            var idx = _queue.FindIndex(e => e.VideoId == entry.VideoId);
            if (idx >= 0)
            {
                CurrentIndex = idx;
            }
            else
            {
                _queue.Insert(0, entry);
                CurrentIndex = 0;
            }
        }

        public void PlayNext(ListenQueueEntry entry)
        {
            _queue.RemoveAll(e => e.VideoId == entry.VideoId);
            var insertAt = CurrentIndex >= 0 ? Math.Min(CurrentIndex + 1, _queue.Count) : 0;
            _queue.Insert(insertAt, entry);
        }

        public void AddToEnd(ListenQueueEntry entry)
        {
            if (_queue.Any(e => e.VideoId == entry.VideoId)) return;
            _queue.Add(entry);
        }

        public void RemoveFromQueue(string videoId)
        {
            var idx = _queue.FindIndex(e => e.VideoId == videoId);
            if (idx < 0) return;
            _queue.RemoveAt(idx);
            if (_queue.Count == 0) CurrentIndex = -1;
            else if (idx < CurrentIndex) CurrentIndex--;
            else if (idx == CurrentIndex) CurrentIndex = Math.Min(CurrentIndex, _queue.Count - 1);
        }

        public void MoveQueueItem(int fromIndex, int toIndex)
        {
            if (fromIndex < 0 || fromIndex >= _queue.Count || toIndex < 0 || toIndex >= _queue.Count || fromIndex == toIndex)
                return;
            var activeId = CurrentEntry?.VideoId;
            var item = _queue[fromIndex];
            _queue.RemoveAt(fromIndex);
            _queue.Insert(toIndex, item);
            if (activeId != null)
            {
                CurrentIndex = _queue.FindIndex(e => e.VideoId == activeId);
            }
        }

        public ListenQueueEntry? Advance()
        {
            var next = CurrentIndex + 1;
            if (next < 0 || next >= _queue.Count) return null;
            CurrentIndex = next;
            return _queue[next];
        }

        public void ClearUpNext()
        {
            var current = CurrentEntry;
            _queue.Clear();
            if (current != null)
            {
                _queue.Add(current);
                CurrentIndex = 0;
            }
            else
            {
                CurrentIndex = -1;
            }
        }
    }
}
