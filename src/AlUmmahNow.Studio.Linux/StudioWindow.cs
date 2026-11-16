using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Shell;

namespace AlUmmahNow.Studio.Linux;

/// <summary>
/// The Studio window: header with the page switcher, channel picker and account menu; a body that
/// shows sign-in, loading, error states or the Studio pages.
/// </summary>
public sealed class StudioWindow
{
    private static readonly Logger Logger = Log.For("ui");

    private readonly Localizer _l;
    private readonly StudioShellViewModel _shell;
    private readonly Gtk.Stack _body = Gtk.Stack.New();
    private readonly Adw.ViewStack _pages = Adw.ViewStack.New();
    private readonly Gtk.MenuButton _channelPicker = Gtk.MenuButton.New();
    private readonly Gtk.Box _channelMenu = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
    private readonly Gtk.Popover _channelPopover = Gtk.Popover.New();
    private readonly Dictionary<string, Gdk.Texture> _logos = new();
    private readonly HashSet<string> _logosLoading = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly Gtk.MenuButton _accountButton = Gtk.MenuButton.New();
    private readonly Gtk.Box _accountMenu = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
    private readonly Gtk.Popover _accountPopover = Gtk.Popover.New();
    private readonly Adw.StatusPage _reauth = Adw.StatusPage.New();
    private readonly Adw.StatusPage _error = Adw.StatusPage.New();
    private readonly Gtk.Label _signInError = Ui.Label("", dim: true, start: false);

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

        var header = Adw.HeaderBar.New();
        var switcher = Adw.ViewSwitcher.New();
        switcher.SetStack(_pages);
        switcher.SetPolicy(Adw.ViewSwitcherPolicy.Wide);
        header.SetTitleWidget(switcher);

        // "Acting as" picker: the active channel's logo, name and "Channel" tag; the menu lists every
        // channel the same way under an "Acting as" heading.
        _channelMenu.SetMarginStart(6);
        _channelMenu.SetMarginEnd(6);
        _channelMenu.SetMarginTop(6);
        _channelMenu.SetMarginBottom(6);
        _channelPopover.SetChild(_channelMenu);
        _channelPopover.AddCssClass("glass");
        _channelPicker.SetPopover(_channelPopover);
        _channelPicker.AddCssClass("flat");
        Ui.Named(_channelPicker, l[ActingAs.HeadingKey]);
        header.PackStart(_channelPicker);

        _accountMenu.SetMarginStart(6);
        _accountMenu.SetMarginEnd(6);
        _accountMenu.SetMarginTop(6);
        _accountMenu.SetMarginBottom(6);
        _accountPopover.SetChild(_accountMenu);
        _accountPopover.AddCssClass("glass");
        _accountButton.SetPopover(_accountPopover);

        header.PackEnd(_accountButton);

        var root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        root.Append(header);
        if (!string.IsNullOrEmpty(banner))
        {
            var b = Adw.Banner.New(banner!);
            b.SetRevealed(true);
            root.Append(b);
        }

        BuildStates();
        _body.SetVexpand(true);
        root.Append(_body);
        Window.SetContent(root);

