using System.Diagnostics;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Services;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Shell;
using AlUmmahNow.Studio.Upload;
using AlUmmahNow.Updates.Views;

namespace AlUmmahNow.Studio.Linux;

/// <summary>
/// Studio settings (Lantern Glass cards): Light or Dark (no "System"), language, the signed-in
/// accounts, the watched folder, and the version. Only what Studio already stores or does.
/// Light/Dark applies at once; a new language needs a restart, offered in a banner.
/// </summary>
public sealed class SettingsPage
{
    private readonly Localizer _l;
    private readonly StudioShellViewModel _shell;
    private readonly UploadsViewModel _uploads;
    private readonly Gtk.Box _accounts = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
    private readonly Gtk.Label _watch = Ui.Label("");
    private readonly Gtk.Button _stopWatching;
    private readonly Adw.Banner _restart;

    public SettingsPage(Localizer l, AppSettings settings, SettingsStore store, StudioShellViewModel shell, UploadsViewModel uploads,
        Func<Task<string?>> chooseFolder, Func<StudioAccount, Task> signOut, string version, UpdateViews updates,
        ITelemetryService? telemetry = null)
    {
        _l = l;
        _shell = shell;
        _uploads = uploads;

        var page = Gtk.Box.New(Gtk.Orientation.Vertical, 18);
        page.SetMarginStart(24);
        page.SetMarginEnd(24);
        page.SetMarginTop(18);
        page.SetMarginBottom(24);
        _restart = Adw.Banner.New(l["studio.settings.restart"]);
        _restart.SetButtonLabel(l["studio.settings.restartNow"]);
        _restart.OnButtonClicked += (_, _) => Restart();
        page.Append(_restart);

        var columns = Gtk.Box.New(Gtk.Orientation.Horizontal, 20);
        columns.SetHomogeneous(true);
        var left = Gtk.Box.New(Gtk.Orientation.Vertical, 18);
        var right = Gtk.Box.New(Gtk.Orientation.Vertical, 18);
        columns.Append(left);
        columns.Append(right);
        page.Append(columns);

        // Theme: Light or Dark, applied at once.
        var dark = settings.Theme != ThemePreference.Light;
        left.Append(Choice(l["settings.theme"], new[] { (l["settings.theme.light"], !dark), (l["settings.theme.dark"], dark) }, i =>
        {
            settings.Theme = i == 0 ? ThemePreference.Light : ThemePreference.Dark;
            store.Save(settings);
            Adw.StyleManager.GetDefault().SetColorScheme(i == 0 ? Adw.ColorScheme.ForceLight : Adw.ColorScheme.ForceDark);
        }));

        // Language: English or العربية, after a restart.
        left.Append(Choice(l["settings.language"], new[] { (l["settings.language.en"], l.Language == "en"), (l["settings.language.ar"], l.Language == "ar") }, i =>
        {
            settings.Language = i == 0 ? "en" : "ar";
            store.Save(settings);
            _restart.SetRevealed(settings.Language != l.Language);
        }));

        // Opt-in diagnostics and crash reports (off by default).
        var telemetryBody = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        telemetryBody.Append(Ui.CardTitle(l["settings.telemetry.title"]));
        telemetryBody.Append(Ui.Label(l["settings.telemetry.body"], dim: true));
        var telemetryUnavailable = telemetry is NullTelemetryService;
        settings.TelemetryEnabled = !telemetryUnavailable && store.LoadTelemetryEnabled() && (telemetry == null || telemetry.Consent == TelemetryConsent.Full);
        var telemetryCheck = Gtk.CheckButton.NewWithLabel(l["settings.telemetry.toggle"]);
        telemetryCheck.SetActive(settings.TelemetryEnabled);
        telemetryCheck.SetSensitive(!telemetryUnavailable);
        var purgeWarningText = l["settings.telemetry.purgeWarning"];
        var disableErrorText = l["settings.telemetry.disableError"];
        var revokingOtherText = l["settings.telemetry.revokingOtherWindow"];
        var telemetryNotice = Gtk.Label.New(string.Empty);
        telemetryNotice.SetHalign(Gtk.Align.Start);
        telemetryNotice.SetWrap(true);
        telemetryNotice.AddCssClass("error");
        telemetryNotice.AccessibleRole = Gtk.AccessibleRole.Alert;
        telemetryNotice.SetVisible(false);
        void SetTelemetryNotice(string text)
        {
            var changed = telemetryNotice.GetText() != text;
            telemetryNotice.SetText(text);
            telemetryNotice.SetVisible(!string.IsNullOrEmpty(text));
            if (changed)
            {
                var descValues = new[] { new GObject.Value(text) };
                using var descHandle = GObject.Internal.ValueArray2OwnedHandle.Create(descValues);
                Gtk.Internal.Accessible.UpdateProperty(telemetryCheck.Handle.DangerousGetHandle(), 1, new[] { Gtk.AccessibleProperty.Description }, descHandle);
                GC.KeepAlive(descValues);
                if (!string.IsNullOrEmpty(text))
                {
                    var alertValues = new[] { new GObject.Value(text) };
                    using var alertHandle = GObject.Internal.ValueArray2OwnedHandle.Create(alertValues);
                    Gtk.Internal.Accessible.UpdateProperty(telemetryNotice.Handle.DangerousGetHandle(), 1, new[] { Gtk.AccessibleProperty.Label }, alertHandle);
                    GC.KeepAlive(alertValues);
                    telemetryNotice.Announce(text, Gtk.AccessibleAnnouncementPriority.High);
                }
            }
        }
        var disableSaveFailed = false;
        var pendingOtherRevocation = false;
        if (telemetry != null && !telemetryUnavailable)
        {
            void SyncPurgeNotice()
            {
                var failed = telemetry.LastPurgeFailed;
                var persistedEnabled = store.LoadTelemetryEnabled();
                if (disableSaveFailed)
                {
                    if (!persistedEnabled)
                    {
                        disableSaveFailed = false;
                    }
                    else
                    {
                        if (!settings.TelemetryEnabled)
                        {
                            settings.TelemetryEnabled = true;
                            telemetryCheck.SetActive(true);
                        }
                        SetTelemetryNotice(failed
                            ? disableErrorText + "\n" + purgeWarningText
                            : disableErrorText);
                        return;
                    }
                }
                if (pendingOtherRevocation && (persistedEnabled || !store.HasActiveFlushGuards()))
                {
                    pendingOtherRevocation = false;
                }
                if (!persistedEnabled)
                {
                    if (telemetry.Consent != TelemetryConsent.Off)
                    {
                        telemetry.SetConsent(TelemetryConsent.Off);
                        failed = telemetry.LastPurgeFailed;
                    }
                    if (settings.TelemetryEnabled)
                    {
                        settings.TelemetryEnabled = false;
                        telemetryCheck.SetActive(false);
                    }
                }
                else
                {
                    if (telemetry.Consent == TelemetryConsent.Off)
                    {
                        telemetry.SetConsent(TelemetryConsent.Full);
                    }
                    if (telemetry.Consent == TelemetryConsent.Full)
                    {
                        if (!settings.TelemetryEnabled)
                        {
                            settings.TelemetryEnabled = true;
                            telemetryCheck.SetActive(true);
                        }
                        if (!string.IsNullOrEmpty(telemetryNotice.GetText()))
                        {
                            SetTelemetryNotice(string.Empty);
                        }
                        return;
                    }
                    if (settings.TelemetryEnabled)
                    {
                        settings.TelemetryEnabled = false;
                        telemetryCheck.SetActive(false);
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
                    var currentNotice = telemetryNotice.GetText();
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
            var telemetryDestroyed = false;
            telemetry.PurgeFailureChanged += onPurgeChanged;
            telemetry.ConsentChanged += onConsentChanged;
            telemetryCheck.OnMap += (_, _) => SyncPurgeNotice();
            GLib.Functions.TimeoutAdd(0, 1000, () =>
            {
                if (telemetryDestroyed) return false;
                SyncPurgeNotice();
                return true;
            });
            telemetryNotice.OnDestroy += (_, _) =>
            {
                telemetryDestroyed = true;
                telemetry.PurgeFailureChanged -= onPurgeChanged;
                telemetry.ConsentChanged -= onConsentChanged;
            };
            SyncPurgeNotice();
        }
        telemetryCheck.OnToggled += (_, _) =>
        {
            if (telemetryUnavailable)
            {
                if (telemetryCheck.GetActive()) telemetryCheck.SetActive(false);
                return;
            }
            var enabled = telemetryCheck.GetActive();
            if (settings.TelemetryEnabled == enabled) return;
            if (enabled)
            {
                if (!store.TrySaveTelemetryEnabled(true, out var writtenToken))
                {
                    settings.TelemetryEnabled = false;
                    telemetryCheck.SetActive(false);
                    SetTelemetryNotice(l["settings.telemetry.enableError"]);
                    return;
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
                    disableSaveFailed = !rolledBack;
                    if (!rolledBack)
                    {
                        settings.TelemetryEnabled = true;
                        telemetryCheck.SetActive(true);
                        SetTelemetryNotice(purged
                            ? disableErrorText
                            : disableErrorText + "\n" + purgeWarningText);
                        return;
                    }
                    settings.TelemetryEnabled = false;
                    telemetryCheck.SetActive(false);
                    SetTelemetryNotice(confirmationFailed ? string.Empty : l["settings.telemetry.enableError"]);
                    return;
                }
                disableSaveFailed = false;
                pendingOtherRevocation = false;
                settings.TelemetryEnabled = true;
                SetTelemetryNotice(string.Empty);
            }
            else
            {
                var optOut = store.SaveTelemetryOptOut();
                var purged = telemetry?.SetConsent(TelemetryConsent.Off) ?? true;
                disableSaveFailed = optOut == SettingsStore.TelemetryOptOutResult.SaveFailed;
                pendingOtherRevocation = optOut == SettingsStore.TelemetryOptOutResult.PendingOtherProcessRevocation;
                if (disableSaveFailed)
                {
                    settings.TelemetryEnabled = true;
                    telemetryCheck.SetActive(true);
                    SetTelemetryNotice(purged
                        ? disableErrorText
                        : disableErrorText + "\n" + purgeWarningText);
                    return;
                }
                settings.TelemetryEnabled = false;
                if (pendingOtherRevocation)
                {
                    SetTelemetryNotice(purged
                        ? revokingOtherText
                        : revokingOtherText + "\n" + purgeWarningText);
                    return;
                }
                SetTelemetryNotice(purged ? string.Empty : purgeWarningText);
            }
        };
        telemetryBody.Append(telemetryCheck);
        telemetryBody.Append(telemetryNotice);
        left.Append(Ui.Card(telemetryBody));

        var about = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        about.Append(Ui.CardTitle(l["studio.settings.about"]));
        about.Append(Ui.Label(l["studio.name"]));
        about.Append(Ui.Label(l.Format("studio.settings.version", l.Version(version)), dim: true));
        left.Append(Ui.Card(about));

        // Accounts: each person, Sign out; Add another account in the footer.
        var accountsBody = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        accountsBody.Append(Ui.CardTitle(l[IdentityMenu.AccountsHeadingKey]));
        _accounts.AddCssClass("boxed-list-rows");
        accountsBody.Append(_accounts);
        right.Append(Ui.Card(accountsBody, Ui.Button(l["studio.shell.addAccount"], () => _ = _shell.SignInAsync(SignInMode.AddAccount))));

        // The watched folder: Stop watching and Choose folder in the footer.
        var watchBody = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        watchBody.Append(Ui.CardTitle(l["studio.upload.watch"]));
        watchBody.Append(_watch);
        var watchActions = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        _stopWatching = Ui.Button(l["studio.upload.stopWatching"], () =>
        {
            _uploads.Watch(null);
            settings.WatchFolder = null;
            store.Save(settings);
        }, "flat");
        watchActions.Append(_stopWatching);
        watchActions.Append(Ui.Button(l["studio.settings.chooseFolder"], async () =>
        {
            if (await chooseFolder() is not { } path) return;
            _uploads.Watch(path);
            settings.WatchFolder = path;
            store.Save(settings);
        }));
        right.Append(Ui.Card(watchBody, watchActions));

        // Only while the server's switch is on
        var updatesCard = Ui.Card(updates.SectionBody, updates.SectionActions);
        right.Append(updatesCard);
        updates.UseSection(updatesCard);

        Root = Gtk.ScrolledWindow.New();
        Root.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        Root.SetChild(page);

        _shell.PropertyChanged += (_, _) => RenderAccounts(signOut);
        _uploads.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UploadsViewModel.WatchFolder)) RenderWatch();
        };
        RenderAccounts(signOut);
        RenderWatch();
    }

