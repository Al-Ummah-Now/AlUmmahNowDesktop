using System.Collections.Generic;
using System.Collections.ObjectModel;
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
                var page = await _client.ListAsync(20, cancellationToken);
                Items.Clear();
                foreach (var item in page.Items) Items.Add(item);
                Badge = page.Items.Count > 0
                    ? await _client.MarkSeenAsync(page.Items.Select(i => i.Id).ToArray(), cancellationToken)
                    : page.Badge;
                QuietHours = await _client.GetQuietHoursAsync(cancellationToken);
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
                var updated = Items.Select(i => new NotificationEntry(i.Id, i.Category, i.NotificationType, i.NotificationKey, i.Title, false, i.CreatedAtMs)).ToList();
                Items.Clear();
                foreach (var item in updated) Items.Add(item);
                RaiseComputed();
            }
            catch (CatalogException)
            {
            }
        }

        public async Task SetDailyQuietAsync(bool enabled, CancellationToken cancellationToken = default) =>
            await SaveQuietAsync(QuietHours.With(enabled: enabled), cancellationToken);

        public async Task SetPrayerQuietAsync(bool enabled, CancellationToken cancellationToken = default) =>
            await SaveQuietAsync(QuietHours.With(prayerQuietEnabled: enabled), cancellationToken);

        private async Task SaveQuietAsync(NotificationQuietHours next, CancellationToken cancellationToken)
        {
            QuietHours = next;
            OnPropertyChanged(nameof(QuietWindowLabel));
            try
            {
                QuietHours = await _client.UpdateQuietHoursAsync(next, cancellationToken);
                OnPropertyChanged(nameof(QuietWindowLabel));
            }
            catch (CatalogException)
            {
            }
        }

        private void RaiseComputed()
        {
            OnPropertyChanged(nameof(BadgeText));
            OnPropertyChanged(nameof(HasBadge));
            OnPropertyChanged(nameof(HasUnread));
            OnPropertyChanged(nameof(QuietWindowLabel));
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
            _localizer.Join(ev.LocalizedChannelName(_localizer.Language), ev.ScheduledStartAt);

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
