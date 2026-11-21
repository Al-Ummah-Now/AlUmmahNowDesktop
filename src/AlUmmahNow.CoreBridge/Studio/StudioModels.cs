using System;
using System.Collections.Generic;

namespace AlUmmahNow.CoreBridge.Studio
{
    public enum ChannelRole { Owner, Admin, Editor, Moderator }

    /// <summary>A channel the account owns or holds a channel role on.</summary>
    public sealed class StudioChannel
    {
        public StudioChannel(string id, string name, string? handle, string? avatarUrl, bool isMain, ChannelRole role, IReadOnlyCollection<string> capabilities, bool isSuspended)
        {
            Id = id;
            Name = name;
            Handle = handle;
            AvatarUrl = avatarUrl;
            IsMain = isMain;
            Role = role;
            Capabilities = capabilities;
            IsSuspended = isSuspended;
        }

        public string Id { get; }
        public string Name { get; }
        public string? Handle { get; }
        public string? AvatarUrl { get; }
        public bool IsMain { get; }
        public ChannelRole Role { get; }
        public IReadOnlyCollection<string> Capabilities { get; }
        public bool IsSuspended { get; }

        /// <summary>Owners can do everything; other roles only what their capabilities list.</summary>
        public bool Can(string capability)
        {
            if (Role == ChannelRole.Owner) return true;
            foreach (var c in Capabilities)
            {
                if (string.Equals(c, capability, StringComparison.Ordinal)) return true;
            }

            return false;
        }
    }

    public enum StudioVisibility { Public, Private }

    public enum StudioVideoStatus { Initiated, Uploading, Processing, Ready, Failed, Unknown }

    public sealed class StudioVideo
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public IReadOnlyDictionary<string, string> TitleLocalized { get; set; } = new Dictionary<string, string>();

        /// <summary>The title in <paramref name="language"/> when the creator gave one, else the original title.</summary>
        public string TitleFor(string language) => LocalizedTitle.Pick(Title, TitleLocalized, language);

