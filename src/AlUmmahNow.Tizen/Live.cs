using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using AlUmmahNow.App.Live;
using AlUmmahNow.App.ViewModels;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Temporary;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace AlUmmahNow.TizenTv
{
    /// <summary>Live streams and premieres on TV: watching, the countdown, and the chat for reading (wiki E17 §6).</summary>
    public sealed partial class TvApp
    {
        private const int ChatWidth = 480, ChatLines = 7;

        private HttpLiveClient _live = null!;
        private LiveListViewModel _liveList = null!;
        private LivePage? _livePage;
        private LiveWatchViewModel? LiveWatch => _livePage?.Current;
        private Action? _unbindLive;
        private List<(string Title, List<(string Title, string? Image, Action Open)> Items)> _rowItems = new List<(string, List<(string, string?, Action)>)>();

        /// <summary>"Live: …", or the title and when it starts.</summary>
        private string LiveTitle(LiveCardViewModel card) => _localizer.Join(card.IsOnAir ? _localizer["video.live"] : "", card.Title, card.IsOnAir ? "" : card.Stats);

        private async Task RefreshLiveAsync()
        {
            var before = string.Join(",", _liveList.All.Select(c => c.Id));
            await _liveList.LoadAsync();
            if (_navigator.Current is App.Navigation.Route.Home && before != string.Join(",", _liveList.All.Select(c => c.Id))) Render();
        }

        /// <summary>Behind another app: nothing is asked for.</summary>
        protected override void OnPause()
        {
            if (LiveWatch != null) LiveWatch.IsHidden = true;
            base.OnPause();
        }

        protected override void OnResume()
        {
            if (LiveWatch != null) LiveWatch.IsHidden = false;
            base.OnResume();
        }

        protected override void OnTerminate()
        {
            LiveWatch?.Close(TimeSpan.FromSeconds(2));
            base.OnTerminate();
        }

        private void CloseLive()
        {
            _unbindLive?.Invoke();
            _unbindLive = null;
            _livePage?.Close();
        }

        private void RenderLive(string id)
        {
            CloseWatch();
            _unbindLive?.Invoke();
            _unbindLive = null;
            var vm = _livePage!.Open(id);
            _root!.BackgroundColor = vm.Watch == null ? Night : Color.Transparent;
            if (vm.State != LoadState.Content)
            {
                _root.Add(Label(_localizer[vm.State == LoadState.Loading ? "state.loading" : "state.error"], 32, Margin, 480, ScreenWidth - (2 * Margin)));
                return;
            }

            var title = Label(_localizer.Join(new[] { vm.Title }.Concat(vm.Badges.Select(b => b.Text))), 34, Margin, 32, ScreenWidth - ChatWidth - (2 * Margin));
            _root.Add(title);
            var status = Label("", 26, Margin, 110, ScreenWidth - ChatWidth - (2 * Margin));
            _root.Add(status);

            TextLabel? notice = null;
            if (vm.Watch is { } watch)
            {
                AddTimedText();
                PropertyChangedEventHandler timed = (s, e) => SyncWatch();
                watch.PropertyChanged += timed;
                _unbindLive += () => watch.PropertyChanged -= timed;
                SyncWatch();
            }
            else
            {
                notice = new TextLabel("")
                {
                    PointSize = 40,
                    TextColor = Text,
                    MultiLine = true,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Size2D = new Size2D(ScreenWidth - ChatWidth - (3 * Margin), 500),
                    Position2D = new Position2D(Mirror(Margin, ScreenWidth - ChatWidth - (3 * Margin)), 280),
                };
                _root.Add(notice);
            }

            TextLabel? chat = null;
            if (vm.HasChat)
            {
                var panel = new View
                {
                    Size2D = new Size2D(ChatWidth, 1080),
                    Position2D = new Position2D(Mirror(ScreenWidth - ChatWidth, ChatWidth), 0),
                    BackgroundColor = new Color(9f / 255, 48f / 255, 65f / 255, 0.88f),
                };
                _root.Add(panel);
                panel.Add(PanelText(vm.ChatTitle, 28, 40, 80));
                chat = PanelText("", 22, 130, 910);
                chat.MultiLine = true;
                chat.VerticalAlignment = VerticalAlignment.Bottom;
                panel.Add(chat);
            }

            void Sync()
            {
                // The remote's Return goes back to the moment: said where it is
                status.Text = vm.IsBehind ? _localizer["live.return"] : _localizer.Join(vm.Channel, vm.Watching);
                status.TextColor = vm.IsBehind ? Lantern : Text;
                if (notice != null)
                {
                    var said = vm.Notice;
                    notice.Text = string.Join("\n", new[] { said.Headline }.Concat(said.Lines));
                }

                if (chat == null) return;
                var lines = vm.Messages.Skip(Math.Max(0, vm.Messages.Count - ChatLines)).Select(m => m.AuthorName + "\n" + m.Text).ToList();
                if (vm.Pinned is { } pinned) lines.Insert(0, _localizer["live.chat.pinned"] + "\n" + pinned.Text);
                chat.Text = lines.Count == 0 ? vm.ChatEmpty : string.Join("\n\n", lines);
            }

            PropertyChangedEventHandler changed = (s, e) => Sync();
            vm.PropertyChanged += changed;
            _unbindLive += () => vm.PropertyChanged -= changed;
            Sync();
        }

        private TextLabel PanelText(string text, float size, int y, int height) => new TextLabel(text)
        {
            PointSize = size,
            TextColor = Text,
            Size2D = new Size2D(ChatWidth - 64, height),
            Position2D = new Position2D(32, y),
            HorizontalAlignment = _localizer.IsRtl ? HorizontalAlignment.End : HorizontalAlignment.Begin,
        };

        private void OnLiveKey(LiveWatchViewModel vm, string name)
        {
            switch (name)
            {
                case "XF86Back": case "Escape": _navigator.Back(); break;
                case "Return" when vm.IsBehind: vm.ReturnToLive(); break;
                case "Return": case "XF86PlayBack": case "XF86AudioPlay": case "XF86AudioPause": vm.Watch?.TogglePlay(); break;
                case "Right": if (_localizer.IsRtl) vm.Watch?.SkipBack(); else vm.Watch?.SkipForward(); break;
                case "Left": if (_localizer.IsRtl) vm.Watch?.SkipForward(); else vm.Watch?.SkipBack(); break;
            }
        }
    }
}
