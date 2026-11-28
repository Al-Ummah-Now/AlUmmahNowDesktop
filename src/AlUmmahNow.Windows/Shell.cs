using AlUmmahNow.App.Theme;
using AlUmmahNow.Contrast;
using System.ComponentModel;
using System.Globalization;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Live;
using AlUmmahNow.App.Navigation;
using AlUmmahNow.App.Services;
using AlUmmahNow.App.Updates;
using AlUmmahNow.App.ViewModels;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Native;
using AlUmmahNow.Updates.Views;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace AlUmmahNow.Windows;

/// <summary>Renders the current route. Views are rebuilt on navigation and language change.</summary>
public sealed partial class Shell
{
    private static readonly global::Windows.UI.Color Night = global::Windows.UI.Color.FromArgb(255, 9, 48, 65);
    private static readonly global::Windows.UI.Color Parchment = global::Windows.UI.Color.FromArgb(255, 247, 241, 232);

    private readonly ICatalogClient _catalog;
    private readonly Localizer _localizer;
    private readonly Navigator _navigator;
    private readonly WindowsMediaPlayer _player;
    private readonly SmtcSession _session;
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly AuthViewModel _auth;
    private readonly Grid _root = new();
    private readonly PageRenderer _page;
    private WatchViewModel? _watch;
    private string? _watchId;
    private Action? _unbind;
    private readonly UpdateService _updates;
    private readonly Action _quit;
    private readonly ITelemetryService _telemetry;

    public Shell(ICatalogClient catalog, Localizer localizer, Navigator navigator, WindowsMediaPlayer player, SmtcSession session, AppSettings settings, SettingsStore store, AuthViewModel auth, ILiveClient live, string shortLinkHost, UpdateService updates, Action quit, ITelemetryService telemetry)
    {
        _updates = updates;
        _quit = quit;
        _telemetry = telemetry;
        _live = live;
        _shortLinkHost = shortLinkHost;
        LiveList = new LiveListViewModel(live, localizer, navigator);
        _page = new PageRenderer(Build);
        _livePage = new LivePage(_page, id => new LiveWatchViewModel(id, live, catalog, player, session, localizer) { IsHidden = _isHidden });
        _auth = auth;
        auth.PropertyChanged += (_, _) => Render();
        _catalog = catalog;
        _localizer = localizer;
        _navigator = navigator;
        _player = player;
        _session = session;
        _settings = settings;
        _store = store;
        Home = new HomeViewModel(catalog, localizer, navigator);
        navigator.Changed += (_, _) =>
        {
            Render();
            if (navigator.Current is not Route.Watch && navigator.Current is not Route.Live) _ = RefreshLiveAsync();
        };
        localizer.LanguageChanged += (_, _) => Render();
        // Every brush follows high contrast itself: no box behind a text on a highlight
        _root.HighContrastAdjustment = ElementHighContrastAdjustment.None;
        AddKey(VirtualKey.Space, () => Watch?.TogglePlay());
        AddKey(VirtualKey.Left, () => Watch?.SkipByArrow(right: false));
        AddKey(VirtualKey.Right, () => Watch?.SkipByArrow(right: true));
    }

    /// <summary>The update bar, screen and section of the page that is there.</summary>
    internal UpdateViews Updates { get; private set; } = null!;

    public Panel Root => _root;
    public HomeViewModel Home { get; }
    public WatchViewModel? Watch => _watch ?? LiveWatch?.Watch;

    public async Task StartAsync()
    {
        Render();
        await Task.WhenAll(Home.LoadAsync(), LiveList.LoadAsync());
        Render();
    }