        public string? ThumbnailUrl { get; set; }
        public string ChannelId { get; set; } = "";
        public bool IsPublic { get; set; }
        public string? Category { get; set; }
        public string? Language { get; set; }
        public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
        public StudioVideoStatus Status { get; set; }
        public double? DurationSeconds { get; set; }
        public long Views { get; set; }
        public long Likes { get; set; }
        public long Comments { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }
        public DateTimeOffset? ScheduledFor { get; set; }
        public bool IsShort { get; set; }
    }

    public sealed class StudioVideoPage
    {
        public StudioVideoPage(IReadOnlyList<StudioVideo> items, string? cursor)
        {
            Items = items;
            Cursor = cursor;
        }

        public IReadOnlyList<StudioVideo> Items { get; }
        public string? Cursor { get; }
    }

    public enum StudioVisibilityFilter { All, Public, Private }

    public sealed class StudioVideoQuery
    {
        public string ChannelId { get; set; } = "";
        public string? Text { get; set; }
        public StudioVisibilityFilter Visibility { get; set; }
        public string? Cursor { get; set; }
        public int Limit { get; set; } = 30;
    }

    /// <summary>Fields to change on one video; null leaves a field unchanged.</summary>
    public sealed class VideoChanges
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public bool? IsPublic { get; set; }

        /// <summary>"" clears the category.</summary>
        public string? Category { get; set; }

        public string? Language { get; set; }

        /// <summary>Replaces the tags.</summary>
        public IReadOnlyList<string>? Tags { get; set; }

        public bool IsEmpty => Title == null && Description == null && IsPublic == null && Category == null && Language == null && Tags == null;
    }

    /// <summary>Changes applied to many videos at once.</summary>
    public sealed class BulkVideoChanges
    {
        public bool? IsPublic { get; set; }

        /// <summary>"" clears the category.</summary>
        public string? Category { get; set; }

        public string? Language { get; set; }
        public IReadOnlyList<string> AddTags { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> RemoveTags { get; set; } = Array.Empty<string>();

        public bool IsEmpty => IsPublic == null && Category == null && Language == null && AddTags.Count == 0 && RemoveTags.Count == 0;
    }

    public enum DeletionStatus { Deleted, Queued, Purging, Purged }

    /// <summary>A video in Recently deleted: restorable until <see cref="PurgeAfter"/> unless queued for permanent deletion.</summary>
    public sealed class DeletedVideo
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public DateTimeOffset DeletedAt { get; set; }
        public DateTimeOffset? PurgeAfter { get; set; }
        public DeletionStatus Status { get; set; }

        public bool CanRestore => Status == DeletionStatus.Deleted;
    }

    /// <summary>Per-video outcome of a bulk delete, restore or purge.</summary>
    public sealed class BulkOutcome
    {
        public BulkOutcome(IReadOnlyList<string> succeeded, IReadOnlyDictionary<string, string> failed)
        {
            Succeeded = succeeded;
            Failed = failed;
        }

        public IReadOnlyList<string> Succeeded { get; }

        /// <summary>Video id → the server's reason.</summary>
        public IReadOnlyDictionary<string, string> Failed { get; }
    }

    public sealed class StudioPlaylist
    {
        public StudioPlaylist(string id, string title, int videoCount, bool isPublic)
        {
            Id = id;
            Title = title;
            VideoCount = videoCount;
            IsPublic = isPublic;
        }

        public string Id { get; }
        public string Title { get; }
        public int VideoCount { get; }
        public bool IsPublic { get; }
    }

    public sealed class AnalyticsTotals
    {
        public long Views { get; set; }
        public double WatchTimeMinutes { get; set; }
        public long Likes { get; set; }
        public long Comments { get; set; }
        public long Subscribers { get; set; }
        public double AverageViewSeconds { get; set; }
    }

    public sealed class AnalyticsDay
    {
        public AnalyticsDay(DateTime date, long views, double watchTimeMinutes)
        {
            Date = date;
            Views = views;
            WatchTimeMinutes = watchTimeMinutes;
        }

        public DateTime Date { get; }
        public long Views { get; }
        public double WatchTimeMinutes { get; }
    }

    public sealed class TopVideo
    {
        public TopVideo(string id, string title, long views, IReadOnlyDictionary<string, string>? titleLocalized = null, string? thumbnailUrl = null)
        {
            Id = id;
            Title = title;
            Views = views;
            TitleLocalized = titleLocalized ?? new Dictionary<string, string>();
            ThumbnailUrl = thumbnailUrl;
        }

        public string Id { get; }
        public string Title { get; }
        public long Views { get; }
        public IReadOnlyDictionary<string, string> TitleLocalized { get; }

        /// <summary>The video's thumbnail (the API's optional <c>thumbnailUrl</c>); null shows the night tile.</summary>
        public string? ThumbnailUrl { get; }

        /// <summary>The title in <paramref name="language"/> when the creator gave one, else the original title.</summary>
        public string TitleFor(string language) => LocalizedTitle.Pick(Title, TitleLocalized, language);
    }

    /// <summary>Picks a per-language title (empty variants count as missing).</summary>
    public static class LocalizedTitle
    {
        public static string Pick(string fallback, IReadOnlyDictionary<string, string> localized, string language) =>
            localized.TryGetValue(language, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    }

    public sealed class ChannelAnalytics
    {
        public AnalyticsTotals Totals { get; set; } = new AnalyticsTotals();
        public IReadOnlyList<AnalyticsDay> Daily { get; set; } = Array.Empty<AnalyticsDay>();
        public IReadOnlyList<TopVideo> TopVideos { get; set; } = Array.Empty<TopVideo>();
    }

    public enum SuggestionKind { Query, Video, Channel, Playlist, Category }

    public sealed class Suggestion
    {
        public Suggestion(string text, SuggestionKind kind, string? id, string? subtitle)
        {
            Text = text;
            Kind = kind;
            Id = id;
            Subtitle = subtitle;
        }

        public string Text { get; }
        public SuggestionKind Kind { get; }
        public string? Id { get; }
        public string? Subtitle { get; }
    }

    /// <summary>Thrown when the server asks us to slow down; carries how long to wait.</summary>
    public sealed class RateLimitedException : Exception
    {
        public RateLimitedException(TimeSpan retryAfter) : base("Rate limited") => RetryAfter = retryAfter;

        public TimeSpan RetryAfter { get; }
    }

    /// <summary>A Studio API call failed. <see cref="Status"/> is the HTTP status when there was one.</summary>
    public sealed class StudioApiException : Exception
    {
        public StudioApiException(string message, int? status = null, Exception? inner = null) : base(message, inner) => Status = status;

        public int? Status { get; }

        /// <summary>The server's error code (<c>too_late</c>, <c>draft_exists</c>, <c>files_missing</c> …) when it sent one.</summary>
        public string? Code { get; private set; }

        /// <summary><c>files_missing</c>: the declared files the server does not have.</summary>
        public IReadOnlyList<string> Missing { get; private set; } = Array.Empty<string>();

        /// <summary><c>invalid_transcript</c>: why, and which segment (<see cref="Segment"/>).</summary>
        public string? Reason { get; private set; }

        public int? Segment { get; private set; }

        /// <summary>No answer from the server (offline, DNS, reset); worth retrying.</summary>
        public bool IsNetwork { get; private set; }

        public static StudioApiException FromServer(string message, int status, string? code, IReadOnlyList<string>? missing = null, string? reason = null, int? segment = null, Exception? inner = null) =>
            new StudioApiException(message, status, inner) { Code = code, Missing = missing ?? Array.Empty<string>(), Reason = reason, Segment = segment };

        /// <summary>404 <c>feature_off</c>: the feature is off, unconfigured or outside the tier. Hide the entry point; say nothing.</summary>
        public bool IsFeatureOff => Status == 404 && Code == "feature_off";

        /// <summary>
        /// The answer means "this person doesn't get this feature": 404 <c>feature_off</c>, 402 or <c>upgrade_required</c>.
        /// The UI hides the entry point and shows no text (no upsell, no "not in your plan").
        /// </summary>
        public bool HidesFeature => IsFeatureOff || Status == 402 || Code == "upgrade_required";

        /// <summary>The <c>code</c> (or <c>error.code</c>) of a JSON error body, or null.</summary>
        public static string? CodeFromBody(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            var text = body!.Trim();
            if (!text.StartsWith("{", StringComparison.Ordinal)) return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
                if (root.TryGetProperty("code", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String) return c.GetString();
                if (root.TryGetProperty("error", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty("code", out var ec) && ec.ValueKind == System.Text.Json.JsonValueKind.String) return ec.GetString();
                return null;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        public static StudioApiException Network(string message, Exception inner) => new StudioApiException(message, null, inner) { IsNetwork = true };

        /// <summary>Offline, 5xx and 429 answers can succeed later; everything else is final.</summary>
        public bool IsTransient => IsNetwork || (Status is int s && (s >= 500 || s == 429));
    }
}
