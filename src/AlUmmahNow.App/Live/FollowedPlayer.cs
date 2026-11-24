using System;
using AlUmmahNow.App.Media;

namespace AlUmmahNow.App.Live
{
    /// <summary>
    /// The shared player as a live page sees it: whether it has arrived where it was sent, whether it is moving, and
    /// whether the viewer moved it. A position read while a seek is on its way is where playback was before, and must not
    /// be taken for where it is.
    /// </summary>
    internal sealed class FollowedPlayer : IMediaPlayer, IDisposable
    {
        private const double Near = 2;
        private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(5);

        private readonly IMediaPlayer _inner;
        private readonly Func<DateTimeOffset> _now;
        private double _target;
        private DateTimeOffset? _sentAt;
        private double _last = -1;
        private bool _seeked;

        public FollowedPlayer(IMediaPlayer inner, Func<DateTimeOffset> now)
        {
            _inner = inner;
            _now = now;
            _inner.StateChanged += OnState;
            _inner.PositionChanged += OnPosition;
        }

        public event EventHandler? StateChanged;
        public event EventHandler? PositionChanged;

        public PlayerState State => _inner.State;
        public double Position => _inner.Position;
        public double Duration => _inner.Duration;
        public double Rate { get => _inner.Rate; set => _inner.Rate = value; }

        public void Open(string url, double startPosition, bool autoPlay)
        {
            Sent(startPosition);
            _inner.Open(url, startPosition, autoPlay);
        }

        public void Play() => _inner.Play();
        public void Pause() => _inner.Pause();
        public void Stop() => _inner.Stop();

        /// <summary>The viewer's own seek.</summary>
        public void Seek(double position)
        {
            _seeked = true;
            Sent(position);
            _inner.Seek(position);
        }

        /// <summary>A seek to the premiere's moment.</summary>
        public void Follow(double position)
        {
            Sent(position);
            _inner.Seek(position);
        }

        /// <summary>One reading a second; null while it has not arrived, or there is nothing to read.</summary>
        public PlayerReading? Read()
        {
            var position = _inner.Position;
            if (_sentAt is { } sent)
            {
                if (Math.Abs(position - _target) > Near && _now() - sent < Arrival) return null;
                _sentAt = null;
                _last = -1;
            }

            if (State != PlayerState.Playing && State != PlayerState.Paused)
            {
                _last = -1;
                return null;
            }

            var isMoving = _last >= 0 && position > _last;
            _last = position;
            var seeked = _seeked;
            _seeked = false;
            return new PlayerReading(position, State == PlayerState.Playing, isMoving, Rate, seeked);
        }

        public void Dispose()
        {
            _inner.StateChanged -= OnState;
            _inner.PositionChanged -= OnPosition;
        }

        private void Sent(double target)
        {
            _target = target;
            _sentAt = _now();
        }

        private void OnState(object? sender, EventArgs e) => StateChanged?.Invoke(this, e);
        private void OnPosition(object? sender, EventArgs e) => PositionChanged?.Invoke(this, e);
    }
}
