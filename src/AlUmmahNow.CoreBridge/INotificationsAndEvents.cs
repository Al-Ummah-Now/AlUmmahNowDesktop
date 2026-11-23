using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AlUmmahNow.CoreBridge.Studio;

namespace AlUmmahNow.CoreBridge
{
    public enum NotificationMenuOpenMode
    {
        Seen,
        Read,
    }

    public sealed class NotificationBadge
    {
        public NotificationBadge(int unreadCount = 0, int? unseenCount = null, NotificationMenuOpenMode menuOpen = NotificationMenuOpenMode.Seen)
        {
            UnreadCount = Math.Max(0, unreadCount);
            UnseenCount = Math.Max(0, unseenCount ?? unreadCount);
            MenuOpen = menuOpen;
        }

        public int UnreadCount { get; }
        public int UnseenCount { get; }
        public NotificationMenuOpenMode MenuOpen { get; }
        public int VisibleCount => MenuOpen == NotificationMenuOpenMode.Read ? UnreadCount : UnseenCount;
    }

    public sealed class PrayerQuietWindow
    {
        public PrayerQuietWindow(int startMinute, int endMinute)
        {
            StartMinute = startMinute;
            EndMinute = endMinute;
        }

        public int StartMinute { get; }
        public int EndMinute { get; }
    }

    /// <summary>Daily quiet window and optional prayer-time quiet windows (<c>E22-D6</c>).</summary>
    public sealed class NotificationQuietHours
    {
        public const int DefaultStartMinute = 1320;
        public const int DefaultEndMinute = 360;

        public NotificationQuietHours(
            bool enabled = false,
            string timezone = "UTC",
            int startMinute = DefaultStartMinute,
            int endMinute = DefaultEndMinute,
            bool prayerQuietEnabled = false,
            IReadOnlyList<PrayerQuietWindow>? prayerWindows = null)
        {
            Enabled = enabled;
            Timezone = string.IsNullOrWhiteSpace(timezone) ? "UTC" : timezone;
            StartMinute = startMinute;
            EndMinute = endMinute;
            PrayerQuietEnabled = prayerQuietEnabled;
            PrayerWindows = prayerWindows ?? Array.Empty<PrayerQuietWindow>();
        }

        public bool Enabled { get; }
        public string Timezone { get; }
        public int StartMinute { get; }
        public int EndMinute { get; }
        public bool PrayerQuietEnabled { get; }
        public IReadOnlyList<PrayerQuietWindow> PrayerWindows { get; }

        public NotificationQuietHours With(
            bool? enabled = null,
            string? timezone = null,
            bool? prayerQuietEnabled = null,
            IReadOnlyList<PrayerQuietWindow>? prayerWindows = null) =>
            new NotificationQuietHours(
                enabled ?? Enabled,
                timezone ?? Timezone,
                StartMinute,
                EndMinute,
                prayerQuietEnabled ?? PrayerQuietEnabled,
                prayerWindows ?? PrayerWindows);

        public bool ContainsMinute(int minuteOfDay)
        {
            var minute = ((minuteOfDay % 1440) + 1440) % 1440;
            if (Enabled && WindowContains(StartMinute, EndMinute, minute)) return true;
            if (PrayerQuietEnabled)
            {
                return PrayerWindows.Take(5).Any(w => WindowContains(w.StartMinute, w.EndMinute, minute));
            }

            return false;
        }

        private static bool WindowContains(int start, int end, int minute)
        {
            if (start == end) return false;
            return start < end ? minute >= start && minute < end : minute >= start || minute < end;
        }
    }

    public sealed class NotificationEntry
    {
        private static readonly IReadOnlyDictionary<string, string> EmptyParams = new Dictionary<string, string>();

        public NotificationEntry(
            string id,
            string category,
            string notificationType,
            string notificationKey,
            string title,
            bool isUnread,
            long createdAtMs,
            IReadOnlyDictionary<string, string>? parameters = null)
        {
            Id = id;
            Category = category;
            NotificationType = notificationType;
            NotificationKey = notificationKey;
            Title = title;
            IsUnread = isUnread;
            CreatedAtMs = createdAtMs;
            Parameters = parameters ?? EmptyParams;
        }

