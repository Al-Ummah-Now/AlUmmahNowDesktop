using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Shell;

namespace AlUmmahNow.Studio.Linux;

/// <summary>
/// The Studio window (Lantern Glass): a sidebar headed by the one identity menu (channels and people)
/// with the pages under it, an icon rail in narrow windows; the page under a night header bar with
/// its title, search and the window controls. Sign-in, loading and error states replace the sidebar
/// and page.
/// </summary>
public sealed class StudioWindow
{
    private static readonly Logger Logger = Log.For("ui");
    private const int SidebarWidth = 264;
    private const int RailWidth = 72;

    private readonly Localizer _l;
    private readonly StudioShellViewModel _shell;
    private readonly Gtk.Stack _body = Gtk.Stack.New();
    private readonly Gtk.Stack _pages = Gtk.Stack.New();
    private readonly Gtk.Box _sidebar = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly Gtk.ListBox _nav = Gtk.ListBox.New();
    // The page's title sits on the page (as on Windows); the header holds the search, as on the web.
    private readonly Gtk.Label _title = Gtk.Label.New("");
    private readonly Gtk.Box _searchBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
    private Gtk.Entry? _searchText;
    private bool _readySettled;
    private readonly Gtk.MenuButton _identityButton = Gtk.MenuButton.New();
    private readonly Gtk.Box _identityMenu = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
    private readonly Gtk.Popover _identityPopover = Gtk.Popover.New();
    private readonly Dictionary<string, (string Title, Gtk.ListBoxRow Row, Gtk.Label Label)> _pageInfo = new();
    private readonly Dictionary<string, Gdk.Texture> _logos = new();
    private readonly HashSet<string> _logosLoading = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly Adw.StatusPage _reauth = Adw.StatusPage.New();
    private readonly Adw.StatusPage _error = Adw.StatusPage.New();
    private readonly Gtk.Label _signInError = Ui.Label("", dim: true, start: false);
    private StudioPages? _studioPages;
    private bool _syncingPages;
    private bool _rail;
    private Gtk.Label? _appName;

