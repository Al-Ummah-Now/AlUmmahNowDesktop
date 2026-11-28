using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Services;
using AlUmmahNow.CoreBridge;
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
    private readonly TextBlock _watch = new() { TextWrapping = TextWrapping.Wrap, TextReadingOrder = TextReadingOrder.UseFlowDirection };
    private readonly Button _stopWatching;
    private readonly InfoBar _restart;
    private readonly FrameworkElement _updatesCard;
    private readonly FrameworkElement? _passkeyCard;
    private readonly Func<StudioAccount, Task> _signOut;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _telemetrySyncTimer;

    public SettingsPage(Localizer l, AppSettings settings, SettingsStore store, StudioShellViewModel shell, UploadsViewModel uploads,
        Func<Task<string?>> chooseFolder, Func<StudioAccount, Task> signOut, Func<Task> addPasskey, string version, UpdateViews updates, Action restart,
        ITelemetryService? telemetry = null)
    {
        _l = l;
        _shell = shell;
        _uploads = uploads;
        _signOut = signOut;
        var startTheme = settings.Theme == ThemePreference.Light ? ThemePreference.Light : ThemePreference.Dark;
        var startLanguage = l.Language;

        Root = new StackPanel { Spacing = 18, Padding = new Thickness(16, 4, 16, 8) };
        var restartNow = new Button { Content = l["studio.settings.restartNow"] };
        // Ends this Studio as its close button does, and starts it again
        restartNow.Click += (_, _) => restart();
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
        // Only while the server's switch is on
        _updatesCard = LanternGlass.Card(updates.SectionBody, updates.SectionActions);
        left.Children.Add(_updatesCard);
        updates.UseSection(_updatesCard);

        var telemetryBody = new StackPanel { Spacing = 8 };
        telemetryBody.Children.Add(Heading(l["settings.telemetry.title"]));
        telemetryBody.Children.Add(new TextBlock { Text = l["settings.telemetry.body"], TextWrapping = TextWrapping.Wrap, Foreground = LanternGlass.Res("StudioMutedBrush") });
        var telemetryUnavailable = telemetry is NullTelemetryService;
        settings.TelemetryEnabled = !telemetryUnavailable && (store.IsTelemetryDisableSaveFailed() || (store.LoadTelemetryEnabled() && (telemetry == null || telemetry.Consent == TelemetryConsent.Full)));
        var telemetryToggle = new ToggleSwitch { Header = l["settings.telemetry.toggle"], IsOn = settings.TelemetryEnabled, IsEnabled = !telemetryUnavailable };
        AutomationProperties.SetAutomationId(telemetryToggle, "SettingsTelemetryToggle");
        var initialPurgeFailed = !telemetryUnavailable && (telemetry?.LastPurgeFailed ?? false);
        var purgeWarningText = l["settings.telemetry.purgeWarning"];
        var disableErrorText = l["settings.telemetry.disableError"];
        var revokingOtherText = l["settings.telemetry.revokingOtherWindow"];
        var telemetryNotice = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = initialPurgeFailed ? Visibility.Visible : Visibility.Collapsed,
            Text = initialPurgeFailed ? purgeWarningText : string.Empty,
        };
        AutomationProperties.SetAutomationId(telemetryNotice, "SettingsTelemetryNotice");
        AutomationProperties.SetLiveSetting(telemetryNotice, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        void SetTelemetryNotice(string text)
        {
            var changed = telemetryNotice.Text != text;
            telemetryNotice.Text = text;
            telemetryNotice.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            if (changed && !string.IsNullOrEmpty(text))
            {
                var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(telemetryNotice)
                    ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(telemetryNotice);
                peer?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
                peer?.RaiseNotificationEvent(
                    Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationKind.ActionAborted,
                    Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.ImportantMostRecent,
                    text,
                    "telemetry-notice");
            }
        }
        var telemetryToggleBusy = false;
        var telemetrySyncInFlight = false;
        if (telemetry != null && !telemetryUnavailable)
        {
            void ScheduleBackgroundConsentSync(TelemetryConsent target)
            {
                if (telemetrySyncInFlight) return;
                telemetrySyncInFlight = true;
                _ = Task.Run(() => telemetry.SetConsent(target)).ContinueWith(_ =>
                {
                    var dq = telemetryNotice.DispatcherQueue;
                    if (dq != null)
                    {
                        dq.TryEnqueue(() =>
                        {
                            telemetrySyncInFlight = false;
                            SyncPurgeNotice();
                        });
                    }
                    else
                    {
                        telemetrySyncInFlight = false;
                    }
                });
            }
            void SyncPurgeNotice()
            {
                if (telemetryToggleBusy) return;
                var failed = telemetry.LastPurgeFailed;
                if (store.IsTelemetryDisableSaveFailed())
                {
                    if (!settings.TelemetryEnabled)
                    {
                        settings.TelemetryEnabled = true;
                        telemetryToggle.IsOn = true;
                    }
                    SetTelemetryNotice(failed
                        ? disableErrorText + "\n" + purgeWarningText
                        : disableErrorText);
                    return;
                }
                var persistedEnabled = store.LoadTelemetryEnabled();
                var pendingOtherRevocation = store.IsTelemetryPendingOtherRevocation();
                if (!persistedEnabled)
                {
                    if (telemetry.Consent != TelemetryConsent.Off)
                    {
                        ScheduleBackgroundConsentSync(TelemetryConsent.Off);
                    }
                    if (settings.TelemetryEnabled)
                    {
                        settings.TelemetryEnabled = false;
                        telemetryToggle.IsOn = false;
                    }
                }
                else
                {
                    if (telemetry.Consent == TelemetryConsent.Off)
                    {
                        ScheduleBackgroundConsentSync(TelemetryConsent.Full);
                        return;
                    }
                    if (telemetry.Consent == TelemetryConsent.Full)
                    {
                        if (!settings.TelemetryEnabled)
                        {
                            settings.TelemetryEnabled = true;
                            telemetryToggle.IsOn = true;
                        }
                        if (!string.IsNullOrEmpty(telemetryNotice.Text))
                        {
                            SetTelemetryNotice(string.Empty);
                        }
                        return;
                    }
                    if (settings.TelemetryEnabled)
                    {
                        settings.TelemetryEnabled = false;
                        telemetryToggle.IsOn = false;
                    }
                }
                if (pendingOtherRevocation)
                {
                    SetTelemetryNotice(failed && telemetry.Consent == TelemetryConsent.Off
                        ? revokingOtherText + "\n" + purgeWarningText
                        : revokingOtherText);
                }
                else if (failed && telemetry.Consent == TelemetryConsent.Off)
                {
                    SetTelemetryNotice(purgeWarningText);
                }
                else
                {
                    var currentNotice = telemetryNotice.Text;
                    if (currentNotice == purgeWarningText ||
                        currentNotice == revokingOtherText ||
                        currentNotice == revokingOtherText + "\n" + purgeWarningText ||
                        currentNotice == disableErrorText ||
                        currentNotice == disableErrorText + "\n" + purgeWarningText)
                    {
                        SetTelemetryNotice(string.Empty);
                    }
                }
            }
            Action<bool> onPurgeChanged = _ => telemetryNotice.DispatcherQueue?.TryEnqueue(SyncPurgeNotice);
            Action<TelemetryConsent> onConsentChanged = _ => telemetryNotice.DispatcherQueue?.TryEnqueue(SyncPurgeNotice);
            var purgeSubscribed = false;
            telemetryNotice.Loaded += (_, _) =>
            {
                if (!purgeSubscribed)
                {
                    telemetry.PurgeFailureChanged += onPurgeChanged;
                    telemetry.ConsentChanged += onConsentChanged;
                    purgeSubscribed = true;
                }
                if (_telemetrySyncTimer == null && telemetryNotice.DispatcherQueue != null)
                {
                    _telemetrySyncTimer = telemetryNotice.DispatcherQueue.CreateTimer();
                    _telemetrySyncTimer.Interval = TimeSpan.FromSeconds(1);
                    _telemetrySyncTimer.Tick += (_, _) => SyncPurgeNotice();
                }
                _telemetrySyncTimer?.Start();
                SyncPurgeNotice();
            };
            telemetryNotice.Unloaded += (_, _) =>
            {
                _telemetrySyncTimer?.Stop();
                _telemetrySyncTimer = null;
                if (purgeSubscribed)
                {
                    telemetry.PurgeFailureChanged -= onPurgeChanged;
                    telemetry.ConsentChanged -= onConsentChanged;
                    purgeSubscribed = false;
                }
            };
        }
        telemetryToggle.Toggled += async (_, _) =>
        {
            if (telemetryToggleBusy) return;
            if (telemetryUnavailable)
            {
                if (telemetryToggle.IsOn) telemetryToggle.IsOn = false;
                return;
            }
            var enabled = telemetryToggle.IsOn;
            if (settings.TelemetryEnabled == enabled) return;
            telemetryToggleBusy = true;
            telemetryToggle.IsEnabled = false;
            var enableErrorText = l["settings.telemetry.enableError"];
            try
            {
                var result = await Task.Run(() =>
                {
                    if (enabled)
                    {
                        if (!store.TrySaveTelemetryEnabled(true, out var writtenToken))
                        {
                            return (FinalEnabled: false, Notice: enableErrorText);
                        }
                        var confirmationFailed = false;
                        var activated = telemetry != null
                            ? telemetry.SetConsent(TelemetryConsent.Full, writtenToken, () =>
                            {
                                var confirmed = store.ConfirmTelemetryEnabled(writtenToken);
                                if (!confirmed) confirmationFailed = true;
                                return confirmed;
                            })
                            : (confirmationFailed = !store.ConfirmTelemetryEnabled(writtenToken)) == false;
                        if (!activated)
                        {
                            var rolledBack = store.RollbackTelemetryEnabled(writtenToken);
                            var purged = telemetry?.SetConsent(TelemetryConsent.Off) ?? true;
                            if (!rolledBack)
                            {
                                return (FinalEnabled: true, Notice: purged
                                    ? disableErrorText
                                    : disableErrorText + "\n" + purgeWarningText);
                            }
                            return (FinalEnabled: false, Notice: confirmationFailed ? string.Empty : enableErrorText);
                        }
                        return (FinalEnabled: true, Notice: string.Empty);
                    }
                    else
                    {
                        var optOut = store.SaveTelemetryOptOut();
                        var purged = telemetry?.SetConsent(TelemetryConsent.Off) ?? true;
                        if (optOut == SettingsStore.TelemetryOptOutResult.SaveFailed)
                        {
                            return (FinalEnabled: true, Notice: purged
                                ? disableErrorText
                                : disableErrorText + "\n" + purgeWarningText);
                        }
                        if (optOut == SettingsStore.TelemetryOptOutResult.PendingOtherProcessRevocation)
                        {
                            return (FinalEnabled: false, Notice: purged
                                ? revokingOtherText
                                : revokingOtherText + "\n" + purgeWarningText);
                        }
                        return (FinalEnabled: false, Notice: purged ? string.Empty : purgeWarningText);
                    }
                });
                settings.TelemetryEnabled = result.FinalEnabled;
                if (telemetryToggle.IsOn != result.FinalEnabled) telemetryToggle.IsOn = result.FinalEnabled;
                SetTelemetryNotice(result.Notice);
            }
            catch
            {
                settings.TelemetryEnabled = !enabled;
                if (telemetryToggle.IsOn != !enabled) telemetryToggle.IsOn = !enabled;
                SetTelemetryNotice(enabled ? enableErrorText : disableErrorText);
            }
            finally
            {
                telemetryToggle.IsEnabled = !telemetryUnavailable;
                telemetryToggleBusy = false;
            }
        };
        telemetryBody.Children.Add(telemetryToggle);
        telemetryBody.Children.Add(telemetryNotice);
        left.Children.Add(LanternGlass.Card(telemetryBody));

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
        var about = new StackPanel { Spacing = 6 };
        about.Children.Add(Heading(l["studio.settings.about"]));
        about.Children.Add(new TextBlock { Text = l["studio.name"] });
        about.Children.Add(new TextBlock { Text = l.Format("studio.settings.version", l.Version(version)), Foreground = LanternGlass.Res("StudioMutedBrush") });
        right.Children.Add(LanternGlass.Card(about));

        _shell.PropertyChanged += (_, _) => RenderAccounts();
        _uploads.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UploadsViewModel.WatchFolder)) RenderWatch();
        };
        RenderAccounts();
        RenderWatch();
    }

    public StackPanel Root { get; }

    // Capture the card and its footer together if the Settings viewport is short.
    public void BringUpdatesIntoView() =>
        _updatesCard.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });

    public bool IsUpdateCardInView
    {
        get
        {
            if (!_updatesCard.IsLoaded || _updatesCard.ActualHeight <= 0) return false;
            var parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(_updatesCard);
            while (parent is not (null or ScrollViewer)) parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent);
            if (parent is not ScrollViewer scroll) return false;
            var bounds = _updatesCard.TransformToVisual(scroll).TransformBounds(
                new global::Windows.Foundation.Rect(0, 0, _updatesCard.ActualWidth, _updatesCard.ActualHeight));
            return bounds.Top >= -1 && bounds.Bottom <= scroll.ViewportHeight + 1;
        }
    }

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
        _watch.Text = _uploads.WatchFolder is { } f ? _l.Format("studio.upload.watchingFolder", Localizer.IsolateLtr(f)) : _l["studio.settings.noWatch"];
        _stopWatching.Visibility = _uploads.WatchFolder != null ? Visibility.Visible : Visibility.Collapsed;
    }
}
