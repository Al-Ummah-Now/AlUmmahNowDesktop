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
        private Cue? _activeCue;
        private int _subtitleRequest;
        private bool _isDisposed;

        [ObservableProperty] private LoadState _state = LoadState.Loading;
        [ObservableProperty] private Video? _video;
        [ObservableProperty] private QualityOption? _quality;
        [ObservableProperty] private SubtitleTrack? _subtitle;
        [ObservableProperty] private string _caption = "";
        [ObservableProperty] private QuranCaptionCredit? _captionCredit;
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

        public string PlainCaption => _activeCue?.Text ?? "";
        public string? CaptionCreditAccessibleName => CaptionCredit?.Language is { } language
            ? _localizer.Bundle(language)["player.caption.quranCredit.browser"]
            : null;

        public void OpenCaptionCredit(IBrowserLauncher browser)
        {
            if (CaptionCredit != null) browser.Open(QuranCaptionCredit.Url);
        }

        public string Title => Video?.Title.For(_localizer.Language) ?? "";
        public string Description => Video?.Description.For(_localizer.Language) ?? "";
        public string Channel => Video?.ChannelName ?? "";
        public string ShareAtPositionLabel => _localizer.Format("watch.shareAtTime", _localizer.Duration(Position));

        /// <summary>Builds the canonical <c>alummahnow.link</c> short URL for the loaded video (#39).</summary>
        public string? ShareUrl(string shortLinkHost = ShortLinks.ProdHost, bool atCurrentPosition = false)
        {
            if (Video == null || string.IsNullOrEmpty(Video.Id)) return null;
            var secs = atCurrentPosition && Position >= 1 ? (long?)Math.Floor(Position) : null;
            var lang = _localizer.Language == "ar" ? "ar" : null;
            return ShortLinks.Video(Video.Id, shortLinkHost, startSeconds: secs, lang: lang);
        }

        /// <summary>A live stream: nothing to seek in, and no speed to choose.</summary>
        public bool IsLive { get; private set; }

        /// <summary>Plays an address that is not a video of the catalog: a live stream, or its recording.</summary>
        public void LoadStream(string url, string title, string channel, string? thumbnailUrl, bool isLive)
        {
            IsLive = isLive;
            if (isLive)
            {
                Speed = 1;
                _player.Rate = 1;
            }

            Video = new Video { Title = new LocalizedText(title), ChannelName = channel, ThumbnailUrl = thumbnailUrl, PlayUrl = url };
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Channel));
            Qualities.Clear();
            var auto = new QualityOption(_localizer["player.quality.auto"], url);
            Qualities.Add(auto);
            Quality = auto;
            PlaybackFailed = false;
            State = LoadState.Content;
            _session.SetMetadata(title, channel, thumbnailUrl, 0);
            _player.Open(url, 0, autoPlay: true);
        }

        /// <summary>Opens a live stream again, at its newest moment.</summary>
        public void Reopen()
        {
            if (Quality is { } quality) _player.Open(quality.Url, 0, autoPlay: true);
        }

        public async Task LoadAsync(string videoId, CancellationToken cancellationToken = default)
        {
            State = LoadState.Loading;
            PlaybackFailed = false;
            try
            {
                await ShowAsync(await _catalog.GetVideoAsync(videoId, cancellationToken), null, cancellationToken);
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

        /// <summary>Plays a video that has been read already.</summary>
        /// <param name="start">Where playback begins, asked when it does; null for the beginning.</param>
        public async Task ShowAsync(Video video, Func<double>? start, CancellationToken cancellationToken = default)
        {
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
            await LoadSubtitlesAsync(video.Id, cancellationToken);
            // Left while it loaded: the player is another page's
            if (_isDisposed) return;

            State = LoadState.Content;
            _session.SetMetadata(Title, Channel, video.ThumbnailUrl, video.DurationSeconds ?? 0);
            _player.Open(Quality!.Url, start?.Invoke() ?? 0, autoPlay: true);
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
            if (IsLive) return;
            Speed = speed;
            _player.Rate = speed;
        }

        public async Task SelectSubtitleAsync(SubtitleTrack? track, CancellationToken cancellationToken = default)
        {
            var request = ++_subtitleRequest;
            Subtitle = track;
            _cues = Array.Empty<Cue>();
            SetActiveCue(null);
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

        /// <summary>
        /// The left or right arrow key. The seek bar runs left to right in every language, so the right arrow always
        /// seeks forward and the left one back: each moves the handle its way (the web player's rule).
        /// </summary>
        public void SkipByArrow(bool right)
        {
            if (right) SkipForward();
            else SkipBack();
        }

        public void Seek(double position)
        {
            if (IsLive) return;
            var max = _player.Duration > 0 ? _player.Duration : double.MaxValue;
            _player.Seek(Math.Max(0, Math.Min(position, max)));
        }

        public void Dispose()
        {
            _isDisposed = true;
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
                foreach (var v in HlsPlaylist.ParseVariants(master, playUrl)) Qualities.Add(new QualityOption(_localizer.Quality(v.Height, v.Bandwidth), v.Url));
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
            SetActiveCue(WebVtt.At(_cues, position));
            CurrentChapter = Video?.Chapters.FirstOrDefault(c => position >= c.Start && position < c.End);
        }

        private void SetActiveCue(Cue? cue)
        {
            if (ReferenceEquals(_activeCue, cue)) return;
            _activeCue = cue;
            var result = QuranCaptionCredit.SplitQuranCredit(PlainCaption);
            Caption = result.Text;
            CaptionCredit = result.Credited ? result : null;
            OnPropertyChanged(nameof(PlainCaption));
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
