using System.ComponentModel;
using System.Globalization;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Navigation;
using AlUmmahNow.App.Services;
using AlUmmahNow.App.ViewModels;
using AlUmmahNow.CoreBridge;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace AlUmmahNow.Windows;

/// <summary>Renders the current route. Views are rebuilt on navigation and language change.</summary>
public sealed class Shell
{
    private static readonly global::Windows.UI.Color Lantern = global::Windows.UI.Color.FromArgb(255, 255, 160, 55);
    private static readonly global::Windows.UI.Color Night = global::Windows.UI.Color.FromArgb(255, 9, 48, 65);
    private static readonly global::Windows.UI.Color Parchment = global::Windows.UI.Color.FromArgb(255, 247, 241, 232);

    private readonly ICatalogClient _catalog;
    private readonly Localizer _localizer;
    private readonly Navigator _navigator;
    private readonly WindowsMediaPlayer _player;
    private readonly SmtcSession _session;
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly Grid _root = new();
    private WatchViewModel? _watch;
    private string? _watchId;
    private Action? _unbind;

    public Shell(ICatalogClient catalog, Localizer localizer, Navigator navigator, WindowsMediaPlayer player, SmtcSession session, AppSettings settings, SettingsStore store)
    {
        _catalog = catalog;
        _localizer = localizer;
        _navigator = navigator;
        _player = player;
        _session = session;
        _settings = settings;
        _store = store;
        Home = new HomeViewModel(catalog, localizer, navigator);
        navigator.Changed += (_, _) => Render();
        localizer.LanguageChanged += (_, _) => Render();
        AddKey(VirtualKey.Space, () => _watch?.TogglePlay());
        AddKey(VirtualKey.Left, () => Skip(!_localizer.IsRtl));
        AddKey(VirtualKey.Right, () => Skip(_localizer.IsRtl));
    }

    public FrameworkElement Root => _root;
    public HomeViewModel Home { get; }
    public WatchViewModel? Watch => _watch;

    public async Task StartAsync()
    {
        Render();
        await Home.LoadAsync();
        Render();
    }

    private void Skip(bool back)
    {
        if (back) _watch?.SkipBack();
        else _watch?.SkipForward();
    }

