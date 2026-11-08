using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Media;
using AlUmmahNow.App.Navigation;
using AlUmmahNow.App.Services;
using AlUmmahNow.App.ViewModels;
using AlUmmahNow.CoreBridge;

namespace AlUmmahNow.Linux;

/// <summary>Renders the current route. Views are rebuilt on navigation and language change.</summary>
public sealed class MainWindow
{
    private const uint KeySpace = 0x20, KeyLeft = 0xff51, KeyRight = 0xff53;

    private readonly Adw.ApplicationWindow _window;
    private readonly ICatalogClient _catalog;
    private readonly Localizer _localizer;
    private readonly Navigator _navigator;
    private readonly HomeViewModel _home;
    private readonly GstMediaPlayer _player;
    private readonly IMediaSession _session;
    private readonly HttpClient _http;
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private WatchViewModel? _watch;
    private string? _watchId;
    private Action? _unbindWatch;

    public MainWindow(Adw.Application app, ICatalogClient catalog, Localizer localizer, Navigator navigator, GstMediaPlayer player, IMediaSession session, HttpClient http, AppSettings settings, SettingsStore store)
    {
        _catalog = catalog;
        _localizer = localizer;
        _navigator = navigator;
        _player = player;
        _session = session;
        _http = http;
        _settings = settings;
        _store = store;
        _home = new HomeViewModel(catalog, localizer, navigator);
        _window = Adw.ApplicationWindow.New(app);
        _window.SetDefaultSize(1280, 800);

        var keys = Gtk.EventControllerKey.New();
        keys.OnKeyPressed += (_, a) => OnKey(a.Keyval);
        _window.AddController(keys);

        navigator.Changed += (_, _) => Render();
        localizer.LanguageChanged += (_, _) => Render();
    }

    public HomeViewModel Home => _home;
    public WatchViewModel? Watch => _watch;

    public async Task StartAsync()
    {
        ApplyTheme();
        Render();
        _window.Present();
        await _home.LoadAsync();
        Render();
    }

    private bool OnKey(uint key)
    {
        if (_watch == null) return false;
        // Left and right follow the mirrored seek direction in RTL.
        var forward = _localizer.IsRtl ? KeyLeft : KeyRight;
        var back = _localizer.IsRtl ? KeyRight : KeyLeft;
        if (key == KeySpace) _watch.TogglePlay();
        else if (key == forward) _watch.SkipForward();
        else if (key == back) _watch.SkipBack();
        else return false;
        return true;
    }

    private void ApplyTheme()
    {
        Adw.StyleManager.GetDefault().SetColorScheme(_settings.Theme switch
        {
            ThemePreference.Dark => Adw.ColorScheme.ForceDark,
            ThemePreference.Light => Adw.ColorScheme.ForceLight,
            _ => Adw.ColorScheme.Default,
        });
    }

    private void Render()
    {
        var direction = _localizer.IsRtl ? Gtk.TextDirection.Rtl : Gtk.TextDirection.Ltr;
        Gtk.Widget.SetDefaultDirection(direction);
        _window.SetDirection(direction);
        _window.SetTitle(_localizer["app.name"]);

        var root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        root.Append(BuildHeader());

        _unbindWatch?.Invoke();
        _unbindWatch = null;
        if (_navigator.Current is Route.Watch route)
        {
            EnsureWatch(route.VideoId);
            root.Append(BuildWatch(_watch!));
        }
        else
        {
            CloseWatch();
            root.Append(BuildHome());
        }

        _window.SetContent(root);
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

    private Gtk.Widget BuildHeader()
    {
        var header = Adw.HeaderBar.New();
        if (_navigator.CanGoBack)
        {
            var back = Gtk.Button.NewFromIconName(_localizer.IsRtl ? "go-next-symbolic" : "go-previous-symbolic");
            back.SetTooltipText(_localizer["nav.back"]);
            back.OnClicked += (_, _) => _navigator.Back();
            header.PackStart(back);
        }

        var language = Gtk.Button.NewWithLabel(_localizer[_localizer.IsRtl ? "settings.language.en" : "settings.language.ar"]);
        language.SetTooltipText(_localizer["settings.language"]);
        language.OnClicked += (_, _) =>
        {
            _settings.Language = _localizer.IsRtl ? "en" : "ar";
            _store.Save(_settings);
            _localizer.Language = _settings.Language;
        };
        header.PackEnd(language);

        var dark = _settings.Theme != ThemePreference.Light;
        var theme = Gtk.Button.NewFromIconName(dark ? "weather-clear-symbolic" : "weather-clear-night-symbolic");
        theme.SetTooltipText(_localizer["settings.theme"] + ": " + _localizer[dark ? "settings.theme.light" : "settings.theme.dark"]);
        theme.OnClicked += (_, _) =>
        {
            _settings.Theme = _settings.Theme == ThemePreference.Light ? ThemePreference.Dark : ThemePreference.Light;
            _store.Save(_settings);
            ApplyTheme();
            Render();
        };
        header.PackEnd(theme);
        return header;
    }

    private Gtk.Widget StateMessage(string key, bool retry)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        box.SetValign(Gtk.Align.Center);
        box.SetHalign(Gtk.Align.Center);
        box.SetVexpand(true);
        box.Append(Gtk.Label.New(_localizer[key]));
        if (retry)
        {
            var button = Gtk.Button.NewWithLabel(_localizer["action.retry"]);
            button.AddCssClass("suggested-action");
            button.OnClicked += async (_, _) =>
            {
                await _home.LoadAsync();
                Render();
            };
            box.Append(button);
        }

        return box;
    }

