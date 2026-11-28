using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Live;
using AlUmmahNow.App.Media;
using AlUmmahNow.App.Navigation;
using AlUmmahNow.App.Services;
using AlUmmahNow.App.Updates;
using AlUmmahNow.App.ViewModels;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Native;
using AlUmmahNow.Updates.Views;

namespace AlUmmahNow.Linux;

/// <summary>Renders the current route. Views are rebuilt on navigation and language change.</summary>
public sealed partial class MainWindow
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
    private readonly AuthViewModel _auth;
    private readonly PageRenderer _page;
    private WatchViewModel? _watch;
    private string? _watchId;
    private Action? _unbindWatch;
    private readonly UpdateService _updates;
    private readonly ITelemetryService _telemetry;
    private bool _hasSettings = true;

    public MainWindow(Adw.Application app, ICatalogClient catalog, Localizer localizer, Navigator navigator, GstMediaPlayer player, IMediaSession session, HttpClient http, AppSettings settings, SettingsStore store, AuthViewModel auth, ILiveClient live, string shortLinkHost, UpdateService updates, ITelemetryService? telemetry = null)
    {
        _updates = updates;
        _telemetry = telemetry ?? NullTelemetryService.Instance;
        _auth = auth;
        _live = live;
        _shortLinkHost = shortLinkHost;
        _liveList = new LiveListViewModel(live, localizer, navigator);
        _page = new PageRenderer(Build);
        _livePage = new LivePage(_page, id => new LiveWatchViewModel(id, live, catalog, player, session, localizer));
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
        // Review frames ask for a size ("WIDTHxHEIGHT", AUN_WINDOW_SIZE), such as a full-page capture; else 1280x800.
        var size = (Environment.GetEnvironmentVariable("AUN_WINDOW_SIZE") ?? "").Split('x');
        _window.SetDefaultSize(
            size.Length == 2 && int.TryParse(size[0], out var w) && w >= 1024 ? w : 1280,
            size.Length == 2 && int.TryParse(size[1], out var h) && h >= 700 ? h : 800);

        var keys = Gtk.EventControllerKey.New();
        keys.OnKeyPressed += (_, a) => OnKey(a.Keyval);
        _window.AddController(keys);

        // Hidden or behind another workspace: nothing is asked for
        _window.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() == "suspended" && LiveWatch != null) LiveWatch.IsHidden = _window.IsSuspended();
        };

        navigator.Changed += (_, _) =>
        {
            Render();
            if (navigator.Current is not Route.Watch && navigator.Current is not Route.Live) _ = RefreshLiveAsync();
        };
        localizer.LanguageChanged += (_, _) => Render();
        auth.PropertyChanged += (_, _) => Render();
        updates.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(UpdateService.IsSectionVisible)) return;
            Render();
        };
    }

    /// <summary>The update bar, screen and section of the page that is there.</summary>
    public UpdateViews Updates { get; private set; } = null!;

    /// <summary>A stream that is on air is being watched: nothing takes it away.</summary>
    public bool IsLivePlaying => LiveWatch is { Mode: LiveMode.Live, Watch.IsPlaying: true };

    public HomeViewModel Home => _home;
    public WatchViewModel? Watch => _watch ?? LiveWatch?.Watch;

    public async Task StartAsync()
    {
        ApplyTheme();
        Render();
        _window.Present();
        await Task.WhenAll(_home.LoadAsync(), _liveList.LoadAsync());
        Render();
    }

    private bool OnKey(uint key)
    {
        if (Watch is not { } watch || _window.GetFocus()?.HasCssClass("caption-credit") == true) return false;
        if (key == KeySpace) watch.TogglePlay();
        else if (key is KeyLeft or KeyRight) watch.SkipByArrow(right: key == KeyRight);
        else return false;
        return true;
    }

    private void ApplyTheme()
    {
        Adw.StyleManager.GetDefault().SetColorScheme(_settings.Theme switch
        {
            // Light or Dark only (owner rule): an old "System" setting reads as Dark.
            ThemePreference.Light => Adw.ColorScheme.ForceLight,
            _ => Adw.ColorScheme.ForceDark,
        });
    }

    private void Render() => _page.Render();

    private void Build()
    {
        var direction = _localizer.IsRtl ? Gtk.TextDirection.Rtl : Gtk.TextDirection.Ltr;
        Gtk.Widget.SetDefaultDirection(direction);
        _window.SetDirection(direction);
        _window.SetTitle(_localizer["app.name"]);

        _liveTitles.Clear();
        Updates?.Dispose();
        Updates = new UpdateViews(_updates, new UpdateText(_localizer, UpdateApp.Viewer, _updates), _window, UpdateText.Icon(UpdateApp.Viewer), () => _window.GetApplication()?.Quit());
        Updates.ForcedChanged += Render;
        _hasSettings = true;

        var root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        root.Append(BuildHeader());
        _unbindWatch?.Invoke();
        _unbindWatch = null;
        if (Updates.IsForced)
        {
            _livePage.Close();
            CloseWatch();
            root.Append(Updates.Forced);
            _window.SetContent(root);
            Updates.FocusForced();
            return;
        }

        root.Append(Updates.Bar);
        if (_navigator.Current is not Route.Live) _livePage.Close();
        if (_navigator.Current is not Route.Watch) CloseWatch();
        switch (_navigator.Current)
        {
            case Route.Watch route:
                EnsureWatch(route.VideoId);
                root.Append(BuildWatch(_watch!));
                break;
            case Route.Live route:
                root.Append(BuildLive(_livePage.Open(route.StreamId)));
                break;
            case Route.LiveList:
                root.Append(BuildLiveList());
                break;
            case Route.Settings:
                root.Append(BuildSettings());
                break;
            default:
                root.Append(BuildHome());
                break;
        }

        _window.SetContent(root);
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

    private Gtk.Widget BuildHeader()
    {
        var header = Adw.HeaderBar.New();
        if (_navigator.CanGoBack && !Updates.IsForced)
        {
            var back = Gtk.Button.NewFromIconName(_localizer.IsRtl ? "go-next-symbolic" : "go-previous-symbolic");
            // The icon has its own direction, which would turn the arrow once more: it is drawn as named
            back.GetChild()?.SetDirection(Gtk.TextDirection.Ltr);
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

        if (!Updates.IsForced) header.PackEnd(BuildAccount());
        if (_hasSettings && !Updates.IsForced && _navigator.Current is not Route.Settings)
        {
            var settings = Gtk.Button.NewFromIconName("emblem-system-symbolic");
            settings.SetTooltipText(_localizer["updates.settings"]);
            settings.OnClicked += (_, _) => _navigator.Push(new Route.Settings());
            header.PackEnd(settings);
        }

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

    private Gtk.Widget BuildAccount()
    {
        if (_auth.IsBusy)
        {
            var waiting = Gtk.Label.New(_localizer["auth.signingIn"]);
            waiting.AddCssClass("dim-label");
            return waiting;
        }

        var label = _auth.IsSignedIn ? "auth.signOut" : "auth.signIn";
        var button = Gtk.Button.NewWithLabel(_localizer[label]);
        button.SetTooltipText(_auth.Failed ? _localizer["auth.failed"] : Localizer.Isolate(_auth.AccountName));
        if (!_auth.IsSignedIn) button.AddCssClass("suggested-action");
        button.OnClicked += async (_, _) =>
        {
            if (_auth.IsSignedIn) _auth.SignOut();
            else await _auth.SignInAsync();
        };
        return button;
    }

    private Gtk.Widget StateMessage(string key, bool retry) => StateMessage(key, retry ? () => _home.LoadAsync() : null);

    private Gtk.Widget StateMessage(string key, Func<Task>? retry)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        box.SetValign(Gtk.Align.Center);
        box.SetHalign(Gtk.Align.Center);
        box.SetVexpand(true);
        box.Append(Gtk.Label.New(_localizer[key]));
        if (retry != null)
        {
            var button = Gtk.Button.NewWithLabel(_localizer["action.retry"]);
            button.AddCssClass("suggested-action");
            button.OnClicked += async (_, _) =>
            {
                await retry();
                Render();
            };
            box.Append(button);
        }

        return box;
    }

    /// <summary>Settings: updates and opt-in diagnostics. Theme and language are in the header.</summary>
    private Gtk.Widget BuildSettings()
    {
        var column = Gtk.Box.New(Gtk.Orientation.Vertical, 18);
        column.SetMarginStart(24);
        column.SetMarginEnd(24);
        column.SetMarginTop(18);
        column.SetMarginBottom(24);
        var title = Gtk.Label.New(_localizer["updates.settings"]);
        title.SetHalign(Gtk.Align.Start);
        title.AddCssClass("title-1");
        column.Append(title);
        var card = Gtk.Box.New(Gtk.Orientation.Vertical, 14);
        card.AddCssClass("update-card");
        card.AddCssClass("update-settings");
        Updates.SectionActions.SetHalign(Gtk.Align.Start);
        card.Append(Updates.SectionBody);
        card.Append(Updates.SectionActions);
        column.Append(card);
        Updates.UseSection(card);

        var telemetryCard = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        telemetryCard.AddCssClass("update-card");
        var telemetryTitle = Gtk.Label.New(_localizer["settings.telemetry.title"]);
        telemetryTitle.SetHalign(Gtk.Align.Start);
        telemetryTitle.AddCssClass("title-3");
        telemetryCard.Append(telemetryTitle);
        var telemetryBody = Gtk.Label.New(_localizer["settings.telemetry.body"]);
        telemetryBody.SetHalign(Gtk.Align.Start);
        telemetryBody.SetWrap(true);
        telemetryBody.AddCssClass("dim-label");
        telemetryCard.Append(telemetryBody);
        _settings.TelemetryEnabled = _store.LoadTelemetryEnabled();
        var telemetryCheck = Gtk.CheckButton.NewWithLabel(_localizer["settings.telemetry.toggle"]);
        telemetryCheck.SetActive(_settings.TelemetryEnabled);
        var purgeWarningText = _localizer["settings.telemetry.purgeWarning"];
        var disableErrorText = _localizer["settings.telemetry.disableError"];
        var telemetryNotice = Gtk.Label.New(string.Empty);
        telemetryNotice.SetHalign(Gtk.Align.Start);
        telemetryNotice.SetWrap(true);
        telemetryNotice.AddCssClass("error");
        telemetryNotice.SetVisible(false);
        var disableSaveFailed = false;
        void SyncPurgeNotice()
        {
            var failed = _telemetry.LastPurgeFailed;
            if (disableSaveFailed)
            {
                if (!_settings.TelemetryEnabled)
                {
                    _settings.TelemetryEnabled = true;
                    telemetryCheck.SetActive(true);
                }
                telemetryNotice.SetText(failed
                    ? disableErrorText + "\n" + purgeWarningText
                    : disableErrorText);
                telemetryNotice.SetVisible(true);
                return;
            }
            var persistedEnabled = _store.LoadTelemetryEnabled();
            if (!persistedEnabled && _telemetry.Consent == TelemetryConsent.Off && _settings.TelemetryEnabled)
            {
                _settings.TelemetryEnabled = false;
                telemetryCheck.SetActive(false);
            }
            else if (persistedEnabled && !_settings.TelemetryEnabled)
            {
                _settings.TelemetryEnabled = true;
                telemetryCheck.SetActive(true);
            }
            if (failed && _telemetry.Consent == TelemetryConsent.Off)
            {
                telemetryNotice.SetText(purgeWarningText);
                telemetryNotice.SetVisible(true);
            }
            else if (!failed && telemetryNotice.GetText() == purgeWarningText)
            {
                telemetryNotice.SetText(string.Empty);
                telemetryNotice.SetVisible(false);
            }
        }
        Action scheduleSync = () =>
        {
            GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT_IDLE, () =>
            {
                SyncPurgeNotice();
                return false;
            });
        };
        Action<bool> onPurgeChanged = _ => scheduleSync();
        Action<TelemetryConsent> onConsentChanged = _ => scheduleSync();
        _telemetry.PurgeFailureChanged += onPurgeChanged;
        _telemetry.ConsentChanged += onConsentChanged;
        telemetryCheck.OnMap += (_, _) => SyncPurgeNotice();
        telemetryNotice.OnDestroy += (_, _) =>
        {
            _telemetry.PurgeFailureChanged -= onPurgeChanged;
            _telemetry.ConsentChanged -= onConsentChanged;
        };
        SyncPurgeNotice();
        telemetryCheck.OnToggled += (_, _) =>
        {
            var enabled = telemetryCheck.GetActive();
            if (_settings.TelemetryEnabled == enabled) return;
            if (enabled)
            {
                if (!_store.TrySaveTelemetryEnabled(true, out var writtenToken))
                {
                    _settings.TelemetryEnabled = false;
                    telemetryCheck.SetActive(false);
                    telemetryNotice.SetText(_localizer["settings.telemetry.enableError"]);
                    telemetryNotice.SetVisible(true);
                    return;
                }
                var confirmationFailed = false;
                if (!_telemetry.SetConsent(TelemetryConsent.Full, writtenToken, () =>
                {
                    var confirmed = _store.ConfirmTelemetryEnabled(writtenToken);
                    if (!confirmed) confirmationFailed = true;
                    return confirmed;
                }))
                {
                    var rolledBack = _store.RollbackTelemetryEnabled(writtenToken);
                    var purged = _telemetry.SetConsent(TelemetryConsent.Off);
                    disableSaveFailed = !rolledBack;
                    if (!rolledBack)
                    {
                        _settings.TelemetryEnabled = true;
                        telemetryCheck.SetActive(true);
                        telemetryNotice.SetText(purged
                            ? disableErrorText
                            : disableErrorText + "\n" + purgeWarningText);
                        telemetryNotice.SetVisible(true);
                        return;
                    }
                    _settings.TelemetryEnabled = false;
                    telemetryCheck.SetActive(false);
                    telemetryNotice.SetText(confirmationFailed ? string.Empty : _localizer["settings.telemetry.enableError"]);
                    telemetryNotice.SetVisible(!confirmationFailed);
                    return;
                }
                disableSaveFailed = false;
                _settings.TelemetryEnabled = true;
                telemetryNotice.SetText(string.Empty);
                telemetryNotice.SetVisible(false);
            }
            else
            {
                var saved = _store.SaveTelemetryEnabled(false);
                var purged = _telemetry.SetConsent(TelemetryConsent.Off);
                disableSaveFailed = !saved;
                if (!saved)
                {
                    _settings.TelemetryEnabled = true;
                    telemetryCheck.SetActive(true);
                    telemetryNotice.SetText(purged
                        ? disableErrorText
                        : disableErrorText + "\n" + purgeWarningText);
                    telemetryNotice.SetVisible(true);
                    return;
                }
                _settings.TelemetryEnabled = false;
                telemetryNotice.SetText(purged ? string.Empty : purgeWarningText);
                telemetryNotice.SetVisible(!purged);
            }
        };
        telemetryCard.Append(telemetryCheck);
        telemetryCard.Append(telemetryNotice);
        column.Append(telemetryCard);

        var page = Gtk.ScrolledWindow.New();
        page.SetVexpand(true);
        page.SetChild(column);
        return page;
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

        if (_liveList.All.Count > 0) AppendLiveRail(column);
        foreach (var rail in _home.Rails)
        {
            var title = Gtk.Label.New(rail.Title);
            title.SetHalign(Gtk.Align.Start);
            title.AddCssClass("title-3");
            // A fixed line box, above both scripts' natural height: the Arabic heading came out 3 px shorter than the
            // English one, and by the last rail the Arabic page sat 27 px higher.
            title.SetSizeRequest(-1, RailHeadingLine);
            title.SetValign(Gtk.Align.Start);
            column.Append(title);

            // One row of RailColumns whole cards that share the width (a short rail keeps the same card size): never a
            // card cut off at the window edge, as a horizontally scrolled rail had.
            var grid = Gtk.Grid.New();
            grid.SetColumnHomogeneous(true);
            grid.SetColumnSpacing(14);
            for (var i = 0; i < RailColumns; i++)
            {
                grid.Attach(i < rail.Items.Count ? BuildCard(rail.Items[i]) : Gtk.Box.New(Gtk.Orientation.Vertical, 0), i, 0, 1, 1);
            }

            column.Append(grid);
        }

        var page = Gtk.ScrolledWindow.New();
        page.SetVexpand(true);
        page.SetChild(column);
        return page;
    }

    /// <summary>Cards per home rail: four, sharing the width.</summary>
    private const int RailColumns = 4;

    /// <summary>Line boxes for the rail heading and a card's title and meta lines: above the natural line height of both
    /// Latin and Arabic (with any fallback glyph), so a card and a rail are the same height in either language.</summary>
    private const int RailHeadingLine = 36, TitleLine = 32, TitleTextLine = 24, MetaLine = 26;

    private Gtk.Widget BuildCard(VideoCardViewModel card)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        box.SetSizeRequest(200, -1);

        var picture = Gtk.Picture.New();
        picture.SetSizeRequest(200, 112);
        picture.SetContentFit(Gtk.ContentFit.Cover);
        // Full cell width and exactly 16:9 of it, so every card in every rail and language is the same size: the picture's
        // own height-for-width came out 3 px shorter in English than in Arabic, where the row was measured differently.
        picture.SetHexpand(true);
        picture.SetValign(Gtk.Align.Start);
        KeepAspect(picture, 16, 9, 200);
        box.Append(picture);
        _ = LoadThumbnailAsync(picture, card.ThumbnailUrl);

        var title = Gtk.Label.New(card.Title);
        title.SetHalign(Gtk.Align.Start);
        // Fixed line boxes: a line that falls back to another font for one glyph (an Arabic decimal mark, a shadda) is a
        // few pixels taller, which put the meta lines of neighbouring cards out of line.
        title.SetSizeRequest(-1, TitleLine);
        title.SetEllipsize(Pango.EllipsizeMode.End);
        title.SetMaxWidthChars(30);
        title.AddCssClass("heading");
        box.Append(title);

        // Channel on one line, views · duration on the next: a compact Arabic count («١٢٫٨ ألف مشاهدة») is a word longer
        // than the full number, and on one shared line it pushed the duration out of the 260 px card.
        foreach (var text in new[] { card.Channel, card.Stats })
        {
            if (string.IsNullOrEmpty(text)) continue;
            var meta = Gtk.Label.New(text);
            meta.SetHalign(Gtk.Align.Start);
            meta.SetEllipsize(Pango.EllipsizeMode.End);
            meta.SetMaxWidthChars(36);
            meta.AddCssClass("dim-label");
            meta.SetSizeRequest(-1, MetaLine);
            box.Append(meta);
        }

        var button = Gtk.Button.New();
        button.AddCssClass("flat");
        button.SetChild(box);
        button.OnClicked += (_, _) => _home.Open(card);
        return button;
    }

    /// <summary>Keeps a widget's height at ratioHeight/ratioWidth of the width it is given (re-checked each frame).</summary>
    private static void KeepAspect(Gtk.Widget widget, int ratioWidth, int ratioHeight, int minWidth)
    {
        var last = -1;
        widget.AddTickCallback((w, _) =>
        {
            var px = w.GetWidth();
            if (px > 0 && px != last)
            {
                last = px;
                w.SetSizeRequest(minWidth, (int)Math.Round(px * (double)ratioHeight / ratioWidth));
            }

            return true;
        });
    }

    private async Task LoadThumbnailAsync(Gtk.Picture picture, string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            // A local file (the demo catalog's title cards) is read from disk: HttpClient has no file:// scheme.
            var data = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile
                ? await File.ReadAllBytesAsync(uri.LocalPath)
                : await _http.GetByteArrayAsync(url);
            picture.SetPaintable(Gdk.Texture.NewFromBytes(GLib.Bytes.New(data)));
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is GLib.GException || ex is IOException)
        {
            // A missing thumbnail leaves the placeholder area empty.
        }
    }

    /// <param name="heading">What stands under the video in place of the title and the channel.</param>
    private Gtk.Widget BuildWatch(WatchViewModel vm, Gtk.Widget? heading = null)
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
        caption.SetWrap(true);
        caption.SetJustify(Gtk.Justification.Center);
        var credit = Gtk.Button.New();
        credit.AccessibleRole = Gtk.AccessibleRole.Link;
        credit.AddCssClass("caption-credit");
        credit.OnClicked += (_, _) => vm.OpenCaptionCredit(new ShellBrowserLauncher());

        var captionBox = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        captionBox.SetValign(Gtk.Align.End);
        captionBox.SetHalign(Gtk.Align.Center);
        captionBox.SetMarginBottom(24);
        captionBox.AddCssClass("caption-overlay");
        captionBox.Append(caption);
        captionBox.Append(credit);
        overlay.AddOverlay(captionBox);
        column.Append(overlay);

        if (heading != null)
        {
            column.Append(heading);
        }
        else
        {
            var title = Gtk.Label.New(vm.Title);
            title.SetHalign(Gtk.Align.Start);
            title.SetWrap(true);
            title.AddCssClass("title-2");
            column.Append(title);

            var channel = Gtk.Label.New(vm.Channel);
            channel.SetHalign(Gtk.Align.Start);
            channel.AddCssClass("dim-label");
            column.Append(channel);
        }

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
        // Media time never mirrors (Apple HIG, Material; our web and mobile players): the transport and the
        // seek bar run left to right in every language. Only the row around them follows the page.
        var transport = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        foreach (var w in new Gtk.Widget[] { transport, back10, play, forward10, seek }) w.SetDirection(Gtk.TextDirection.Ltr);
        // The icon inside each button has its own direction (the app default, RTL in Arabic), which picks the
        // mirrored -rtl icons: set it too.
        foreach (var b in new[] { back10, play, forward10 }) b.GetChild()?.SetDirection(Gtk.TextDirection.Ltr);
        transport.Append(back10);
        transport.Append(play);
        transport.Append(forward10);
        controls.Append(transport);
        controls.Append(seek);
        controls.Append(time);

        var speeds = WatchViewModel.Speeds.ToList();
        var speed = BuildSelector("player.speed", WatchViewModel.Speeds.Select(s => _localizer.Speed(s)).ToArray(),
            speeds.IndexOf(vm.Speed), i => vm.SelectSpeed(speeds[i]));
        controls.Append(speed);
        // A live stream has no place to go to and no speed to choose
        foreach (var w in new Gtk.Widget[] { back10, forward10, seek, time, speed }) w.SetVisible(!vm.IsLive);

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
            if (e.PropertyName is null or nameof(WatchViewModel.CaptionCredit))
            {
                var attribution = vm.CaptionCredit;
                credit.SetVisible(attribution != null);
                if (attribution != null)
                {
                    var direction = attribution.Language == "ar" ? Gtk.TextDirection.Rtl : Gtk.TextDirection.Ltr;
                    captionBox.SetDirection(direction);
                    credit.SetDirection(direction);
                    credit.SetHalign(Gtk.Align.End);
                    credit.SetLabel(attribution.DisplayLabel!);
                    var values = new[] { new GObject.Value(vm.CaptionCreditAccessibleName!) };
                    using var handle = GObject.Internal.ValueArray2OwnedHandle.Create(values);
                    Gtk.Internal.Accessible.UpdateProperty(credit.Handle.DangerousGetHandle(), 1, new[] { Gtk.AccessibleProperty.Label }, handle);
                    GC.KeepAlive(values);
                }
                else captionBox.SetDirection(Gtk.TextDirection.None);
            }
            captionBox.SetVisible(vm.Caption.Length > 0 || vm.CaptionCredit != null);
            chapter.SetText(vm.CurrentChapter?.Title.For(_localizer.Language) ?? "");
            time.SetText(_localizer.Duration(vm.Position) + " / " + _localizer.Duration(vm.Duration));
            play.SetIconName(vm.IsPlaying ? "media-playback-pause-symbolic" : "media-playback-start-symbolic");
            play.GetChild()?.SetDirection(Gtk.TextDirection.Ltr);
            if (speeds.IndexOf(vm.Speed) is var at && at >= 0 && (int)speed.GetSelected() != at) speed.SetSelected((uint)at);
            syncing = true;
            if (vm.Duration > 0) seek.SetRange(0, vm.Duration);
            seek.SetValue(vm.Position);
            syncing = false;
        }

        vm.PropertyChanged += Sync;
        _unbindWatch += () => vm.PropertyChanged -= Sync;
        Sync(null, new System.ComponentModel.PropertyChangedEventArgs(null));
        return column;
    }

    private Gtk.DropDown BuildSelector(string labelKey, string[] items, int selected, Action<int> onChange)
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