    public Gtk.ScrolledWindow Root { get; }

    /// <summary>A card with a heading and one choice among options (radio buttons).</summary>
    private static Gtk.Widget Choice(string title, (string Label, bool On)[] options, Action<int> picked)
    {
        var body = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        body.Append(Ui.CardTitle(title));
        Gtk.CheckButton? first = null;
        for (var i = 0; i < options.Length; i++)
        {
            var index = i;
            var option = Gtk.CheckButton.NewWithLabel(options[i].Label);
            if (first == null) first = option;
            else option.SetGroup(first);
            option.SetActive(options[i].On);
            option.OnToggled += (_, _) =>
            {
                if (option.GetActive()) picked(index);
            };
            body.Append(option);
        }

        return Ui.Card(body);
    }

    private void RenderAccounts(Func<StudioAccount, Task> signOut)
    {
        Ui.Clear(_accounts);
        var people = IdentityMenu.Accounts(_shell.AccountList, _shell.ActiveAccount, a => _shell.NameOf(a), k => _l[k], _l.Separator);
        foreach (var r in people)
        {
            var account = _shell.AccountList.First(a => a.UserId == r.Id);
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
            row.Append(Ui.Avatar(r.Initials, null, 32, person: true));
            var text = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            text.SetHexpand(true);
            text.SetValign(Gtk.Align.Center);
            var name = Ui.Label(r.Name);
            name.AddCssClass("heading");
            text.Append(name);
            var tag = Ui.Label(r.Tag, dim: true);
            tag.AddCssClass("caption");
            text.Append(tag);
            row.Append(text);
            var button = Ui.Button(_l["auth.signOut"], () => _ = signOut(account), "flat");
            button.AddCssClass("danger");
            Ui.Named(button, _l.Format("studio.shell.signOutOf", r.Name));
            button.SetValign(Gtk.Align.Center);
            row.Append(button);
            _accounts.Append(row);
        }
    }

    private void RenderWatch()
    {
        _watch.SetText(_uploads.WatchFolder is { } f ? _l.Format("studio.upload.watchingFolder", Localizer.Isolate(f)) : _l["studio.settings.noWatch"]);
        _stopWatching.SetVisible(_uploads.WatchFolder != null);
    }

    /// <summary>Starts a new Studio and quits this one (the language is read at start-up).</summary>
    private static void Restart()
    {
        var path = Environment.ProcessPath;
        if (path == null) return;
        var start = new ProcessStartInfo(path) { UseShellExecute = false };
        // Run through the dotnet host: pass the app's own assembly again.
        if (Path.GetFileNameWithoutExtension(path) == "dotnet" && typeof(SettingsPage).Assembly.Location is { Length: > 0 } dll) start.ArgumentList.Add(dll);
        Process.Start(start);
        Gio.Application.GetDefault()?.Quit();
    }
}
