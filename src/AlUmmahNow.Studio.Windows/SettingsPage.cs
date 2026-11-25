using System.Diagnostics;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Services;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Shell;
using AlUmmahNow.Studio.Upload;
using AlUmmahNow.Updates.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>
/// Studio settings (Lantern Glass cards): Light or Dark (no "System"), language, the signed-in
/// accounts, passkeys, the watched folder, and the version. Only what Studio already stores or does.
/// The theme and the language are read at start-up, so a change offers "Restart now".
/// </summary>
internal sealed class SettingsPage
{
    private readonly Localizer _l;
    private readonly StudioShellViewModel _shell;
    private readonly UploadsViewModel _uploads;
    private readonly StackPanel _accounts = new() { Spacing = 6 };
    private readonly TextBlock _watch = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _stopWatching;
    private readonly InfoBar _restart;
    private readonly FrameworkElement? _passkeyCard;
    private readonly Func<StudioAccount, Task> _signOut;

    public SettingsPage(Localizer l, AppSettings settings, SettingsStore store, StudioShellViewModel shell, UploadsViewModel uploads,
        Func<Task<string?>> chooseFolder, Func<StudioAccount, Task> signOut, Func<Task> addPasskey, string version, UpdateViews updates, Action close)
    {
        _l = l;
        _shell = shell;
        _uploads = uploads;
        _signOut = signOut;
        var startTheme = settings.Theme == ThemePreference.Light ? ThemePreference.Light : ThemePreference.Dark;
        var startLanguage = l.Language;

        Root = new StackPanel { Spacing = 18, Padding = new Thickness(16, 4, 16, 8) };
        var restartNow = new Button { Content = l["studio.settings.restartNow"] };
        restartNow.Click += (_, _) => Restart(close);
        _restart = new InfoBar { Severity = InfoBarSeverity.Informational, IsClosable = false, Message = l["studio.settings.restart"], ActionButton = restartNow };
        Root.Children.Add(_restart);
        void Changed() => _restart.IsOpen = (settings.Theme == ThemePreference.Light ? ThemePreference.Light : ThemePreference.Dark) != startTheme || settings.Language != startLanguage;

        var columns = new Grid { ColumnSpacing = 20 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel { Spacing = 18 };
        var right = new StackPanel { Spacing = 18 };
        Grid.SetColumn(right, 1);
        columns.Children.Add(left);
        columns.Children.Add(right);
        Root.Children.Add(columns);

        left.Children.Add(Choice(l["settings.theme"], new[] { l["settings.theme.light"], l["settings.theme.dark"] }, startTheme == ThemePreference.Light ? 0 : 1, i =>
        {
            settings.Theme = i == 0 ? ThemePreference.Light : ThemePreference.Dark;
            store.Save(settings);
            Changed();
        }));
        left.Children.Add(Choice(l["settings.language"], new[] { l["settings.language.en"], l["settings.language.ar"] }, l.Language == "en" ? 0 : 1, i =>
        {
            settings.Language = i == 0 ? "en" : "ar";
            store.Save(settings);
            Changed();
        }));
        var about = new StackPanel { Spacing = 6 };
        about.Children.Add(Heading(l["studio.settings.about"]));
        about.Children.Add(new TextBlock { Text = l["studio.name"] });
        about.Children.Add(new TextBlock { Text = l.Format("studio.settings.version", l.Version(version)), Foreground = LanternGlass.Res("StudioMutedBrush") });
        left.Children.Add(LanternGlass.Card(about));
        // Only while the server's switch is on
        var updatesCard = LanternGlass.Card(updates.SectionBody, updates.SectionActions);
        left.Children.Add(updatesCard);
        updates.UseSection(updatesCard);

        var accountsBody = new StackPanel { Spacing = 8 };
        accountsBody.Children.Add(Heading(l[IdentityMenu.AccountsHeadingKey]));
        accountsBody.Children.Add(_accounts);
        var add = new Button { Content = l["studio.shell.addAccount"] };
        add.Click += (_, _) => _ = _shell.SignInAsync(SignInMode.AddAccount);
        right.Children.Add(LanternGlass.Card(accountsBody, Footer(add)));

        if (_shell.PasskeysAvailable)
        {
            var passkeyBody = new StackPanel { Spacing = 8 };
            passkeyBody.Children.Add(Heading(l["studio.settings.passkeys"]));
            passkeyBody.Children.Add(new TextBlock { Text = l["passkey.intro"], TextWrapping = TextWrapping.Wrap, Foreground = LanternGlass.Res("StudioMutedBrush") });
            var addPasskeyButton = new Button { Content = l["passkey.add"], Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            AutomationProperties.SetAutomationId(addPasskeyButton, "SettingsAddPasskey");
            addPasskeyButton.Click += (_, _) => _ = addPasskey();
            _passkeyCard = LanternGlass.Card(passkeyBody, Footer(addPasskeyButton));
            right.Children.Add(_passkeyCard);
        }

        var watchBody = new StackPanel { Spacing = 8 };
        watchBody.Children.Add(Heading(l["studio.upload.watch"]));
        watchBody.Children.Add(_watch);
        _stopWatching = new Button { Content = l["studio.upload.stopWatching"] };
        _stopWatching.Click += (_, _) =>
        {
            _uploads.Watch(null);
            settings.WatchFolder = null;
            store.Save(settings);
        };
        var choose = new Button { Content = l["studio.settings.chooseFolder"] };
        choose.Click += async (_, _) =>
        {
            if (await chooseFolder() is not { } path) return;
            _uploads.Watch(path);
            settings.WatchFolder = path;
            store.Save(settings);
        };
        right.Children.Add(LanternGlass.Card(watchBody, Footer(_stopWatching, choose)));

        _shell.PropertyChanged += (_, _) => RenderAccounts();
        _uploads.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UploadsViewModel.WatchFolder)) RenderWatch();
        };
        RenderAccounts();
        RenderWatch();
    }

    public StackPanel Root { get; }

    private static TextBlock Heading(string text)
    {
        var t = new TextBlock { Text = text, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] };
        AutomationProperties.SetHeadingLevel(t, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        return t;
    }

    private static StackPanel Footer(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }

    /// <summary>A card with a heading and one choice among options.</summary>
    private static FrameworkElement Choice(string title, string[] options, int selected, Action<int> picked)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(Heading(title));
        var radios = new RadioButtons { ItemsSource = options, SelectedIndex = selected };
        AutomationProperties.SetName(radios, title);
        radios.SelectionChanged += (_, _) =>
        {
            if (radios.SelectedIndex >= 0) picked(radios.SelectedIndex);
        };
        body.Children.Add(radios);
        return LanternGlass.Card(body);
    }

    private void RenderAccounts()
    {
        _accounts.Children.Clear();
        foreach (var r in IdentityMenu.Accounts(_shell.AccountList, _shell.ActiveAccount, a => _shell.NameOf(a), k => _l[k], _l.Separator))
        {
            var account = _shell.AccountList.First(a => a.UserId == r.Id);
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(LanternGlass.Avatar(r.Initials, r.AvatarUrl, 32, person: true));
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = r.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = r.Tag, FontSize = 12, Foreground = LanternGlass.Res("StudioMutedBrush") });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var signOut = new Button { Content = _l["auth.signOut"], Foreground = LanternGlass.Res("StudioDangerBrush"), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(signOut, _l.Format("studio.shell.signOutOf", r.Name));
            signOut.Click += (_, _) => _ = _signOut(account);
            Grid.SetColumn(signOut, 2);
            row.Children.Add(signOut);
            _accounts.Children.Add(row);
        }
    }

    private void RenderWatch()
    {
        _watch.Text = _uploads.WatchFolder is { } f ? _l.Format("studio.upload.watchingFolder", Localizer.Isolate(f)) : _l["studio.settings.noWatch"];
        _stopWatching.Visibility = _uploads.WatchFolder != null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Starts a new Studio and quits this one (the theme and language are read at start-up).</summary>
    private static void Restart(Action close)
    {
        if (Environment.ProcessPath is not { } path) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = false });
        // As its window closes: the new one is running by then, so no update is installed under it
        close();
    }
}
