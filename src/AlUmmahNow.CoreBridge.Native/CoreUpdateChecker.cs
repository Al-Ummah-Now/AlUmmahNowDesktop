using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AlUmmahNow.Core;
using CoreKind = AlUmmahNow.Core.UpdatePromptKind;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Native.SelfUpdate;

namespace AlUmmahNow.CoreBridge.Native;

/// <summary>
/// <see cref="IUpdateChecker"/> on aun-core: its <c>ApiClient</c> asks <c>GET /api/v1/app-config</c> (no account, no
/// device), its <c>UpdateChecker</c> decides. This build knows the repo's root key (wiki AUN-APP-UPDATES §3.5, §4.1.1), so
/// it tells of signed releases only: an answer about an unsigned one ("notice") is nothing. It reads none of the answer's
/// links. <see cref="SelfUpdater"/> asks it what may be installed, under the keys it trusts at that moment.
/// </summary>
public sealed class CoreUpdateChecker : IUpdateChecker, IReleaseVerifier
{
    public const string Repo = "AlUmmahNowDesktop";
    public const string Path = "/api/v1/app-config";

    /// <summary>The repo's offline root public key (AlUmmahNowDesktop). Compiled in, never from the network.</summary>
#if UPDATE_TEST
    // CI's update test alone (scripts/update-test.mjs): keys made for the run. scripts/no-test-hooks.mjs refuses a release with it.
    public static readonly IReadOnlyList<string> RootKeys = new[] { TestHook("AUN_UPDATE_TEST_ROOT") };

    internal static string TestHook(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name);
#else
    public static readonly IReadOnlyList<string> RootKeys = new[] { "u6dWtZq34aGYg7i4C3VS2qAHh1s3+HO4y/tVAtCEu6w=" };
#endif

    private static readonly Logger Logger = Log.For("updates");

    /// <summary>This repo's release tags (release.yml).</summary>
    private static readonly Regex TagShape = new(@"\Av[0-9]{1,6}\.[0-9]{1,6}\.[0-9]{1,6}(-[0-9A-Za-z.]{1,24})?\z", RegexOptions.CultureInvariant);

    private readonly Func<CancellationToken, Task<string>> _fetch;
    private readonly UpdateChecker? _core;
    private readonly bool _hasRootKey;
    private readonly Func<string, string, CancellationToken, Task>? _report;

    /// <param name="app">"viewer" or "studio".</param>
    /// <param name="platform">"windows" or "linux".</param>
    /// <param name="state">What <see cref="State"/> gave last time.</param>
    /// <param name="installs">This copy installs updates in place: it asks for those.</param>
    public CoreUpdateChecker(Uri videosApi, string app, string platform, string version, string userAgent, string? state, bool installs = false)
        : this(new Api(videosApi, app, platform, version, userAgent, installs), version, state)
    {
    }

    private CoreUpdateChecker(Api api, string version, string? state)
        : this(api.FetchAsync, version, state, RootKeys) => _report = api.ReportAsync;

    internal CoreUpdateChecker(Func<CancellationToken, Task<string>> fetch, string version, string? state, IReadOnlyList<string> rootKeys)
    {
        _fetch = fetch;
        _hasRootKey = rootKeys.Count > 0;
        try
        {
            _core = new UpdateChecker(Repo, rootKeys.ToArray(), version, IsBeta(version), state, KeyList("update-keys.json"), KeyList("update-keys.json.sig"));
        }
        catch (AunException ex)
        {
            // A version that is none of ours, or a key list that is not the root key's: every check comes to nothing
            Logger.Warn("no update checks for version " + version, ex);
        }
        catch (DllNotFoundException ex)
        {
            Logger.Warn("no update checks without the core", ex);
        }
    }

