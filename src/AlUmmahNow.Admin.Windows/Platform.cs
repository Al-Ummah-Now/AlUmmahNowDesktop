using AlUmmahNow.Admin.Auth;
using AlUmmahNow.CoreBridge.Logging;
using Microsoft.Win32;
using Microsoft.Windows.AppLifecycle;

namespace AlUmmahNow.Admin.WindowsApp;

/// <summary>Unpackaged builds register alummahnow-admin:// for the current user (the MSIX declares it).</summary>
internal static class ProtocolRegistration
{
    public static void EnsureRegistered()
    {
        if (WindowsPush.IsPackaged()) return;
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
            ActivationRegistrationManager.RegisterForProtocolActivation(AdminRedirect.Scheme, exe + ",0", "Al Ummah Now Admin", exe);
        }
        catch (Exception ex)
        {
            Log.For("auth").Warn("could not register the sign-in callback: " + ex.GetType().Name);
        }
    }
}

/// <summary>Windows session lock and unlock count as hidden and shown (lock after 5 minutes).</summary>
internal static class SessionWatch
{
    private static AdminWindow? _window;

    public static void Start(AdminWindow window)
    {
        _window = window;
        try
        {
            SystemEvents.SessionSwitch += OnSwitch;
        }
        catch (Exception ex)
        {
            Log.For("lock").Warn("session events unavailable: " + ex.GetType().Name);
        }
    }

    private static void OnSwitch(object sender, SessionSwitchEventArgs e)
    {
        var w = _window;
        if (w == null) return;
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            w.Window.DispatcherQueue.TryEnqueue(w.OnHidden);
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            w.Window.DispatcherQueue.TryEnqueue(w.OnShown);
    }
}
