using System;
using System.Threading;
using System.Threading.Tasks;
using AlUmmahNow.CoreBridge.Logging;

namespace AlUmmahNow.App.Services
{
    /// <summary>How the app comes to its end.</summary>
    public enum EndKind
    {
        /// <summary>The window's close button, Quit or Restart now: the app ends itself when its work has stopped.</summary>
        Asked,

        /// <summary>The window is gone already, or the process ends by itself: the work stops, and nothing is installed unless that was decided before.</summary>
        Process,

        /// <summary>The person signs out, or the computer shuts down: nothing is installed, whatever was decided before. The updater would be cut off.</summary>
        Session,
    }

    /// <summary>
    /// The one owner of the app's end, however it comes. Always in this order, and once: whether an update may be installed is
    /// decided, the work stops (within <see cref="StopLimit"/>), an update is handed to the updater only if the work really
    /// stopped, the app starts again if that was asked for and no updater does it, and the app exits. An end that comes
    /// meanwhile joins the one that began. (As QuitCoordinator in AlUmmahNowTools.)
    /// </summary>
    public sealed class AppEnd
    {
        /// <summary>Running work stops and is saved.</summary>
        public static readonly TimeSpan StopLimit = TimeSpan.FromSeconds(10);

        /// <summary>The update is checked again and the updater started.</summary>
        public static readonly TimeSpan HandOverLimit = TimeSpan.FromSeconds(10);

        /// <summary>All an end may take that the process itself waits for.</summary>
        public static readonly TimeSpan Total = TimeSpan.FromSeconds(22);

        /// <summary>What Windows leaves an app whose session ends, before it ends it.</summary>
        public static readonly TimeSpan SessionLimit = TimeSpan.FromSeconds(4);

        private static readonly Logger Logger = Log.For("end");

        private readonly Action _begin;
        private readonly Func<CancellationToken, Task> _stop;
        private readonly Func<bool> _handOver;
        private readonly Func<bool> _isRestartAsked;
        private readonly Action _startAgain;
        private readonly Action _exit;
        private readonly Action<string> _log;
        private readonly TimeSpan _stopLimit;
        private readonly TimeSpan _handOverLimit;
        private readonly object _gate = new object();
        private readonly TaskCompletionSource<bool> _ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _hasBegun;
        private bool _isRefused;

        /// <param name="begin">On the asking thread, before anything stops: decides whether an update may be installed, and what must be told on the UI thread.</param>
        /// <param name="stop">Stops the work and waits until it is saved. A failure, and being late, install nothing.</param>
        /// <param name="handOver">Checks the update once more and starts the updater; whether it did.</param>
        /// <param name="isRestartAsked">Restart now was pressed.</param>
        /// <param name="startAgain">Starts the app beside this one, which is ending.</param>
        /// <param name="exit">Ends the app.</param>
        /// <param name="log">What happened, for CI's update test.</param>
        public AppEnd(Action begin, Func<CancellationToken, Task> stop, Func<bool> handOver, Func<bool> isRestartAsked, Action startAgain, Action exit,
            Action<string>? log = null, TimeSpan? stopLimit = null, TimeSpan? handOverLimit = null)
        {
            _begin = begin;
            _stop = stop;
            _handOver = handOver;
            _isRestartAsked = isRestartAsked;
            _startAgain = startAgain;
            _exit = exit;
            _log = log ?? (_ => { });
            _stopLimit = stopLimit ?? StopLimit;
            _handOverLimit = handOverLimit ?? HandOverLimit;
        }

        /// <summary>The end has begun: the window may go.</summary>
        public bool HasBegun
        {
            get
            {
                lock (_gate) return _hasBegun;
            }
        }

        /// <summary>Ends the app, or joins the end that began. Completes when the work has stopped and everything after it is done.</summary>
        public Task End(EndKind kind)
        {
            lock (_gate)
            {
                if (kind == EndKind.Session) _isRefused = true;
                if (_hasBegun) return _ended.Task;
                _hasBegun = true;
            }

            _log("ending " + kind);
            Try("the end's beginning", _begin);
            // Off the asking thread: a stop that blocks must not block the UI thread, which what stops may need
            _ = Task.Run(() => EndAsync(kind));
            return _ended.Task;
        }

        /// <summary>The same, for a process that is ending: waits for it, but never for longer than that.</summary>
        public void EndAndWait(EndKind kind, TimeSpan limit)
        {
            try
            {
                End(kind).Wait(limit);
            }
            catch (AggregateException ex)
            {
                Logger.Error("the end failed", ex);
            }
        }

        private async Task EndAsync(EndKind kind)
        {
            var stopped = await WithinAsync("the work did not stop", _stopLimit, _stop);
            _log(stopped ? "stopped" : "not stopped");
            bool mayHandOver;
            lock (_gate) mayHandOver = stopped && kind == EndKind.Asked && !_isRefused;
            var handedOver = false;
            if (mayHandOver)
            {
                var answer = false;
                handedOver = await WithinAsync("the update was not handed over", _handOverLimit, _ => Task.Run(() => answer = _handOver())) && answer;
                if (handedOver) _log("handed over");
            }

            bool isRefused;
            lock (_gate) isRefused = _isRefused;
            // The updater would have started the app again: without it, the app does. Restart now never ends with nothing. Also
            // when the hand-over is late: if the updater does start, what was started waits for it, and is replaced by it
            if (kind == EndKind.Asked && !handedOver && !isRefused && _isRestartAsked())
            {
                _log("starting again");
                Try("starting again", _startAgain);
            }

            _ended.TrySetResult(true);
            if (kind == EndKind.Asked) Try("the exit", _exit);
        }

        private async Task<bool> WithinAsync(string what, TimeSpan limit, Func<CancellationToken, Task> work)
        {
            using var late = new CancellationTokenSource();
            try
            {
                var working = Task.Run(() => work(late.Token));
                if (await Task.WhenAny(working, Task.Delay(limit)) != working)
                {
                    late.Cancel();
                    Logger.Warn(what + " in time");
                    return false;
                }

                await working;
                return true;
            }
#pragma warning disable CA1031 // However it goes wrong, the app ends.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Logger.Error(what, ex);
                return false;
            }
        }

        /// <summary>All of them, whatever one of them does.</summary>
        public static Action Each(params Action[] parts) => () =>
        {
            foreach (var part in parts) Try("a part of the end's beginning", part);
        };

        private static void Try(string what, Action action)
        {
            try
            {
                action();
            }
#pragma warning disable CA1031 // However it goes wrong, the app ends.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Logger.Error(what, ex);
            }
        }
    }
}
