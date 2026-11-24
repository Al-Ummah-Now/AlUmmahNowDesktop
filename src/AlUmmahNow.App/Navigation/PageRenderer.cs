using System;
using System.Collections.Generic;

namespace AlUmmahNow.App.Navigation
{
    /// <summary>
    /// Builds the window's page, never inside itself: what a page starts as it is built may ask for the page at once, and
    /// the two would both be in the window.
    /// </summary>
    public sealed class PageRenderer
    {
        private readonly Action _build;
        private readonly Queue<Action> _after = new Queue<Action>();
        private bool _isBuilding;
        private bool _again;

        public PageRenderer(Action build) => _build = build;

        /// <summary>Builds the page; asked for while it is being built, once more when it is there.</summary>
        public void Render()
        {
            _again = true;
            if (_isBuilding) return;
            while (_again)
            {
                _again = false;
                _isBuilding = true;
                try
                {
                    _build();
                }
                finally
                {
                    _isBuilding = false;
                }
            }

            while (_after.Count > 0) _after.Dequeue()();
        }

        /// <summary>What the page starts: once it is in the window.</summary>
        public void After(Action start)
        {
            if (_isBuilding) _after.Enqueue(start);
            else start();
        }
    }
}
