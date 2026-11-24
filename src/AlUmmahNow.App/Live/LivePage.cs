using System;
using AlUmmahNow.App.Navigation;

namespace AlUmmahNow.App.Live
{
    /// <summary>The live page a window shows: opened as the page is built, run once it is there, built again as it changes.</summary>
    public sealed class LivePage
    {
        private readonly PageRenderer _page;
        private readonly Func<string, LiveWatchViewModel> _create;

        public LivePage(PageRenderer page, Func<string, LiveWatchViewModel> create)
        {
            _page = page;
            _create = create;
        }

        public LiveWatchViewModel? Current { get; private set; }

        public LiveWatchViewModel Open(string id)
        {
            if (Current is { } open && open.StreamId == id) return open;
            Close();
            var vm = Current = _create(id);
            vm.LayoutChanged += (s, e) =>
            {
                if (ReferenceEquals(vm, Current)) _page.Render();
            };
            _page.After(() =>
            {
                if (ReferenceEquals(vm, Current)) _ = vm.RunAsync();
            });
            return vm;
        }

        public void Close()
        {
            Current?.Dispose();
            Current = null;
        }
    }
}
