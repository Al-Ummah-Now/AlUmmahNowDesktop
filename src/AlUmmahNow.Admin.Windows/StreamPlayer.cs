using AlUmmahNow.Admin.Media;
using AlUmmahNow.Admin.Review;
using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Web.Http.Filters;

namespace AlUmmahNow.Admin.WindowsApp;

/// <summary>
/// Plays the review's short-lived HLS/DASH stream over TLS, never from a file:
/// AdaptiveMediaSource with an HTTP client whose cache neither reads nor writes, no system media
/// controls (no OS thumbnails or "now playing"), no cast or full-screen buttons, and the URL is
/// dropped as soon as the item closes. Progressive files are refused: Media Foundation's network
/// source can keep a disk cache, adaptive segments do not.
/// </summary>
internal sealed class StreamPlayer
{
    private static readonly Logger Logger = Log.For("player");

    private readonly Localizer _l;
    private readonly MediaPlayerElement _element = new() { AreTransportControlsEnabled = false, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
    private readonly Button _play = new() { MinWidth = 44, MinHeight = 36 };
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1, StepFrequency = 1, MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(12) };
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _tick;
    private MediaPlayer? _player;
    private AdaptiveMediaSource? _source;
    private Windows.Web.Http.HttpClient? _http;
    private bool _seeking;
    private int _generation;

    public StreamPlayer(Localizer l)
    {
        _l = l;
        AutomationProperties.SetName(_element, l["admin.player"]);
        AutomationProperties.SetAutomationId(_element, "Player");
        AutomationProperties.SetAutomationId(_play, "PlayPause");
        AutomationProperties.SetName(_seek, l["admin.player"]);
        _play.Click += (_, _) => Toggle();
        _seek.ValueChanged += (_, e) =>
        {
            if (_seeking || _player == null) return;
            _player.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue);
        };
        var bar = new Grid { ColumnSpacing = 4 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(_play);
        Grid.SetColumn(_seek, 1);
        bar.Children.Add(_seek);
        Grid.SetColumn(_time, 2);
        bar.Children.Add(_time);
        var surface = new Grid { MinHeight = 240, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black) };
        surface.Children.Add(_element);
        surface.Children.Add(_message);
        var root = new StackPanel { Spacing = 4 };
        root.Children.Add(surface);
        root.Children.Add(bar);
        Root = root;
        _tick = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(500);
        _tick.Tick += (_, _) => Refresh();
        Refresh();
    }

    public FrameworkElement Root { get; }

    public bool IsPlaying => _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    /// <summary>Starts streaming <paramref name="stream"/>; null shows "not available".</summary>
    public async Task LoadAsync(ReviewStream? stream)
    {
        Stop();
        var generation = ++_generation;
        if (stream?.Url is not { } url || !PrivateHttp.IsAllowed(url))
        {
            Show(_l["admin.detail.nostream"]);
            return;
        }

        var filter = new HttpBaseProtocolFilter { AllowUI = false };
        filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.NoCache;
        filter.CacheControl.WriteBehavior = HttpCacheWriteBehavior.NoCache;
        filter.CookieUsageBehavior = HttpCookieUsageBehavior.NoCookies;
        var http = new Windows.Web.Http.HttpClient(filter);
        http.DefaultRequestHeaders.CacheControl.TryParseAdd("no-store");
        try
        {
            var created = await AdaptiveMediaSource.CreateFromUriAsync(url, http);
            if (generation != _generation)
            {
                created.MediaSource?.Dispose();
                http.Dispose();
                return;
            }

            if (created.Status != AdaptiveMediaSourceCreationStatus.Success)
            {
                Logger.Warn("stream refused: " + created.Status + " from " + PrivateHttp.Describe(url));
                http.Dispose();
                Show(_l["admin.detail.nostream"]);
                return;
            }

            _http = http;
            _source = created.MediaSource;
            _player = new MediaPlayer { AutoPlay = false, Source = MediaSource.CreateFromAdaptiveMediaSource(_source) };
            _player.CommandManager.IsEnabled = false;
            _element.SetMediaPlayer(_player);
            _message.Visibility = Visibility.Collapsed;
            _tick.Start();
        }
        catch (Exception ex)
        {
            Logger.Warn("stream failed: " + ex.GetType().Name + " from " + PrivateHttp.Describe(url));
            http.Dispose();
            Show(_l["admin.detail.nostream"]);
        }
    }

    public void Toggle()
    {
        if (_player == null) return;
        if (IsPlaying) _player.Pause();
        else _player.Play();
        Refresh();
    }

    public void Pause() => _player?.Pause();

    /// <summary>Drops the player, the source and the signed URL with them.</summary>
    public void Stop()
    {
        _generation++;
        _tick.Stop();
        _element.SetMediaPlayer(null);
        _player?.Pause();
        _player?.Dispose();
        _player = null;
        _source?.Dispose();
        _source = null;
        _http?.Dispose();
        _http = null;
        _message.Visibility = Visibility.Collapsed;
        Refresh();
    }

    private void Show(string text)
    {
        _message.Text = text;
        _message.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        _message.Visibility = Visibility.Visible;
        Refresh();
    }

    private void Refresh()
    {
        var playing = IsPlaying;
        _play.Content = new SymbolIcon(playing ? Symbol.Pause : Symbol.Play);
        AutomationProperties.SetName(_play, _l["admin.key.play"]);
        _play.IsEnabled = _player != null;
        _seek.IsEnabled = _player != null;
        if (_player == null)
        {
            _time.Text = "";
            return;
        }

        var session = _player.PlaybackSession;
        _seeking = true;
        _seek.Maximum = Math.Max(1, session.NaturalDuration.TotalSeconds);
        _seek.Value = Math.Min(_seek.Maximum, session.Position.TotalSeconds);
        _seeking = false;
        _time.Text = Clock(session.Position) + " / " + Clock(session.NaturalDuration);
    }

    internal static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}
