using System.Runtime.InteropServices;
using AlUmmahNow.App.Updates;

namespace AlUmmahNow.Updates.Views;

/// <summary>How this copy of the app came onto the computer.</summary>
internal static class WindowsInstall
{
    public static UpdateInstall Detect()
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

        return UpdateInstalls.OnWindows(UpdateInstalls.IsReleaseBuild, packaged, store, AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
    }
}
