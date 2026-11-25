using System.Net;
using System.Security.Cryptography;
using AlUmmahNow.CoreBridge.Native;
using AlUmmahNow.CoreBridge.Native.SelfUpdate;
using Xunit;

namespace AlUmmahNow.CoreBridge.Native.Tests;

/// <summary>What is downloaded, from where, and what is refused (wiki AUN-APP-UPDATES §5.3).</summary>
public sealed class SelfUpdaterTests : IDisposable
{
    private const string Base = "https://github.com/Al-Ummah-Now/AlUmmahNowDesktop/releases/download/v1.5.0/";
    private const string Asset = "https://release-assets.githubusercontent.com/x/update.nupkg?sig=1";

    private sealed class Http : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpResponseMessage>> Answers = new();
        public readonly List<string> Calls = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Calls.Add(url);
            return Task.FromResult(Answers.TryGetValue(url, out var a) ? a() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class Verifier : IReleaseVerifier
    {
        public string? Refuses;
        public CheckedRelease Release = null!;
        public readonly List<(byte[] Manifest, string Signature, string Tag)> Seen = new();

        public CheckedRelease VerifyManifest(byte[] manifest, string signature, string tag)
        {
            Seen.Add((manifest, signature, tag));
            return Refuses == null ? Release : throw new UpdateRefusedException(Refuses);
        }

        // The core's own rule, over its real code in CoreUpdateCheckerTests
        public bool IsAllowed(string url) => url.StartsWith(Base[..Base.IndexOf("/v1", StringComparison.Ordinal)], StringComparison.Ordinal) || url.StartsWith("https://release-assets.githubusercontent.com/", StringComparison.Ordinal);
    }

    private sealed class Installer : IPackageInstaller
    {
        public byte[]? Staged;
        public string? Refuses;
        public Exception? Breaks;
        public int Exits, Restarts, Discards;

        public Task StageAsync(string file, string version, ReleaseFile listed, CancellationToken cancellationToken)
        {
            if (Breaks != null) throw Breaks;
            Staged = File.ReadAllBytes(file);
            return Task.CompletedTask;
        }

        public void VerifyStaged()
        {
            if (Refuses != null) throw new UpdateRefusedException(Refuses);
        }

        public void Discard()
        {
            Discards++;
            Staged = null;
        }

        public bool IsOpenElsewhere => false;
        public void ApplyOnExit() => Exits++;
        public void ApplyAndRestart() => Restarts++;
    }

    private readonly Http _http = new();
    private readonly Verifier _verifier = new();
    private readonly Installer _installer = new();
    private readonly List<string> _reports = new();
    private readonly byte[] _package = RandomNumberGenerator.GetBytes(300_000);
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aun-test-" + Guid.NewGuid().ToString("N"));
    private readonly SelfUpdater _updater;

    public SelfUpdaterTests()
    {
        Directory.CreateDirectory(_root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Lists(_package);
        _http.Answers[Base + "app-release.json"] = () => Ok("manifest"u8.ToArray());
        _http.Answers[Base + "app-release.json.sig"] = () => Ok("signature"u8.ToArray());
        _http.Answers[Base + "update.nupkg"] = () => Moved(Asset);
        _http.Answers[Asset] = () => Ok(_package);
        _updater = new SelfUpdater(_verifier, _installer, _http, (tag, reason, _) =>
        {
            _reports.Add(tag + " " + reason);
            return Task.CompletedTask;
        }, Path.Combine(_root, "updates"), "studio", "windows", "x64", _root);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private void Lists(byte[] package, string app = "studio", string platform = "windows", string? arch = "x64", string kind = "velopack-full", long? size = null) =>
        _verifier.Release = new CheckedRelease("1.5.0", "v1.5.0", "new", "جديد", new[]
        {
            new CheckedArtifact("viewer", "windows", "x64", "velopack-full", new ReleaseFile(Base + "viewer.nupkg", new string('0', 64), 1)),
            new CheckedArtifact(app, platform, arch, kind, new ReleaseFile(Base + "update.nupkg", Convert.ToHexStringLower(SHA256.HashData(package)), size ?? package.Length)),
        });

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage Moved(string to)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return r;
    }

    private Task<VerifiedUpdate> StageAsync() => _updater.StageAsync("v1.5.0", "1.5.0", null);

    private async Task Refused(string reason)
    {
        Assert.Equal(reason, (await Assert.ThrowsAsync<UpdateRefusedException>(StageAsync)).Reason);
        AssertNothing();
    }

    private void AssertNothing()
    {
        Assert.Null(_installer.Staged);
        Assert.False(Directory.Exists(Path.Combine(_root, "updates")) && Directory.EnumerateFiles(Path.Combine(_root, "updates")).Any());
        _updater.ApplyOnExit();
        _updater.ApplyAndRestart();
        Assert.Equal(0, _installer.Exits + _installer.Restarts);
    }

    [Fact]
    public async Task The_package_of_this_app_is_downloaded_checked_and_staged()
    {
        var told = new List<double>();
        var update = await _updater.StageAsync("v1.5.0", "1.5.0", new Told(told.Add));
        Assert.Equal(("1.5.0", "v1.5.0", "new", "جديد"), (update.Version, update.Tag, update.NotesEn, update.NotesAr));
        Assert.Equal(_package, _installer.Staged);
        Assert.Equal(1, told[^1]);
        Assert.Equal(new[] { Base + "app-release.json", Base + "app-release.json.sig", Base + "update.nupkg", Asset }, _http.Calls);
        Assert.Equal("signature", _verifier.Seen[0].Signature);
        Assert.Equal("v1.5.0", _verifier.Seen[0].Tag);
        // Nothing is kept but what the installer took, and nothing is installed yet
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "updates")));
        Assert.Equal(0, _installer.Exits + _installer.Restarts);
        _updater.VerifyStaged();
        Assert.Equal(_verifier.Seen[0].Manifest, _verifier.Seen[1].Manifest);
        _updater.ApplyOnExit();
        Assert.Equal(1, _installer.Exits);
    }

    private sealed class Told(Action<double> told) : IProgress<double>
    {
        public void Report(double value) => told(value);
    }

    [Theory]
    [InlineData("bad_signature")]
    [InlineData("unknown_key")]
    [InlineData("wrong_repo")]
    [InlineData("not_newer")]
    [InlineData("below_floor")]
    [InlineData("yanked")]
    public async Task What_the_core_refuses_is_not_downloaded(string reason)
    {
        _verifier.Refuses = reason;
        await Refused(reason);
        Assert.DoesNotContain(Base + "update.nupkg", _http.Calls);
        Assert.Equal(reason, (await Assert.ThrowsAsync<UpdateRefusedException>(() => _updater.VerifyAsync("v1.5.0", "1.5.0"))).Reason);
    }

    [Fact]
    public async Task The_manifest_names_the_version_not_the_answer() =>
        Assert.Equal("wrong_tag", (await Assert.ThrowsAsync<UpdateRefusedException>(() => _updater.StageAsync("v1.5.0", "1.6.0", null))).Reason);

    [Fact]
    public async Task A_changed_package_is_refused()
    {
        var other = (byte[])_package.Clone();
        other[1000] ^= 1;
        _http.Answers[Asset] = () => Ok(other);
        await Refused("hash");
    }

    [Fact]
    public async Task A_longer_package_is_refused()
    {
        _http.Answers[Asset] = () => Ok(_package.Append((byte)0).ToArray());
        await Refused("hash");
        // Also when its length is not told
        _http.Answers[Asset] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Unsized(_package.Append((byte)0).ToArray())) };
        await Refused("hash");
    }

    private sealed class Unsized(byte[] body) : MemoryStream(body)
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public async Task A_package_that_ends_early_is_trouble_and_no_refusal()
    {
        _http.Answers[Asset] = () => Ok(_package[..1000]);
        await Assert.ThrowsAsync<UpdateCheckFailedException>(StageAsync);
        AssertNothing();
    }

    [Theory]
    [InlineData("https://downloads.example.com/update.nupkg")]
    [InlineData("http://release-assets.githubusercontent.com/x")]
    [InlineData("https://github.com/evil/AlUmmahNowDesktop/releases/download/v1.5.0/update.nupkg")]
    public async Task A_redirect_elsewhere_is_refused_and_not_followed(string to)
    {
        _http.Answers[Base + "update.nupkg"] = () => Moved(to);
        await Refused("host");
        Assert.DoesNotContain(to, _http.Calls);
    }

    [Fact]
    public async Task Redirects_that_do_not_end_are_refused()
    {
        _http.Answers[Asset] = () => Moved(Asset);
        await Refused("host");
        Assert.Equal(SelfUpdater.MaxRedirects + 1, _http.Calls.Count(c => c != Base + "app-release.json" && c != Base + "app-release.json.sig"));
    }

    [Theory]
    [InlineData("viewer", "windows", "x64", "velopack-full")]
    [InlineData("studio", "linux", "x64", "velopack-full")]
    [InlineData("studio", "windows", "arm64", "velopack-full")]
    [InlineData("studio", "windows", "x64", "msi")]
    public async Task Only_this_apps_own_package_is_taken(string app, string platform, string arch, string kind)
    {
        Lists(_package, app, platform, arch, kind);
        await Refused("bad_manifest");
    }

    [Theory]
    [InlineData("app-release.json")]
    [InlineData("app-release.json.sig")]
    [InlineData("update.nupkg")]
    public async Task What_cannot_be_reached_is_trouble(string file)
    {
        _http.Answers.Remove(Base + file);
        await Assert.ThrowsAsync<UpdateCheckFailedException>(StageAsync);
        AssertNothing();
    }

    [Fact]
    public async Task A_manifest_that_is_too_large_is_refused()
    {
        _http.Answers[Base + "app-release.json"] = () => Ok(new byte[256 * 1024 + 1]);
        await Refused("hash");
        Assert.Empty(_verifier.Seen);
    }

    [Fact]
    public async Task What_is_staged_is_checked_again_manifest_and_file()
    {
        await StageAsync();
        _verifier.Refuses = "yanked";
        Assert.Equal("yanked", Assert.Throws<UpdateRefusedException>(_updater.VerifyStaged).Reason);
        AssertNothing();

        _verifier.Refuses = null;
        await StageAsync();
        _installer.Refuses = "hash";
        Assert.Equal("hash", Assert.Throws<UpdateRefusedException>(_updater.VerifyStaged).Reason);
        AssertNothing();
        Assert.Throws<UpdateRefusedException>(_updater.VerifyStaged);
    }

    [Fact]
    public async Task An_installer_that_cannot_take_it_is_trouble()
    {
        _installer.Breaks = new InvalidOperationException("no updater");
        await Assert.ThrowsAsync<UpdateCheckFailedException>(StageAsync);
        AssertNothing();
        _installer.Breaks = null;
        _installer.Refuses = "hash";
        await Refused("hash");
    }

    [Fact]
    public async Task A_folder_others_can_write_is_not_used()
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(_root, (UnixFileMode)511);
        await Assert.ThrowsAsync<UpdateCheckFailedException>(StageAsync);
        Assert.Null(_installer.Staged);
    }

    [Fact]
    public async Task A_report_never_throws()
    {
        await _updater.ReportAsync("v1.5.0", "hash");
        Assert.Equal(new[] { "v1.5.0 hash" }, _reports);
        await new SelfUpdater(_verifier, _installer, _http, (_, _, _) => throw new InvalidOperationException(), _root, "studio", "windows", "x64", _root).ReportAsync("v1.5.0", "hash");
    }

    [Fact]
    public void A_handler_that_follows_redirects_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new SelfUpdater(_verifier, _installer, new SocketsHttpHandler(), (_, _, _) => Task.CompletedTask, _root, "studio", "windows", "x64"));
        using var ours = SelfUpdater.CreateHandler();
        Assert.False(((SocketsHttpHandler)ours).AllowAutoRedirect);
    }

    [Fact]
    public void A_staged_file_that_was_replaced_linked_or_removed_is_refused()
    {
        var folder = Path.Combine(_root, "packages");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "planted-full.nupkg"), "x");
        File.WriteAllText(Path.Combine(folder, "half.partial"), "x");
        var listed = new ReleaseFile(Base, Convert.ToHexStringLower(SHA256.HashData(_package)), _package.Length);
        var staged = StagedPackage.Prepare(folder, "p-full.nupkg", listed, _root);
        Assert.Empty(Directory.EnumerateFiles(folder));
        void Is(bool good)
        {
            if (good) staged.Verify();
            else Assert.Equal("hash", Assert.Throws<UpdateRefusedException>(staged.Verify).Reason);
        }

        Is(false);
        File.WriteAllBytes(staged.Path, _package);
        Is(true);
        var other = (byte[])_package.Clone();
        other[^1] ^= 1;
        File.WriteAllBytes(staged.Path, other);
        Is(false);
        File.Delete(staged.Path);
        var elsewhere = Path.Combine(_root, "elsewhere");
        File.WriteAllBytes(elsewhere, _package);
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(staged.Path, elsewhere);
            Is(false);
        }

        staged.Discard();
        Assert.False(File.Exists(staged.Path));
    }
}
