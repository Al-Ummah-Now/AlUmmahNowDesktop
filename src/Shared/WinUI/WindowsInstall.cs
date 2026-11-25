using System.Runtime.InteropServices;
using AlUmmahNow.App.Services;
using AlUmmahNow.App.Updates;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Native;
using AlUmmahNow.CoreBridge.Native.SelfUpdate;

namespace AlUmmahNow.Updates.Views;

/// <summary>How this copy of the app came onto the computer.</summary>
internal static class WindowsInstall
{
    /// <summary>The check of a Windows app, and the updater of a copy that our setup installed.</summary>
    /// <param name="app">"viewer" or "studio".</param>
    /// <param name="folder">The app's data folder; what is downloaded goes into it.</param>
    /// <param name="close">Closes the app, as its window's close button does.</param>
    public static UpdateService Service(string app, Uri videosApi, string version, string userAgent, string folder, UpdateStore store, bool isDemo, Func<bool> isBusy, Action close)
    {
#if UPDATE_TEST
        // CI's update test is of a demo: the app itself asks nothing of a server
        isDemo = false;
#endif
        IPackageInstaller? installer = null;
#if !AUN_STORE
        if (!isDemo && UpdateInstalls.IsReleaseBuild) installer = VelopackInstaller.Find(close);
#endif
        var checker = new CoreUpdateChecker(videosApi, app, "windows", version, userAgent, store.Load().Core, installs: installer != null);
        var self = installer == null ? null : new SelfUpdater(checker, installer, SelfUpdater.CreateHandler(), checker.ReportAsync, Path.Combine(folder, "updates"), app, "windows", CoreUpdateChecker.Arch);
        var service = new UpdateService(checker, store, isDemo ? UpdateInstall.Development : Detect(installer != null), version, isBusy, new DownloadPageDelivery(new ShellBrowserLauncher()), self: self);
#if UPDATE_TEST
        UpdateTestWatch.Start(service, close);
#endif
        return service;
    }

    private static UpdateInstall Detect(bool installsItself)
    {
        var packaged = false;
        var store = false;
        try
        {
            var package = global::Windows.ApplicationModel.Package.Current;
            packaged = true;
            store = package.SignatureKind == global::Windows.ApplicationModel.PackageSignatureKind.Store;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            // No package: the installer or the zip
        }

        return UpdateInstalls.OnWindows(UpdateInstalls.IsReleaseBuild, packaged, store, AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), installsItself);
    }
}
