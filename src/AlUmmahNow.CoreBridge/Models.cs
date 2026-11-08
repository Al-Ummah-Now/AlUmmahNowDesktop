using System;
using System.Collections.Generic;

namespace AlUmmahNow.CoreBridge
{
    /// <summary>Text with per-language translations and a fallback.</summary>
    public sealed class LocalizedText
    {
        private readonly IReadOnlyDictionary<string, string> _translations;

        public LocalizedText(string fallback, IReadOnlyDictionary<string, string>? translations = null)
        {
            Fallback = fallback;
            _translations = translations ?? new Dictionary<string, string>();
        }

        public string Fallback { get; }

        /// <summary>Returns the translation, or the fallback when it is missing or blank.</summary>
        public string For(string language) =>
            _translations.TryGetValue(language, out var value) && !string.IsNullOrWhiteSpace(value) ? value : Fallback;
    }

    public sealed class Chapter
    {
        public Chapter(double start, double end, LocalizedText title)
        {
            Start = start;
            End = end;
            Title = title;
        }

        public double Start { get; }
        public double End { get; }
        public LocalizedText Title { get; }
    }

    public sealed class Video
    {
        public string Id { get; set; } = "";
        public LocalizedText Title { get; set; } = new LocalizedText("");
        public LocalizedText Description { get; set; } = new LocalizedText("");
        public string ChannelName { get; set; } = "";
        public string? ChannelAvatarUrl { get; set; }
        public string? ThumbnailUrl { get; set; }
        public string? PlayUrl { get; set; }
        public double? DurationSeconds { get; set; }
        public long Views { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
        public string? Category { get; set; }
        public bool IsLive { get; set; }
        public bool IsPublic { get; set; }
        public string UploadStatus { get; set; } = "";
        public IReadOnlyList<Chapter> Chapters { get; set; } = Array.Empty<Chapter>();

        public bool IsPlayable => IsPublic && UploadStatus == "ready" && !string.IsNullOrEmpty(PlayUrl);
    }

    public sealed class VideoPage
    {
        public VideoPage(IReadOnlyList<Video> items, string? cursor)
        {
            Items = items;
            Cursor = cursor;
        }

        public IReadOnlyList<Video> Items { get; }
        public string? Cursor { get; }
    }

    public enum VideoSort { Date, Views, Title, Likes }

    public sealed class VideoQuery
    {
        public int Limit { get; set; } = 24;
        public VideoSort Sort { get; set; } = VideoSort.Date;
        public string? Category { get; set; }
        public string? Cursor { get; set; }
    }

    public sealed class SubtitleTrack
    {
        public SubtitleTrack(string language, string label, string url, bool isDefault)
        {
            Language = language;
            Label = label;
            Url = url;
            IsDefault = isDefault;
        }

        public string Language { get; }
        public string Label { get; }
        public string Url { get; }
        public bool IsDefault { get; }
    }
}
