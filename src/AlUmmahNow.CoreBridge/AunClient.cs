using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace AlUmmahNow.CoreBridge
{
    /// <summary>
    /// The <c>X-AUN-Client</c> header (AlUmmahNow request origin, wiki AUN-ORIGIN): which app, app version and OS sends
    /// a request, e.g. <c>aun-studio/1.4.0 (Windows 10.0)</c>. The servers count requests by it for errors and
    /// analytics. It carries no identifier: no device id, no user, no install.
    /// </summary>
    public static class AunClient
    {
        public const string Header = "X-AUN-Client";

        /// <summary>From the app's User-Agent ("AlUmmahNowStudioWindows/1.4.0"): its kind, its version, this OS.</summary>
        public static string FromUserAgent(string? userAgent) => FromUserAgent(userAgent, CurrentOs());

        public static string FromUserAgent(string? userAgent, (string Name, string Version) os)
        {
            var parts = (userAgent ?? "").Split(new[] { '/' }, 2);
            var name = parts[0].ToLowerInvariant();
            var app = name.Contains("admin") ? "admin" : name.Contains("studio") ? "studio" : "desktop";
            var version = Token(parts.Length > 1 ? parts[1] : "");
            var osVersion = Token(os.Version);
            return $"aun-{app}/{(version.Length == 0 ? "0" : version)} ({os.Name}{(osVersion.Length == 0 ? "" : " " + osVersion)})";
        }

        /// <summary>The OS name the servers map to a surface: Windows, Linux, macOS or Tizen.</summary>
        public static (string Name, string Version) CurrentOs()
        {
            var v = Environment.OSVersion.Version;
            var version = $"{v.Major}.{v.Minor}";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return ("Windows", version);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return ("macOS", version);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return (Environment.GetEnvironmentVariable("TIZEN_API_VERSION") != null ? "Tizen" : "Linux", version);
            return ("unknown", version);
        }

        internal static string Token(string text) =>
            new string(text.Where(c => c < 128 && (char.IsLetterOrDigit(c) || ".-_+".IndexOf(c) >= 0)).Take(40).ToArray());
    }
}
