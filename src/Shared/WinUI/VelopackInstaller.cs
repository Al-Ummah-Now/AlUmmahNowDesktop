#if !AUN_STORE
using System.Security.Cryptography;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Native.SelfUpdate;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace AlUmmahNow.Updates.Views;

/// <summary>
/// Installs what <see cref="SelfUpdater"/> downloaded and checked (wiki AUN-APP-UPDATES §5.2, §5.3). Velopack downloads
/// nothing here: its feed is the checked file alone, and its "download" copies that file. The package folder is cleared at
/// start and before staging, and the staged file is checked again right before the updater gets it. (As in AlUmmahNowTools.)
/// </summary>
internal sealed class VelopackInstaller : IPackageInstaller
{
    private static readonly Logger Logger = Log.For("updates");

    private static readonly InstallGuard? Guard = InstallGuard.ForThisProcess();
    private readonly UpdateManager _manager;
    private readonly string _folder;
    private UpdateInfo? _info;
    private StagedPackage? _package;

    private VelopackInstaller(UpdateManager manager, string folder)
    {
        _manager = manager;
        _folder = folder;
    }

    /// <summary>
    /// Null in a copy that our setup did not install (the MSI, the zip), or whose package folder others can change: those
    /// get the notice with the link.
    /// </summary>
    public static VelopackInstaller? Find()
    {
        try
        {
            var manager = new UpdateManager(new CheckedFile(null, null, null));
            if (!manager.IsInstalled || VelopackLocator.Current.PackagesDir is not { } folder) return null;
            PrivateFolder.Ensure(folder, PrivateFolder.UserRoot);
            // Nothing an earlier run left is ever installed. Not while another copy runs, whose it may be
            if (!IsRunningTwice()) StagedPackage.Clear(folder);
            return new VelopackInstaller(manager, folder);
        }
#pragma warning disable CA1031 // Whatever it is, this copy then only tells of a new version.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Logger.Warn("this copy does not install updates", ex);
            return null;
        }
    }

    public async Task StageAsync(string file, string version, ReleaseFile listed, CancellationToken cancellationToken)
    {
        // The folder may hold what the other one staged: this one waits for the next check
        if (IsRunningTwice()) throw new IOException("the app is open a second time");
        var id = VelopackLocator.Current.AppId ?? throw new IOException("no package id");
        var package = StagedPackage.Prepare(_folder, CheckedFile.NameOf(id, version), listed);
        _package = package;
        var manager = new UpdateManager(new CheckedFile(file, version, listed));
        // Null when it is not newer than this copy: the manifest says it is, the package must too
        var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false) ?? throw new UpdateRefusedException("bad_manifest");
        await manager.DownloadUpdatesAsync(info, cancelToken: cancellationToken).ConfigureAwait(false);
        package.Verify();
        _info = info;
    }

    public void VerifyStaged()
    {
        if (_package is null || _info is null) throw new UpdateRefusedException("hash");
        _package.Verify();
    }

    public void Discard()
    {
        _package?.Discard();
        _package = null;
        _info = null;
    }

    public bool IsOpenElsewhere => IsRunningTwice();

    /// <summary>One that cannot be told is one.</summary>
    private static bool IsRunningTwice() => Guard?.OtherCopyRunning() != false;

    /// <summary>
    /// As the app closes, never earlier: the updater waits 60 seconds for it. If it cannot run, the next start clears the
    /// package and the next check stages it again.
    /// </summary>
    public void ApplyOnExit(bool restart)
    {
#if UPDATE_TEST
        if (Environment.GetEnvironmentVariable("AUN_UPDATE_TEST_NO_UPDATER") == "1") throw new IOException("the updater does not start (test)");
#endif
        if (_info is { } info) _manager.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: !restart, restart, restart ? new[] { AppStart.AfterUpdate } : null);
    }

    /// <summary>Velopack's source: one release, the file that was checked.</summary>
    private sealed class CheckedFile : IUpdateSource
    {
        private readonly string? _path;
        private readonly string? _version;
        private readonly ReleaseFile? _listed;

        public CheckedFile(string? path, string? version, ReleaseFile? listed)
        {
            _path = path;
            _version = version;
            _listed = listed;
        }

        /// <summary>The name Velopack itself gives a full package.</summary>
        public static string NameOf(string id, string version) => $"{id}-{version}-full.nupkg";

        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            if (_path is null || _version is null || _listed is null || appId is null) return Task.FromResult(new VelopackAssetFeed { Assets = Array.Empty<VelopackAsset>() });
            using var file = File.OpenRead(_path);
            var asset = new VelopackAsset
            {
                PackageId = appId,
                Version = SemanticVersion.Parse(_version),
                Type = VelopackAssetType.Full,
                FileName = NameOf(appId, _version),
                SHA1 = Convert.ToHexString(SHA1.HashData(file)),
                // Velopack compares upper-case hex, letter by letter; the manifest's is lower case
                SHA256 = _listed.Sha256.ToUpperInvariant(),
                Size = _listed.Size,
            };
            return Task.FromResult(new VelopackAssetFeed { Assets = new[] { asset } });
        }

        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
        {
            if (_path is null || _version is null || !string.Equals(NameOf(releaseEntry.PackageId, _version), releaseEntry.FileName, StringComparison.Ordinal)) throw new InvalidOperationException("not the checked file");
            File.Copy(_path, localFile, overwrite: true);
            progress(100);
            return Task.CompletedTask;
        }
    }
}
#endif
