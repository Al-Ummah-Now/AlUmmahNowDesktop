using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AlUmmahNow.CoreBridge.Native.SelfUpdate;

/// <summary>
/// A folder only this user can change, and nobody else can swap for another: no part of its path inside the user's own
/// root is a link, belongs to someone else or can be written by others. An update is staged nowhere else, because the
/// updater reads the file again after our last check. (As in AlUmmahNowTools.)
/// </summary>
public static class PrivateFolder
{
    /// <summary>The user's own root on this system: the home folder, on Windows the local app data folder.</summary>
    public static string UserRoot => OperatingSystem.IsWindows()
        ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Creates <paramref name="folder"/> if needed, closes it to others, and checks it and everything above it up to <paramref name="root"/>.</summary>
    /// <exception cref="IOException">The folder isn't private.</exception>
    public static void Ensure(string folder, string root)
    {
        folder = Path.GetFullPath(folder);
        root = Path.GetFullPath(root);
        var parts = PartsUpTo(folder, root);
        // Top down: each part is checked before anything is made inside it
        parts.Reverse();
        foreach (var part in parts)
        {
            if (!Directory.Exists(part))
            {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(part);
                else Directory.CreateDirectory(part, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var leaf = part == folder;
            if (new DirectoryInfo(part).LinkTarget is not null || (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException(part + " is a link");
            if (OperatingSystem.IsWindows()) CheckWindows(part, leaf);
            else CheckUnix(part, leaf);
        }
    }

    private static List<string> PartsUpTo(string folder, string root)
    {
        var parts = new List<string>();
        for (var p = folder; p is not null; p = Path.GetDirectoryName(p))
        {
            parts.Add(p);
            if (string.Equals(p, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return parts;
        }

        throw new IOException($"{folder} is not inside {root}");
    }

    [UnsupportedOSPlatform("windows")]
    private static void CheckUnix(string path, bool leaf)
    {
        try
        {
            var mode = leaf ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : File.GetUnixFileMode(path);
            // Only its owner may set a folder's mode, so this also proves the folder is ours
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new IOException(path + " belongs to someone else", ex);
        }

        if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) throw new IOException(path + " can be written by others");
    }

    [SupportedOSPlatform("windows")]
    private const FileSystemRights Changes = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    [SupportedOSPlatform("windows")]
    private static void CheckWindows(string path, bool leaf)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("no user");
        var trusted = new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) };
        var info = new DirectoryInfo(path);
        if (leaf)
        {
            var own = new DirectorySecurity();
            own.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in trusted)
                own.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            try
            {
                info.SetAccessControl(own);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new IOException(path + " belongs to someone else", ex);
            }
        }

        var security = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner)) throw new IOException(path + " belongs to someone else");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            // A rule that only passes to what is inside (CREATOR OWNER) says nothing about this folder
            if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            if ((rule.FileSystemRights & Changes) != 0 && !trusted.Contains((SecurityIdentifier)rule.IdentityReference)) throw new IOException(path + " can be written by others");
        }
    }
}
