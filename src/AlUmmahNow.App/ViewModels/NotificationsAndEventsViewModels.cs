using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Studio;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlUmmahNow.App.ViewModels
{
    public sealed partial class NotificationsViewModel : ObservableObject
    {
        private readonly INotificationsClient _client;
        private readonly Localizer _localizer;

        [ObservableProperty] private LoadState _state = LoadState.Loading;
        [ObservableProperty] private bool _isQuietHoursEnabled;
        [ObservableProperty] private NotificationBadge _badge = new NotificationBadge();
        [ObservableProperty] private NotificationQuietHours _quietHours = new NotificationQuietHours();

        public NotificationsViewModel(INotificationsClient client, Localizer localizer)
        {
            _client = client;
            _localizer = localizer;
            _localizer.LanguageChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(BadgeText));
                OnPropertyChanged(nameof(QuietWindowLabel));
            };
        }

        public ObservableCollection<NotificationEntry> Items { get; } = new ObservableCollection<NotificationEntry>();

        public string BadgeText => _localizer.BadgeCap9(Badge.VisibleCount);
        public bool HasBadge => Badge.VisibleCount > 0;
        public bool HasUnread => Items.Any(i => i.IsUnread);
        public bool IsPrayerQuietVisible => IsQuietHoursEnabled && QuietHours.PrayerWindows.Count > 0;

        public string TitleFor(NotificationEntry entry)
        {
            string Param(string name) =>
                entry.Parameters.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : "";

            var isAr = _localizer.Language == "ar";
            var video = Param("videoTitle");
            var stream = Param("streamTitle");
            var channel = Param("channelName");
            if (string.IsNullOrEmpty(channel)) channel = Param("channel");
            var tier = isAr ? Param("tier_ar") : Param("tier_en");
            if (string.IsNullOrEmpty(tier)) tier = Param("tier");

            switch (entry.NotificationKey)
            {
                case "newVideo" when !string.IsNullOrEmpty(channel) && !string.IsNullOrEmpty(video):
                    return _localizer.Format("notifications.msg.newVideo", channel, video);
                case "liveStarted" when !string.IsNullOrEmpty(channel) && !string.IsNullOrEmpty(stream.Length > 0 ? stream : video):
                    return _localizer.Format("notifications.msg.liveStarted", channel, stream.Length > 0 ? stream : video);
                case "liveSoon" when !string.IsNullOrEmpty(channel) && !string.IsNullOrEmpty(stream):
                    return _localizer.Format("notifications.msg.liveSoon", channel, stream);
                case "premiereCancelled" when !string.IsNullOrEmpty(stream):
                    return _localizer.Format("notifications.msg.premiereCancelled", stream);
                case "commentReply" when !string.IsNullOrEmpty(Param("actorName")):
                    return _localizer.Format("notifications.msg.commentReply", Param("actorName"));
                case "newComment" when !string.IsNullOrEmpty(video):
                    return _localizer.Format("notifications.msg.newComment", video);
                case "newSubscriber" when !string.IsNullOrEmpty(channel):
                    return _localizer.Format("notifications.msg.newSubscriber", channel);
                case "uploadReady" when !string.IsNullOrEmpty(video):
                    return _localizer.Format("notifications.msg.uploadReady", video);
                case "uploadFailed" when !string.IsNullOrEmpty(video):
                    return _localizer.Format("notifications.msg.uploadFailed", video);
                case "reviewApproved" when !string.IsNullOrEmpty(video):
                    return _localizer.Format("notifications.msg.reviewApproved", video);
                case "reviewRejected" when !string.IsNullOrEmpty(video):
                    return _localizer.Format("notifications.msg.reviewRejected", video);
                case "reviewNeeded" when !string.IsNullOrEmpty(video):
                    return _localizer.Format("notifications.msg.reviewNeeded", video);
                case "channelInviteReceived" when !string.IsNullOrEmpty(Param("inviter")) && !string.IsNullOrEmpty(channel):
                    return _localizer.Format("notifications.msg.channelInviteReceived", Param("inviter"), channel);
                case "channelInviteAccepted" when !string.IsNullOrEmpty(Param("member")) && !string.IsNullOrEmpty(channel):
                    return _localizer.Format("notifications.msg.channelInviteAccepted", Param("member"), channel);
                case "channelRoleChanged" when !string.IsNullOrEmpty(channel):
                    return _localizer.Format("notifications.msg.channelRoleChanged", channel);
                case "channelMemberRemoved" when !string.IsNullOrEmpty(channel):
                    return _localizer.Format("notifications.msg.channelMemberRemoved", channel);
                case "channelMemberLeft" when !string.IsNullOrEmpty(Param("member")) && !string.IsNullOrEmpty(channel):
                    return _localizer.Format("notifications.msg.channelMemberLeft", Param("member"), channel);
                case "tierRequestApproved" when !string.IsNullOrEmpty(tier):
                    return _localizer.Format("notifications.msg.tierRequestApproved", tier);
                case "tierRequestDeclined" when !string.IsNullOrEmpty(tier):
                    return _localizer.Format("notifications.msg.tierRequestDeclined", tier);
                case "tierRequestReceived" when !string.IsNullOrEmpty(tier):
                    return _localizer.Format("notifications.msg.tierRequestReceived", tier);
            }

            var titleKey = "notifications.title." + entry.NotificationKey;
            var resolved = _localizer[titleKey];
            if (resolved != titleKey) return resolved;
            return !string.IsNullOrWhiteSpace(entry.Title) ? entry.Title : _localizer["notifications.title.default"];
        }

        public string QuietWindowLabel => _localizer.Format(
            "notifications.quietWindow",
            _localizer.MinuteOfDay(QuietHours.StartMinute),
            _localizer.MinuteOfDay(QuietHours.EndMinute),
            Localizer.Isolate(QuietHours.Timezone));

        public async Task RefreshBadgeAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                Badge = await _client.GetBadgeAsync(cancellationToken);
                OnPropertyChanged(nameof(BadgeText));
                OnPropertyChanged(nameof(HasBadge));
            }
            catch (CatalogException)
            {
            }
        }

        public async Task OpenInboxAsync(CancellationToken cancellationToken = default)
        {
            State = LoadState.Loading;
            try
            {
                try
                {
                    var features = await _client.GetViewerFeaturesAsync(cancellationToken);
                    IsQuietHoursEnabled = features.IsOn(UserFeatures.QuietHours);
                }
                catch (CatalogException)
                {
                    IsQuietHoursEnabled = false;
                }

                var page = await _client.ListAsync(20, cancellationToken);
                Items.Clear();
                foreach (var item in page.Items) Items.Add(item);
                Badge = page.Items.Count > 0
                    ? await _client.MarkSeenAsync(page.Items.Select(i => i.Id).ToArray(), cancellationToken)
                    : page.Badge;
                if (IsQuietHoursEnabled)
                {
                    try
                    {
                        QuietHours = await _client.GetQuietHoursAsync(cancellationToken);
                    }
                    catch (CatalogException)
                    {
                        IsQuietHoursEnabled = false;
                        QuietHours = new NotificationQuietHours();
                    }
                }
                else
                {
                    QuietHours = new NotificationQuietHours();
                }

                RaiseComputed();
                State = Items.Count == 0 ? LoadState.Empty : LoadState.Content;
            }
            catch (CatalogException)
            {
                State = LoadState.Error;
            }
        }

        public async Task MarkAllReadAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                Badge = await _client.MarkAllReadAsync(cancellationToken);
                var updated = Items.Select(i => new NotificationEntry(i.Id, i.Category, i.NotificationType, i.NotificationKey, i.Title, false, i.CreatedAtMs, i.Parameters)).ToList();
                Items.Clear();
                foreach (var item in updated) Items.Add(item);
                RaiseComputed();
            }
            catch (CatalogException)
            {
            }
        }

        public async Task SetDailyQuietAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            if (!IsQuietHoursEnabled) return;
            await SaveQuietAsync(QuietHours.With(enabled: enabled), cancellationToken);
        }

        public async Task SetPrayerQuietAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            if (!IsPrayerQuietVisible) return;
            await SaveQuietAsync(QuietHours.With(prayerQuietEnabled: enabled), cancellationToken);
        }

        /// <summary>
        /// Syncs locally computed prayer quiet windows (Epic 21.1) when non-empty and changed.
        /// </summary>
        public async Task SyncPrayerWindowsAsync(IReadOnlyList<PrayerQuietWindow> localWindows, string timezone, CancellationToken cancellationToken = default)
        {
            if (!IsQuietHoursEnabled || localWindows.Count == 0) return;
            var trimmed = localWindows.Take(5).ToArray();
            var sameZone = QuietHours.Timezone == timezone;
            var sameWindows = QuietHours.PrayerWindows.Count == trimmed.Length
                && QuietHours.PrayerWindows.Zip(trimmed, (a, b) => a.StartMinute == b.StartMinute && a.EndMinute == b.EndMinute).All(x => x);
            if (sameZone && sameWindows) return;
            await SaveQuietAsync(QuietHours.With(timezone: timezone, prayerWindows: trimmed), cancellationToken);
        }

        private async Task SaveQuietAsync(NotificationQuietHours next, CancellationToken cancellationToken)
        {
            var previous = QuietHours;
            QuietHours = next;
            OnPropertyChanged(nameof(QuietWindowLabel));
            OnPropertyChanged(nameof(IsPrayerQuietVisible));
            try
            {
                QuietHours = await _client.UpdateQuietHoursAsync(next, cancellationToken);
                OnPropertyChanged(nameof(QuietWindowLabel));
                OnPropertyChanged(nameof(IsPrayerQuietVisible));
            }
            catch (CatalogException)
            {
                QuietHours = previous;
                OnPropertyChanged(nameof(QuietWindowLabel));
                OnPropertyChanged(nameof(IsPrayerQuietVisible));
            }
        }

        private void RaiseComputed()
        {
            OnPropertyChanged(nameof(BadgeText));
            OnPropertyChanged(nameof(HasBadge));
            OnPropertyChanged(nameof(HasUnread));
            OnPropertyChanged(nameof(QuietWindowLabel));
            OnPropertyChanged(nameof(IsPrayerQuietVisible));
        }
    }

    public sealed partial class EventsCalendarViewModel : ObservableObject
    {
        private readonly IEventsClient _client;
        private readonly Localizer _localizer;

        [ObservableProperty] private bool _isFeatureEnabled;
        [ObservableProperty] private LoadState _state = LoadState.Empty;
        [ObservableProperty] private string? _selectedFilter;

        public EventsCalendarViewModel(IEventsClient client, Localizer localizer)
        {
            _client = client;
            _localizer = localizer;
        }

        public ObservableCollection<CalendarEvent> Items { get; } = new ObservableCollection<CalendarEvent>();

        public string SubtitleFor(CalendarEvent ev) =>
            _localizer.Join(ev.LocalizedChannelName(_localizer.Language), DateTimeOffset.TryParse(ev.ScheduledStartAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? _localizer.DateTime(at) : null);

        public async Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var features = await _client.GetViewerFeaturesAsync(cancellationToken);
                if (!features.IsOn(UserFeatures.EventCalendar))
                {
                    HideFeature();
                    return;
                }

                IsFeatureEnabled = true;
                await LoadEventsAsync(cancellationToken);
            }
            catch (CatalogException)
            {
                HideFeature();
            }
        }

        public async Task SelectFilterAsync(string? eventType, CancellationToken cancellationToken = default)
        {
            SelectedFilter = eventType;
            if (!IsFeatureEnabled) return;
            await LoadEventsAsync(cancellationToken);
        }

        private async Task LoadEventsAsync(CancellationToken cancellationToken)
        {
            State = LoadState.Loading;
            try
            {
                var list = await _client.ListUpcomingAsync(SelectedFilter, 24, cancellationToken);
                Items.Clear();
                foreach (var item in list) Items.Add(item);
                State = Items.Count == 0 ? LoadState.Empty : LoadState.Content;
            }
            catch (CatalogException)
            {
                State = LoadState.Error;
            }
        }

        private void HideFeature()
        {
            IsFeatureEnabled = false;
            Items.Clear();
            State = LoadState.Empty;
        }
    }
}
