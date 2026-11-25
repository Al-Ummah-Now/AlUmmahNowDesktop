using System.Net;
using System.Security.Cryptography;
using System.Text;
using AlUmmahNow.CoreBridge.Logging;

namespace AlUmmahNow.CoreBridge.Native.SelfUpdate;

/// <summary>What installs a package (Velopack on Windows); it downloads nothing and decides nothing.</summary>
public interface IPackageInstaller
{
    /// <summary>Takes the checked file; it is installed by the two applying calls only.</summary>
    Task StageAsync(string file, string version, ReleaseFile listed, CancellationToken cancellationToken);

    /// <exception cref="UpdateRefusedException">What is staged is not the file the signed manifest lists.</exception>
    void VerifyStaged();

    void Discard();

    /// <summary>Another process runs from the same folder.</summary>
    bool IsOpenElsewhere { get; }

    void ApplyOnExit();
    void ApplyAndRestart();
}

/// <summary>The core's part: what is trusted, under the keys it has at that moment.</summary>
public interface IReleaseVerifier
{
    /// <exception cref="UpdateRefusedException">The manifest was refused.</exception>
    CheckedRelease VerifyManifest(byte[] manifest, string signature, string tag);

    /// <summary>One address of a download, also each one it is sent on to.</summary>
    bool IsAllowed(string url);
}

public sealed record CheckedRelease(string Version, string Tag, string NotesEn, string NotesAr, IReadOnlyList<CheckedArtifact> Artifacts);

public sealed record CheckedArtifact(string App, string Platform, string? Arch, string Kind, ReleaseFile File);

/// <summary>
/// Downloads, verifies and stages an update (wiki AUN-APP-UPDATES §5.3): the manifest and its signature from the release the
/// answer names, the package the signed manifest lists for this app. Every address, also of a redirect, is the core's to
/// allow. Anything that fails installs nothing.
/// </summary>
public sealed class SelfUpdater : ISelfUpdate
{
    public const int MaxRedirects = 5;
    private const long MaxManifest = 256 * 1024;
    private const long MaxSignature = 4096;

    private static readonly Logger Logger = Log.For("updates");

    private readonly IReleaseVerifier _verifier;
    private readonly IPackageInstaller _installer;
    private readonly HttpMessageInvoker _http;
    private readonly Func<string, string, CancellationToken, Task> _report;
    private readonly string _folder;
    private readonly string _app;
    private readonly string _platform;
    private readonly string _arch;
    private readonly string _root;
    private Proof? _staged;

    /// <summary>What proves the staged update; checked again before it is installed.</summary>
    private sealed record Proof(string Tag, byte[] Manifest, string Signature);

    /// <param name="handler">Follows no redirect by itself.</param>
    /// <param name="report">Tells the server of a refusal (tag, reason).</param>
    /// <param name="folder">Where the package is downloaded to, below the user's own root.</param>
    /// <param name="root">The user's own root; another one in tests.</param>
    public SelfUpdater(IReleaseVerifier verifier, IPackageInstaller installer, HttpMessageHandler handler, Func<string, string, CancellationToken, Task> report,
        string folder, string app, string platform, string arch, string? root = null)
    {
        if (handler is SocketsHttpHandler { AllowAutoRedirect: true } or HttpClientHandler { AllowAutoRedirect: true })
            throw new ArgumentException("the update handler must not follow redirects", nameof(handler));
        _verifier = verifier;
        _installer = installer;
        _http = new HttpMessageInvoker(handler);
        _report = report;
        _folder = folder;
        _app = app;
        _platform = platform;
        _arch = arch;
        _root = root ?? PrivateFolder.UserRoot;
    }

    /// <summary>HTTPS through the OS stack; redirects are left to <see cref="SelfUpdater"/>.</summary>
    public static HttpMessageHandler CreateHandler() =>
#if UPDATE_TEST
        new UpdateTestHandler();
#else
        new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(20) };
