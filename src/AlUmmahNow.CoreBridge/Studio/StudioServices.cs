using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge.Studio
{
    /// <summary>Caption drafts, server transcription and on-device transcripts of one video (AUN-CAPTIONS, E19 19.4).</summary>
    public interface ICaptionsApi
    {
        Task<CaptionDraftList> DraftsAsync(string videoId, string? channelId, CancellationToken cancellationToken = default);

        Task<CaptionDraft> DraftAsync(string videoId, string language, string? channelId, CancellationToken cancellationToken = default);

        /// <summary>Saves over <paramref name="baseVersion"/>; <see cref="StudioApiException.Code"/> <c>draft_conflict</c> when it changed meanwhile.</summary>
        Task<CaptionDraftSummary> SaveDraftAsync(string videoId, string language, int baseVersion, IReadOnlyList<CaptionCue> cues, string? channelId, CancellationToken cancellationToken = default);

        Task DeleteDraftAsync(string videoId, string language, string? channelId, CancellationToken cancellationToken = default);

        /// <summary><c>replaces_upload</c> (409) when an uploaded track exists and <paramref name="replaceUpload"/> is false.</summary>
        Task<CaptionDraftSummary> PublishDraftAsync(string videoId, string language, int baseVersion, bool replaceUpload, string? channelId, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<CaptionJob>> JobsAsync(string videoId, string? channelId, CancellationToken cancellationToken = default);

        Task<CaptionJob> StartJobAsync(string videoId, string language, bool replace, string? channelId, CancellationToken cancellationToken = default);

        Task<CaptionJob> CancelJobAsync(string videoId, string jobId, string? channelId, CancellationToken cancellationToken = default);

        Task<CaptionMinutes> MinutesAsync(string videoId, string? channelId, CancellationToken cancellationToken = default);

        /// <summary>Queues a draft from a transcript made on this computer; returns the segment count. 409 <c>draft_exists</c> / <c>claim_pending</c>.</summary>
        Task<int> SubmitTranscriptAsync(string videoId, string language, OnDeviceTranscript transcript, string? channelId, CancellationToken cancellationToken = default);
    }

    /// <summary>Institution mode (E19 19.9): the account's brand kit and shared assets. Writes are owner-only (403 otherwise).</summary>
    public interface IInstitutionApi
    {
        Task<BrandKitState> BrandKitAsync(string? channelId, CancellationToken cancellationToken = default);

        Task<BrandKit> SaveBrandKitAsync(BrandKit kit, CancellationToken cancellationToken = default);

        Task<StudioAssetLibrary> AssetsAsync(string? channelId, CancellationToken cancellationToken = default);

        /// <summary>The server reads the type from the bytes. 409 <c>library_full</c>; 400 <c>too_large</c>, <c>empty</c>, <c>unsupported_type</c>.</summary>
        Task<StudioAsset> AddAssetAsync(string name, byte[] bytes, CancellationToken cancellationToken = default);

        Task<byte[]> AssetDataAsync(string assetId, string? channelId, CancellationToken cancellationToken = default);

        Task DeleteAssetAsync(string assetId, CancellationToken cancellationToken = default);
    }

    /// <summary>Analytics beyond the overview (E19 19.6).</summary>
    public interface IInsightsApi
    {
        Task<AnalyticsSummary> SummaryAsync(string? channelId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<DailyMetric>> DailyAsync(string? channelId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

        Task<VideoStats> VideoAsync(string videoId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

        Task<RetentionCurve> RetentionAsync(string videoId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
    }

    /// <summary>Renditions encoded on this computer (D11). <c>too_late</c> comes back as a 409 <see cref="StudioApiException"/>.</summary>
    public interface IRenditionsApi
    {
        Task<RenditionStaging> CreateAsync(string videoId, IReadOnlyList<string> rungs, IReadOnlyList<StagedFile> files, string encoder, string? channelId, CancellationToken cancellationToken = default);

        Task PutFileAsync(string videoId, string stagingId, StagedFile file, byte[] bytes, string? channelId, CancellationToken cancellationToken = default);

        /// <summary>409 <c>files_missing</c> with <see cref="StudioApiException.Missing"/> lists files to send again.</summary>
        Task CompleteAsync(string videoId, string stagingId, string? channelId, CancellationToken cancellationToken = default);

        Task CancelAsync(string videoId, string stagingId, string? channelId, CancellationToken cancellationToken = default);
    }

    /// <summary>The NAT-S2 Studio routes over <see cref="StudioHttpClient"/>.</summary>
    public sealed class StudioServicesClient : ICaptionsApi, IInstitutionApi, IInsightsApi, IRenditionsApi
    {
        private readonly StudioHttpClient _http;

        public StudioServicesClient(StudioHttpClient http) => _http = http;

        private static string Seg(string v) => StudioHttpClient.Segment(v);

        private static string Captions(string videoId) => "/api/videos/" + Seg(videoId) + "/captions";

        private async Task<T> Json<T>(HttpMethod method, string path, string? channelId, string? body, Func<JsonElement, T> read, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default)
        {
            using var doc = await _http.SendJsonAsync(method, path, channelId, body, query, ct).ConfigureAwait(false);
            return read(doc.RootElement);
        }

        // Captions

        public Task<CaptionDraftList> DraftsAsync(string videoId, string? channelId, CancellationToken cancellationToken = default) =>
            Json(HttpMethod.Get, Captions(videoId) + "/drafts", channelId, null, r => CaptionJson.DraftList(J.Data(r)), null, cancellationToken);

        public Task<CaptionDraft> DraftAsync(string videoId, string language, string? channelId, CancellationToken cancellationToken = default) =>
            Json(HttpMethod.Get, Captions(videoId) + "/drafts/" + Seg(language), channelId, null, r => CaptionJson.Draft(J.Data(r)), null, cancellationToken);

        public Task<CaptionDraftSummary> SaveDraftAsync(string videoId, string language, int baseVersion, IReadOnlyList<CaptionCue> cues, string? channelId, CancellationToken cancellationToken = default) =>
            Json(HttpMethod.Put, Captions(videoId) + "/drafts/" + Seg(language), channelId, CaptionJson.SaveBody(baseVersion, cues), r => CaptionJson.Summary(J.Data(r)), null, cancellationToken);

        public async Task DeleteDraftAsync(string videoId, string language, string? channelId, CancellationToken cancellationToken = default) =>
            (await _http.SendJsonAsync(HttpMethod.Delete, Captions(videoId) + "/drafts/" + Seg(language), channelId, null, null, cancellationToken).ConfigureAwait(false)).Dispose();

        public Task<CaptionDraftSummary> PublishDraftAsync(string videoId, string language, int baseVersion, bool replaceUpload, string? channelId, CancellationToken cancellationToken = default)
        {
            var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["baseVersion"] = baseVersion, ["replaceUpload"] = replaceUpload });
            return Json(HttpMethod.Post, Captions(videoId) + "/drafts/" + Seg(language) + "/publish", channelId, body,
                r => J.Data(r).TryGetProperty("draft", out var d) ? CaptionJson.Summary(d) : CaptionJson.Summary(J.Data(r)), null, cancellationToken);
        }

        public Task<IReadOnlyList<CaptionJob>> JobsAsync(string videoId, string? channelId, CancellationToken cancellationToken = default) =>
            Json<IReadOnlyList<CaptionJob>>(HttpMethod.Get, Captions(videoId) + "/jobs", channelId, null, r => J.Array(J.Data(r), "jobs").Select(CaptionJson.Job).ToList(), null, cancellationToken);

        public Task<CaptionJob> StartJobAsync(string videoId, string language, bool replace, string? channelId, CancellationToken cancellationToken = default)
        {
            var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["language"] = language, ["replace"] = replace });
            return Json(HttpMethod.Post, Captions(videoId) + "/jobs", channelId, body, r => OneJob(r), null, cancellationToken);
        }

        public Task<CaptionJob> CancelJobAsync(string videoId, string jobId, string? channelId, CancellationToken cancellationToken = default) =>
            Json(HttpMethod.Post, Captions(videoId) + "/jobs/" + Seg(jobId) + "/cancel", channelId, null, r => OneJob(r), null, cancellationToken);

        private static CaptionJob OneJob(JsonElement r) =>
            J.Data(r).TryGetProperty("job", out var j) ? CaptionJson.Job(j) : throw new StudioApiException("The caption job was not returned");

        public Task<CaptionMinutes> MinutesAsync(string videoId, string? channelId, CancellationToken cancellationToken = default) =>
            Json(HttpMethod.Get, Captions(videoId) + "/minutes", channelId, null, r => CaptionJson.Minutes(J.Data(r)), null, cancellationToken);

        public Task<int> SubmitTranscriptAsync(string videoId, string language, OnDeviceTranscript transcript, string? channelId, CancellationToken cancellationToken = default) =>
            Json(HttpMethod.Post, Captions(videoId) + "/drafts/" + Seg(language) + "/transcript", channelId, CaptionJson.TranscriptBody(transcript),
                r => (int)J.Num(J.Data(r), "segments"), null, cancellationToken);

        // Institution

        public Task<BrandKitState> BrandKitAsync(string? channelId, CancellationToken cancellationToken = default) =>
            Json(HttpMethod.Get, "/api/studio/brand-kit", channelId, null, r =>
            {
                var d = J.Data(r);
                var kit = d.TryGetProperty("kit", out var k) ? BrandKit.Read(k) : new BrandKit();
                return new BrandKitState(kit, J.Bool(d, "saved"), J.Bool(d, "canEdit"));
            }, null, cancellationToken);

        public Task<BrandKit> SaveBrandKitAsync(BrandKit kit, CancellationToken cancellationToken = default) =>
            Json(HttpMethod.Put, "/api/studio/brand-kit", null, kit.ToJson(), r => J.Data(r).TryGetProperty("kit", out var k) ? BrandKit.Read(k) : kit, null, cancellationToken);

        public Task<StudioAssetLibrary> AssetsAsync(string? channelId, CancellationToken cancellationToken = default) =>
            Json(HttpMethod.Get, "/api/studio/assets", channelId, null, r => StudioAssetLibrary.Read(J.Data(r)), null, cancellationToken);

        public async Task<StudioAsset> AddAssetAsync(string name, byte[] bytes, CancellationToken cancellationToken = default)
        {
            var trimmed = (name ?? "").Trim();
            if (trimmed.Length > StudioAssetLibrary.MaxNameChars) trimmed = trimmed.Substring(0, StudioAssetLibrary.MaxNameChars);
            var query = new Dictionary<string, string> { ["name"] = trimmed };
            using var doc = await _http.SendBytesAsync(HttpMethod.Post, "/api/studio/assets", bytes, null, query, cancellationToken).ConfigureAwait(false);
            return StudioAsset.Read(J.Data(doc.RootElement));
        }

        public Task<byte[]> AssetDataAsync(string assetId, string? channelId, CancellationToken cancellationToken = default) =>
            _http.GetBytesAsync("/api/studio/assets/" + Seg(assetId), channelId, cancellationToken);

        public async Task DeleteAssetAsync(string assetId, CancellationToken cancellationToken = default) =>
            (await _http.SendJsonAsync(HttpMethod.Delete, "/api/studio/assets/" + Seg(assetId), null, null, null, cancellationToken).ConfigureAwait(false)).Dispose();

        // Insights (raw objects, no envelope)

        private static Dictionary<string, string> Range(DateTimeOffset from, DateTimeOffset to, string? channelId = null)
        {
            var q = new Dictionary<string, string> { ["dateFrom"] = Iso(from), ["dateTo"] = Iso(to) };
            if (!string.IsNullOrEmpty(channelId)) q["channelId"] = channelId!;
            return q;
        }

        private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        public Task<AnalyticsSummary> SummaryAsync(string? channelId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            Json(HttpMethod.Get, "/api/analytics/summary", null, null, r => AnalyticsSummary.Read(J.Data(r)), Range(from, to, channelId), cancellationToken);

        public Task<IReadOnlyList<DailyMetric>> DailyAsync(string? channelId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            Json<IReadOnlyList<DailyMetric>>(HttpMethod.Get, "/api/analytics/metrics", null, null, r =>
            {
                var arr = r.ValueKind == JsonValueKind.Array ? r : J.Data(r);
                return arr.ValueKind == JsonValueKind.Array
                    ? arr.EnumerateArray().Select(DailyMetric.Read).Where(d => d != null).Select(d => d!).OrderBy(d => d.Day).ToList()
                    : new List<DailyMetric>();
            }, Range(from, to, channelId), cancellationToken);

        public Task<VideoStats> VideoAsync(string videoId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            Json(HttpMethod.Get, "/api/analytics/videos/" + Seg(videoId), null, null, r =>
            {
                var d = J.Data(r);
                return VideoStats.Read(d.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Object ? res : d);
            }, Range(from, to), cancellationToken);

        public Task<RetentionCurve> RetentionAsync(string videoId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            Json(HttpMethod.Get, "/api/analytics/videos/" + Seg(videoId) + "/retention", null, null, r => RetentionCurve.Read(J.Data(r)), Range(from, to), cancellationToken);

        // Renditions

        private static string Renditions(string videoId) => "/api/videos/" + Seg(videoId) + "/renditions";

        public Task<RenditionStaging> CreateAsync(string videoId, IReadOnlyList<string> rungs, IReadOnlyList<StagedFile> files, string encoder, string? channelId, CancellationToken cancellationToken = default)
        {
            var body = CaptionJson.Write(w =>
            {
                w.WriteStartObject();
                w.WriteStartArray("rungs");
                foreach (var r in rungs) w.WriteStringValue(r);
                w.WriteEndArray();
                w.WriteStartArray("files");
                foreach (var f in files)
                {
                    w.WriteStartObject();
                    w.WriteString("path", f.Path);
                    w.WriteNumber("bytes", f.Bytes);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteString("encoder", encoder);
                w.WriteEndObject();
            });
            return Json(HttpMethod.Post, Renditions(videoId), channelId, body, r =>
            {
                var d = J.Data(r);
                var id = J.Str(d, "stagingId") ?? throw new StudioApiException("No staging id was returned");
                return new RenditionStaging(id, J.Str(d, "status") ?? "uploading", J.NumOrNull(d, "maxFileBytes") is double m ? (long)m : (long?)null);
            }, null, cancellationToken);
        }

        public async Task PutFileAsync(string videoId, string stagingId, StagedFile file, byte[] bytes, string? channelId, CancellationToken cancellationToken = default)
        {
            var parts = file.Parts ?? throw new ArgumentException("Not a <rung>/<file> path: " + file.Path, nameof(file));
            if (bytes.LongLength != file.Bytes) throw new ArgumentException(file.Path + " is " + bytes.LongLength + " bytes, declared " + file.Bytes, nameof(bytes));
            var path = Renditions(videoId) + "/" + Seg(stagingId) + "/files/" + Seg(parts.Rung) + "/" + Seg(parts.File);
            (await _http.SendBytesAsync(HttpMethod.Put, path, bytes, channelId, null, cancellationToken).ConfigureAwait(false)).Dispose();
        }

        public async Task CompleteAsync(string videoId, string stagingId, string? channelId, CancellationToken cancellationToken = default) =>
            (await _http.SendJsonAsync(HttpMethod.Post, Renditions(videoId) + "/" + Seg(stagingId) + "/complete", channelId, null, null, cancellationToken).ConfigureAwait(false)).Dispose();

        public async Task CancelAsync(string videoId, string stagingId, string? channelId, CancellationToken cancellationToken = default) =>
            (await _http.SendJsonAsync(HttpMethod.Delete, Renditions(videoId) + "/" + Seg(stagingId), channelId, null, null, cancellationToken).ConfigureAwait(false)).Dispose();
    }
}
