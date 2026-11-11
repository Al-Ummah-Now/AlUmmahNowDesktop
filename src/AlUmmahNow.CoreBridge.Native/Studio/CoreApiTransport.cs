using AlUmmahNow.Core;
using AlUmmahNow.CoreBridge.Studio;
using CoreKind = AlUmmahNow.Core.SuggestionKind;
using Kind = AlUmmahNow.CoreBridge.Studio.SuggestionKind;

namespace AlUmmahNow.CoreBridge.Native.Studio;

/// <summary>
/// <see cref="IApiTransport"/> on aun-core's ApiClient, bound to the active account: a fresh token
/// on every call, one refresh on 401, retries for idempotent calls.
/// </summary>
public sealed class CoreApiTransport : IApiTransport
{
    private readonly CoreStudioAccounts _accounts;
    private readonly ApiClientConfig _config;
    private readonly object _gate = new();
    private (string UserId, ApiClient Client)? _client;

    public CoreApiTransport(CoreStudioAccounts accounts, Uri videosApi, string userAgent, string? locale)
    {
        _accounts = accounts;
        _config = new ApiClientConfig(videosApi.AbsoluteUri.TrimEnd('/'), userAgent, locale, 30_000, 3);
    }

    public async Task<string> SendAsync(ApiRequest request, CancellationToken cancellationToken = default)
    {
        var client = Client();
        var call = new ApiCall(request.OperationId, request.PathParams, request.Query, request.Headers, request.BodyJson, request.IdempotencyKey);
        try
        {
            var response = await client.Request(call).WaitAsync(cancellationToken).ConfigureAwait(false);
            return response.Body;
        }
        catch (AunException ex)
        {
            throw Map(ex, request.OperationId);
        }
    }

    public async Task<IReadOnlyList<Suggestion>> SuggestAsync(string text, string? channelId, int limit, CancellationToken cancellationToken = default)
    {
        var client = Client();
        try
        {
            // The core drops the HTTP request when its task is dropped; WaitAsync stops us waiting.
            var found = await client
                .SearchSuggestions(new SuggestionQuery(text, (uint)limit, new[] { CoreKind.Video, CoreKind.Query }, channelId))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return found.Select(s => new Suggestion(s.Text, ToKind(s.Kind), s.Id, s.Subtitle)).ToList();
        }
        catch (AunException ex)
        {
            throw Map(ex, "search suggestions");
        }
    }

    private ApiClient Client()
    {
        var active = _accounts.Active ?? throw new AuthRequiredException("Not signed in");
        lock (_gate)
        {
            if (_client is { } c && c.UserId == active.UserId) return c.Client;
            try
            {
                var created = _accounts.Manager.ApiClient(_config, active.UserId);
                _client = (active.UserId, created);
                return created;
            }
            catch (AunException.Unauthorized ex)
            {
                throw new AuthRequiredException("Not signed in: " + ex.@detail);
            }
        }
    }

    internal static Exception Map(AunException ex, string what) => ex switch
    {
        AunException.Unauthorized u => new AuthRequiredException(what + ": " + u.@detail),
        AunException.RateLimited r => new RateLimitedException(TimeSpan.FromMilliseconds(r.@retryAfterMs ?? 5000)),
        AunException.Http h => new StudioApiException(what + " failed: HTTP " + h.@status + " " + h.@detail, h.@status, ex),
        AunException.Forbidden f => new StudioApiException(what + " is not allowed: " + f.@detail, 403, ex),
        AunException.NotFound n => new StudioApiException(what + " not found: " + n.@detail, 404, ex),
        AunException.Conflict c => new StudioApiException(what + " conflict: " + c.@detail, 409, ex),
        AunException.InvalidInput i => new StudioApiException(what + " rejected: " + i.@detail, 400, ex),
        AunException.Network or AunException.Offline => new StudioApiException(what + ": network unavailable", null, ex),
        _ => new StudioApiException(what + " failed: " + ex.GetType().Name, null, ex),
    };

    private static Kind ToKind(CoreKind k) => k switch
    {
        CoreKind.Video => Kind.Video,
        CoreKind.Channel => Kind.Channel,
        CoreKind.Playlist => Kind.Playlist,
        CoreKind.Category => Kind.Category,
        _ => Kind.Query,
    };
}
