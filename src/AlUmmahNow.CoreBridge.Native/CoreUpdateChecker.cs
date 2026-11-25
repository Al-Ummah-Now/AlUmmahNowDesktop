using System.Runtime.InteropServices;
using System.Text.Json;
using AlUmmahNow.Core;
using CoreKind = AlUmmahNow.Core.UpdatePromptKind;
using AlUmmahNow.CoreBridge.Logging;

namespace AlUmmahNow.CoreBridge.Native;

/// <summary>
/// <see cref="IUpdateChecker"/> on aun-core: its <c>ApiClient</c> asks <c>GET /api/v1/app-config</c> (no account, no
/// device), its <c>UpdateChecker</c> decides. This build knows the repo's root key (wiki AUN-APP-UPDATES §3.5, §4.1.1), so
/// it tells of signed releases only: an answer about an unsigned one ("notice") is nothing. It reads none of the answer's
/// links and fetches no file.
/// </summary>
public sealed class CoreUpdateChecker : IUpdateChecker
{
    public const string Repo = "AlUmmahNowDesktop";
    public const string Path = "/api/v1/app-config";

    /// <summary>The repo's offline root public key (AlUmmahNowDesktop). Compiled in, never from the network.</summary>
    public static readonly IReadOnlyList<string> RootKeys = new[] { "u6dWtZq34aGYg7i4C3VS2qAHh1s3+HO4y/tVAtCEu6w=" };

    private static readonly Logger Logger = Log.For("updates");

    private readonly Func<CancellationToken, Task<string>> _fetch;
    private readonly UpdateChecker? _core;
    private readonly bool _hasRootKey;

    /// <param name="app">"viewer" or "studio".</param>
    /// <param name="platform">"windows" or "linux".</param>
    /// <param name="state">What <see cref="State"/> gave last time.</param>
    public CoreUpdateChecker(Uri videosApi, string app, string platform, string version, string userAgent, string? state)
        : this(Fetch(videosApi, app, platform, version, userAgent), version, state, RootKeys)
    {
    }

    internal CoreUpdateChecker(Func<CancellationToken, Task<string>> fetch, string version, string? state, IReadOnlyList<string> rootKeys)
    {
        _fetch = fetch;
        _hasRootKey = rootKeys.Count > 0;
        try
        {
            _core = new UpdateChecker(Repo, rootKeys.ToArray(), version, IsBeta(version), state, _hasRootKey ? KeyFile("update-keys.json") : null, _hasRootKey ? KeyFile("update-keys.json.sig") : null);
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

    private static Func<CancellationToken, Task<string>> Fetch(Uri videosApi, string app, string platform, string version, string userAgent)
    {
        ApiClient? client = null;
        return async cancel =>
        {
            client ??= new ApiClient(new ApiClientConfig(videosApi.AbsoluteUri.TrimEnd('/'), userAgent, null, 20_000, 2, AunClient.FromUserAgent(userAgent)), null);
            return (await client.RequestPath(Call(app, platform, version)).WaitAsync(cancel)).Body;
        };
    }

    /// <summary>The question: which app, where, its version, and that it was not installed by a store.</summary>
    internal static PathCall Call(string app, string platform, string version) => new(
        "GET", Path, new(),
        new()
        {
            ["app"] = app,
            ["platform"] = platform,
            ["version"] = version,
            ["channel"] = IsBeta(version) ? "beta" : "stable",
            ["source"] = "managed",
            ["arch"] = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64",
        },
        new(), null, null, false);

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