        public string Id { get; }
        public string Category { get; }
        public string NotificationType { get; }
        public string NotificationKey { get; }
        public string Title { get; }
        public bool IsUnread { get; }
        public long CreatedAtMs { get; }
        public IReadOnlyDictionary<string, string> Parameters { get; }
    }

    public sealed class NotificationsPage
    {
        public NotificationsPage(IReadOnlyList<NotificationEntry> items, NotificationBadge badge, string? nextCursor = null)
        {
            Items = items;
            Badge = badge;
            NextCursor = nextCursor;
        }

        public IReadOnlyList<NotificationEntry> Items { get; }
        public NotificationBadge Badge { get; }
        public string? NextCursor { get; }
    }

    /// <summary>Unified online event (<c>GET /api/events</c>, <c>E22-D7</c>).</summary>
    public sealed class CalendarEvent
    {
        public CalendarEvent(
            string streamId,
            string channelId,
            string channelName,
            string? channelNameAr,
            string? channelHandle,
            string title,
            string? description,
            string kind,
            string eventType,
            string status,
            string? scheduledStartAt,
            string? startedAt,
            string watchPath)
        {
            StreamId = streamId;
            ChannelId = channelId;
            ChannelName = channelName;
            ChannelNameAr = channelNameAr;
            ChannelHandle = channelHandle;
            Title = title;
            Description = description;
            Kind = kind;
            EventType = eventType;
            Status = status;
            ScheduledStartAt = scheduledStartAt;
            StartedAt = startedAt;
            WatchPath = watchPath;
        }

        public string StreamId { get; }
        public string ChannelId { get; }
        public string ChannelName { get; }
        public string? ChannelNameAr { get; }
        public string? ChannelHandle { get; }
        public string Title { get; }
        public string? Description { get; }
        public string Kind { get; }
        public string EventType { get; }
        public string Status { get; }
        public string? ScheduledStartAt { get; }
        public string? StartedAt { get; }
        public string WatchPath { get; }

        public string LocalizedChannelName(string language) =>
            language == "ar" && !string.IsNullOrWhiteSpace(ChannelNameAr) ? ChannelNameAr! : ChannelName;
    }

    /// <summary>
    /// WNS package-identity readiness for Windows apps (Admin, Viewer, and Studio). Without Partner Center
    /// MSIX identity, push stays off cleanly without crashing or polling.
    /// </summary>
    public sealed class PushConfig
    {
        public PushConfig(string? packageIdentity, bool packaged)
        {
            PackageIdentity = packageIdentity;
            Packaged = packaged;
        }

        /// <summary>The Partner Center identity name stamped into the MSIX at release; empty in dev builds.</summary>
        public string? PackageIdentity { get; }

        /// <summary>Running from an MSIX (WNS channels need package identity).</summary>
        public bool Packaged { get; }

        public bool CanRegister =>
            Packaged
            && !string.IsNullOrWhiteSpace(PackageIdentity)
            && !PackageIdentity!.EndsWith(".Dev", StringComparison.Ordinal);
    }

    public interface INotificationsClient
    {
        Task<UserFeatures> GetViewerFeaturesAsync(CancellationToken cancellationToken = default);
        Task<NotificationBadge> GetBadgeAsync(CancellationToken cancellationToken = default);
        Task<NotificationsPage> ListAsync(int limit = 20, CancellationToken cancellationToken = default);
        Task<NotificationBadge> MarkSeenAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default);
        Task<NotificationBadge> MarkAllReadAsync(CancellationToken cancellationToken = default);
        Task<NotificationQuietHours> GetQuietHoursAsync(CancellationToken cancellationToken = default);
        Task<NotificationQuietHours> UpdateQuietHoursAsync(NotificationQuietHours quietHours, CancellationToken cancellationToken = default);
    }

    public interface IEventsClient
    {
        Task<UserFeatures> GetViewerFeaturesAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<CalendarEvent>> ListUpcomingAsync(string? eventType = null, int limit = 24, CancellationToken cancellationToken = default);
    }

    public interface IAppPushRegistrar
    {
        Task RegisterAsync(string app, string installId, string channelUri, string locale, string appVersion, CancellationToken cancellationToken = default);
        Task UnregisterAsync(string installId, CancellationToken cancellationToken = default);
    }
}
