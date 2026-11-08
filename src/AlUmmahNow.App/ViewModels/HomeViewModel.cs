using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Navigation;
using AlUmmahNow.CoreBridge;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AlUmmahNow.App.ViewModels
{
    public enum LoadState { Loading, Content, Empty, Error }

    public sealed class RailViewModel
    {
        private readonly Localizer _localizer;

        public RailViewModel(string titleKey, IReadOnlyList<VideoCardViewModel> items, Localizer localizer)
        {
            TitleKey = titleKey;
            Items = items;
            _localizer = localizer;
        }

        public string TitleKey { get; }
        public string Title => _localizer[TitleKey];
        public IReadOnlyList<VideoCardViewModel> Items { get; }
    }

    public sealed partial class HomeViewModel : ObservableObject
    {
        private static readonly (string Key, VideoSort Sort, string? Category)[] RailDefinitions =
        {
            ("home.rail.latest", VideoSort.Date, null),
            ("home.rail.popular", VideoSort.Views, null),
            ("home.rail.quran", VideoSort.Date, "quran_recitation"),
            ("home.rail.tafsir", VideoSort.Date, "quran_tafsir"),
            ("home.rail.hadith", VideoSort.Date, "hadith_studies"),
            ("home.rail.fiqh", VideoSort.Date, "fiqh"),
            ("home.rail.history", VideoSort.Date, "islamic_history"),
            ("home.rail.nasheed", VideoSort.Date, "islamic_nasheed"),
            ("home.rail.ramadan", VideoSort.Date, "ramadan"),
        };

        private readonly ICatalogClient _catalog;
        private readonly Localizer _localizer;
        private readonly Navigator _navigator;

        [ObservableProperty]
        private LoadState _state = LoadState.Loading;

        [ObservableProperty]
        private VideoCardViewModel? _featured;

        public HomeViewModel(ICatalogClient catalog, Localizer localizer, Navigator navigator)
        {
            _catalog = catalog;
            _localizer = localizer;
            _navigator = navigator;
        }

        public ObservableCollection<RailViewModel> Rails { get; } = new();

        [RelayCommand]
        public async Task LoadAsync(CancellationToken cancellationToken = default)
        {
            State = LoadState.Loading;
            Rails.Clear();
            Featured = null;

            var tasks = RailDefinitions.Select(d => LoadRailAsync(d.Key, d.Sort, d.Category, cancellationToken)).ToList();
            var rails = await Task.WhenAll(tasks);

            foreach (var rail in rails)
            {
                if (rail != null && rail.Items.Count > 0) Rails.Add(rail);
            }

            Featured = Rails.FirstOrDefault()?.Items.FirstOrDefault();
            // A rail that fails alone is hidden; only a total failure is an error.
            State = rails.All(r => r == null) ? LoadState.Error
                : Rails.Count == 0 ? LoadState.Empty
                : LoadState.Content;
        }

        public void Open(VideoCardViewModel card) => _navigator.Push(new Route.Watch(card.Id));

        private async Task<RailViewModel?> LoadRailAsync(string key, VideoSort sort, string? category, CancellationToken cancellationToken)
        {
            try
            {
                var page = await _catalog.ListVideosAsync(new VideoQuery { Limit = 50, Sort = sort, Category = category }, cancellationToken);
                var cards = page.Items.Where(v => v.IsPlayable).Take(20).Select(v => new VideoCardViewModel(v, _localizer)).ToList();
                return new RailViewModel(key, cards, _localizer);
            }
            catch (CatalogException)
            {
                return null;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }
    }
}
