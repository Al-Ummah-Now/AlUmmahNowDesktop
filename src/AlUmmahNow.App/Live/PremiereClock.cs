using System;

namespace AlUmmahNow.App.Live
{
    /// <summary>Where a premiere is (wiki E17 §5.4): everyone sees the same moment, by the server's clock.</summary>
    public sealed class PremiereClock
    {
        private readonly DateTimeOffset _startedAt;
        private readonly TimeSpan _offset;
        private readonly double? _duration;

        public PremiereClock(DateTimeOffset startedAt, TimeSpan offset, double? durationSeconds)
        {
            _startedAt = startedAt;
            _offset = offset;
            _duration = durationSeconds;
        }

        /// <summary>Seconds into the video at <paramref name="now"/>, between 0 and its length.</summary>
        public double Position(DateTimeOffset now)
        {
            var at = Math.Max(0, (now + _offset - _startedAt).TotalSeconds);
            return _duration is { } d ? Math.Min(at, d) : at;
        }

        public bool IsOver(DateTimeOffset now) => _duration is { } d && Position(now) >= d;
    }

    /// <summary>What the player is doing at one tick.</summary>
    public readonly struct PlayerReading
    {
        public PlayerReading(double position, bool isPlaying, bool canKeepUp, double speed, bool seeked = false)
        {
            Position = position;
            IsPlaying = isPlaying;
            CanKeepUp = canKeepUp;
            Speed = speed;
            Seeked = seeked;
        }

        public double Position { get; }
        public bool IsPlaying { get; }

        /// <summary>Playback is moving: another seek would not only start the loading over.</summary>
        public bool CanKeepUp { get; }
        public double Speed { get; }

        /// <summary>The viewer moved it since the last tick.</summary>
        public bool Seeked { get; }
    }

    /// <summary>What to do with the player: a seek, normal speed, both or neither.</summary>
    public readonly struct FollowAction
    {
        public FollowAction(double? seekTo, bool normalSpeed)
        {
            SeekTo = seekTo;
            NormalSpeed = normalSpeed;
        }

        public double? SeekTo { get; }
        public bool NormalSpeed { get; }
    }

    /// <summary>
    /// Keeps a player at the premiere's moment. A viewer may pause or go back: they are then behind, until they catch up or
    /// return. Going ahead is not possible, and neither is playing faster than everyone else while at the moment.
    /// </summary>
    public sealed class PremiereFollower
    {
        /// <summary>Further from the moment than this, playback is brought back to it.</summary>
        public const double Resync = 3;

        /// <summary>Nobody gets ahead of it by more than the jitter of a seek.</summary>
        public const double Ahead = 1;

        private bool _hasPlayed;

        public bool IsFollowing { get; private set; } = true;
        public bool IsBehind { get; private set; }

        public void Returned()
        {
            IsFollowing = true;
            IsBehind = false;
        }

        public FollowAction Tick(double moment, PlayerReading reading)
        {
            var action = Decide(moment, reading);
            IsBehind = !IsFollowing && moment - reading.Position > Resync;
            return action;
        }

        private FollowAction Decide(double moment, PlayerReading reading)
        {
            var drift = moment - reading.Position;
            if (reading.Seeked && drift > Resync) IsFollowing = false;
            // Ahead, also when they caught up and passed it between two ticks
            if (drift < -Ahead)
            {
                IsFollowing = true;
                return new FollowAction(moment, reading.Speed != 1);
            }

            if (!reading.IsPlaying)
            {
                if (_hasPlayed) IsFollowing = false;
                return default;
            }

            _hasPlayed = true;
            if (!IsFollowing && Math.Abs(drift) <= Ahead) IsFollowing = true;
            if (!IsFollowing) return default;
            return new FollowAction(reading.CanKeepUp && drift > Resync ? moment : (double?)null, reading.Speed != 1);
        }
    }
}
