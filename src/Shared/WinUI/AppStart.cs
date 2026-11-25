using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AlUmmahNow.Updates.Views;

/// <summary>The start of both Windows apps.</summary>
internal static class AppStart
{
    /// <summary>The updater itself started this run, and may not have ended yet.</summary>
    public const string AfterUpdate = "--after-update";

    /// <param name="product">The app's name, as the setup has it.</param>
    /// <param name="shortcut">Its name in the Start menu, as the MSI has it.</param>
    public static void Run(string product, string shortcut, Func<Application> create)
    {
#if !AUN_STORE
        // Velopack's own calls on install, update and uninstall run here and end the process. It never installs a package by
        // itself: what is staged is checked again first, and only UpdateService has it installed
        Velopack.VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnAfterInstallFastCallback(_ => Rename(product, shortcut))
            .OnFirstRun(_ => Rename(product, shortcut))
            .Run();
        // Started while an update is installed: the updater ends what runs from the folder, so no work begins before it is done
        if (!Environment.GetCommandLineArgs().Contains(AfterUpdate)) CoreBridge.Native.SelfUpdate.InstallGuard.ForThisProcess()?.WaitForUpdater(TimeSpan.FromSeconds(90));
#endif
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            create();
        });
        // However the loop came to return: the work stops before the process ends
        WindowsEnd.WaitForEnd();
    }

#if !AUN_STORE
    /// <summary>Velopack names the Start menu's shortcut after the product; ours is short. An update keeps the name.</summary>
    private static void Rename(string product, string shortcut)
    {
        try
        {
            var menu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            var made = Path.Combine(menu, product + ".lnk");
            var ours = Path.Combine(menu, shortcut + ".lnk");
            if (File.Exists(made) && !File.Exists(ours)) File.Move(made, ours);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It keeps the long name
        }
    }
#endif
}
