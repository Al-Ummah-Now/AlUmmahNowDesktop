using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Navigation;
using AlUmmahNow.App.ViewModels;
using AlUmmahNow.CoreBridge;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlUmmahNow.App.Live
{
    /// <summary>The words of the live pages. Shells rebuild on language change.</summary>
    public static class LiveText
    {
        public static string Event(Localizer localizer, LiveEventType? type) => localizer[type switch
        {
            LiveEventType.Jumuah => "live.event.jumuah",
            LiveEventType.Taraweeh => "live.event.taraweeh",
            LiveEventType.Conference => "live.event.conference",
            LiveEventType.Halqa => "live.event.halqa",
            _ => "live.event.all",
        }];

        public static string Watching(Localizer localizer, long viewers) => localizer.Format("live.watching", localizer.Compact(viewers));

        /// <summary>"Live", "Premiere" and the event, as a stream has them.</summary>
        public static IReadOnlyList<LiveBadge> Badges(Localizer localizer, bool isOnAir, LiveKind kind, LiveEventType? type)
        {
            var badges = new List<LiveBadge>();
            if (isOnAir) badges.Add(new LiveBadge(localizer["video.live"], true));
            if (kind == LiveKind.Premiere) badges.Add(new LiveBadge(localizer["live.premiere"], false));
            if (type != null) badges.Add(new LiveBadge(Event(localizer, type), false));
            return badges;
        }
    }

    public sealed class LiveBadge
    {
        public LiveBadge(string text, bool isOnAir)
        {
            Text = text;
            IsOnAir = isOnAir;
        }

        public string Text { get; }

        /// <summary>In gold: it is on now.</summary>
        public bool IsOnAir { get; }
    }

    public sealed class LiveCardViewModel
    {
        private readonly Localizer _localizer;

        public LiveCardViewModel(LiveStream stream, Localizer localizer)
        {
            Stream = stream;
            _localizer = localizer;
        }

        public LiveStream Stream { get; }
        public string Id => Stream.Id;
        public string Title => Stream.Title;
        public string Channel => Stream.ChannelName;
        public string? ThumbnailUrl => Stream.ThumbnailUrl;
        public bool IsOnAir => Stream.Status == LiveStatus.Live;
        public IReadOnlyList<LiveBadge> Badges => LiveText.Badges(_localizer, IsOnAir, Stream.Kind, Stream.EventType);

        /// <summary>How many are watching, or when it starts; empty when neither is known.</summary>
        public string Stats =>
            IsOnAir ? (Stream.Viewers is { } v ? LiveText.Watching(_localizer, v) : "")
            : Stream.ScheduledStart is { } at ? _localizer.DateTime(at)
            : "";
    }

    /// <summary>What is on air and what is coming: home's Live row, and the list with the event filter.</summary>
    public sealed partial class LiveListViewModel : ObservableObject
    {
        private readonly ILiveClient _live;
        private readonly Localizer _localizer;
        private readonly Navigator _navigator;
        private readonly Func<DateTimeOffset> _now;
        private int _request;

        [ObservableProperty] private LoadState _state = LoadState.Loading;

        public LiveListViewModel(ILiveClient live, Localizer localizer, Navigator navigator, Func<DateTimeOffset>? now = null)
        {
            _live = live;
            _localizer = localizer;
            _navigator = navigator;
            _now = now ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>The chosen event; null for all.</summary>
        public LiveEventType? Event { get; private set; }

        /// <summary>The event filter is offered.</summary>
        public bool HasEvents { get; private set; }
        public IReadOnlyList<LiveCardViewModel> OnAir { get; private set; } = Array.Empty<LiveCardViewModel>();
        public IReadOnlyList<LiveCardViewModel> Coming { get; private set; } = Array.Empty<LiveCardViewModel>();

        /// <summary>Home's row: on air first. Nothing on the page while there is none.</summary>
        public IReadOnlyList<LiveCardViewModel> All => OnAir.Concat(Coming).ToList();

        public void Open(LiveCardViewModel card) => _navigator.Push(new Route.Live(card.Id));
        public void OpenList() => _navigator.Push(new Route.LiveList());

        public Task ChooseAsync(LiveEventType? type, CancellationToken cancellationToken = default)
        {
            Event = type;
            return LoadAsync(cancellationToken);
        }

        public async Task LoadAsync(CancellationToken cancellationToken = default)
        {
            var request = ++_request;
            try
            {
                var features = await _live.GetFeaturesAsync(cancellationToken);
                var chosen = features.Events ? Event : null;
                var onAir = _live.ListAsync(LiveStatus.Live, chosen, cancellationToken);
                var scheduled = features.HasComing
                    ? _live.ListAsync(LiveStatus.Scheduled, chosen, cancellationToken)
                    : Task.FromResult<IReadOnlyList<LiveStream>>(Array.Empty<LiveStream>());
                await Task.WhenAll(onAir, scheduled);
                // A later choice was made meanwhile
                if (request != _request) return;
                var now = _now();
                Event = chosen;
                HasEvents = features.Events;
                OnAir = Cards(await onAir);
                Coming = Cards((await scheduled).Where(s => s.ScheduledStart > now).OrderBy(s => s.ScheduledStart));
                State = LoadState.Content;
            }
            catch (CatalogException)
            {
                // What is shown stays, as after a failed refresh
                if (request == _request && State != LoadState.Content) State = LoadState.Error;
            }
        }

        private IReadOnlyList<LiveCardViewModel> Cards(IEnumerable<LiveStream> streams) =>
            streams.Select(s => new LiveCardViewModel(s, _localizer)).ToList();
    }
}