    private void AddKey(VirtualKey key, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key };
        accelerator.Invoked += (_, a) =>
        {
            action();
            a.Handled = true;
        };
        _root.KeyboardAccelerators.Add(accelerator);
    }

    private bool IsDark => _settings.Theme != ThemePreference.Light;

    private void Render()
    {
        _unbind?.Invoke();
        _unbind = null;
        _root.FlowDirection = _localizer.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _root.RequestedTheme = IsDark ? ElementTheme.Dark : ElementTheme.Light;
        _root.Background = new SolidColorBrush(IsDark ? Night : Parchment);
        _root.Children.Clear();
        _root.RowDefinitions.Clear();
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = BuildHeader();
        Grid.SetRow(header, 0);
        _root.Children.Add(header);

        FrameworkElement content;
        if (_navigator.Current is Route.Watch route)
        {
            EnsureWatch(route.VideoId);
            content = BuildWatch(_watch!);
        }
        else
        {
            CloseWatch();
            content = BuildHome();
        }

        Grid.SetRow(content, 1);
        _root.Children.Add(content);
    }

    private void EnsureWatch(string id)
    {
        if (_watch != null && _watchId == id) return;
        CloseWatch();
        _watchId = id;
        _watch = new WatchViewModel(_catalog, _player, _session, _localizer);
        _ = LoadWatchAsync(_watch, id);
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
        if (_navigator.CanGoBack)
        {
            var back = new Button { Content = new SymbolIcon(Symbol.Back) };
            ToolTipService.SetToolTip(back, _localizer["nav.back"]);
            AutomationName(back, _localizer["nav.back"]);
            back.Click += (_, _) => _navigator.Back();
            start.Children.Add(back);
        }

        start.Children.Add(new TextBlock { Text = _localizer["app.name"], FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(start);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
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

    private static void AutomationName(DependencyObject element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);

    private FrameworkElement Message(string key, bool retry)
    {
        var panel = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = _localizer[key], TextAlignment = TextAlignment.Center });
        if (retry)
        {
            var button = new Button { Content = _localizer["action.retry"], HorizontalAlignment = HorizontalAlignment.Center };
            button.Click += async (_, _) =>
            {
                await Home.LoadAsync();
                Render();
            };
            panel.Children.Add(button);
        }

        return panel;
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
        foreach (var rail in Home.Rails)
        {
            column.Children.Add(new TextBlock { Text = rail.Title, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            foreach (var card in rail.Items) row.Children.Add(BuildCard(card));
            column.Children.Add(new ScrollViewer
            {
                Content = row,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Enabled,
                VerticalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });
        }

        return new ScrollViewer { Content = column };
    }

    private FrameworkElement BuildCard(VideoCardViewModel card)
    {
        var panel = new StackPanel { Width = 260, Spacing = 4 };
        var image = new Image { Width = 260, Height = 146, Stretch = Stretch.UniformToFill };
        if (!string.IsNullOrEmpty(card.ThumbnailUrl)) image.Source = new BitmapImage(new Uri(card.ThumbnailUrl));
        panel.Children.Add(image);
        panel.Children.Add(new TextBlock { Text = card.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = card.Meta, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
        var button = new Button { Content = panel, Padding = new Thickness(6), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
        AutomationName(button, card.Title);
        button.Click += (_, _) => Home.Open(card);
        return button;
    }

    private FrameworkElement BuildWatch(WatchViewModel vm)
    {
        if (vm.State == LoadState.Loading) return Message("state.loading", false);
        if (vm.State != LoadState.Content) return Message(vm.State == LoadState.Error ? "state.error" : "state.empty", false);

        var grid = new Grid { Padding = new Thickness(20, 4, 20, 16), RowSpacing = 6 };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var surface = new Grid { Background = new SolidColorBrush(Colors.Black) };
        var element = new MediaPlayerElement { Stretch = Stretch.Uniform, AreTransportControlsEnabled = false };
        element.SetMediaPlayer(_player.Player);
        // The picture itself must not mirror; only the captions and controls follow the language.
        element.FlowDirection = FlowDirection.LeftToRight;
        surface.Children.Add(element);
        var caption = new TextBlock { FontSize = 20, Foreground = new SolidColorBrush(Parchment), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        var captionBox = new Border
        {
            Child = caption,
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(210, 9, 48, 65)),
            Padding = new Thickness(14, 6, 14, 6),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 24),
        };
        surface.Children.Add(captionBox);
        grid.Children.Add(surface);

        var title = new TextBlock { Text = vm.Title, FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(title, 1);
        grid.Children.Add(title);
        var channel = new TextBlock { Text = vm.Channel, Opacity = 0.7 };
        Grid.SetRow(channel, 2);
        grid.Children.Add(channel);

        var controls = new Grid { ColumnSpacing = 8 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var transport = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var back = Icon("", "player.seekBack", () => vm.SkipBack());
        var play = Icon(vm.IsPlaying ? "" : "", vm.IsPlaying ? "player.pause" : "player.play", () => vm.TogglePlay());
        var forward = Icon("", "player.seekForward", () => vm.SkipForward());
        transport.Children.Add(back);
        transport.Children.Add(play);
        transport.Children.Add(forward);
        controls.Children.Add(transport);

        var slider = new Slider { Minimum = 0, Maximum = Math.Max(1, vm.Duration), VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Lantern) };
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
        options.Children.Add(Selector("player.speed", WatchViewModel.Speeds.Select(s => s.ToString("0.##x", CultureInfo.InvariantCulture)).ToList(),
            WatchViewModel.Speeds.ToList().IndexOf(vm.Speed), i => vm.SelectSpeed(WatchViewModel.Speeds[i])));
        options.Children.Add(Selector("player.quality", vm.Qualities.Select((q, i) => i == 0 ? _localizer["player.quality.auto"] : q.Label).ToList(),
            vm.Qualities.IndexOf(vm.Quality!), i => vm.SelectQuality(vm.Qualities[i])));
        var tracks = vm.SubtitleTracks.ToList();
        options.Children.Add(Selector("player.subtitles", new[] { _localizer["player.subtitles.off"] }.Concat(tracks.Select(t => t.Label)).ToList(),
            vm.Subtitle == null ? 0 : tracks.IndexOf(vm.Subtitle) + 1, i => _ = vm.SelectSubtitleAsync(i == 0 ? null : tracks[i - 1])));
        Grid.SetColumn(options, 2);
        controls.Children.Add(options);
        Grid.SetRow(controls, 3);
        grid.Children.Add(controls);

        var chapter = new TextBlock { Opacity = 0.7 };
        Grid.SetRow(chapter, 4);
        grid.Children.Add(chapter);

        void Sync(object? s, PropertyChangedEventArgs e)
        {
            caption.Text = vm.Caption;
            captionBox.Visibility = vm.Caption.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            chapter.Text = vm.CurrentChapter?.Title.For(_localizer.Language) ?? "";
            time.Text = _localizer.Duration(vm.Position) + " / " + _localizer.Duration(vm.Duration);
            ((FontIcon)play.Content).Glyph = vm.IsPlaying ? "" : "";
            AutomationName(play, _localizer[vm.IsPlaying ? "player.pause" : "player.play"]);
            syncing = true;
            if (vm.Duration > 0) slider.Maximum = vm.Duration;
            slider.Value = vm.Position;
            syncing = false;
        }

        vm.PropertyChanged += Sync;
        _unbind = () => vm.PropertyChanged -= Sync;
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

    private FrameworkElement Selector(string key, IReadOnlyList<string> items, int selected, Action<int> onChange)
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