    private void AddKey(VirtualKey key, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key };
        accelerator.Invoked += (_, a) =>
        {
            if (FocusManager.GetFocusedElement(_root.XamlRoot) is HyperlinkButton { Name: "QuranCaptionCreditLink" }) return;
            action();
            a.Handled = true;
        };
        _root.KeyboardAccelerators.Add(accelerator);
    }

    public bool IsDark => _settings.Theme != ThemePreference.Light;

    /// <summary>The header and one page: what the window must hold (CI's scenarios ask).</summary>
    public bool IsBuiltOnce => _root.Children.Count == 2;

    public void Rebuild() => Render();

    private void Render() => _page.Render();

    private void Build()
    {
        _unbind?.Invoke();
        _unbind = null;
        _root.FlowDirection = _localizer.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _root.RequestedTheme = IsDark ? ElementTheme.Dark : ElementTheme.Light;
        _root.Background = SystemBrushes.Brush(IsDark ? Night : Parchment, HighContrastRole.Window);
        _root.Children.Clear();
        _liveTitles.Clear();
        _root.RowDefinitions.Clear();
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Updates?.Dispose();
        Updates = new UpdateViews(_updates, new UpdateText(_localizer, UpdateApp.Viewer, _updates), UpdateText.Icon(UpdateApp.Viewer), _quit);
        Updates.ForcedChanged += Render;
        var header = new StackPanel();
        header.Children.Add(BuildHeader());
        header.Children.Add(Updates.Bar);
        Grid.SetRow(header, 0);
        _root.Children.Add(header);
        if (Updates.IsForced)
        {
            _livePage.Close();
            CloseWatch();
            Grid.SetRow(Updates.Forced, 1);
            _root.Children.Add(Updates.Forced);
            Updates.FocusForced();
            return;
        }

        if (_navigator.Current is not Route.Live) _livePage.Close();
        if (_navigator.Current is not Route.Watch) CloseWatch();
        FrameworkElement content;
        switch (_navigator.Current)
        {
            case Route.Watch route:
                EnsureWatch(route.VideoId);
                content = BuildWatch(_watch!);
                break;
            case Route.Live route:
                content = BuildLive(_livePage.Open(route.StreamId));
                break;
            case Route.LiveList:
                content = BuildLiveList();
                break;
            case Route.Settings:
                content = BuildSettings();
                break;
            default:
                content = BuildHome();
                break;
        }

        Grid.SetRow(content, 1);
        _root.Children.Add(content);
    }

    private void EnsureWatch(string id)
    {
        if (_watch != null && _watchId == id) return;
        CloseWatch();
        _watchId = id;
        var vm = _watch = new WatchViewModel(_catalog, _player, _session, _localizer);
        _page.After(() =>
        {
            if (ReferenceEquals(vm, _watch)) _ = LoadWatchAsync(vm, id);
        });
    }

    private async Task LoadWatchAsync(WatchViewModel vm, string id)
    {
        await vm.LoadAsync(id);
        if (ReferenceEquals(vm, _watch)) Render();
    }

    private void CloseWatch()
    {
        _watch?.Dispose();
        _watch = null;
        _watchId = null;
    }

    private FrameworkElement BuildHeader()
    {
        var bar = new Grid { Padding = new Thickness(16, 10, 16, 10) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var start = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        if (_navigator.CanGoBack && !Updates.IsForced)
        {
            var back = new Button { Content = new FontIcon { Glyph = "\uE72B", MirroredWhenRightToLeft = true } };
            ToolTipService.SetToolTip(back, _localizer["nav.back"]);
            AutomationName(back, _localizer["nav.back"]);
            back.Click += (_, _) => _navigator.Back();
            start.Children.Add(back);
        }

        start.Children.Add(new TextBlock { Text = _localizer["app.name"], FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(start);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (!Updates.IsForced) actions.Children.Add(BuildAccount());
        if (!Updates.IsForced && _navigator.Current is not Route.Settings)
        {
            var settings = new Button { Content = new FontIcon { Glyph = "\uE713", FontSize = 16 } };
            ToolTipService.SetToolTip(settings, _localizer["updates.settings"]);
            AutomationName(settings, _localizer["updates.settings"]);
            settings.Click += (_, _) => _navigator.Push(new Route.Settings());
            actions.Children.Add(settings);
        }

        var theme = new Button { Content = _localizer[IsDark ? "settings.theme.light" : "settings.theme.dark"] };
        AutomationName(theme, _localizer["settings.theme"]);
        theme.Click += (_, _) =>
        {
            _settings.Theme = IsDark ? ThemePreference.Light : ThemePreference.Dark;
            _store.Save(_settings);
            Render();
        };
        actions.Children.Add(theme);

        var language = new Button { Content = _localizer[_localizer.IsRtl ? "settings.language.en" : "settings.language.ar"] };
        AutomationName(language, _localizer["settings.language"]);
        language.Click += (_, _) =>
        {
            _settings.Language = _localizer.IsRtl ? "en" : "ar";
            _store.Save(_settings);
            _localizer.Language = _settings.Language;
        };
        actions.Children.Add(language);
        Grid.SetColumn(actions, 2);
        bar.Children.Add(actions);
        return bar;
    }

    private FrameworkElement BuildAccount()
    {
        if (_auth.IsBusy) return new TextBlock { Text = _localizer["auth.signingIn"], VerticalAlignment = VerticalAlignment.Center }.Dim(0.7);

        var button = new Button { Content = _localizer[_auth.IsSignedIn ? "auth.signOut" : "auth.signIn"] };
        var tip = _auth.Failed ? _localizer["auth.failed"] : Localizer.Isolate(_auth.AccountName);
        if (tip.Length > 2) ToolTipService.SetToolTip(button, tip);
        button.Click += async (_, _) =>
        {
            if (_auth.IsSignedIn) _auth.SignOut();
            else await _auth.SignInAsync();
        };
        return button;
    }

    private static void AutomationName(DependencyObject element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);

    private FrameworkElement Message(string key, bool retry) => Message(key, retry ? () => Home.LoadAsync() : null);

    private FrameworkElement Message(string key, Func<Task>? retry)
    {
        var panel = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = _localizer[key], TextAlignment = TextAlignment.Center });
        if (retry != null)
        {
            var button = new Button { Content = _localizer["action.retry"], HorizontalAlignment = HorizontalAlignment.Center };
            button.Click += async (_, _) =>
            {
                await retry();
                Render();
            };
            panel.Children.Add(button);
        }

        return panel;
    }

    /// <summary>Settings: Diagnostics, and the updates while the server's switch is on. Theme and language are in the header.</summary>
    private FrameworkElement BuildSettings()
    {
        var title = new TextBlock { Text = _localizer["updates.settings"], FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.Bold };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        var column = new StackPanel { Spacing = 18, Padding = new Thickness(24, 18, 24, 24) };
        column.Children.Add(title);
        column.Children.Add(BuildDiagnostics());
        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(Updates.SectionBody);
        body.Children.Add(Updates.SectionActions);
        var card = Updates.Card(body);
        Updates.UseSection(card);
        column.Children.Add(card);
        return new ScrollViewer { Content = column };
    }

    /// <summary>Diagnostics: off until turned on; turning it off deletes what was kept.</summary>
    private FrameworkElement BuildDiagnostics()
    {
        var body = new StackPanel { Spacing = 10 };
        var heading = new TextBlock { Text = _localizer["settings.telemetry"], FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        body.Children.Add(heading);
        var choice = new RadioButtons { ItemsSource = new[] { _localizer["settings.telemetry.off"], _localizer["settings.telemetry.on"] }, SelectedIndex = _settings.TelemetryEnabled ? 1 : 0 };
        AutomationName(choice, _localizer["settings.telemetry"]);
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedIndex < 0) return;
            _settings.TelemetryEnabled = choice.SelectedIndex == 1;
            _store.Save(_settings);
            _telemetry.SetEnabled(_settings.TelemetryEnabled);
        };
        body.Children.Add(choice);
        body.Children.Add(new TextBlock
        {
            Text = _localizer["settings.telemetry.note"],
            TextWrapping = TextWrapping.Wrap,
            TextReadingOrder = TextReadingOrder.UseFlowDirection,
            Foreground = ViewerTheme.Brush("TextMuted", IsDark, HighContrastRole.Text),
        });
        return Updates.Card(body);
    }

    private FrameworkElement BuildHome()
    {
        switch (Home.State)
        {
            case LoadState.Loading: return Message("state.loading", false);
            case LoadState.Error: return Message("state.error", true);
            case LoadState.Empty: return Message("state.empty", false);
        }

        var column = new StackPanel { Spacing = 20, Padding = new Thickness(20, 8, 20, 24) };
        if (LiveList.All.Count > 0) AppendLiveRail(column);
        foreach (var rail in Home.Rails)
        {
            column.Children.Add(new TextBlock { Text = rail.Title, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            // One row of RailColumns whole cards that share the width (a short rail keeps the same card size): never a
            // card cut off at the window edge, as a horizontally scrolled rail had.
            var grid = new Grid { ColumnSpacing = 14 };
            for (var i = 0; i < RailColumns; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (i >= rail.Items.Count) continue;
                var card = BuildCard(rail.Items[i]);
                Grid.SetColumn(card, i);
                grid.Children.Add(card);
            }

            column.Children.Add(grid);
        }

        return new ScrollViewer { Content = column };
    }

    /// <summary>Cards per home rail: four, sharing the width.</summary>
    private const int RailColumns = 4;

    private FrameworkElement BuildCard(VideoCardViewModel card)
    {
        var panel = new StackPanel { Spacing = 4 };
        var image = new Image { Height = 146, Stretch = Stretch.UniformToFill };
        // 16:9 at the card's width (the cards share the rail's width).
        panel.SizeChanged += (_, e) => image.Height = e.NewSize.Width * 9 / 16;
        if (!string.IsNullOrEmpty(card.ThumbnailUrl)) image.Source = new BitmapImage(new Uri(card.ThumbnailUrl));
        panel.Children.Add(image);
        panel.Children.Add(new TextBlock { Text = card.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        // Channel on one line, views · duration on the next, so a compact Arabic count never trims the duration.
        foreach (var text in new[] { card.Channel, card.Stats })
        {
            if (!string.IsNullOrEmpty(text)) panel.Children.Add(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis }.Dim(0.7));
        }
        var button = new Button { Content = panel, Padding = new Thickness(6), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top };
        AutomationName(button, card.Title);
        button.Click += (_, _) => Home.Open(card);
        return button;
    }

    /// <param name="heading">What stands under the video in place of the title and the channel.</param>
    private FrameworkElement BuildWatch(WatchViewModel vm, FrameworkElement? heading = null)
    {
        if (vm.State == LoadState.Loading) return Message("state.loading", false);
        if (vm.State != LoadState.Content) return Message(vm.State == LoadState.Error ? "state.error" : "state.empty", false);

        var grid = new Grid { Padding = new Thickness(20, 4, 20, 16), RowSpacing = 6 };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var surface = new Grid { Background = SystemBrushes.Brush(Colors.Black, HighContrastRole.Window) };
        var element = new MediaPlayerElement { Stretch = Stretch.Uniform, AreTransportControlsEnabled = false };
        element.SetMediaPlayer(_player.Player);
        // The picture itself must not mirror; only the captions and controls follow the language.
        element.FlowDirection = FlowDirection.LeftToRight;
        surface.Children.Add(element);
        var caption = new TextBlock { FontSize = 20, Foreground = SystemBrushes.Brush(Parchment, HighContrastRole.Text), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        var credit = new HyperlinkButton
        {
            Name = "QuranCaptionCreditLink",
            FontSize = 14,
            Foreground = SystemBrushes.Brush(Parchment, HighContrastRole.Text),
            Padding = new Thickness(0),
            MinHeight = 0,
        };
        credit.Click += (_, _) => vm.OpenCaptionCredit(new ShellBrowserLauncher());
        var captionRows = new StackPanel { Spacing = 2 };
        captionRows.Children.Add(caption);
        captionRows.Children.Add(credit);
        var captionBox = new Border
        {
            Child = captionRows,
            Background = SystemBrushes.Brush(global::Windows.UI.Color.FromArgb(210, 9, 48, 65), HighContrastRole.Window),
            Padding = new Thickness(14, 6, 14, 6),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 24),
        };
        surface.Children.Add(captionBox);
        grid.Children.Add(surface);

        if (heading != null)
        {
            Grid.SetRow(heading, 1);
            grid.Children.Add(heading);
        }
        else
        {
            var title = new TextBlock { Text = vm.Title, FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(title, 1);
            grid.Children.Add(title);
            var channel = new TextBlock { Text = vm.Channel }.Dim(0.7);
            Grid.SetRow(channel, 2);
            grid.Children.Add(channel);
        }

        var controls = new Grid { ColumnSpacing = 8 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var transport = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, FlowDirection = FlowDirection.LeftToRight };
        var back = Icon("", "player.seekBack", () => vm.SkipBack());
        var play = Icon(vm.IsPlaying ? "" : "", vm.IsPlaying ? "player.pause" : "player.play", () => vm.TogglePlay());
        var forward = Icon("", "player.seekForward", () => vm.SkipForward());
        transport.Children.Add(back);
        transport.Children.Add(play);
        transport.Children.Add(forward);
        controls.Children.Add(transport);

        // Media time never mirrors (Apple HIG, Material; our web and mobile players): the seek bar runs left to
        // right in every language.
        var slider = new Slider { Minimum = 0, Maximum = Math.Max(1, vm.Duration), VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
        var syncing = false;
        slider.ValueChanged += (_, e) =>
        {
            if (!syncing) vm.Seek(e.NewValue);
        };
        Grid.SetColumn(slider, 1);
        controls.Children.Add(slider);

        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var time = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        options.Children.Add(time);
        var speeds = WatchViewModel.Speeds.ToList();
        var speed = Selector("player.speed", speeds.Select(s => _localizer.Speed(s)).ToList(), speeds.IndexOf(vm.Speed), i => vm.SelectSpeed(speeds[i]));
        options.Children.Add(speed);
        // A live stream has no place to go to and no speed to choose
        foreach (var part in new FrameworkElement[] { back, forward, slider, time, speed }) part.Visibility = vm.IsLive ? Visibility.Collapsed : Visibility.Visible;
        options.Children.Add(Selector("player.quality", vm.Qualities.Select((q, i) => i == 0 ? _localizer["player.quality.auto"] : q.Label).ToList(),
            vm.Qualities.IndexOf(vm.Quality!), i => vm.SelectQuality(vm.Qualities[i])));
        var tracks = vm.SubtitleTracks.ToList();
        options.Children.Add(Selector("player.subtitles", new[] { _localizer["player.subtitles.off"] }.Concat(tracks.Select(t => t.Label)).ToList(),
            vm.Subtitle == null ? 0 : tracks.IndexOf(vm.Subtitle) + 1, i => _ = vm.SelectSubtitleAsync(i == 0 ? null : tracks[i - 1])));
        Grid.SetColumn(options, 2);
        controls.Children.Add(options);
        Grid.SetRow(controls, 3);
        grid.Children.Add(controls);

        var chapter = new TextBlock().Dim(0.7);
        Grid.SetRow(chapter, 4);
        grid.Children.Add(chapter);

        void Sync(object? s, PropertyChangedEventArgs e)
        {
            caption.Text = vm.Caption;
            if (e.PropertyName is null or nameof(WatchViewModel.CaptionCredit))
            {
                var attribution = vm.CaptionCredit;
                credit.Visibility = attribution != null ? Visibility.Visible : Visibility.Collapsed;
                if (attribution != null)
                {
                    var rtl = attribution.Language == "ar";
                    captionRows.FlowDirection = FlowDirection.LeftToRight;
                    caption.FlowDirection = credit.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
                    credit.HorizontalAlignment = rtl ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                    credit.Content = attribution.DisplayLabel;
                    AutomationName(credit, vm.CaptionCreditAccessibleName!);
                }
                else
                {
                    captionRows.ClearValue(FrameworkElement.FlowDirectionProperty);
                    caption.ClearValue(FrameworkElement.FlowDirectionProperty);
                }
            }
            captionBox.Visibility = vm.Caption.Length > 0 || vm.CaptionCredit != null ? Visibility.Visible : Visibility.Collapsed;
            chapter.Text = vm.CurrentChapter?.Title.For(_localizer.Language) ?? "";
            time.Text = _localizer.Duration(vm.Position) + " / " + _localizer.Duration(vm.Duration);
            ((FontIcon)play.Content).Glyph = vm.IsPlaying ? "" : "";
            AutomationName(play, _localizer[vm.IsPlaying ? "player.pause" : "player.play"]);
            if (speeds.IndexOf(vm.Speed) is var at && at >= 0 && speed.SelectedIndex != at) speed.SelectedIndex = at;
            syncing = true;
            if (vm.Duration > 0) slider.Maximum = vm.Duration;
            slider.Value = vm.Position;
            syncing = false;
        }

        vm.PropertyChanged += Sync;
        _unbind += () => vm.PropertyChanged -= Sync;
        Sync(null, new PropertyChangedEventArgs(null));
        return grid;
    }

    private Button Icon(string glyph, string key, Action action)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, MirroredWhenRightToLeft = glyph != "\uE769" && glyph != "\uE768" } };
        AutomationName(button, _localizer[key]);
        ToolTipService.SetToolTip(button, _localizer[key]);
        button.Click += (_, _) => action();
        return button;
    }

    private ComboBox Selector(string key, IReadOnlyList<string> items, int selected, Action<int> onChange)
    {
        var box = new ComboBox { MinWidth = 90 };
        foreach (var item in items) box.Items.Add(item);
        box.SelectedIndex = selected;
        AutomationName(box, _localizer[key]);
        ToolTipService.SetToolTip(box, _localizer[key]);
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0) onChange(box.SelectedIndex);
        };
        return box;
    }
}
