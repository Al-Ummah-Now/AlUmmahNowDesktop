using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge.Studio
{
    /// <summary>
    /// Which user-facing features this person sees (owner rule 2026-11-16, "Hidden when off"): api-videos
    /// GET /api/me/features. A feature that is off, unconfigured or outside the creator tier has no tab,
    /// button or text at all. A key that is missing, unknown or not <c>true</c> is off (fail closed).
    /// </summary>
    public sealed class UserFeatures
    {
        public const string Upload = "upload";
        public const string CreateChannel = "createChannel";
        public const string Live = "live";
        public const string CaptionsTranscribe = "captionsTranscribe";
        public const string CaptionsTranslate = "captionsTranslate";
        public const string InlineTranslate = "inlineTranslate";
        public const string ViewerTranslatedTitles = "viewerTranslatedTitles";
        public const string QuranEditions = "quranEditions";
        public const string QuranText = "quranText";
        public const string AudioTracks = "audioTracks";
        public const string SiteDomains = "siteDomains";
        public const string SocialAutopost = "socialAutopost";
        public const string YoutubeImport = "youtubeImport";
        public const string AiCategorySuggest = "aiCategorySuggest";
        public const string BulkSchedule = "bulkSchedule";
        public const string EventCalendar = "eventCalendar";

        /// <summary>Nothing on: before the first answer, signed out, or an unreadable answer.</summary>
        public static readonly UserFeatures None = new UserFeatures(Array.Empty<string>());

        private readonly HashSet<string> _on;

        public UserFeatures(IEnumerable<string> on) => _on = new HashSet<string>(on, StringComparer.Ordinal);

        public bool IsOn(string key) => _on.Contains(key);

        public IReadOnlyCollection<string> On => _on;

        /// <summary>Reads <c>{ "version": 1, "features": { "upload": true, … } }</c>; only literal <c>true</c> counts.</summary>
        public static UserFeatures Parse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return None;
                // Tolerate a { data: … } envelope like the rest of api-videos.
                if (!root.TryGetProperty("features", out var features) && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) data.TryGetProperty("features", out features);
                if (features.ValueKind != JsonValueKind.Object) return None;
                return new UserFeatures(features.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.True).Select(p => p.Name));
            }
            catch (JsonException)
            {
                return None;
            }
        }

        public bool SameAs(UserFeatures other) => _on.SetEquals(other._on);
    }

    /// <summary>GET /api/me/features as the active account.</summary>
    public interface IFeaturesApi
    {
        /// <param name="channelId">Evaluate channel features (upload, live, audio tracks …) against that channel's owner's tier and the caller's role there.</param>
        Task<UserFeatures> MyFeaturesAsync(string? channelId, CancellationToken cancellationToken = default);
    }

    public sealed class FeaturesClient : IFeaturesApi
    {
        public const string Route = "/api/me/features";

        private readonly IApiTransport _transport;

        public FeaturesClient(IApiTransport transport) => _transport = transport;

        public async Task<UserFeatures> MyFeaturesAsync(string? channelId, CancellationToken cancellationToken = default)
        {
            var r = ApiRequest.Path("GET", Route);
            if (!string.IsNullOrEmpty(channelId)) r.Query["channelId"] = channelId!;
            var body = await _transport.SendAsync(r, cancellationToken).ConfigureAwait(false);
            return UserFeatures.Parse(body);
        }
    }
}