#endif

    /// <summary>Where a release's manifest is. From the tag alone: no address is taken from an answer.</summary>
    internal static string ManifestUrl(string tag) => $"https://github.com/Al-Ummah-Now/{CoreUpdateChecker.Repo}/releases/download/{Uri.EscapeDataString(tag)}/app-release.json";

    public async Task<VerifiedUpdate> VerifyAsync(string tag, string version, CancellationToken cancellationToken = default)
    {
        try
        {
            return Told((await FetchReleaseAsync(tag, version, cancellationToken).ConfigureAwait(false)).Release);
        }
        catch (Exception ex) when (IsTrouble(ex, cancellationToken))
        {
            throw new UpdateCheckFailedException("the release could not be read", ex);
        }
    }

    public async Task<VerifiedUpdate> StageAsync(string tag, string version, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        Discard();
        var path = Path.Combine(_folder, $"{_app}-{_arch}.nupkg");
        try
        {
            var (release, proof) = await FetchReleaseAsync(tag, version, cancellationToken).ConfigureAwait(false);
            var artifact = release.Artifacts.FirstOrDefault(a => a.App == _app && a.Platform == _platform && a.Kind == "velopack-full" && (a.Arch is null || a.Arch == _arch))
                ?? throw new UpdateRefusedException("bad_manifest");
            PrivateFolder.Ensure(_folder, _root);
            await DownloadAsync(artifact.File, path, progress, cancellationToken).ConfigureAwait(false);
            try
            {
                await _installer.StageAsync(path, release.Version, artifact.File, cancellationToken).ConfigureAwait(false);
                _installer.VerifyStaged();
            }
            catch (Exception ex) when (ex is not (UpdateRefusedException or OperationCanceledException))
            {
                // It could not take a checked package (the disk, permissions): no refusal
                throw new IOException("the installer could not take the update", ex);
            }

            _staged = proof;
            return Told(release);
        }
        catch (UpdateRefusedException)
        {
            Discard();
            throw;
        }
        catch (Exception ex) when (IsTrouble(ex, cancellationToken))
        {
            Discard();
            throw new UpdateCheckFailedException("the update could not be downloaded", ex);
        }
        finally
        {
            Remove(path);
        }
    }

    public void VerifyStaged()
    {
        if (_staged is not { } staged) throw new UpdateRefusedException("hash");
        try
        {
            _verifier.VerifyManifest(staged.Manifest, staged.Signature, staged.Tag);
            _installer.VerifyStaged();
        }
        catch (UpdateRefusedException)
        {
            Discard();
            throw;
        }
    }

    public void Discard()
    {
        _staged = null;
        _installer.Discard();
    }

    public bool IsOpenElsewhere => _installer.IsOpenElsewhere;

    public void ApplyOnExit()
    {
        if (_staged != null) _installer.ApplyOnExit();
    }

    public void ApplyAndRestart()
    {
        if (_staged != null) _installer.ApplyAndRestart();
    }

    public async Task ReportAsync(string tag, string reason, CancellationToken cancellationToken = default)
    {
        Logger.Warn($"the update {tag} was refused: {reason}");
        try
        {
            await _report(tag, reason, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort: the next check tries again, and reports again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Logger.Warn("the refusal could not be reported", ex);
        }
    }

    private static VerifiedUpdate Told(CheckedRelease r) => new(r.Version, r.Tag, r.NotesEn, r.NotesAr);

    private static bool IsTrouble(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or IOException or UnauthorizedAccessException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    private async Task<(CheckedRelease Release, Proof Proof)> FetchReleaseAsync(string tag, string version, CancellationToken cancellationToken)
    {
        var url = ManifestUrl(tag);
        var manifest = await FetchAsync(url, MaxManifest, cancellationToken).ConfigureAwait(false);
        var signature = Encoding.UTF8.GetString(await FetchAsync(url + ".sig", MaxSignature, cancellationToken).ConfigureAwait(false));
        var release = _verifier.VerifyManifest(manifest, signature, tag);
        // The signed manifest names the version, not the answer
        if (release.Version != version) throw new UpdateRefusedException("wrong_tag");
        return (release, new Proof(tag, manifest, signature));
    }

    private async Task<byte[]> FetchAsync(string url, long max, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await StreamAsync(url, stream, max, null, cancellationToken).ConfigureAwait(false);
        return stream.ToArray();
    }

    /// <summary>GET with redirects followed by hand: every address must be allowed.</summary>
    private async Task StreamAsync(string url, Stream into, long max, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromMinutes(30));
        var current = url;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!_verifier.IsAllowed(current)) throw new UpdateRefusedException("host");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await _http.SendAsync(request, limit.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
            {
                current = (next.IsAbsoluteUri ? next : new Uri(new Uri(current), next)).ToString();
                continue;
            }

            if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("HTTP " + (int)response.StatusCode);
            var total = response.Content.Headers.ContentLength;
            if (total > max) throw new UpdateRefusedException("hash");
            await using var body = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await body.ReadAsync(buffer, limit.Token).ConfigureAwait(false)) > 0)
            {
                read += n;
                if (read > max) throw new UpdateRefusedException("hash");
                await into.WriteAsync(buffer.AsMemory(0, n), limit.Token).ConfigureAwait(false);
                if (max > 0) progress?.Report((double)read / max);
            }

            return;
        }

        throw new UpdateRefusedException("host");
    }

    private async Task DownloadAsync(ReleaseFile listed, string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Remove(path);
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await StreamAsync(listed.Url, file, listed.Size, progress, cancellationToken).ConfigureAwait(false);
        }

        byte[] hash;
        await using (var check = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Not all of it is no other file: the connection ended
            if (check.Length < listed.Size) throw new IOException("the download ended early");
            hash = await SHA256.HashDataAsync(check, cancellationToken).ConfigureAwait(false);
        }

        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(listed.Sha256))) throw new UpdateRefusedException("hash");
    }

    private static void Remove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not there, or left behind: the next download writes a new one or fails
        }
    }
}