        _shell.PropertyChanged += (_, _) => Render();
        Render();
    }

    public Adw.ApplicationWindow Window { get; }

    public void AddPage(string name, string title, string icon, Gtk.Widget page) =>
        _pages.AddTitledWithIcon(page, name, title, icon);

    public void ShowPage(string name) => _pages.SetVisibleChildName(name);

    /// <summary>For CI screenshots: open the "Acting as" picker.</summary>
    public void OpenChannelPicker() => _channelPicker.Popup();

    /// <summary>For CI screenshots: open the account menu.</summary>
    public void OpenAccountMenu() => _accountButton.Popup();

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
        _signInError.SetText(_shell.ErrorKey is { } k && state == ShellState.SignedOut ? _l[k] : "");
        _error.SetDescription(_shell.ErrorKey is { } e ? _l[e] : "");
        _reauth.SetDescription(_l.Format("studio.shell.reauthBody", Localizer.Isolate(_shell.ActiveAccount?.Label ?? "")));
        RenderChannels();
        RenderAccounts();
    }

    private void RenderChannels()
    {
        var channels = _shell.Channels;
        var active = _shell.ActiveChannel;
        _channelPicker.SetVisible(channels.Count > 0);
        _channelPicker.SetChild(active == null ? Gtk.Label.New(_l[ActingAs.HeadingKey]) : IdentityRow(active, 24));
        Ui.Named(_channelPicker, active == null ? _l[ActingAs.HeadingKey] : _l.Format(ActingAs.ButtonNameKey, active.Name));

        Ui.Clear(_channelMenu);
        var heading = Gtk.Label.New(_l[ActingAs.HeadingKey]);
        heading.SetXalign(0);
        heading.AddCssClass("heading");
        heading.SetMarginStart(6);
        heading.SetMarginBottom(4);
        _channelMenu.Append(heading);
        foreach (var channel in channels)
        {
            var row = IdentityRow(channel, 32);
            var check = Gtk.Image.NewFromIconName("object-select-symbolic");
            check.SetOpacity(ReferenceEquals(channel, active) ? 1 : 0);
            check.SetHexpand(true);
            check.SetHalign(Gtk.Align.End);
            row.Append(check);
            var pick = Gtk.Button.New();
            pick.SetChild(row);
            pick.AddCssClass("flat");
            Ui.Named(pick, ActingAs.OptionText(channel, k => _l[k]));
            pick.OnClicked += (_, _) =>
            {
                _channelPopover.Popdown();
                _shell.SelectChannel(channel);
            };
            _channelMenu.Append(pick);
        }
    }

    /// <summary>Logo (initials until the picture loads), name and the small "Channel" tag.</summary>
    private Gtk.Box IdentityRow(StudioChannel channel, int logoSize)
    {
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        // Adw.Avatar draws the first letter of each of the first two words of its text, so it gets our
        // initials one per word («قناة النور» → «ق», not «قا»). No letters: the generic icon.
        var initials = Initials.For(channel.Name);
        var logo = Adw.Avatar.New(logoSize, initials == null ? null : string.Join(" ", ActingAs.Graphemes(initials)), initials != null);
        if (channel.AvatarUrl is { Length: > 0 } url)
        {
            if (_logos.TryGetValue(url, out var texture)) logo.SetCustomImage(texture);
            else _ = LoadLogoAsync(url);
        }
        row.Append(logo);
        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        text.SetValign(Gtk.Align.Center);
        var name = Gtk.Label.New(channel.Name);
        name.SetXalign(0);
        name.SetEllipsize(Pango.EllipsizeMode.End);
        name.SetMaxWidthChars(24);
        var tag = Gtk.Label.New(ActingAs.ChannelTag(channel, k => _l[k]));
        tag.SetXalign(0);
        tag.AddCssClass("caption");
        tag.AddCssClass("dim-label");
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
            RenderChannels();
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is GLib.GException)
        {
            // A logo that doesn't load leaves the initials.
            Logger.Warn("channel logo did not load", ex);
        }
    }

    private void RenderAccounts()
    {
        var active = _shell.ActiveAccount;
        _accountButton.SetVisible(_shell.AccountList.Count > 0);
        _accountButton.SetLabel(active?.Label ?? _l["studio.shell.account"]);
        Ui.Named(_accountButton, active == null ? _l["studio.shell.account"] : _l.Format("studio.shell.accountMenu", active.Label));
        Ui.Clear(_accountMenu);
        foreach (var account in _shell.AccountList)
        {
            var isActive = account.UserId == active?.UserId;
            var text = account.Label + (account.NeedsReauth ? " · " + _l["studio.shell.needsReauth"] : "");
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
            var pick = Gtk.Button.NewWithLabel(isActive ? "✓ " + text : text);
            pick.SetHexpand(true);
            pick.AddCssClass("flat");
            Ui.Named(pick, _l.Format("studio.shell.switchTo", account.Label));
            pick.OnClicked += (_, _) =>
            {
                _accountPopover.Popdown();
                _ = _shell.SwitchAccountAsync(account.UserId);
            };
            var signOut = Gtk.Button.NewWithLabel(_l["auth.signOut"]);
            signOut.AddCssClass("flat");
            Ui.Named(signOut, _l.Format("studio.shell.signOutOf", account.Label));
            signOut.OnClicked += (_, _) =>
            {
                _accountPopover.Popdown();
                _ = SignOutAsync(account);
            };
            row.Append(pick);
            row.Append(signOut);
            _accountMenu.Append(row);
        }

        _accountMenu.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        var add = Gtk.Button.NewWithLabel(_l["studio.shell.addAccount"]);
        add.AddCssClass("flat");
        add.OnClicked += (_, _) =>
        {
            _accountPopover.Popdown();
            _ = _shell.SignInAsync(SignInMode.AddAccount);
        };
        _accountMenu.Append(add);
    }

    private async Task SignOutAsync(StudioAccount account)
    {
        var ok = await Confirm.AskAsync(Window, _l.Format("studio.shell.signOutTitle", Localizer.Isolate(account.Label)), _l["studio.shell.signOutBody"], _l["auth.signOut"], _l["common.cancel"]);
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
