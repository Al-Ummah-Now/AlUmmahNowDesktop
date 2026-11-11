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
    private readonly Gtk.DropDown _channelPicker = Gtk.DropDown.NewFromStrings(Array.Empty<string>());
    private readonly Gtk.Box _channelBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
    private readonly Gtk.MenuButton _accountButton = Gtk.MenuButton.New();
    private readonly Gtk.Box _accountMenu = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
    private readonly Gtk.Popover _accountPopover = Gtk.Popover.New();
    private readonly Adw.StatusPage _reauth = Adw.StatusPage.New();
    private readonly Adw.StatusPage _error = Adw.StatusPage.New();
    private readonly Gtk.Label _signInError = Ui.Label("", dim: true, start: false);
    private bool _syncingPicker;

    public StudioWindow(Adw.Application app, Localizer l, StudioShellViewModel shell, string? banner)
    {
        _l = l;
        _shell = shell;
        Window = Adw.ApplicationWindow.New(app);
        Window.SetDefaultSize(1280, 800);
        Window.SetTitle(l["studio.name"]);

        var header = Adw.HeaderBar.New();
        var switcher = Adw.ViewSwitcher.New();
        switcher.SetStack(_pages);
        switcher.SetPolicy(Adw.ViewSwitcherPolicy.Wide);
        header.SetTitleWidget(switcher);

        _channelBox.Append(Ui.Labelled(l["studio.shell.channel"], _channelPicker));
        _channelPicker.OnNotify += (_, e) =>
        {
            if (_syncingPicker || e.Pspec.GetName() != "selected") return;
            var i = (int)_channelPicker.GetSelected();
            if (i >= 0 && i < _shell.Channels.Count) _shell.SelectChannel(_shell.Channels[i]);
        };
        header.PackStart(_channelBox);

        _accountMenu.SetMarginStart(6);
        _accountMenu.SetMarginEnd(6);
        _accountMenu.SetMarginTop(6);
        _accountMenu.SetMarginBottom(6);
        _accountPopover.SetChild(_accountMenu);
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
        _channelBox.SetVisible(_shell.Channels.Count > 0);
        _syncingPicker = true;
        try
        {
            var names = _shell.Channels.Select(c => c.Role == ChannelRole.Owner ? c.Name : c.Name + " · " + _l["studio.shell.channelRole." + c.Role.ToString().ToLowerInvariant()]).ToArray();
            _channelPicker.SetModel(Gtk.StringList.New(names));
            var index = _shell.ActiveChannel is { } a ? _shell.Channels.ToList().IndexOf(a) : -1;
            _channelPicker.SetSelected(index >= 0 ? (uint)index : uint.MaxValue);
        }
        finally
        {
            _syncingPicker = false;
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
