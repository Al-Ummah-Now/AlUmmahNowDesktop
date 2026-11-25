using System.Diagnostics;
using AlUmmahNow.CoreBridge.Native.SelfUpdate;
using Xunit;

namespace AlUmmahNow.CoreBridge.Native.Tests;

/// <summary>An update waits while another copy runs from the same install.</summary>
public sealed class InstallGuardTests
{
    private sealed class Listed(params (int, string)[] all) : IRunningProcesses
    {
        public readonly List<(int Pid, string Path)> Running = all.Select(p => (p.Item1, p.Item2)).ToList();
        /// <summary>Short names, junctions and links, and where they lead</summary>
        public readonly Dictionary<string, string> Links = new(StringComparer.OrdinalIgnoreCase);
        public Exception? Fails;
        public IReadOnlyList<(int Pid, string Path)> All() => Fails is null ? Running : throw Fails;
        public string Final(string path)
        {
            foreach (var (from, to) in Links)
            {
                if (path.StartsWith(from, StringComparison.OrdinalIgnoreCase)) return to + path[from.Length..];
            }
            return path;
        }
    }

    private const string Windows = @"C:\Users\a\AppData\Local\AlUmmahNow.Studio\current\Tools.exe";
    private const string Mac = "/Applications/Al Ummah Now Studio.app/Contents/MacOS/Tools";

    private static InstallGuard Guard(IRunningProcesses listed, string program) => new(listed, program, 1, program == Mac, true);

    private static bool Other(string program, params (int, string)[] running) => Guard(new Listed(running), program).OtherCopyRunning();

    [Theory]
    [InlineData(@"C:\Users\a\AppData\Local\AlUmmahNow.Studio\current\Tools.exe", false, @"C:\Users\a\AppData\Local\AlUmmahNow.Studio")]
    [InlineData(@"C:\Tools\Tools.exe", false, @"C:\Tools")]
    [InlineData(@"C:\current\Tools.exe", false, @"C:\current")]
    [InlineData("/Applications/Al Ummah Now Studio.app/Contents/MacOS/Tools", true, "/Applications/Al Ummah Now Studio.app")]
    [InlineData("/usr/lib/tools/Tools", false, "/usr/lib/tools")]
    // Never a whole drive, file system or share
    [InlineData(@"C:\Tools.exe", false, null)]
    [InlineData("/Tools", false, null)]
    [InlineData("/Tools", true, null)]
    [InlineData(@"\\nas\share\Tools.exe", false, null)]
    [InlineData(@"\\nas\share\current\Tools.exe", false, @"\\nas\share\current")]
    [InlineData(@"\\nas\share\x\current\Tools.exe", false, @"\\nas\share\x")]
    // A bundle only on macOS, and only around Contents
    [InlineData(@"C:\dev.app\Tools\current\Tools.exe", false, @"C:\dev.app\Tools")]
    [InlineData(@"C:\dev.app\Contents\Tools\Tools.exe", false, @"C:\dev.app\Contents\Tools")]
    [InlineData("/Users/a/dev.app/Tools/Tools", true, "/Users/a/dev.app/Tools")]
    [InlineData("/Users/a/dev.app/x/Tools.app/Contents/MacOS/Tools", true, "/Users/a/dev.app/x/Tools.app")]
    public void The_folder_the_updater_replaces(string program, bool mac, string? root) => Assert.Equal(root, InstallGuard.RootOf(program, mac));

    [Fact]
    public void Only_another_copy_of_the_app_from_the_same_install_counts()
    {
        Assert.False(Other(Windows));
        Assert.False(Other(Windows, (1, Windows)));
        Assert.True(Other(Windows, (1, Windows), (2, Windows.ToUpperInvariant())));
        Assert.True(Other(Mac, (1, Mac), (2, Mac)));
        // The updater, the folder scanner the app started, another install, and a folder whose name only begins the same
        Assert.False(Other(Windows, (2, @"C:\Users\a\AppData\Local\AlUmmahNow.Studio\Update.exe"), (3, @"C:\Users\a\AppData\Local\AlUmmahNow.Studio\current\FileScanService.exe"),
            (4, @"C:\Program Files\Al Ummah Now Studio\Tools.exe"), (5, @"C:\Users\a\AppData\Local\AlUmmahNow.StudioOld\current\Tools.exe")));
        Assert.False(Other(Mac, (2, "/Applications/Al Ummah Now Studio.app/Contents/MacOS/UpdateMac"), (3, "/Users/a/Al Ummah Now Studio.app/Contents/MacOS/Tools")));
    }

    [Fact]
    public void A_program_on_a_drive_itself_is_only_compared_with_itself()
    {
        Assert.False(Other(@"C:\Tools.exe", (2, @"C:\elsewhere\Tools.exe")));
        Assert.True(Other(@"C:\Tools.exe", (2, @"c:\tools.EXE")));
    }

    [Theory]
    [InlineData(@"C:\Users\a\AppData\Local\ALUMMA~1.STU\current\Tools.exe", @"C:\Users\a\AppData\Local\ALUMMA~1.STU", @"C:\Users\a\AppData\Local\AlUmmahNow.Studio")]
    [InlineData(@"C:\Users\a\AppData\Local\AlUmmahNow.Studio\current\TOOLS~1.EXE", @"C:\Users\a\AppData\Local\AlUmmahNow.Studio\current\TOOLS~1.EXE", Windows)]
    [InlineData(@"D:\link\current\Tools.exe", @"D:\link", @"C:\Users\a\AppData\Local\AlUmmahNow.Studio")]
    public void A_copy_started_by_a_short_name_or_through_a_junction_is_found(string started, string from, string to)
    {
        var listed = new Listed((1, Windows), (2, started)) { Links = { [from] = to } };
        Assert.True(Guard(listed, Windows).OtherCopyRunning());
        // And the other way round: this copy was started so, the other one plainly
        var other = new Listed((1, started), (2, Windows)) { Links = { [from] = to } };
        Assert.True(Guard(other, started).OtherCopyRunning());
        Assert.False(Guard(new Listed((1, started)) { Links = { [from] = to } }, started).OtherCopyRunning());
    }

