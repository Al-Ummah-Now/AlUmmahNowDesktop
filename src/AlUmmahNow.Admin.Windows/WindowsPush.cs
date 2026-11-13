using AlUmmahNow.Admin.Push;
using AlUmmahNow.Admin.Review;
using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Logging;
using Microsoft.Windows.AppNotifications;
using Windows.Networking.PushNotifications;
using Windows.UI.Notifications;

namespace AlUmmahNow.Admin.WindowsApp;

/// <summary>
/// Review alerts over WNS (AlUmmahNow #1286): the server sends a high-priority toast (scenario
/// urgent, group "review", buttons Open and Claim, no images) and a raw "review_clear" when the
/// item is claimed or decided elsewhere, which removes that toast here. Needs the owner's
/// Partner Center identity in a signed MSIX; without it push stays off and the queue says so.
/// </summary>
internal sealed class WindowsPush
{
    private static readonly Logger Logger = Log.For("push");

    private readonly PushConfig _config;
    private readonly IPushRegistry _registry;
    private readonly Func<string> _installId;
    private readonly Localizer _l;
    private readonly Func<PushActivation, Task> _activate;
    private PushNotificationChannel? _channel;

    public WindowsPush(PushConfig config, IPushRegistry registry, Func<string> installId, Localizer l, Func<PushActivation, Task> activate)
    {
        _config = config;
        _registry = registry;
        _installId = installId;
        _l = l;
        _activate = activate;
    }

    public PushState State { get; private set; } = PushState.NotConfigured;

    /// <summary>Shown above the queue when alerts are not on.</summary>
    public string? Message => State switch
    {
        PushState.NotConfigured => _l["admin.push.off"],
        PushState.Failed => _l["admin.push.failed"],
        _ => null,
    };

    public static bool IsPackaged()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current != null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Toast buttons reach the app through AppNotificationManager (packaged builds only).</summary>
    public void ListenForToasts()
    {
        if (!_config.Packaged) return;
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, args) =>
            {
                if (PushActivation.Parse(args.Argument) is { } a) _ = _activate(a);
            };
            AppNotificationManager.Default.Register();
        }
        catch (Exception ex)
        {
            Logger.Warn("toast activation unavailable: " + ex.GetType().Name);
        }
    }

    public async Task StartAsync(string appVersion)
    {
        if (!_config.CanRegister)
        {
            State = PushState.NotConfigured;
            Logger.Info("push off: no WNS package identity in this build");
            return;
        }

        State = PushState.Registering;
        try
        {
            _channel = await PushNotificationChannelManager.CreatePushNotificationChannelForApplicationAsync();
            _channel.PushNotificationReceived += OnReceived;
            await _registry.RegisterAsync(_installId(), _channel.Uri, _l.Language, appVersion);
            State = PushState.Registered;
        }
        catch (Exception ex)
        {
            // The channel URI is a credential for this install: never logged.
            Logger.Warn("push registration failed: " + ex.GetType().Name);
            State = PushState.Failed;
        }
    }

    private void OnReceived(PushNotificationChannel sender, PushNotificationReceivedEventArgs e)
    {
        if (e.NotificationType != PushNotificationType.Raw) return;
        e.Cancel = true;
        if (PushClear.Parse(e.RawNotification?.Content) is not { } clear) return;
        try
        {
            ToastNotificationManager.History.Remove(clear.Tag, clear.Group);
        }
        catch (Exception ex)
        {
            Logger.Warn("could not remove a delivered alert: " + ex.GetType().Name);
        }
    }

    /// <summary>
    /// Sign-out: always removes this install's registration (even when this run opened no
    /// channel, e.g. offline start), before the token is revoked. False when the server could
    /// not be told, so the caller retires the install id and the old row matches nothing.
    /// </summary>
    public async Task<bool> StopAsync()
    {
        var ok = true;
        try
        {
            await _registry.UnregisterAsync(_installId());
        }
        catch (ReviewException ex)
        {
            Logger.Warn("push unregister failed: " + ex.GetType().Name);
            ok = false;
        }

        try
        {
            if (_config.Packaged) ToastNotificationManager.History.RemoveGroup(PushClear.ReviewGroup);
        }
        catch (Exception ex)
        {
            Logger.Warn("could not clear delivered alerts: " + ex.GetType().Name);
        }

        if (_channel != null)
        {
            _channel.PushNotificationReceived -= OnReceived;
            _channel.Close();
            _channel = null;
        }

        State = _config.CanRegister ? PushState.Off : PushState.NotConfigured;
        return ok;
    }
}
