using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace AlUmmahNow.Admin.WindowsApp;

/// <summary>
/// One running Admin per user: a second launch (the browser handing back
/// alummahnow-admin://auth/callback, a toast button) is redirected to the first and exits.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var current = AppInstance.FindOrRegisterForKey("al-ummah-now-admin");
        if (!current.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            current.RedirectActivationToAsync(activation).AsTask().GetAwaiter().GetResult();
            return 0;
        }

        Application.Start(callback =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App(current);
        });
        return 0;
    }
}
