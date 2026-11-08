using AlUmmahNow.App.Media;
using Microsoft.UI.Dispatching;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace AlUmmahNow.Windows;

/// <summary>Windows.Media.Playback behind the shared player contract. Events reach the UI thread.</summary>
public sealed class WindowsMediaPlayer : IMediaPlayer
{
    private readonly DispatcherQueue _ui;
    private readonly DispatcherQueueTimer _timer;
    private double _pendingStart = -1;
    private bool _autoPlay;
    private PlayerState _state = PlayerState.Idle;

    public WindowsMediaPlayer(DispatcherQueue ui)
    {
        _ui = ui;
        Player = new MediaPlayer { AutoPlay = false };
        Player.CommandManager.IsEnabled = false;
        Player.MediaOpened += (_, _) => _ui.TryEnqueue(OnOpened);
        Player.MediaFailed += (_, e) => _ui.TryEnqueue(() =>
        {
            Console.Error.WriteLine("playback error: " + e.Error + " " + e.ErrorMessage);
            SetState(PlayerState.Failed);
        });
        Player.MediaEnded += (_, _) => _ui.TryEnqueue(() => SetState(PlayerState.Ended));
        Player.PlaybackSession.PlaybackStateChanged += (_, _) => _ui.TryEnqueue(SyncState);
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? StateChanged;
    public event EventHandler? PositionChanged;

    public MediaPlayer Player { get; }
    public PlayerState State => _state;
    public double Position => Player.PlaybackSession.Position.TotalSeconds;
    public double Duration => Player.PlaybackSession.NaturalDuration.TotalSeconds;

    public double Rate
    {
        get => Player.PlaybackSession.PlaybackRate;
        set => Player.PlaybackSession.PlaybackRate = value;
    }

    public void Open(string url, double startPosition, bool autoPlay)
    {
        _pendingStart = startPosition;
        _autoPlay = autoPlay;
        SetState(PlayerState.Loading);
        var rate = Player.PlaybackSession.PlaybackRate;
        Player.Source = MediaSource.CreateFromUri(new Uri(url));
        Player.PlaybackSession.PlaybackRate = rate;
        _timer.Start();
    }

    public void Play() => Player.Play();
    public void Pause() => Player.Pause();
    public void Seek(double position) => Player.PlaybackSession.Position = TimeSpan.FromSeconds(position);

    public void Stop()
    {
        _timer.Stop();
        Player.Pause();
        Player.Source = null;
        SetState(PlayerState.Idle);
    }

    private void OnOpened()
    {
        if (_pendingStart > 0) Seek(_pendingStart);
        _pendingStart = -1;
        if (_autoPlay) Player.Play();
        else SetState(PlayerState.Paused);
    }

    private void SyncState()
    {
        switch (Player.PlaybackSession.PlaybackState)
        {
            case MediaPlaybackState.Playing: SetState(PlayerState.Playing); break;
            case MediaPlaybackState.Paused when _state != PlayerState.Ended && _state != PlayerState.Loading: SetState(PlayerState.Paused); break;
        }
    }

    private void SetState(PlayerState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>System Media Transport Controls: media keys and the Windows volume flyout.</summary>
public sealed class SmtcSession : IMediaSession
{
    private readonly global::Windows.Media.SystemMediaTransportControls _smtc;
    private readonly DispatcherQueue _ui;

    public SmtcSession(MediaPlayer player, DispatcherQueue ui)
    {
        _ui = ui;
        _smtc = player.SystemMediaTransportControls;
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = _smtc.IsPauseEnabled = _smtc.IsNextEnabled = _smtc.IsPreviousEnabled = true;
        _smtc.ButtonPressed += (_, e) =>
        {
            MediaCommand? command = e.Button switch
            {
                global::Windows.Media.SystemMediaTransportControlsButton.Play => MediaCommand.Play,
                global::Windows.Media.SystemMediaTransportControlsButton.Pause => MediaCommand.Pause,
                global::Windows.Media.SystemMediaTransportControlsButton.Next => MediaCommand.Next,
                global::Windows.Media.SystemMediaTransportControlsButton.Previous => MediaCommand.Previous,
                _ => null,
            };
            if (command is { } c) _ui.TryEnqueue(() => Command?.Invoke(this, c));
        };
    }

    public event EventHandler<MediaCommand>? Command;

    public void SetMetadata(string title, string artist, string? artworkUrl, double duration)
    {
        var updater = _smtc.DisplayUpdater;
        updater.ClearAll();
        updater.Type = global::Windows.Media.MediaPlaybackType.Video;
        updater.VideoProperties.Title = title;
        updater.VideoProperties.Subtitle = artist;
        if (!string.IsNullOrEmpty(artworkUrl))
        {
            updater.Thumbnail = global::Windows.Storage.Streams.RandomAccessStreamReference.CreateFromUri(new Uri(artworkUrl));
        }

        updater.Update();
    }

    public void SetPlayback(PlayerState state, double position, double rate) =>
        _smtc.PlaybackStatus = state switch
        {
            PlayerState.Playing => global::Windows.Media.MediaPlaybackStatus.Playing,
            PlayerState.Paused => global::Windows.Media.MediaPlaybackStatus.Paused,
            PlayerState.Loading => global::Windows.Media.MediaPlaybackStatus.Changing,
            _ => global::Windows.Media.MediaPlaybackStatus.Stopped,
        };

    public void Clear()
    {
        _smtc.DisplayUpdater.ClearAll();
        _smtc.DisplayUpdater.Update();
        _smtc.PlaybackStatus = global::Windows.Media.MediaPlaybackStatus.Closed;
    }
}