    [Fact]
    public void A_listing_that_fails_makes_the_update_wait()
    {
        Assert.True(Guard(new Listed { Fails = new InvalidOperationException("x") }, Windows).OtherCopyRunning());
    }

    [Fact]
    public void A_start_during_an_update_waits_for_the_updater()
    {
        foreach (var (program, updater) in new[] { (Windows, @"C:\Users\a\AppData\Local\AlUmmahNow.Studio\Update.exe"), (Mac, "/Applications/Al Ummah Now Studio.app/Contents/MacOS/UpdateMac") })
        {
            var listed = new Listed((1, program), (9, updater));
            var guard = Guard(listed, program);
            Assert.True(guard.UpdaterRunning());
            var slept = 0;
            Assert.True(guard.WaitForUpdater(TimeSpan.FromSeconds(60), _ => { if (++slept == 3) listed.Running.RemoveAt(1); }));
            Assert.Equal(3, slept);
            Assert.True(guard.WaitForUpdater(TimeSpan.FromSeconds(60), _ => Assert.Fail("nothing to wait for")));
        }
        Assert.False(Guard(new Listed((9, @"C:\Users\a\AppData\Local\AlUmmahNow.Studio\Update.exe")), Windows).WaitForUpdater(TimeSpan.FromSeconds(5), _ => { }));
        // Another app's updater is none of ours
        Assert.False(Guard(new Listed((9, @"C:\Users\a\AppData\Local\Other\Update.exe")), Windows).UpdaterRunning());
    }

    /// <summary>A second run of one program, as a second copy of the app would be; also through a junction or a linked folder, and by its short name where Windows keeps them.</summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("link")]
    [InlineData("short")]
    public async Task The_system_lists_a_running_program_by_its_final_path(string way)
    {
        // Ping from a folder of its own on Windows; elsewhere one nothing else here runs, where it is (macOS ends a copied system
        // program, and some Linux ones are one program by many names)
        var windows = OperatingSystem.IsWindows();
        var folder = "/usr/bin";
        var name = "awk";
        if (windows)
        {
            folder = Path.Combine(Folder(), "a long folder name");
            name = "aun second copy.exe";
            Directory.CreateDirectory(folder);
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), Path.Combine(folder, name));
        }
        var program = Path.Combine(folder, name);
        var other = program;
        switch (way)
        {
            case "link":
                var link = Path.Combine(Folder(), "link");
                if (windows)
                {
                    using var make = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{folder}\"") { UseShellExecute = false, RedirectStandardOutput = true })!;
                    await make.WaitForExitAsync();
                    Assert.Equal(0, make.ExitCode);
                }
                else Directory.CreateSymbolicLink(link, folder);
                other = Path.Combine(link, name);
                break;
            case "short":
                if (!windows) return;
                using (var ask = Process.Start(new ProcessStartInfo("cmd.exe", $"/c for %I in (\"{program}\") do @echo %~sI") { UseShellExecute = false, RedirectStandardOutput = true })!)
                {
                    other = (await ask.StandardOutput.ReadToEndAsync()).Trim();
                }
                // This volume keeps no short names
                if (!other.Contains('~')) return;
                break;
        }
        Process Start(string path)
        {
            var start = new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardInput = true };
            foreach (var a in windows ? new[] { "-n", "60", "127.0.0.1" } : new[] { "BEGIN { getline }" }) start.ArgumentList.Add(a);
            return Process.Start(start)!;
        }
        using var first = Start(program);
        using var second = Start(other);
        try
        {
            var system = new SystemProcesses();
            string? path = null;
            await WaitUntil(() => (path = system.All().FirstOrDefault(p => p.Pid == first.Id).Path) is not null && path.Contains(windows ? "aun second copy" : "awk", StringComparison.OrdinalIgnoreCase), 20_000);
            var guard = new InstallGuard(system, path!, first.Id, OperatingSystem.IsMacOS(), !OperatingSystem.IsLinux());
            await WaitUntil(guard.OtherCopyRunning, 20_000);
            // And seen from the other one
            await WaitUntil(new InstallGuard(system, other, second.Id, OperatingSystem.IsMacOS(), !OperatingSystem.IsLinux()).OtherCopyRunning, 20_000);
            second.Kill();
            await second.WaitForExitAsync();
            await WaitUntil(() => !guard.OtherCopyRunning(), 20_000);
        }
        finally
        {
            first.Kill();
            if (!second.HasExited) second.Kill();
        }
    }

    [Fact]
    public void This_process_has_a_guard()
    {
        var guard = InstallGuard.ForThisProcess();
        Assert.NotNull(guard);
        Assert.False(guard.UpdaterRunning());
    }

    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "aun-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static async Task WaitUntil(Func<bool> met, int milliseconds)
    {
        for (var waited = 0; waited < milliseconds && !met(); waited += 50) await Task.Delay(50);
        Assert.True(met());
    }
}
