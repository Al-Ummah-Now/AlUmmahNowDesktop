using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Media;
using AlUmmahNow.App.Navigation;
using AlUmmahNow.App.Services;
using AlUmmahNow.App.ViewModels;
using AlUmmahNow.CoreBridge.Temporary;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace AlUmmahNow.TizenTv
{
    /// <summary>10-foot viewer: absolute layout at 1920x1080, mirrored by hand for RTL, D-pad driven.</summary>
    public sealed class TvApp : NUIApplication
    {
        private const int ScreenWidth = 1920, Margin = 96, CardWidth = 400, CardHeight = 225, CardGap = 32, RowHeight = 400, TopInset = 120;

        private static readonly Color Night = new Color(9f / 255, 48f / 255, 65f / 255, 1);
        private static readonly Color Lantern = new Color(1, 160f / 255, 55f / 255, 1);
        private static readonly Color Text = new Color(247f / 255, 241f / 255, 232f / 255, 1);

        private Localizer _localizer = null!;
        private Navigator _navigator = null!;
        private HomeViewModel _home = null!;
        private TizenMediaPlayer _player = null!;
        private HttpCatalogClient _catalog = null!;
        private WatchViewModel? _watch;
        private TvFocusGrid? _grid;
        private View? _root;
        private View? _rows;
        private readonly List<List<View>> _cards = new List<List<View>>();
        private TextLabel? _caption;
        private TextLabel? _time;

        protected override async void OnCreate()
        {
            base.OnCreate();
            var window = Window.Instance;
            window.BackgroundColor = Color.Transparent;
            window.KeyEvent += OnKey;

            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _catalog = new HttpCatalogClient(http, AppEnvironment.Qa.VideosApi);
            _localizer = new Localizer("ar");
            _navigator = new Navigator();
            _home = new HomeViewModel(_catalog, _localizer, _navigator);
            _player = new TizenMediaPlayer(SynchronizationContext.Current!, window);
            _navigator.Changed += (s, e) => Render();
            _localizer.LanguageChanged += (s, e) => Render();

            Render();
            await _home.LoadAsync();
            Render();
        }

        private TextLabel Label(string text, float size, int x, int y, int width)
        {
            var label = new TextLabel(text)
            {
                PointSize = size,
                TextColor = Text,
                Size2D = new Size2D(width, (int)(size * 3)),
                Position2D = new Position2D(Mirror(x, width), y),
                HorizontalAlignment = _localizer.IsRtl ? HorizontalAlignment.End : HorizontalAlignment.Begin,
            };
            return label;
        }

        private int Mirror(int x, int width) => _localizer.IsRtl ? ScreenWidth - x - width : x;

        private void Render()
        {
            if (_root != null)
            {
                Window.Instance.Remove(_root);
                _root.Dispose();
            }

            _caption = null;
            _time = null;
            _cards.Clear();
            _rows = null;
            _root = new View { Size2D = new Size2D(ScreenWidth, 1080) };
            Window.Instance.Add(_root);

            if (_navigator.Current is Route.Watch route) RenderWatch(route.VideoId);
            else RenderHome();
        }

        private void RenderHome()
        {
            CloseWatch();
            _root!.BackgroundColor = Night;
            _root.Add(Label(_localizer["app.name"], 40, Margin, 32, 900));
            if (_home.State != LoadState.Content)
            {
                var key = _home.State == LoadState.Loading ? "state.loading" : _home.State == LoadState.Error ? "state.error" : "state.empty";
                _root.Add(Label(_localizer[key], 32, Margin, 480, ScreenWidth - (2 * Margin)));
                return;
            }

            _grid = new TvFocusGrid(_home.Rails.Select(r => r.Items.Count).ToList());
            _rows = new View { Position2D = new Position2D(0, TopInset), Size2D = new Size2D(ScreenWidth, RowHeight * _home.Rails.Count) };
            _root.Add(_rows);
            for (var r = 0; r < _home.Rails.Count; r++)
            {
                var rail = _home.Rails[r];
                _rows.Add(Label(rail.Title, 30, Margin, r * RowHeight, 900));
                var cards = new List<View>();
                for (var c = 0; c < rail.Items.Count; c++)
                {
                    var card = BuildCard(rail.Items[c]);
                    card.Position2D = new Position2D(Mirror(Margin + (c * (CardWidth + CardGap)), CardWidth), (r * RowHeight) + 70);
                    _rows.Add(card);
                    cards.Add(card);
                }

                _cards.Add(cards);
            }

            UpdateFocus();
        }

        private View BuildCard(VideoCardViewModel card)
        {
            var view = new View { Size2D = new Size2D(CardWidth, CardHeight + 100) };
            var image = new ImageView { Size2D = new Size2D(CardWidth, CardHeight) };
            if (card.ThumbnailUrl != null) image.ResourceUrl = card.ThumbnailUrl;
            view.Add(image);
            view.Add(new TextLabel(card.Title)
            {
                PointSize = 20,
                TextColor = Text,
                Ellipsis = true,
                Size2D = new Size2D(CardWidth, 50),
                Position2D = new Position2D(0, CardHeight + 12),
                HorizontalAlignment = _localizer.IsRtl ? HorizontalAlignment.End : HorizontalAlignment.Begin,
            });
            return view;
        }

        private void UpdateFocus()
        {
            if (_grid == null || _rows == null) return;
            for (var r = 0; r < _cards.Count; r++)
            {
                for (var c = 0; c < _cards[r].Count; c++)
                {
                    var focused = r == _grid.Row && c == _grid.Column;
                    _cards[r][c].Scale = focused ? new Vector3(1.08f, 1.08f, 1) : Vector3.One;
                    _cards[r][c].BackgroundColor = focused ? Lantern : Color.Transparent;
                }
            }

            _rows.PositionY = TopInset - (_grid.Row * RowHeight);
            var shift = Math.Max(0, _grid.Column - 2) * (CardWidth + CardGap);
            for (var c = 0; c < _cards[_grid.Row].Count; c++)
            {
                var x = Margin + (c * (CardWidth + CardGap)) - shift;
                _cards[_grid.Row][c].PositionX = Mirror(x, CardWidth);
            }
        }

        private void RenderWatch(string id)
        {
            _root!.BackgroundColor = Color.Transparent;
            if (_watch == null)
            {
                _watch = new WatchViewModel(_catalog, _player, new NullMediaSession(), _localizer);
                _watch.PropertyChanged += (s, e) => SyncWatch();
                _ = _watch.LoadAsync(id);
            }

            _caption = new TextLabel("")
            {
                PointSize = 34,
                TextColor = Text,
                MultiLine = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                Size2D = new Size2D(ScreenWidth - 200, 180),
                Position2D = new Position2D(100, 800),
            };
            _root.Add(_caption);
            _time = Label("", 26, Margin, 1000, 600);
            _root.Add(_time);
            SyncWatch();
        }

        private void SyncWatch()
        {
            if (_watch == null || _caption == null || _time == null) return;
            _caption.Text = _watch.Caption;
            _time.Text = _localizer.Duration(_watch.Position) + " / " + _localizer.Duration(_watch.Duration);
        }

        private void CloseWatch()
        {
            _watch?.Dispose();
            _watch = null;
        }

        private void OnKey(object sender, Window.KeyEventArgs e)
        {
            if (e.Key.State != Key.StateType.Down) return;
            var name = e.Key.KeyPressedName;
            if (_watch != null)
            {
                switch (name)
                {
                    case "Return": case "XF86PlayBack": case "XF86AudioPlay": case "XF86AudioPause": _watch.TogglePlay(); break;
                    case "Right": if (_localizer.IsRtl) _watch.SkipBack(); else _watch.SkipForward(); break;
                    case "Left": if (_localizer.IsRtl) _watch.SkipForward(); else _watch.SkipBack(); break;
                    case "XF86Back": case "Escape": _navigator.Back(); break;
                }

                return;
            }

            if (name == "XF86Back" || name == "Escape")
            {
                Exit();
                return;
            }

            if (name == "XF86Red")
            {
                _localizer.Language = _localizer.IsRtl ? "en" : "ar";
                return;
            }

            if (_grid == null) return;
            if (name == "Return")
            {
                _home.Open(_home.Rails[_grid.Row].Items[_grid.Column]);
                return;
            }

            DpadKey? key = name == "Up" ? DpadKey.Up : name == "Down" ? DpadKey.Down : name == "Left" ? DpadKey.Left : name == "Right" ? DpadKey.Right : (DpadKey?)null;
            if (key is { } k && _grid.Move(k, _localizer.IsRtl)) UpdateFocus();
        }

        private static void Main(string[] args) => new TvApp().Run(args);
    }
}