    /// <summary>The root-signed key list as of this build, byte for byte.</summary>
    internal static string KeyFile(string name)
    {
        using var stream = typeof(CoreUpdateChecker).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("missing resource " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>A pre-release is told of pre-releases too.</summary>
    internal static bool IsBeta(string version) => version.Contains('-');

    private string? KeyList(string name)
    {
#if UPDATE_TEST
        // The test's own root key has signed no list yet: the answer brings one
        return null;
#else
        return _hasRootKey ? KeyFile(name) : null;
#endif
    }

    /// <summary>The server, through the core's client: no account, no device.</summary>
    private sealed class Api
    {
        private readonly string _app, _platform, _version;
        private readonly bool _installs;
        private readonly Lazy<ApiClient> _client;

        public Api(Uri videosApi, string app, string platform, string version, string userAgent, bool installs)
        {
            (_app, _platform, _version, _installs) = (app, platform, version, installs);
#if UPDATE_TEST
            var server = TestHook("AUN_UPDATE_TEST_SERVER");
#else
            var server = videosApi.AbsoluteUri.TrimEnd('/');
#endif
            _client = new(() => new ApiClient(new ApiClientConfig(server, userAgent, null, 20_000, 2, AunClient.FromUserAgent(userAgent)), null));
        }

        public async Task<string> FetchAsync(CancellationToken cancellationToken) =>
            (await _client.Value.RequestPath(Call(_app, _platform, _version, _installs)).WaitAsync(cancellationToken)).Body;

        public async Task ReportAsync(string tag, string reason, CancellationToken cancellationToken) =>
            await _client.Value.RequestPath(FailureCall(_app, _platform, _version, tag, reason)).WaitAsync(cancellationToken);
    }

    /// <summary>The question: which app, where, its version, and whether it installs updates itself.</summary>
    internal static PathCall Call(string app, string platform, string version, bool installs = false) => new(
        "GET", Path, new(),
        new()
        {
            ["app"] = app,
            ["platform"] = platform,
            ["version"] = version,
            ["channel"] = IsBeta(version) ? "beta" : "stable",
            ["source"] = installs ? "direct" : "managed",
            ["arch"] = Arch,
        },
        new(), null, null, false);

    /// <summary>What is told of a refusal: the app, its version, the release and why. No account, no device.</summary>
    internal static PathCall FailureCall(string app, string platform, string version, string tag, string reason) => new(
        "POST", Path + "/update-failure", new(), new(), new(),
        JsonSerializer.Serialize(new { app, platform, version, repo = Repo, tag, reason }), null, false);

    public static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>Tells the server that a release was refused.</summary>
    public Task ReportAsync(string tag, string reason, CancellationToken cancellationToken) => _report?.Invoke(tag, reason, cancellationToken) ?? Task.CompletedTask;

    public CheckedRelease VerifyManifest(byte[] manifest, string signature, string tag)
    {
        if (_core == null) throw new UpdateRefusedException("bad_manifest");
        var check = _core.VerifyManifest(manifest, signature, tag);
        if (check.Refused != null || check.Release is not { } r) throw new UpdateRefusedException(check.Refused ?? "bad_manifest");
        return new CheckedRelease(r.Version, r.Tag, r.NotesEn, r.NotesAr,
            r.Artifacts.Select(a => new CheckedArtifact(a.App, a.Platform, a.Arch, a.Kind, new ReleaseFile(a.Url, a.Sha256, (long)a.Size))).ToList());
    }

    public bool IsAllowed(string url) => _core?.AllowedDownloadUrl(url) == true;

    public async Task<UpdateAnswer> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_core == null) throw new UpdateCheckFailedException("no core, or no version");
        try
        {
            return Read(await _fetch(cancellationToken));
        }
        catch (Exception ex) when (ex is AunException or JsonException or InvalidOperationException or FormatException)
        {
            throw new UpdateCheckFailedException(ex.GetType().Name, ex);
        }
    }

    private UpdateAnswer Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        // An answer without the block is no answer: it must not read as "switched off"
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("update", out var update) || update.ValueKind != JsonValueKind.Object)
            throw new JsonException("no update block");
        var prompt = _core!.ApplyAnswer(json, null);
        var kind = Kind(prompt.Kind, prompt.Action, update.TryGetProperty("signed", out var signed) && signed.ValueKind == JsonValueKind.True);
        var answer = new UpdateAnswer
        {
            Enabled = update.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True,
            Kind = kind,
            CheckHours = (int)prompt.CheckHours,
        };
        // Nothing to update to, or nothing that may be told of: no word of it goes further (the core names it all the same)
        if (kind is not (UpdatePromptKind.Notice or UpdatePromptKind.Forced)) return answer;
        answer.Version = prompt.Version;
        answer.Tag = update.TryGetProperty("tag", out var tag) && tag.ValueKind == JsonValueKind.String && TagShape.IsMatch(tag.GetString()!) ? tag.GetString() : null;
        answer.CanInstall = prompt.Action == "install" && answer.Tag != null && _hasRootKey;
        answer.InstallsByDefault = !(document.RootElement.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object
            && config.TryGetProperty("updates.autoInstallDefault", out var byDefault) && byDefault.ValueKind == JsonValueKind.False);
        answer.MinimumVersion = update.TryGetProperty("minimumVersion", out var minimum) && minimum.ValueKind == JsonValueKind.String ? minimum.GetString() : null;
        answer.NotesEn = prompt.NotesEn;
        answer.NotesAr = prompt.NotesAr;
        answer.MessageEn = prompt.MessageEn;
        answer.MessageAr = prompt.MessageAr;
        return answer;
    }

    /// <summary>
    /// With a root key only a signed release counts; without one "notice", "install" and "download" are the same notice. What
    /// must not be told of is never the screen: at most the bar that stays.
    /// </summary>
    private UpdatePromptKind Kind(CoreKind kind, string action, bool isSigned)
    {
        var canTell = action is "install" or "download" ? isSigned || !_hasRootKey : action == "notice" && !_hasRootKey;
        return kind switch
        {
            CoreKind.RequiredBar => UpdatePromptKind.RequiredBar,
            CoreKind.Forced => canTell ? UpdatePromptKind.Forced : UpdatePromptKind.RequiredBar,
            CoreKind.Notice => canTell ? UpdatePromptKind.Notice : UpdatePromptKind.None,
            _ => UpdatePromptKind.None,
        };
    }

    public UpdatePromptKind LastKnown() =>
        _core?.OfflinePromptKind() == CoreKind.RequiredBar ? UpdatePromptKind.RequiredBar : UpdatePromptKind.None;

    public string State() => _core?.StateJson() ?? "";
}