    public StudioWindow(Adw.Application app, Localizer l, StudioShellViewModel shell, string? banner)
    {
        _l = l;
        _shell = shell;
        Window = Adw.ApplicationWindow.New(app);
        // A minimum size instead of a phone layout: nothing clips or truncates at any size it can take.
        Window.SetSizeRequest(StudioWindowSize.MinWidth, StudioWindowSize.MinHeight);
        var (width, height) = StudioWindowSize.Initial(Environment.GetEnvironmentVariable("AUN_WINDOW_SIZE"));
        Window.SetDefaultSize(width, height);
        Window.SetTitle(l["studio.name"]);

        // One header across the window, as on the web (the gradient spans the whole width): the app's
        // icon and name at the start, the page title, search and the window controls.
        var appName = Gtk.Label.New(l["studio.name"]);
        appName.AddCssClass("caption-heading");
        appName.SetEllipsize(Pango.EllipsizeMode.End);
        // The videos app's icon (the owner's art, as is) beside the name.
        var brand = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        // A fixed 22 px image (a Gtk.Picture would ask for the art's full 180 px and stretch the header).
        var icon = Gtk.Image.NewFromPaintable(Gdk.Texture.NewFromBytes(GLib.Bytes.New(ReadAll(StudioTheme.AppIcon()))));
        icon.SetPixelSize(22);
        icon.AddCssClass("app-icon");
        brand.Append(icon);
        brand.Append(appName);
        brand.SetMarginStart(6);
        _appName = appName;
        _sidebar.AddCssClass("studio-sidebar");
        _sidebar.SetSizeRequest(SidebarWidth, -1);

        _identityMenu.SetMarginStart(4);
        _identityMenu.SetMarginEnd(4);
        _identityMenu.SetMarginTop(4);
        _identityMenu.SetMarginBottom(4);
        _identityMenu.SetSizeRequest(300, -1);
        _identityPopover.SetChild(_identityMenu);
        _identityPopover.AddCssClass("glass");
        _identityPopover.SetHasArrow(false);
        _identityButton.SetPopover(_identityPopover);
        _identityButton.AddCssClass("identity");
        _identityButton.SetMarginStart(10);
        _identityButton.SetMarginEnd(10);
        _identityButton.SetMarginTop(12);
        _sidebar.Append(_identityButton);

        _nav.AddCssClass("navigation-sidebar");
        _nav.AddCssClass("studio-nav");
        _nav.SetSelectionMode(Gtk.SelectionMode.Single);
        _nav.SetMarginStart(8);
        _nav.SetMarginEnd(8);
        _nav.SetMarginTop(12);
        _nav.OnRowSelected += (_, e) =>
        {
            if (e.Row?.GetName() is { Length: > 0 } name && _pageInfo.TryGetValue(name, out var p))
            {
                _pages.SetVisibleChildName(name);
                _title.SetText(p.Title);
                if (!_syncingPages) _studioPages?.Select(name);
            }
        };
        _sidebar.Append(_nav);

        // The header: the app's icon and name, the search pill (the web's .ah-search), the window controls.
        var header = Adw.HeaderBar.New();
        header.AddCssClass("night");
        header.PackStart(brand);
        BuildSearch(l["studio.videos.search"]);
        header.SetTitleWidget(_searchBox);
        var content = Adw.ToolbarView.New();
        if (!string.IsNullOrEmpty(banner))
        {
            var b = Adw.Banner.New(banner!);
            b.SetRevealed(true);
            content.AddTopBar(b);
        }

        BuildStates();
        // Each page has its own width: a GTK Stack is otherwise as wide as its widest page, hidden or not.
        _pages.SetHhomogeneous(false);
        _body.SetHhomogeneous(false);
        _body.SetVexpand(true);
        _body.SetHexpand(true);
        _body.AddCssClass("studio-content");
        _title.AddCssClass("title-1");
        _title.SetXalign(0);
        _title.SetMarginStart(28);
        _title.SetMarginEnd(28);
        _title.SetMarginTop(20);
        _title.SetMarginBottom(4);
        var page = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        page.AddCssClass("studio-content");
        page.Append(_title);
        page.Append(_body);
        content.SetContent(page);
        content.SetHexpand(true);

        var columns = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        columns.SetVexpand(true);
        columns.Append(_sidebar);
        columns.Append(content);
        var root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        root.Append(header);
        root.Append(columns);
        Window.SetContent(root);

        Window.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() is not ("default-width" or "maximized" or "fullscreened")) return;
            Window.GetDefaultSize(out var w, out int _);
            var actual = Window.GetWidth();
            SetRail(StudioWindowSize.IsRail(actual > 0 ? actual : w));
        };
        SetRail(StudioWindowSize.IsRail(width));

        // Ctrl+K or "/" jump into the search. Bubble phase: a field you're typing in takes "/" first.
        var shortcuts = Gtk.ShortcutController.New();
        shortcuts.SetScope(Gtk.ShortcutScope.Managed);
        shortcuts.AddShortcut(Gtk.Shortcut.New(Gtk.ShortcutTrigger.ParseString("<Control>k|slash|KP_Divide"), Gtk.CallbackAction.New((_, _) => FocusSearch())));
        Window.AddController(shortcuts);
        // The window opens at rest, as the web does (GTK would focus the first field, the search).
        Window.OnShow += (_, _) => GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_LOW, () =>
        {
            Settle();
            return false;
        });

        _shell.PropertyChanged += (_, _) => Render();
        Render();
    }

    /// <summary>CI: whether the header search has keyboard focus (it must not when the window opens).</summary>
    public bool SearchHasFocus => Window.GetFocus() is { } f && (f == _searchText || f.IsAncestor(_searchBox));

    /// <summary>Moves focus off the header search to the current page in the sidebar (already a tab stop).</summary>
    private void Settle()
    {
        if (!SearchHasFocus) return;
        if (_nav.GetSelectedRow() is { } row) row.GrabFocus();
        else Window.SetFocus(null);
    }

    private bool FocusSearch() => _searchBox.GetVisible() && _searchText != null && _searchText.GrabFocus();

    public Adw.ApplicationWindow Window { get; }

    private static byte[] ReadAll(Stream s)
    {
        using (s)
        using (var m = new MemoryStream())
        {
            s.CopyTo(m);
            return m.ToArray();
        }
    }

    /// <summary>The header's search was submitted: the Videos page searches for the text.</summary>
    public event EventHandler<string>? SearchRequested;

    /// <summary>
    /// The web header's search field (<c>.ah-search</c>), shape and colour (StudioStyle.HeaderCss): a 40 px
    /// pill, the text 15 px with 16 px at its start, a 52 px search button at its end behind a divider.
    /// </summary>
    private void BuildSearch(string label)
    {
        var text = Gtk.Entry.New();
        text.SetPlaceholderText(label);
        text.SetHexpand(true);
        text.SetValign(Gtk.Align.Center);
        Ui.Named(text, label);
        var go = Gtk.Button.NewFromIconName("system-search-symbolic");
        Ui.Named(go, label);
        void Submit()
        {
            ShowPage(StudioPages.Videos);
            SearchRequested?.Invoke(this, text.GetText());
        }

        text.OnActivate += (_, _) => Submit();
        _searchText = text;
        go.OnClicked += (_, _) => Submit();
        _searchBox.AddCssClass("header-search");
        _searchBox.SetOverflow(Gtk.Overflow.Hidden);
        _searchBox.SetSizeRequest((int)FawaneesHeader.SearchMaxWidth, (int)FawaneesHeader.SearchHeight);
        _searchBox.SetValign(Gtk.Align.Center);
        // Its own width (640, or 480 in the rail), centred: the entry's expand must not stretch it across the header.
        _searchBox.SetHalign(Gtk.Align.Center);
        _searchBox.SetHexpand(false);
        _searchBox.Append(text);
        _searchBox.Append(go);
    }

    private void SetRail(bool rail)
    {
        if (rail == _rail && _sidebar.GetWidth() > 0) return;
        _rail = rail;
        _searchBox.SetSizeRequest((int)(rail ? FawaneesHeader.SearchNarrowWidth : FawaneesHeader.SearchMaxWidth), (int)FawaneesHeader.SearchHeight);
        _sidebar.SetSizeRequest(rail ? RailWidth : SidebarWidth, -1);

        foreach (var p in _pageInfo.Values) p.Label.SetVisible(!rail);
        _identityButton.SetMarginStart(rail ? 8 : 10);
        _identityButton.SetMarginEnd(rail ? 8 : 10);
        RenderIdentity();
    }

    public void AddPage(string name, string title, string icon, Gtk.Widget page)
    {
        // Each page scrolls itself (its root is a ScrolledWindow).
        _pages.AddNamed(page, name);
        var row = Gtk.ListBoxRow.New();
        row.SetName(name);
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 14);
        box.Append(Gtk.Image.NewFromIconName(icon));
        var label = Gtk.Label.New(title);
        label.SetXalign(0);
        label.SetVisible(!_rail);
        box.Append(label);
        row.SetChild(box);
        row.SetTooltipText(title);
        _nav.Append(row);
        _pageInfo[name] = (title, row, label);
    }

    public void ShowPage(string name)
    {
        var info = _pageInfo.TryGetValue(name, out var p) && p.Row.GetVisible() ? p : _pageInfo.Values.FirstOrDefault(v => v.Row.GetVisible());
        if (info.Row != null) _nav.SelectRow(info.Row);
    }

    /// <summary>Hidden-when-off pages: a page whose feature is off leaves the sidebar, and the window shows <see cref="StudioPages.Current"/>.</summary>
    public void UsePages(StudioPages pages)
    {
        _studioPages = pages;
        pages.Changed += (_, _) => ApplyPages();
        ApplyPages();
    }

    private void ApplyPages()
    {
        if (_studioPages is not { } pages) return;
        _syncingPages = true;
        try
        {
            foreach (var (name, info) in _pageInfo) info.Row.SetVisible(pages.IsVisible(name));
            if (_nav.GetSelectedRow()?.GetName() != pages.Current) ShowPage(pages.Current);
        }
        finally
        {
            _syncingPages = false;
        }
    }

    /// <summary>For CI screenshots: open the identity menu (formerly the "Acting as" picker).</summary>
    public void OpenChannelPicker() => _identityButton.Popup();

    /// <summary>For CI screenshots: open the identity menu (formerly the account menu).</summary>
    public void OpenAccountMenu() => _identityButton.Popup();

    /// <summary>For CI screenshots: show the sign-out confirmation for the active account.</summary>
    public void AskSignOut()
    {
        if (_shell.ActiveAccount is { } a) _ = SignOutAsync(a);
    }

    private void BuildStates()
    {
        var signIn = Adw.StatusPage.New();
        signIn.SetIconName("system-users-symbolic");
        signIn.SetTitle(_l["studio.shell.signInTitle"]);
        signIn.SetDescription(_l["studio.shell.signInBody"]);
        var signInBox = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        signInBox.SetHalign(Gtk.Align.Center);
        var signInButton = Ui.Button(_l["auth.signIn"], () => _ = _shell.SignInAsync(SignInMode.Default), "suggested-action");
        signInButton.AddCssClass("pill");
        signInBox.Append(signInButton);
        signInBox.Append(_signInError);
        signIn.SetChild(signInBox);
        _body.AddNamed(signIn, "signin");

        var waiting = Adw.StatusPage.New();
        waiting.SetTitle(_l["auth.signingIn"]);
        var spinner = Gtk.Spinner.New();
        spinner.Start();
        spinner.SetSizeRequest(32, 32);
        waiting.SetChild(spinner);
        _body.AddNamed(waiting, "signingin");

        var loading = Adw.StatusPage.New();
        loading.SetTitle(_l["studio.shell.loading"]);
        var spinner2 = Gtk.Spinner.New();
        spinner2.Start();
        spinner2.SetSizeRequest(32, 32);
        loading.SetChild(spinner2);
        _body.AddNamed(loading, "loading");

        var none = Adw.StatusPage.New();
        none.SetIconName("folder-videos-symbolic");
        none.SetTitle(_l["studio.shell.noChannelTitle"]);
        none.SetDescription(_l["studio.shell.noChannelBody"]);
        none.SetChild(Ui.Button(_l["studio.shell.retry"], () => _ = _shell.RetryAsync()));
        _body.AddNamed(none, "nochannel");

        _error.SetIconName("network-error-symbolic");
        _error.SetTitle(_l["studio.shell.errorTitle"]);
        _error.SetChild(Ui.Button(_l["studio.shell.retry"], () => _ = _shell.RetryAsync()));
        _body.AddNamed(_error, "error");

        _reauth.SetIconName("dialog-password-symbolic");
        _reauth.SetTitle(_l["studio.shell.reauthTitle"]);
        _reauth.SetChild(Ui.Button(_l["auth.signIn"], () => _ = _shell.SignInAsync(SignInMode.Default), "suggested-action"));
        _body.AddNamed(_reauth, "reauth");

        _body.AddNamed(_pages, "studio");
    }

    private void Render()
    {
        var state = _shell.State;
        _body.SetVisibleChildName(state switch
        {
            ShellState.SignedOut => "signin",
            ShellState.SigningIn => "signingin",
            ShellState.Loading => "loading",
            ShellState.NoChannel => "nochannel",
            ShellState.Error => "error",
            ShellState.NeedsReauth => "reauth",
            _ => "studio",
        });
        var ready = state == ShellState.Ready;
        _sidebar.SetVisible(ready);
        _searchBox.SetVisible(ready);
        if (ready && !_readySettled)
        {
            // The signed-in Studio replaces the sign-in state: settle once its widgets exist.
            _readySettled = true;
            GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_LOW, () =>
            {
                Settle();
                return false;
            });
        }
        _title.SetVisible(ready);
        if (!ready) _title.SetText(_l["studio.name"]);
        else if (_nav.GetSelectedRow()?.GetName() is { } name && _pageInfo.TryGetValue(name, out var p)) _title.SetText(p.Title);
        _signInError.SetText(_shell.ErrorKey is { } k && state == ShellState.SignedOut ? _l[k] : "");
        _error.SetDescription(_shell.ErrorKey is { } e ? _l[e] : "");
        _reauth.SetDescription(_l.Format("studio.shell.reauthBody", Localizer.Isolate(_shell.ActiveAccountName ?? "")));
        RenderIdentity();
    }

    /// <summary>The sidebar button (the acting identity) and the one identity menu it opens.</summary>
    private void RenderIdentity()
    {
        string T(string k) => _l[k];
        var channels = IdentityMenu.Channels(_shell.Channels, _shell.ActiveChannel, T, _l.Separator);
        var people = IdentityMenu.Accounts(_shell.AccountList, _shell.ActiveAccount, a => _shell.NameOf(a), T, _l.Separator);
        var current = IdentityMenu.Current(channels, people);
        _identityButton.SetVisible(current != null);
        if (current == null) return;

        Ui.Named(_identityButton, _l.Format(ActingAs.ButtonNameKey, current.Name));
        var face = Row(current, 36);
        if (_rail)
        {
            // The rail shows the logo only.
            while (face.GetLastChild() is { } last && last != face.GetFirstChild()) face.Remove(last);
        }
        else
        {
            var chevron = Gtk.Image.NewFromIconName("pan-down-symbolic");
            chevron.SetHexpand(true);
            chevron.SetHalign(Gtk.Align.End);
            chevron.AddCssClass("dim-label");
            face.Append(chevron);
        }

        _identityButton.SetChild(face);

        Ui.Clear(_identityMenu);
        if (channels.Count > 0)
        {
            _identityMenu.Append(Heading(_l[IdentityMenu.ChannelsHeadingKey]));
            foreach (var r in channels)
            {
                var channel = _shell.Channels.First(c => c.Id == r.Id);
                _identityMenu.Append(MenuRow(r, () => _shell.SelectChannel(channel)));
            }
        }

        if (people.Count > 0)
        {
            _identityMenu.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
            _identityMenu.Append(Heading(_l[IdentityMenu.AccountsHeadingKey]));
            foreach (var r in people)
            {
                var row = MenuRow(r, () => _ = _shell.SwitchAccountAsync(r.Id));
                Ui.Named(row, _l.Format("studio.shell.switchTo", r.Name));
                _identityMenu.Append(row);
            }
        }

        _identityMenu.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        _identityMenu.Append(MenuAction(_l["studio.shell.addAccount"], "list-add-symbolic", () => _ = _shell.SignInAsync(SignInMode.AddAccount)));
        if (_shell.ActiveAccount is { } active)
        {
            var signOut = MenuAction(_l.Format("studio.shell.signOutOf", _shell.NameOf(active)), "system-log-out-symbolic", () => _ = SignOutAsync(active));
            signOut.AddCssClass("danger");
            _identityMenu.Append(signOut);
        }
    }

    private static Gtk.Label Heading(string text)
    {
        var heading = Gtk.Label.New(text);
        heading.SetXalign(0);
        heading.AddCssClass("identity-heading");
        return heading;
    }

    private Gtk.Button MenuRow(IdentityRow r, Action pick)
    {
        var row = Row(r, 32);
        var check = Gtk.Image.NewFromIconName("object-select-symbolic");
        check.AddCssClass("check");
        check.SetOpacity(r.IsActive ? 1 : 0);
        check.SetHexpand(true);
        check.SetHalign(Gtk.Align.End);
        row.Append(check);
        var button = Gtk.Button.New();
        button.SetChild(row);
        button.AddCssClass("flat");
        button.AddCssClass("identity-row");
        if (r.IsActive) button.AddCssClass("active");
        Ui.Named(button, r.AccessibleName);
        button.OnClicked += (_, _) =>
        {
            _identityPopover.Popdown();
            pick();
        };
        return button;
    }

    private Gtk.Button MenuAction(string text, string icon, Action click)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        var image = Gtk.Image.NewFromIconName(icon);
        image.SetSizeRequest(32, -1);
        box.Append(image);
        var label = Gtk.Label.New(text);
        label.SetXalign(0);
        box.Append(label);
        var button = Gtk.Button.New();
        button.SetChild(box);
        button.AddCssClass("flat");
        button.AddCssClass("identity-row");
        button.OnClicked += (_, _) =>
        {
            _identityPopover.Popdown();
            click();
        };
        return button;
    }

    /// <summary>Logo (initials until the picture loads), name and the small tag ("Channel", "Personal").</summary>
    private Gtk.Box Row(IdentityRow r, int logoSize)
    {
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        var logo = Ui.Avatar(r.Initials, r.AvatarUrl is { Length: > 0 } url && _logos.TryGetValue(url, out var texture) ? texture : null, logoSize, r.Kind == IdentityKind.Account);
        if (r.AvatarUrl is { Length: > 0 } u && !_logos.ContainsKey(u)) _ = LoadLogoAsync(u);

        row.Append(logo);
        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        text.SetValign(Gtk.Align.Center);
        var name = Gtk.Label.New(r.Name);
        name.SetXalign(0);
        name.SetEllipsize(Pango.EllipsizeMode.End);
        name.SetMaxWidthChars(22);
        name.AddCssClass("heading");
        var tag = Gtk.Label.New(r.Tag);
        tag.SetXalign(0);
        tag.AddCssClass("identity-tag");
        text.Append(name);
        text.Append(tag);
        row.Append(text);
        return row;
    }

    private async Task LoadLogoAsync(string url)
    {
        if (!_logosLoading.Add(url)) return;
        try
        {
            var data = await Http.GetByteArrayAsync(url);
            _logos[url] = Gdk.Texture.NewFromBytes(GLib.Bytes.New(data));
            RenderIdentity();
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is GLib.GException)
        {
            // A logo that doesn't load leaves the initials.
            Logger.Warn("identity logo did not load", ex);
        }
    }

    private async Task SignOutAsync(StudioAccount account)
    {
        var ok = await Confirm.AskAsync(Window, _l.Format("studio.shell.signOutTitle", Localizer.Isolate(_shell.NameOf(account))), _l["studio.shell.signOutBody"], _l["auth.signOut"], _l["common.cancel"]);
        if (!ok) return;
        try
        {
            await _shell.SignOutAsync(account.UserId);
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidOperationException)
        {
            Logger.Error("sign-out failed", ex);
        }
    }
}
