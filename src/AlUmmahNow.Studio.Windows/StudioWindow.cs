using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>
/// The Studio window: top navigation for the pages, channel picker and account menu, and a body
/// that shows sign-in, loading and error states or the selected page.
/// </summary>
internal sealed class StudioWindow
{
    private static readonly Logger Logger = Log.For("ui");

    private readonly Localizer _l;
    private readonly StudioShellViewModel _shell;
    private readonly FlowDirection _direction;
    private readonly Grid _body = new();
    private readonly NavigationView _nav;
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private readonly ComboBox _channelPicker;
    private readonly DropDownButton _accountButton;
    private readonly MenuFlyout _accountMenu = new() { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
    private bool _syncingPicker;

    public StudioWindow(Localizer l, StudioShellViewModel shell, string? banner)
    {
        _l = l;
        _shell = shell;
        _direction = l.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        _channelPicker = new ComboBox { MinWidth = 220, VerticalAlignment = VerticalAlignment.Center, PlaceholderText = l["studio.shell.channel"] };
        AutomationProperties.SetName(_channelPicker, l["studio.shell.channel"]);
        AutomationProperties.SetAutomationId(_channelPicker, "ChannelPicker");
        _channelPicker.SelectionChanged += (_, _) =>
        {
            if (_syncingPicker) return;
            var i = _channelPicker.SelectedIndex;
            if (i >= 0 && i < _shell.Channels.Count) _shell.SelectChannel(_shell.Channels[i]);
        };

        _accountButton = new DropDownButton { Flyout = _accountMenu, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(_accountButton, "AccountMenu");

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 12, 0) };
        bar.Children.Add(_channelPicker);
        bar.Children.Add(_accountButton);

        _nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Top,
            IsSettingsVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            PaneFooter = bar,
        };
        _nav.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItem is NavigationViewItem item && item.Tag is string name && _pages.TryGetValue(name, out var page)) _nav.Content = new ScrollViewer { Content = page };
        };

        var root = new Grid { FlowDirection = _direction };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        if (!string.IsNullOrEmpty(banner)) root.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = banner });
        Grid.SetRow(_body, 1);
        root.Children.Add(_body);

        Window = new Window { Title = l["studio.name"], Content = root };
        _shell.PropertyChanged += (_, _) => Render();
        Render();
    }

    public Window Window { get; }

    public void AddPage(string name, string title, Symbol icon, FrameworkElement page)
    {
        _pages[name] = page;
        var item = new NavigationViewItem { Content = title, Tag = name, Icon = new SymbolIcon(icon) };
        AutomationProperties.SetAutomationId(item, "Page_" + name);
        _nav.MenuItems.Add(item);
    }

    public void ShowPage(string name)
    {
        var item = _nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == name) ?? _nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault();
        _nav.SelectedItem = item;
    }

    /// <summary>For CI screenshots: open the account menu.</summary>
    public void OpenAccountMenu() => _accountMenu.ShowAt(_accountButton);

    /// <summary>For CI screenshots: show the sign-out confirmation for the active account.</summary>
    public void AskSignOut()
    {
        if (_shell.ActiveAccount is { } a) _ = SignOutAsync(a);
    }

    private void Render()
    {
        _body.Children.Clear();
        _body.Children.Add(_shell.State switch
        {
            ShellState.SignedOut => Status(_l["studio.shell.signInTitle"], _l["studio.shell.signInBody"], Action(_l["auth.signIn"], () => _ = _shell.SignInAsync(SignInMode.Default), primary: true, id: "SignIn"), _shell.ErrorKey is { } k ? _l[k] : null),
            ShellState.SigningIn => Status(_l["auth.signingIn"], null, new ProgressRing { IsActive = true }),
            ShellState.Loading => Status(_l["studio.shell.loading"], null, new ProgressRing { IsActive = true }),
            ShellState.NoChannel => Status(_l["studio.shell.noChannelTitle"], _l["studio.shell.noChannelBody"], Action(_l["studio.shell.retry"], () => _ = _shell.RetryAsync())),
            ShellState.Error => Status(_l["studio.shell.errorTitle"], _shell.ErrorKey is { } e ? _l[e] : null, Action(_l["studio.shell.retry"], () => _ = _shell.RetryAsync())),
            ShellState.NeedsReauth => Status(_l["studio.shell.reauthTitle"], _l.Format("studio.shell.reauthBody", Localizer.Isolate(_shell.ActiveAccount?.Label ?? "")), Action(_l["auth.signIn"], () => _ = _shell.SignInAsync(SignInMode.Default), primary: true, id: "SignIn")),
            _ => _nav,
        });
        RenderChannels();
        RenderAccounts();
    }

    private static FrameworkElement Status(string title, string? body, FrameworkElement action, string? note = null)
    {
        var panel = new StackPanel { Spacing = 16, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var heading = new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        panel.Children.Add(heading);
        if (!string.IsNullOrEmpty(body)) panel.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Opacity = 0.8 });
        action.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(action);
        if (!string.IsNullOrEmpty(note))
        {
            var n = new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
            AutomationProperties.SetLiveSetting(n, AutomationLiveSetting.Assertive);
            panel.Children.Add(n);
        }

        return panel;
    }

    private static Button Action(string text, Action click, bool primary = false, string? id = null)
    {
        var b = new Button { Content = text };
        if (primary) b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        if (id != null) AutomationProperties.SetAutomationId(b, id);
        b.Click += (_, _) => click();
        return b;
    }

    private void RenderChannels()
    {
        _syncingPicker = true;
        try
        {
            _channelPicker.Visibility = _shell.Channels.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _channelPicker.ItemsSource = _shell.Channels.Select(c => c.Role == ChannelRole.Owner ? c.Name : c.Name + " · " + _l["studio.shell.channelRole." + c.Role.ToString().ToLowerInvariant()]).ToList();
            _channelPicker.SelectedIndex = _shell.ActiveChannel is { } a ? _shell.Channels.ToList().IndexOf(a) : -1;
        }
        finally
        {
            _syncingPicker = false;
        }
    }

    private void RenderAccounts()
    {
        var active = _shell.ActiveAccount;
        _accountButton.Visibility = _shell.AccountList.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _accountButton.Content = active?.Label ?? _l["studio.shell.account"];
        AutomationProperties.SetName(_accountButton, active == null ? _l["studio.shell.account"] : _l.Format("studio.shell.accountMenu", active.Label));
        _accountMenu.Items.Clear();
        foreach (var account in _shell.AccountList)
        {
            var isActive = account.UserId == active?.UserId;
            var item = new ToggleMenuFlyoutItem
            {
                Text = account.Label + (account.NeedsReauth ? " · " + _l["studio.shell.needsReauth"] : ""),
                IsChecked = isActive,
            };
            AutomationProperties.SetName(item, _l.Format("studio.shell.switchTo", account.Label));
            item.Click += (_, _) => _ = _shell.SwitchAccountAsync(account.UserId);
            _accountMenu.Items.Add(item);
        }

        _accountMenu.Items.Add(new MenuFlyoutSeparator());
        var add = new MenuFlyoutItem { Text = _l["studio.shell.addAccount"], Icon = new SymbolIcon(Symbol.AddFriend) };
        add.Click += (_, _) => _ = _shell.SignInAsync(SignInMode.AddAccount);
        _accountMenu.Items.Add(add);
        _accountMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var account in _shell.AccountList)
        {
            var signOut = new MenuFlyoutItem { Text = _l.Format("studio.shell.signOutOf", account.Label) };
            signOut.Click += (_, _) => _ = SignOutAsync(account);
            _accountMenu.Items.Add(signOut);
        }
    }

    private async Task SignOutAsync(StudioAccount account)
    {
        if (Window.Content?.XamlRoot is not { } root) return;
        var ok = await Confirm.AskAsync(root, _direction, _l.Format("studio.shell.signOutTitle", Localizer.Isolate(account.Label)), _l["studio.shell.signOutBody"], _l["auth.signOut"], _l["common.cancel"]);
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
