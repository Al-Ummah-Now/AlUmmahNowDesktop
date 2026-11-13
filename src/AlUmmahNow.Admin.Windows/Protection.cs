using System.Runtime.InteropServices;
using AlUmmahNow.CoreBridge.Logging;
using Windows.Security.Credentials.UI;

namespace AlUmmahNow.Admin.WindowsApp;

/// <summary>
/// Window protections (wiki AUN-ADMIN-NATIVE, Media handling): every Admin window is excluded
/// from screenshots, screen recording, screen sharing and the Alt+Tab / taskbar thumbnails.
/// </summary>
internal static class Protection
{
    private const uint WdaExcludeFromCapture = 0x00000011;
    private const uint WdaMonitor = 0x00000001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    private static readonly Logger Logger = Log.For("protect");

    /// <summary>Applies WDA_EXCLUDEFROMCAPTURE (Windows 10 2004+), falling back to WDA_MONITOR (black in captures).</summary>
    public static bool ExcludeFromCapture(IntPtr hwnd)
    {
        if (SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture)) return true;
        Logger.Warn("WDA_EXCLUDEFROMCAPTURE refused (" + Marshal.GetLastWin32Error() + "), using WDA_MONITOR");
        return SetWindowDisplayAffinity(hwnd, WdaMonitor);
    }

    /// <summary>What the window has now (CI checks it is not 0).</summary>
    public static uint Affinity(IntPtr hwnd) => GetWindowDisplayAffinity(hwnd, out var a) ? a : 0;
}

internal enum HelloResult { Verified, NotAvailable, Failed }

/// <summary>Windows Hello (PIN, face, fingerprint) through UserConsentVerifier, owned by the Admin window.</summary>
internal static class Hello
{
    public static async Task<HelloResult> VerifyAsync(IntPtr hwnd, string message)
    {
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            if (availability != UserConsentVerifierAvailability.Available) return HelloResult.NotAvailable;
            var result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(hwnd, message);
            return result == UserConsentVerificationResult.Verified ? HelloResult.Verified : HelloResult.Failed;
        }
        catch (Exception ex)
        {
            Log.For("hello").Warn("Windows Hello failed: " + ex.GetType().Name);
            return HelloResult.Failed;
        }
    }
}
