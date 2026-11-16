using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge.Studio
{
    /// <summary>
    /// The name a person is shown by (owner rule 2026-11-12): display name, else first + last name, else the email.
    /// Never an @handle: handles belong to channels. The server resolves it (api-videos GET /api/profile
    /// <c>resolvedDisplayName</c>, in the request language); the raw fields are a fallback for an older answer.
    /// </summary>
    public static class AccountName
    {
        /// <summary>The name from a profile, or null when the profile has none (the caller then shows the email).</summary>
        public static string? Resolve(string? resolvedDisplayName, string? displayName, string? firstName, string? lastName)
        {
            if (Clean(resolvedDisplayName) is { } resolved) return resolved;
            if (Clean(displayName) is { } display) return display;
            var first = Clean(firstName);
            var last = Clean(lastName);
            if (first != null && last != null) return first + " " + last;
            return first ?? last;
        }

        /// <summary>The account's label: <paramref name="name"/>, else the email, else the user id. Never an @handle.</summary>
        public static string Label(string? name, string? email, string userId) =>
            Clean(name) ?? Clean(email) ?? userId;

        /// <summary>Reads <c>{ success, result: { profile: { resolvedDisplayName, displayName, firstName, lastName } } }</c>.</summary>
        public static string? FromProfileJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var p = doc.RootElement;
                foreach (var key in new[] { "result", "data" })
                {
                    if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var inner) && inner.ValueKind == JsonValueKind.Object) p = inner;
                }

                if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object) p = profile;
                if (p.ValueKind != JsonValueKind.Object) return null;
                // userName is deliberately not read: it is a handle, not a name.
                return Resolve(Str(p, "resolvedDisplayName"), Str(p, "displayName"), Str(p, "firstName"), Str(p, "lastName"));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        /// <summary>Blank and @handle-shaped values count as missing.</summary>
        private static string? Clean(string? value)
        {
            var v = value?.Trim();
            return string.IsNullOrEmpty(v) || v!.StartsWith("@", System.StringComparison.Ordinal) ? null : v;
        }
    }

    /// <summary>The signed-in person's own name, from api-videos GET /api/profile.</summary>
    public interface IProfileApi
    {
        /// <returns>The resolved name, or null when the profile has no name.</returns>
        Task<string?> MyNameAsync(string? language, CancellationToken cancellationToken = default);
    }

    public sealed class ProfileClient : IProfileApi
    {
        public const string Route = "/api/profile";

        private readonly IApiTransport _transport;

        public ProfileClient(IApiTransport transport) => _transport = transport;

        public async Task<string?> MyNameAsync(string? language, CancellationToken cancellationToken = default)
        {
            var r = ApiRequest.Path("GET", Route);
            if (!string.IsNullOrEmpty(language)) r.Query["lang"] = language!;
            return AccountName.FromProfileJson(await _transport.SendAsync(r, cancellationToken).ConfigureAwait(false));
        }
    }
}
