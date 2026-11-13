using System.Net;
using System.Text;

namespace AlUmmahNow.Admin.Tests;

/// <summary>A clock tests move by hand; timers fire when it passes their due time.</summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by)
    {
        var end = _now + by;
        while (true)
        {
            var next = _timers.Where(t => t.Due != null && t.Due <= end).OrderBy(t => t.Due).FirstOrDefault();
            if (next == null) break;
            _now = next.Due!.Value;
            next.Fire();
        }

        _now = end;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new ManualTimer(this, callback, state);
        t.Change(dueTime, period);
        _timers.Add(t);
        return t;
    }

    public int ActiveTimers => _timers.Count(t => t.Due != null);

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTime _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period;

        public ManualTimer(ManualTime owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset? Due { get; private set; }

        public void Fire()
        {
            Due = _period > TimeSpan.Zero ? Due + _period : null;
            _callback(_state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._now + dueTime;
            _period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
            return true;
        }

        public void Dispose() => Due = null;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Answers requests from a function and records what was asked.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string?, HttpResponseMessage> _answer;

    public StubHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> answer) => _answer = answer;

    public List<(HttpRequestMessage Request, string? Body)> Seen { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Seen.Add((request, body));
        return _answer(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
