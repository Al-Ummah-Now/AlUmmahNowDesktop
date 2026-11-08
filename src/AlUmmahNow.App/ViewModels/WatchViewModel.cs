using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Media;
using AlUmmahNow.CoreBridge;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AlUmmahNow.App.ViewModels
{
    public sealed class QualityOption
    {
        public QualityOption(string label, string url)
        {
            Label = label;
            Url = url;
        }

        public string Label { get; }
        public string Url { get; }
    }

    public sealed partial class WatchViewModel : ObservableObject, IDisposable
    {
        public const double SkipSeconds = 10;
        public static readonly IReadOnlyList<double> Speeds = new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };

        private readonly ICatalogClient _catalog;
        private readonly IMediaPlayer _player;
        private readonly IMediaSession _session;
        private readonly Localizer _localizer;
        private IReadOnlyList<Cue> _cues = Array.Empty<Cue>();
        private int _subtitleRequest;

        [ObservableProperty] private LoadState _state = LoadState.Loading;
        [ObservableProperty] private Video? _video;
        [ObservableProperty] private QualityOption? _quality;
        [ObservableProperty] private SubtitleTrack? _subtitle;
        [ObservableProperty] private string _caption = "";
        [ObservableProperty] private Chapter? _currentChapter;
        [ObservableProperty] private double _position;
        [ObservableProperty] private double _duration;
        [ObservableProperty] private double _speed = 1.0;
        [ObservableProperty] private bool _isPlaying;
        [ObservableProperty] private bool _playbackFailed;

        public WatchViewModel(ICatalogClient catalog, IMediaPlayer player, IMediaSession session, Localizer localizer)
        {
            _catalog = catalog;
            _player = player;
            _session = session;
            _localizer = localizer;
            _player.StateChanged += OnPlayerState;
            _player.PositionChanged += OnPlayerPosition;
            _session.Command += OnSessionCommand;
        }

        public ObservableCollection<QualityOption> Qualities { get; } = new();
        public ObservableCollection<SubtitleTrack> SubtitleTracks { get; } = new();

        public string Title => Video?.Title.For(_localizer.Language) ?? "";
        public string Description => Video?.Description.For(_localizer.Language) ?? "";
        public string Channel => Video?.ChannelName ?? "";

        public async Task LoadAsync(string videoId, CancellationToken cancellationToken = default)
        {
            State = LoadState.Loading;
            PlaybackFailed = false;
            try
            {
                var video = await _catalog.GetVideoAsync(videoId, cancellationToken);
                Video = video;
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Description));
                OnPropertyChanged(nameof(Channel));
                if (string.IsNullOrEmpty(video.PlayUrl))
                {
                    State = LoadState.Empty;
                    return;
                }

                await LoadQualitiesAsync(video.PlayUrl!, cancellationToken);
                await LoadSubtitlesAsync(videoId, cancellationToken);

                State = LoadState.Content;
                _session.SetMetadata(Title, Channel, video.ThumbnailUrl, video.DurationSeconds ?? 0);
                _player.Open(Quality!.Url, 0, autoPlay: true);
            }
            catch (CatalogException)
            {
                State = LoadState.Error;
            }
            catch (System.Text.Json.JsonException)
            {
                State = LoadState.Error;
            }
        }

        /// <summary>Switches rendition, resuming at the current position and play state.</summary>
        public void SelectQuality(QualityOption option)
        {
            if (ReferenceEquals(option, Quality)) return;
            var resume = _player.Position;
            var wasPlaying = _player.State == PlayerState.Playing;
            Quality = option;
            _player.Open(option.Url, resume, wasPlaying);
        }

        public void SelectSpeed(double speed)
        {
            Speed = speed;
            _player.Rate = speed;
        }

        public async Task SelectSubtitleAsync(SubtitleTrack? track, CancellationToken cancellationToken = default)
        {
            var request = ++_subtitleRequest;
            Subtitle = track;
            _cues = Array.Empty<Cue>();
            Caption = "";
            if (track == null) return;
            try
            {
                var text = await _catalog.GetTextAsync(track.Url, cancellationToken);
                if (request != _subtitleRequest) return;
                _cues = WebVtt.Parse(text);
                UpdateTimedText(_player.Position);
            }
            catch (CatalogException)
            {
                // Playback continues without captions.
            }
        }

        [RelayCommand]
        public void TogglePlay()
        {
            if (_player.State == PlayerState.Playing) _player.Pause();
            else _player.Play();
        }

        [RelayCommand]
        public void SkipForward() => Seek(_player.Position + SkipSeconds);

        [RelayCommand]
        public void SkipBack() => Seek(_player.Position - SkipSeconds);

        public void Seek(double position)
        {
            var max = _player.Duration > 0 ? _player.Duration : double.MaxValue;
            _player.Seek(Math.Max(0, Math.Min(position, max)));
        }

        public void Dispose()
        {
            _player.StateChanged -= OnPlayerState;
            _player.PositionChanged -= OnPlayerPosition;
            _session.Command -= OnSessionCommand;
            _player.Stop();
            _session.Clear();
        }

        private async Task LoadQualitiesAsync(string playUrl, CancellationToken cancellationToken)
        {
            Qualities.Clear();
            var auto = new QualityOption(_localizer["player.quality.auto"], playUrl);
            Qualities.Add(auto);
            Quality = auto;
            try
            {
                var master = await _catalog.GetTextAsync(playUrl, cancellationToken);
                foreach (var v in HlsPlaylist.ParseVariants(master, playUrl)) Qualities.Add(new QualityOption(v.Label, v.Url));
            }
            catch (CatalogException)
            {
                // The player can still try the master playlist; manual quality is just unavailable.
            }
        }

        private async Task LoadSubtitlesAsync(string videoId, CancellationToken cancellationToken)
        {
            SubtitleTracks.Clear();
            IReadOnlyList<SubtitleTrack> tracks;
            try
            {
                tracks = await _catalog.GetSubtitleTracksAsync(videoId, cancellationToken);
            }
            catch (CatalogException)
            {
                return;
            }

            foreach (var t in tracks) SubtitleTracks.Add(t);
            var preferred = tracks.FirstOrDefault(t => t.Language == _localizer.Language) ?? tracks.FirstOrDefault(t => t.IsDefault);
            if (preferred != null) await SelectSubtitleAsync(preferred, cancellationToken);
        }

        private void OnPlayerState(object? sender, EventArgs e)
        {
            IsPlaying = _player.State == PlayerState.Playing;
            PlaybackFailed = _player.State == PlayerState.Failed;
            _session.SetPlayback(_player.State, _player.Position, _player.Rate);
        }

        private void OnPlayerPosition(object? sender, EventArgs e)
        {
            Position = _player.Position;
            Duration = _player.Duration;
            UpdateTimedText(Position);
        }

        private void UpdateTimedText(double position)
        {
            Caption = WebVtt.At(_cues, position)?.Text ?? "";
            CurrentChapter = Video?.Chapters.FirstOrDefault(c => position >= c.Start && position < c.End);
        }

        private void OnSessionCommand(object? sender, MediaCommand command)
        {
            switch (command)
            {
                case MediaCommand.Play: _player.Play(); break;
                case MediaCommand.Pause: _player.Pause(); break;
                case MediaCommand.Toggle: TogglePlay(); break;
                case MediaCommand.SkipForward:
                case MediaCommand.Next: SkipForward(); break;
                case MediaCommand.SkipBack:
                case MediaCommand.Previous: SkipBack(); break;
            }
        }
    }
}
