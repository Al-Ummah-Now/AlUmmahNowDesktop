using System;

namespace AlUmmahNow.App.Updates
{
    /// <summary>The app that checks.</summary>
    public enum UpdateApp { Viewer, Studio }

    /// <summary>How this copy was installed: it decides whether the app checks, and how a person updates it.</summary>
    public enum UpdateInstall
    {
        /// <summary>Not built by the release workflow: nothing is checked.</summary>
        Development,

        /// <summary>Updated by the Microsoft Store: nothing is checked.</summary>
        MicrosoftStore,

        /// <summary>An MSIX from the release page.</summary>
        Msix,

        /// <summary>Our per-user setup: it installs updates itself.</summary>
        SelfUpdating,

        /// <summary>The MSI.</summary>
        Installer,

        /// <summary>The portable zip.</summary>
        Portable,

        Deb,
        Flatpak,

        /// <summary>Linux, from none of our packages.</summary>
        LinuxFolder,
    }

    public static class UpdateInstalls
    {
        /// <summary>
        /// The only page the app opens for a new version. Compiled in: no answer of the server, and no file, can send a
        /// person elsewhere.
        /// </summary>
        public static readonly Uri DownloadPage = new Uri("https://github.com/Al-Ummah-Now/AlUmmahNowDesktop/releases");

        /// <summary>Built by the release workflow (it alone passes <c>AunProd</c>).</summary>
#if AUN_RELEASE
        public const bool IsReleaseBuild = true;
#else
        public const bool IsReleaseBuild = false;
#endif

        /// <summary>Whether this copy asks the server at all.</summary>
        public static bool Checks(this UpdateInstall install) =>
            install != UpdateInstall.Development && install != UpdateInstall.MicrosoftStore;

        /// <param name="isPackaged">Runs with an MSIX identity.</param>
        /// <param name="isStoreSigned">That package was signed by the Microsoft Store.</param>
        /// <param name="folder">Where the app runs from.</param>
        /// <param name="programFiles">Windows' folder of installed programs.</param>
        /// <param name="installsItself">Our setup installed it, in a folder only this user can change.</param>
        public static UpdateInstall OnWindows(bool isRelease, bool isPackaged, bool isStoreSigned, string folder, string programFiles, bool installsItself = false)
        {
            if (isPackaged && isStoreSigned) return UpdateInstall.MicrosoftStore;
            if (!isRelease) return UpdateInstall.Development;
            if (isPackaged) return UpdateInstall.Msix;
            if (installsItself) return UpdateInstall.SelfUpdating;
            return IsUnder(folder, programFiles, '\\', StringComparison.OrdinalIgnoreCase) ? UpdateInstall.Installer : UpdateInstall.Portable;
        }

        /// <param name="isFlatpak">Runs in a Flatpak sandbox (<c>/.flatpak-info</c> is there).</param>
        /// <param name="folder">Where the app runs from.</param>
        public static UpdateInstall OnLinux(bool isRelease, bool isFlatpak, string folder, UpdateApp app)
        {
            if (!isRelease) return UpdateInstall.Development;
            if (isFlatpak) return UpdateInstall.Flatpak;
            return IsUnder(folder, "/opt/" + DebPackage(app), '/', StringComparison.Ordinal) ? UpdateInstall.Deb : UpdateInstall.LinuxFolder;
        }

        /// <summary>The package's name (scripts/build-deb.sh, release.yml).</summary>
        public static string DebPackage(UpdateApp app) => app == UpdateApp.Studio ? "alummahnow-studio" : "alummahnow";

        /// <summary>What a person types to install the downloaded file; null where they open it.</summary>
        public static string? Command(UpdateInstall install, UpdateApp app) => install switch
        {
            UpdateInstall.Deb => "sudo apt install ./" + DebPackage(app) + "_*_amd64.deb",
            UpdateInstall.Flatpak => "flatpak install ./AlUmmahNow-" + (app == UpdateApp.Studio ? "Studio-" : "") + "Linux-*-x86_64.flatpak",
            _ => null,
        };

        /// <summary>What to do with the download page's file, as a key of the texts.</summary>
        public static string HowKey(UpdateInstall install) => install switch
        {
            UpdateInstall.Msix => "updates.how.msix",
            UpdateInstall.Portable => "updates.how.portable",
            UpdateInstall.Deb => "updates.how.deb",
            UpdateInstall.Flatpak => "updates.how.flatpak",
            UpdateInstall.LinuxFolder => "updates.how.folder",
            _ => "updates.how.installer",
        };

        private static bool IsUnder(string folder, string parent, char separator, StringComparison comparison) =>
            parent.Length > 0 && (folder.TrimEnd(separator) + separator).StartsWith(parent.TrimEnd(separator) + separator, comparison);
    }
}
