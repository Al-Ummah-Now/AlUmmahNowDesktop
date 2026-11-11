using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge.Studio
{
    /// <summary>One call to an api-videos operation, by OpenAPI operation id.</summary>
    public sealed class ApiRequest
    {
        public ApiRequest(string operationId)
        {
            OperationId = operationId;
        }

        public string OperationId { get; }
        public Dictionary<string, string> PathParams { get; } = new Dictionary<string, string>();
        public Dictionary<string, string> Query { get; } = new Dictionary<string, string>();
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>();
        public string? BodyJson { get; set; }

        /// <summary>Makes a POST safe to retry.</summary>
        public string? IdempotencyKey { get; set; }
    }

    /// <summary>
    /// Sends API calls as the active account. The core implementation adds the bearer token,
    /// refreshes once on 401 and retries idempotent calls. Throws <see cref="AuthRequiredException"/>,
    /// <see cref="RateLimitedException"/> or <see cref="StudioApiException"/>.
    /// </summary>
    public interface IApiTransport
    {
        /// <returns>The response body.</returns>
        Task<string> SendAsync(ApiRequest request, CancellationToken cancellationToken = default);

        /// <summary>aun-search suggestions (one attempt, Arabic-aware, grouped order).</summary>
        Task<IReadOnlyList<Suggestion>> SuggestAsync(string text, string? channelId, int limit, CancellationToken cancellationToken = default);
    }
}
