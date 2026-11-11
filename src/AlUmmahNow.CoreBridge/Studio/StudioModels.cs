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
        public TopVideo(string id, string title, long views)
        {
            Id = id;
            Title = title;
            Views = views;
        }

        public string Id { get; }
        public string Title { get; }
        public long Views { get; }
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
    }
}