    private Gtk.Widget BuildHome()
    {
        switch (_home.State)
        {
            case LoadState.Loading: return StateMessage("state.loading", false);
            case LoadState.Error: return StateMessage("state.error", true);
            case LoadState.Empty: return StateMessage("state.empty", false);
        }

        var column = Gtk.Box.New(Gtk.Orientation.Vertical, 20);
        column.SetMarginTop(16);
        column.SetMarginBottom(24);
        column.SetMarginStart(20);
        column.SetMarginEnd(20);

        foreach (var rail in _home.Rails)
        {
            var title = Gtk.Label.New(rail.Title);
            title.SetHalign(Gtk.Align.Start);
            title.AddCssClass("title-3");
            column.Append(title);

            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 14);
            foreach (var card in rail.Items) row.Append(BuildCard(card));
            var scroller = Gtk.ScrolledWindow.New();
            scroller.SetPolicy(Gtk.PolicyType.Automatic, Gtk.PolicyType.Never);
            scroller.SetChild(row);
            column.Append(scroller);
        }

        var page = Gtk.ScrolledWindow.New();
        page.SetVexpand(true);
        page.SetChild(column);
        return page;
    }

    private Gtk.Widget BuildCard(VideoCardViewModel card)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        box.SetSizeRequest(260, -1);

        var picture = Gtk.Picture.New();
        picture.SetSizeRequest(260, 146);
        picture.SetContentFit(Gtk.ContentFit.Cover);
        box.Append(picture);
        _ = LoadThumbnailAsync(picture, card.ThumbnailUrl);

        var title = Gtk.Label.New(card.Title);
        title.SetHalign(Gtk.Align.Start);
        title.SetEllipsize(Pango.EllipsizeMode.End);
        title.SetMaxWidthChars(30);
        title.AddCssClass("heading");
        box.Append(title);

        var meta = Gtk.Label.New(string.Join(" · ", new[] { card.Channel, card.Views, card.Duration }.Where(s => s.Length > 0)));
        meta.SetHalign(Gtk.Align.Start);
        meta.SetEllipsize(Pango.EllipsizeMode.End);
        meta.SetMaxWidthChars(36);
        meta.AddCssClass("dim-label");
        box.Append(meta);

        var button = Gtk.Button.New();
        button.AddCssClass("flat");
        button.SetChild(box);
        button.OnClicked += (_, _) => _home.Open(card);
        return button;
    }

    private async Task LoadThumbnailAsync(Gtk.Picture picture, string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            var data = await _http.GetByteArrayAsync(url);
            picture.SetPaintable(Gdk.Texture.NewFromBytes(GLib.Bytes.New(data)));
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is GLib.GException)
        {
            // A missing thumbnail leaves the placeholder area empty.
        }
    }

    private Gtk.Widget BuildWatch(WatchViewModel vm)
    {
        if (vm.State == LoadState.Loading) return StateMessage("state.loading", false);
        if (vm.State != LoadState.Content) return StateMessage(vm.State == LoadState.Error ? "state.error" : "state.empty", false);

        var column = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        column.SetMarginStart(20);
        column.SetMarginEnd(20);
        column.SetMarginTop(12);
        column.SetMarginBottom(16);
        column.SetVexpand(true);

        var overlay = Gtk.Overlay.New();
        overlay.SetVexpand(true);
        var picture = Gtk.Picture.New();
        picture.SetPaintable(_player.Paintable);
        overlay.SetChild(picture);

        var caption = Gtk.Label.New(vm.Caption);
        caption.SetValign(Gtk.Align.End);
        caption.SetMarginBottom(24);
        caption.SetWrap(true);
        caption.SetJustify(Gtk.Justification.Center);
        caption.AddCssClass("caption-overlay");
        overlay.AddOverlay(caption);
        column.Append(overlay);

        var title = Gtk.Label.New(vm.Title);
        title.SetHalign(Gtk.Align.Start);
        title.SetWrap(true);
        title.AddCssClass("title-2");
        column.Append(title);

        var channel = Gtk.Label.New(vm.Channel);
        channel.SetHalign(Gtk.Align.Start);
        channel.AddCssClass("dim-label");
        column.Append(channel);

        var seek = Gtk.Scale.NewWithRange(Gtk.Orientation.Horizontal, 0, Math.Max(1, vm.Duration), 1);
        seek.SetDrawValue(false);
        seek.SetHexpand(true);
        var syncing = false;
        seek.OnValueChanged += (_, _) =>
        {
            if (!syncing) vm.Seek(seek.GetValue());
        };

        var time = Gtk.Label.New("");
        var chapter = Gtk.Label.New("");
        chapter.AddCssClass("dim-label");

        var controls = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var play = Gtk.Button.NewFromIconName(vm.IsPlaying ? "media-playback-pause-symbolic" : "media-playback-start-symbolic");
        play.SetTooltipText(_localizer[vm.IsPlaying ? "player.pause" : "player.play"]);
        play.OnClicked += (_, _) => vm.TogglePlay();
        var back10 = Gtk.Button.NewFromIconName("media-seek-backward-symbolic");
        back10.SetTooltipText(_localizer["player.seekBack"]);
        back10.OnClicked += (_, _) => vm.SkipBack();
        var forward10 = Gtk.Button.NewFromIconName("media-seek-forward-symbolic");
        forward10.SetTooltipText(_localizer["player.seekForward"]);
        forward10.OnClicked += (_, _) => vm.SkipForward();
        controls.Append(back10);
        controls.Append(play);
        controls.Append(forward10);
        controls.Append(seek);
        controls.Append(time);

        controls.Append(BuildSelector("player.speed", WatchViewModel.Speeds.Select(s => s.ToString("0.##x", System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
            WatchViewModel.Speeds.ToList().IndexOf(vm.Speed), i => vm.SelectSpeed(WatchViewModel.Speeds[i])));

        var qualityLabels = vm.Qualities.Select((q, i) => i == 0 ? _localizer["player.quality.auto"] : q.Label).ToArray();
        controls.Append(BuildSelector("player.quality", qualityLabels, vm.Qualities.IndexOf(vm.Quality!), i => vm.SelectQuality(vm.Qualities[i])));

        var tracks = vm.SubtitleTracks.ToList();
        var subtitleLabels = new[] { _localizer["player.subtitles.off"] }.Concat(tracks.Select(t => t.Label)).ToArray();
        controls.Append(BuildSelector("player.subtitles", subtitleLabels, vm.Subtitle == null ? 0 : tracks.IndexOf(vm.Subtitle) + 1, i => _ = vm.SelectSubtitleAsync(i == 0 ? null : tracks[i - 1])));
        column.Append(controls);
        column.Append(chapter);

        void Sync(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            caption.SetText(vm.Caption);
            caption.SetVisible(vm.Caption.Length > 0);
            chapter.SetText(vm.CurrentChapter?.Title.For(_localizer.Language) ?? "");
            time.SetText(_localizer.Duration(vm.Position) + " / " + _localizer.Duration(vm.Duration));
            play.SetIconName(vm.IsPlaying ? "media-playback-pause-symbolic" : "media-playback-start-symbolic");
            syncing = true;
            if (vm.Duration > 0) seek.SetRange(0, vm.Duration);
            seek.SetValue(vm.Position);
            syncing = false;
        }

        vm.PropertyChanged += Sync;
        _unbindWatch = () => vm.PropertyChanged -= Sync;
        Sync(null, new System.ComponentModel.PropertyChangedEventArgs(null));
        return column;
    }

    private Gtk.Widget BuildSelector(string labelKey, string[] items, int selected, Action<int> onChange)
    {
        var dropdown = Gtk.DropDown.NewFromStrings(items);
        dropdown.SetTooltipText(_localizer[labelKey]);
        if (selected >= 0) dropdown.SetSelected((uint)selected);
        var ready = false;
        dropdown.OnNotify += (_, a) =>
        {
            if (ready && a.Pspec.GetName() == "selected") onChange((int)dropdown.GetSelected());
        };
        ready = true;
        return dropdown;
    }
}
