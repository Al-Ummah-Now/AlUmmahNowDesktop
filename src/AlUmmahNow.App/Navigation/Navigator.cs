using System;
using System.Collections.Generic;

namespace AlUmmahNow.App.Navigation
{
    public abstract class Route
    {
        public sealed class Home : Route { }

        public sealed class Watch : Route
        {
            public Watch(string videoId) => VideoId = videoId;
            public string VideoId { get; }
        }
    }

    /// <summary>A back stack shared by every shell. Each shell renders <see cref="Current"/>.</summary>
    public sealed class Navigator
    {
        private readonly Stack<Route> _stack = new();

        public Navigator() => _stack.Push(new Route.Home());

        public event EventHandler? Changed;

        public Route Current => _stack.Peek();
        public bool CanGoBack => _stack.Count > 1;

        public void Push(Route route)
        {
            _stack.Push(route);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public bool Back()
        {
            if (!CanGoBack) return false;
            _stack.Pop();
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
    }
}
