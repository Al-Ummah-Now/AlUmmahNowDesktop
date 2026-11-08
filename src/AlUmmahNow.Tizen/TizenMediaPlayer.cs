using System;
using System.Threading;
using AlUmmahNow.App.Media;
using Tizen.Multimedia;
using PlayerState = AlUmmahNow.App.Media.PlayerState;

namespace AlUmmahNow.TizenTv
{
    /// <summary>
    /// Tizen.Multimedia.Player: the .NET player on Samsung TVs ("AVPlay" is the name of its Web API).
    /// NOT verified on hardware yet; CI has no TV or emulator.
    /// </summary>
    public sealed class TizenMediaPlayer : IMediaPlayer
    {
        private readonly SynchronizationContext _ui;
        private readonly Tizen.NUI.Window _window;
        private readonly Tizen.NUI.Timer _timer;
        private Player? _player;
        private PlayerState _state = PlayerState.Idle;
        private double _rate = 1;
        private int _generation;

        public TizenMediaPlayer(SynchronizationContext ui, Tizen.NUI.Window window)
        {
            _ui = ui;
            _window = window;
            _timer = new Tizen.NUI.Timer(250);
            _timer.Tick += (s, e) =>
            {
                PositionChanged?.Invoke(this, EventArgs.Empty);
                return true;
            };
            _timer.Start();
        }

        public event EventHandler? StateChanged;
        public event EventHandler? PositionChanged;

        public PlayerState State => _state;

        public double Position
        {
            get
            {
                try
                {
                    return _player != null && Ready ? _player.GetPlayPosition() / 1000.0 : 0;
                }
                catch (InvalidOperationException)
                {
                    return 0;
                }
            }
        }

        public double Duration
        {
            get
            {
                try
                {
                    return _player != null && Ready ? _player.StreamInfo.GetDuration() / 1000.0 : 0;
                }
                catch (InvalidOperationException)
                {
                    return 0;
                }
            }
        }

        public double Rate
        {
            get => _rate;
            set
            {
                _rate = value;
                if (_player != null && Ready) _player.SetPlaybackRate((float)value);
            }
        }

        private bool Ready => _state == PlayerState.Playing || _state == PlayerState.Paused;

        public async void Open(string url, double startPosition, bool autoPlay)
        {
            var generation = ++_generation;
            Release();
            SetState(PlayerState.Loading);
            var player = new Player { Display = new Display(_window) };
            _player = player;
            player.ErrorOccurred += (s, e) => _ui.Post(_ => Fail("player error: " + e.Error), null);
            player.PlaybackCompleted += (s, e) => _ui.Post(_ => SetState(PlayerState.Ended), null);
            try
            {
                player.SetSource(new MediaUriSource(url));
                await player.PrepareAsync();
                if (generation != _generation) return;
                if (startPosition > 0) await player.SetPlayPositionAsync((int)(startPosition * 1000), false);
                if (Math.Abs(_rate - 1) > 0.001) player.SetPlaybackRate((float)_rate);
                if (autoPlay)
                {
                    player.Start();
                    SetState(PlayerState.Playing);
                }
                else
                {
                    SetState(PlayerState.Paused);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is NotSupportedException || ex is UnauthorizedAccessException || ex is System.IO.IOException)
            {
                if (generation == _generation) Fail(ex.Message);
            }
        }

        public void Play()
        {
            if (_player == null || !Ready) return;
            _player.Start();
            SetState(PlayerState.Playing);
        }

        public void Pause()
        {
            if (_player == null || _state != PlayerState.Playing) return;
            _player.Pause();
            SetState(PlayerState.Paused);
        }

        public async void Seek(double position)
        {
            if (_player == null || !Ready) return;
            try
            {
                await _player.SetPlayPositionAsync((int)(position * 1000), false);
            }
            catch (InvalidOperationException ex)
            {
                Tizen.Log.Warn("AUN", "seek failed: " + ex.Message);
            }
        }

        public void Stop()
        {
            _generation++;
            Release();
            SetState(PlayerState.Idle);
        }

        private void Release()
        {
            var player = _player;
            _player = null;
            if (player == null) return;
            try
            {
                player.Unprepare();
            }
            catch (InvalidOperationException)
            {
                // Not prepared yet.
            }

            player.Dispose();
        }

        private void Fail(string message)
        {
            Tizen.Log.Error("AUN", message);
            SetState(PlayerState.Failed);
        }

        private void SetState(PlayerState state)
        {
            if (_state == state) return;
            _state = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
