using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace AlUmmahNow.CoreBridge.Native.SelfUpdate;

/// <summary>The running processes and the programs they run, as far as this user may see them.</summary>
public interface IRunningProcesses
{
    IReadOnlyList<(int Pid, string Path)> All();

    /// <summary>The one path a file has in the end: short (8.3) names written out, junctions and links followed. The path itself when the file cannot be asked.</summary>
    string Final(string path);
}

/// <summary>
/// Velopack's updater ends every process that runs from the install folder before it replaces it. So an update is never
/// installed while another copy of the app runs from the same folder: that copy may be uploading, encoding or live. The
/// copies are found by their program's final path. (As in AlUmmahNowTools.)
/// </summary>
public sealed class InstallGuard
{
    private readonly IRunningProcesses _processes;
    private readonly int _pid;
    private readonly string _program;
    private readonly string? _root;
    private readonly StringComparison _same;

    public InstallGuard(IRunningProcesses processes, string program, int pid, bool mac, bool ignoresCase)
    {
        _processes = processes;
        _pid = pid;
        _program = processes.Final(program);
        _root = RootOf(_program, mac);
        _same = ignoresCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    /// <summary>For this process, or null where there is no self-update.</summary>
    public static InstallGuard? ForThisProcess()
    {
        var program = OperatingSystem.IsMacOS() ? SystemProcesses.MacPath(Environment.ProcessId) ?? Environment.ProcessPath : Environment.ProcessPath;
        return program is null ? null : new InstallGuard(new SystemProcesses(), program, Environment.ProcessId, OperatingSystem.IsMacOS(), !OperatingSystem.IsLinux());
    }

    /// <summary>
    /// The folder the updater replaces: the app bundle on macOS (<c>X.app</c> around <c>Contents</c>), the folder around
    /// <c>current</c> on Windows, otherwise the program's own. Null when that would be a whole drive or share: then only
    /// the very same program counts.
    /// </summary>
    public static string? RootOf(string program, bool mac)
    {
        var parts = program.Split('/', '\\');
        if (mac)
        {
            for (var i = parts.Length - 2; i > 0; i--)
            {
                if (parts[i].EndsWith(".app", StringComparison.OrdinalIgnoreCase) && parts[i + 1].Equals("Contents", StringComparison.Ordinal)) return Upto(program, parts, i);
            }
        }
        var folder = parts.Length - 2;
        // Before these there is only the drive ("C:"), the file system ("") or a share (\\server\share)
        var first = program.StartsWith(@"\\", StringComparison.Ordinal) ? 4 : 1;
        if (folder > first && parts[folder].Equals("current", StringComparison.OrdinalIgnoreCase)) folder--;
        return folder < first ? null : Upto(program, parts, folder);
    }

    private static string Upto(string program, string[] parts, int last) => program[..(parts.Take(last + 1).Sum(p => p.Length) + last)];

    /// <summary>Another copy of this app runs from the same install. A listing that fails counts as one: the update can wait.</summary>
    public bool OtherCopyRunning()
    {
        try
        {
            return Ours([FileOf(_program)]).Any();
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>The updater is at work in this install (<c>Update.exe</c>, <c>UpdateMac</c>).</summary>
    public bool UpdaterRunning()
    {
        try
        {
            return Ours(["Update.exe", "UpdateMac"]).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The other processes that run one of these files from this install, by their final paths.</summary>
    private IEnumerable<string> Ours(string[] files)
    {
        bool Named(string path) => files.Any(f => FileOf(path).Equals(f, _same));
        // Only a file of that name, or one whose name may be short for it, is worth asking for its final path
        return _processes.All()
            .Where(p => p.Pid != _pid && (Named(p.Path) || FileOf(p.Path).Contains('~')))
            .Select(p => _processes.Final(p.Path))
            .Where(p => Named(p) && Inside(p));
    }

    /// <summary>
    /// A copy started while the updater works waits here, before it opens a window: the updater ends what runs from the
    /// folder, and no work must be running then.
    /// </summary>
    public bool WaitForUpdater(TimeSpan limit, Action<TimeSpan>? sleep = null)
    {
        sleep ??= Thread.Sleep;
        var step = TimeSpan.FromMilliseconds(500);
        for (var waited = TimeSpan.Zero; UpdaterRunning(); waited += step)
        {
            if (waited >= limit) return false;
            sleep(step);
        }
        return true;
    }

    private bool Inside(string path) => _root is null
        ? path.Equals(_program, _same)
        : path.Length > _root.Length && path.StartsWith(_root, _same) && path[_root.Length] is '/' or '\\';

    private static string FileOf(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
}

/// <summary>What the system lists.</summary>
public sealed class SystemProcesses : IRunningProcesses
{
    public IReadOnlyList<(int Pid, string Path)> All()
    {
        if (OperatingSystem.IsWindows()) return Windows();
        if (OperatingSystem.IsMacOS()) return Mac();
        return Linux();
    }

    public string Final(string path) => (OperatingSystem.IsWindows() ? WindowsFinal(path) : UnixFinal(path)) ?? path;

    private static List<(int, string)> Windows()
    {
        var found = new List<(int, string)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                // Not readable: another person's process, whose install is their own
                if (WindowsPath(process.Id) is { } path) found.Add((process.Id, path));
            }
        }
        return found;
    }

    private static string? WindowsFinal(string path)
    {
        const uint shareAll = 7, openExisting = 3;
        using var file = CreateFileW(path, 0, shareAll, IntPtr.Zero, openExisting, 0, IntPtr.Zero);
        if (file.IsInvalid) return null;
        var buffer = new char[32768];
        var length = (int)GetFinalPathNameByHandleW(file, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) return null;
        var final = new string(buffer, 0, length);
        // \\?\C:\… and \\?\UNC\server\share\… as they are written otherwise
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + final[8..];
        return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
    }

    private static string? UnixFinal(string path)
    {
        var found = realpath(path, IntPtr.Zero);
        if (found == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUTF8(found);
        }
        finally
        {
            free(found);
        }
    }

    private static string? WindowsPath(int pid)
    {
        const uint queryLimited = 0x1000;
        var handle = OpenProcess(queryLimited, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[32768];
            var length = buffer.Length;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref length) ? new string(buffer, 0, length) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static List<(int, string)> Mac()
    {
        var count = proc_listallpids(null, 0);
        var pids = new int[Math.Max(count, 0) + 256];
        count = proc_listallpids(pids, pids.Length * sizeof(int));
        if (count <= 0) throw new InvalidOperationException("The processes could not be listed");
        return pids.Take(count).Select(pid => (Pid: pid, Path: MacPath(pid))).Where(p => p.Path is not null).Select(p => (p.Pid, p.Path!)).ToList();
    }

    internal static string? MacPath(int pid)
    {
        var buffer = new byte[4096];
        var length = proc_pidpath(pid, buffer, (uint)buffer.Length);
        return length > 0 ? Encoding.UTF8.GetString(buffer, 0, length) : null;
    }

    private static List<(int, string)> Linux()
    {
        var found = new List<(int, string)>();
        foreach (var folder in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(folder), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
            try
            {
                if (new FileInfo(Path.Combine(folder, "exe")).LinkTarget is { } path) found.Add((pid, path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Gone, or another person's
            }
        }
        return found;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, [Out] char[] name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    [DllImport("libc")]
    private static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

    [DllImport("libc")]
    private static extern void free(IntPtr memory);

    [DllImport("libproc.dylib")]
    private static extern int proc_listallpids(int[]? buffer, int size);

    [DllImport("libproc.dylib")]
    private static extern int proc_pidpath(int pid, byte[] buffer, uint size);
}
