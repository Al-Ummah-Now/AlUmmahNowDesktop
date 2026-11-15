using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AlUmmahNow.CoreBridge.Studio
{
    /// <summary>Where a caption draft came from (<c>origin</c> in the drafts API).</summary>
    public enum CaptionOrigin { Unknown, ReviewTranscript, Transcription, Translation, OnDevice }

    public enum CaptionTrack { None, Machine, Upload }

    /// <summary>A draft in the list (<c>GET /api/videos/:id/captions/drafts</c>) or the head of one draft.</summary>
    public sealed class CaptionDraftSummary
    {
        public string Language { get; set; } = "";
        public int Version { get; set; } = 1;
        public CaptionOrigin Origin { get; set; }
        public bool Edited { get; set; }
        public int CueCount { get; set; }

        /// <summary>Quran suggestions the creator has not accepted yet.</summary>
        public int PendingQuran { get; set; }

        public DateTimeOffset? UpdatedAt { get; set; }
        public int? PublishedVersion { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
        public CaptionTrack Track { get; set; }

        public bool IsPublished => PublishedVersion != null;

        /// <summary>Published, then edited since.</summary>
        public bool IsOutdated => PublishedVersion != null && PublishedVersion != Version;
    }

    public enum CaptionClaimStatus { Queued, Failed }

    /// <summary>A review-transcript or on-device transcript still being turned into a draft, or one that failed.</summary>
    public sealed class CaptionClaim
    {
        public CaptionClaim(string language, CaptionClaimStatus status, string? error = null)
        {
            Language = language;
            Status = status;
            Error = error;
        }

        public string Language { get; }
        public CaptionClaimStatus Status { get; }
        public string? Error { get; }
    }

    public sealed class CaptionDraftList
    {
        public IReadOnlyList<CaptionDraftSummary> Drafts { get; set; } = Array.Empty<CaptionDraftSummary>();
        public IReadOnlyList<CaptionClaim> Claims { get; set; } = Array.Empty<CaptionClaim>();
        public bool TranscriptAvailable { get; set; }
    }

    /// <summary>
    /// One piece of a cue: speech text, or a Quran reference. Studio never edits a Quran reference, so it keeps
    /// the server's JSON for it exactly as sent.
    /// </summary>
    public sealed class CaptionSegment : IEquatable<CaptionSegment>
    {
        private CaptionSegment(string? speech, string? quranJson)
        {
            Speech = speech;
            QuranJson = quranJson;
        }

        public static CaptionSegment Text(string text) => new CaptionSegment(text ?? "", null);

        public static CaptionSegment Quran(string json) => new CaptionSegment(null, json);

        /// <summary>Set for speech segments.</summary>
        public string? Speech { get; }

        /// <summary>Set for Quran segments: the segment object as the server sent it.</summary>
        public string? QuranJson { get; }

        public bool IsQuran => QuranJson != null;

        /// <summary>The verse range of a Quran segment, e.g. "2:255".</summary>
        public string? QuranRange
        {
            get
            {
                if (QuranJson == null) return null;
                using var doc = JsonDocument.Parse(QuranJson);
                return doc.RootElement.TryGetProperty("range", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            }
        }

        public bool Equals(CaptionSegment? other) => other != null && Speech == other.Speech && QuranJson == other.QuranJson;

        public override bool Equals(object? obj) => Equals(obj as CaptionSegment);

        public override int GetHashCode() => (Speech ?? QuranJson ?? "").GetHashCode();
    }

    /// <summary>One caption line with its time on the video.</summary>
    public sealed class CaptionCue : IEquatable<CaptionCue>
    {
        public CaptionCue(string id, int startMs, int endMs, IReadOnlyList<CaptionSegment> segments)
        {
            Id = id;
            StartMs = startMs;
            EndMs = endMs;
            Segments = segments;
        }

        public string Id { get; }
        public int StartMs { get; }
        public int EndMs { get; }
        public IReadOnlyList<CaptionSegment> Segments { get; }

        /// <summary>The cue's speech, for search and accessible names. Quran references are not text.</summary>
        public string SpeechText => string.Join(" ", Segments.Where(s => s.Speech != null).Select(s => s.Speech));

        public bool HasQuran => Segments.Any(s => s.IsQuran);

        public CaptionCue With(int? startMs = null, int? endMs = null, IReadOnlyList<CaptionSegment>? segments = null, string? id = null) =>
            new CaptionCue(id ?? Id, startMs ?? StartMs, endMs ?? EndMs, segments ?? Segments);

        public bool Equals(CaptionCue? other) =>
            other != null && Id == other.Id && StartMs == other.StartMs && EndMs == other.EndMs && Segments.SequenceEqual(other.Segments);

        public override bool Equals(object? obj) => Equals(obj as CaptionCue);

        public override int GetHashCode() => (Id, StartMs, EndMs).GetHashCode();
    }

    /// <summary>One draft with its cues (<c>GET …/captions/drafts/:language</c>).</summary>
    public sealed class CaptionDraft
    {
        public CaptionDraft(CaptionDraftSummary summary, IReadOnlyList<CaptionCue> cues)
        {
            Summary = summary;
            Cues = cues;
        }

        public CaptionDraftSummary Summary { get; }
        public IReadOnlyList<CaptionCue> Cues { get; }
    }

    public enum CaptionJobStatus { Queued, WaitingBudget, Extracting, Transcribing, Postprocessing, Ready, Failed, Cancelled }

    /// <summary>A server transcription (<c>POST|GET …/captions/jobs</c>).</summary>
    public sealed class CaptionJob
    {
        public string Id { get; set; } = "";
        public string Language { get; set; } = "";
        public CaptionJobStatus Status { get; set; }
        public int? ProgressDone { get; set; }
        public int? ProgressTotal { get; set; }
        public double MinutesReserved { get; set; }
        public double? MinutesCharged { get; set; }
        public string? Error { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }

        public bool IsActive => Status != CaptionJobStatus.Ready && Status != CaptionJobStatus.Failed && Status != CaptionJobStatus.Cancelled;

        public double? Fraction => ProgressDone is int d && ProgressTotal is int t && t > 0 ? Math.Min(1.0, (double)d / t) : (double?)null;
    }

    /// <summary>Caption minutes for transcribing one video (<c>GET …/captions/minutes</c>).</summary>
    public sealed class CaptionMinutes
    {
        public bool Unlimited { get; set; }

        /// <summary><c>tier</c>, <c>super_admin</c> or <c>none</c> (the plan has no minutes).</summary>
        public string Via { get; set; } = "none";

        public double? MonthLimit { get; set; }
        public double MonthUsed { get; set; }
        public double? Remaining { get; set; }
        public double Overdraft { get; set; }
        public double PacksRemaining { get; set; }
        public double Held { get; set; }
        public bool Warning { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }

        /// <summary>What transcribing this video costs; null while its length is unknown.</summary>
        public double? MinutesNeeded { get; set; }

        public bool HasNoPlan => Via == "none" && !Unlimited;

        /// <summary>A new job may start while the balance is above 0.</summary>
        public bool CanStart => Unlimited || (Remaining ?? 0) > 0;
    }

    /// <summary>One timed line of an on-device transcript.</summary>
    public sealed class TranscriptSegment : IEquatable<TranscriptSegment>
    {
        public TranscriptSegment(int startMs, int endMs, string text)
        {
            StartMs = startMs;
            EndMs = endMs;
            Text = text;
        }

        public int StartMs { get; }
        public int EndMs { get; }
        public string Text { get; }

        public bool Equals(TranscriptSegment? other) => other != null && StartMs == other.StartMs && EndMs == other.EndMs && Text == other.Text;

        public override bool Equals(object? obj) => Equals(obj as TranscriptSegment);

        public override int GetHashCode() => (StartMs, EndMs, Text).GetHashCode();
    }

    /// <summary>The body of <c>POST …/drafts/:language/transcript</c>.</summary>
    public sealed class OnDeviceTranscript
    {
        public OnDeviceTranscript(IReadOnlyList<TranscriptSegment> segments, string? model, bool replace = false)
        {
            Segments = segments;
            Model = model;
            Replace = replace;
        }

        public IReadOnlyList<TranscriptSegment> Segments { get; }

        /// <summary>The whisper model id; the provider is always whisper.cpp.</summary>
        public string? Model { get; }

        /// <summary>Replaces an existing draft of the language; send it only after asking.</summary>
        public bool Replace { get; }

        public const string Provider = "whisper.cpp";
    }

    /// <summary>Reads and writes the captions JSON.</summary>
    public static class CaptionJson
    {
        public static CaptionOrigin Origin(string? s) => s switch
        {
            "review_transcript" => CaptionOrigin.ReviewTranscript,
            "transcription" => CaptionOrigin.Transcription,
            "translation" => CaptionOrigin.Translation,
            "on_device" => CaptionOrigin.OnDevice,
            _ => CaptionOrigin.Unknown,
        };

        /// <summary>The i18n key for an origin: <c>studio.captions.origin.on_device</c> …</summary>
        public static string OriginKey(CaptionOrigin origin) => "studio.captions.origin." + origin switch
        {
            CaptionOrigin.ReviewTranscript => "review_transcript",
            CaptionOrigin.Transcription => "transcription",
            CaptionOrigin.Translation => "translation",
            CaptionOrigin.OnDevice => "on_device",
            _ => "unknown",
        };

        public static CaptionDraftSummary Summary(JsonElement e) => new CaptionDraftSummary
        {
            Language = J.Str(e, "language") ?? "",
            Version = (int)(J.NumOrNull(e, "version") ?? 1),
            Origin = Origin(J.Str(e, "origin")),
            Edited = J.Bool(e, "edited"),
            // The list sends `cues` as a count; one draft sends the cue array and `cueCount`.
            CueCount = (int)(J.NumOrNull(e, "cueCount") ?? J.NumOrNull(e, "cues") ?? 0),
            PendingQuran = (int)J.Num(e, "pendingQuran"),
            UpdatedAt = J.Date(e, "updatedAt"),
            PublishedVersion = J.NumOrNull(e, "publishedVersion") is double p ? (int)p : (int?)null,
            PublishedAt = J.Date(e, "publishedAt"),
            Track = J.Str(e, "track") switch { "machine" => CaptionTrack.Machine, "upload" => CaptionTrack.Upload, _ => CaptionTrack.None },
        };

        public static CaptionDraftList DraftList(JsonElement data) => new CaptionDraftList
        {
            Drafts = J.Array(data, "drafts").Select(Summary).Where(d => d.Language.Length > 0).ToList(),
            Claims = J.Array(data, "claims")
                .Select(c => new CaptionClaim(J.Str(c, "language") ?? "", J.Str(c, "status") == "failed" ? CaptionClaimStatus.Failed : CaptionClaimStatus.Queued, J.Str(c, "error")))
                .Where(c => c.Language.Length > 0)
                .ToList(),
            TranscriptAvailable = J.Bool(data, "transcriptAvailable"),
        };

        public static CaptionDraft Draft(JsonElement data) => new CaptionDraft(Summary(data), J.Array(data, "cues").Select(Cue).ToList());

        public static CaptionCue Cue(JsonElement c)
        {
            var segments = new List<CaptionSegment>();
            foreach (var s in J.Array(c, "segments"))
            {
                var type = J.Str(s, "type");
                if (type == "quran") segments.Add(CaptionSegment.Quran(s.GetRawText()));
                else if (type == "speech") segments.Add(CaptionSegment.Text(J.Str(s, "text") ?? ""));
            }

            return new CaptionCue(J.Str(c, "id") ?? "", (int)J.Num(c, "startMs"), (int)J.Num(c, "endMs"), segments);
        }

        public static void WriteCue(Utf8JsonWriter w, CaptionCue cue)
        {
            w.WriteStartObject();
            w.WriteString("id", cue.Id);
            w.WriteNumber("startMs", cue.StartMs);
            w.WriteNumber("endMs", cue.EndMs);
            w.WriteStartArray("segments");
            foreach (var s in cue.Segments)
            {
                if (s.QuranJson != null)
                {
                    w.WriteRawValue(s.QuranJson, skipInputValidation: false);
                }
                else
                {
                    w.WriteStartObject();
                    w.WriteString("type", "speech");
                    w.WriteString("text", s.Speech);
                    w.WriteEndObject();
                }
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        /// <summary>The PUT body: <c>{baseVersion, cues}</c>, cues as saved (<see cref="CueEditing.ForSaving"/>).</summary>
        public static string SaveBody(int baseVersion, IReadOnlyList<CaptionCue> cues) => Write(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("baseVersion", baseVersion);
            w.WriteStartArray("cues");
            foreach (var cue in CueEditing.ForSaving(cues)) WriteCue(w, cue);
            w.WriteEndArray();
            w.WriteEndObject();
        });

        public static string TranscriptBody(OnDeviceTranscript t) => Write(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("segments");
            foreach (var s in t.Segments)
            {
                w.WriteStartObject();
                w.WriteNumber("startMs", s.StartMs);
                w.WriteNumber("endMs", s.EndMs);
                w.WriteString("text", s.Text);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartObject("engine");
            w.WriteString("provider", OnDeviceTranscript.Provider);
            if (t.Model != null) w.WriteString("model", t.Model);
            w.WriteEndObject();
            if (t.Replace) w.WriteBoolean("replace", true);
            w.WriteEndObject();
        });

        /// <summary>Segments in the transcript body's shape, as Core's <c>transcript_to_caption_segments_json</c> returns them.</summary>
        public static IReadOnlyList<TranscriptSegment> Segments(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : doc.RootElement.TryGetProperty("segments", out var s) ? s : default;
            if (root.ValueKind != JsonValueKind.Array) return Array.Empty<TranscriptSegment>();
            return root.EnumerateArray().Select(x => new TranscriptSegment((int)J.Num(x, "startMs"), (int)J.Num(x, "endMs"), J.Str(x, "text") ?? "")).ToList();
        }

        public static CaptionJob Job(JsonElement j)
        {
            var p = j.TryGetProperty("progress", out var pr) && pr.ValueKind == JsonValueKind.Object ? pr : default;
            return new CaptionJob
            {
                Id = J.Str(j, "id") ?? "",
                Language = J.Str(j, "language") ?? "",
                Status = J.Str(j, "status") switch
                {
                    "queued" => CaptionJobStatus.Queued,
                    "waiting_budget" => CaptionJobStatus.WaitingBudget,
                    "extracting" => CaptionJobStatus.Extracting,
                    "transcribing" => CaptionJobStatus.Transcribing,
                    "postprocessing" => CaptionJobStatus.Postprocessing,
                    "ready" => CaptionJobStatus.Ready,
                    "cancelled" => CaptionJobStatus.Cancelled,
                    _ => CaptionJobStatus.Failed,
                },
                ProgressDone = p.ValueKind == JsonValueKind.Object ? (int?)(int)J.Num(p, "done") : null,
                ProgressTotal = p.ValueKind == JsonValueKind.Object ? (int?)(int)J.Num(p, "total") : null,
                MinutesReserved = J.Num(j, "minutesReserved"),
                MinutesCharged = J.NumOrNull(j, "minutesCharged"),
                Error = J.Str(j, "error"),
                CreatedAt = J.Date(j, "createdAt"),
            };
        }

        public static CaptionMinutes Minutes(JsonElement data)
        {
            var u = data.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object ? usage : default;
            return new CaptionMinutes
            {
                Unlimited = J.Bool(u, "unlimited"),
                Via = J.Str(u, "via") ?? "none",
                MonthLimit = J.NumOrNull(u, "monthLimit"),
                MonthUsed = J.Num(u, "monthUsed"),
                Remaining = J.NumOrNull(u, "remaining"),
                Overdraft = J.Num(u, "overdraft"),
                PacksRemaining = J.Num(u, "packsRemaining"),
                Held = J.Num(u, "held"),
                Warning = J.Bool(u, "warning"),
                ResetsAt = J.Date(u, "resetsAt"),
                MinutesNeeded = J.NumOrNull(data, "minutesNeeded"),
            };
        }

        internal static string Write(Action<Utf8JsonWriter> body)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                body(w);
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    /// <summary>Small JSON readers shared by the Studio clients.</summary>
    internal static class J
    {
        public static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : Enumerable.Empty<JsonElement>();

        public static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public static bool Bool(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        public static double? NumOrNull(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : (double?)null;

        public static double Num(JsonElement e, string name) => NumOrNull(e, name) ?? 0;

        public static long Long(JsonElement e, string name) => (long)Math.Round(Num(e, name));

        public static DateTimeOffset? Date(JsonElement e, string name) =>
            DateTimeOffset.TryParse(Str(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : (DateTimeOffset?)null;

        /// <summary><c>{success, data}</c> → data; anything else as is.</summary>
        public static JsonElement Data(JsonElement root) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) ? d : root;
    }
}
