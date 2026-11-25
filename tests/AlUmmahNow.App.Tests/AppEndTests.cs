using AlUmmahNow.App.Services;
using Xunit;

namespace AlUmmahNow.App.Tests;

/// <summary>The one owner of the app's end: in order, once, in bounded time, and Restart now never ends with nothing.</summary>
public sealed class AppEndTests
{
    private readonly List<string> _log = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<CancellationToken, Task>? _stop;
    private Func<bool> _handOver = () => true;
    private bool _restart;

    private void Add(string what)
    {
        lock (_log) _log.Add(what);
    }

    private AppEnd End(TimeSpan? limit = null) => new(
        () => Add("begin"),
        async stop =>
        {
            if (_stop != null) await _stop(stop);
            Add("stop");
        },
        () =>
        {
            Add("hand over");
            return _handOver();
        },
        () => _restart,
        () => Add("start again"),
        () =>
        {
            Add("exit");
            _exited.TrySetResult();
        },
        stopLimit: limit, handOverLimit: limit);

    private string Done => string.Join(", ", _log);

    private async Task AskedAsync(AppEnd end)
    {
        await end.End(EndKind.Asked);
        await _exited.Task;
    }

    [Fact]
    public async Task Work_stops_then_the_update_is_handed_over_then_the_app_exits()
    {
        await AskedAsync(End());
        Assert.Equal("begin, stop, hand over, exit", Done);
    }

    [Fact]
    public async Task The_updater_starts_the_app_again_and_not_this_one_as_well()
    {
        _restart = true;
        await AskedAsync(End());
        Assert.Equal("begin, stop, hand over, exit", Done);
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("throws")]
    [InlineData("the work cannot be stopped")]
    [InlineData("the work is late")]
    public async Task Restart_now_starts_the_app_again_when_no_updater_does(string why)
    {
        _restart = true;
        if (why == "refused") _handOver = () => false;
        else if (why == "throws") _handOver = () => throw new InvalidOperationException("no updater");
        else if (why == "the work cannot be stopped") _stop = _ => throw new InvalidOperationException("no");
        else _stop = _ => Task.Delay(Timeout.Infinite, CancellationToken.None);
        await AskedAsync(End(why == "the work is late" ? TimeSpan.FromMilliseconds(50) : null));
        Assert.EndsWith("start again, exit", Done);
        Assert.Equal(why is "refused" or "throws", Done.Contains("hand over"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Work_that_does_not_stop_installs_nothing_and_the_app_ends(bool throws)
    {
        _stop = throws ? _ => throw new InvalidOperationException("no") : stop => Task.Delay(Timeout.Infinite, stop);
        await AskedAsync(End(TimeSpan.FromMilliseconds(50)));
        Assert.Equal("begin, exit", Done);
    }

    /// <summary>Whether the updater started is not known: what is started waits for it if it did.</summary>
    [Fact]
    public async Task A_hand_over_that_is_late_never_ends_with_nothing()
    {
        _restart = true;
        using var go = new ManualResetEventSlim();
        _handOver = () =>
        {
            go.Wait();
            return true;
        };
        await AskedAsync(End(TimeSpan.FromMilliseconds(50)));
        Assert.Equal("begin, stop, hand over, start again, exit", Done);
        go.Set();
    }

    [Fact]
    public async Task The_end_of_the_session_stops_the_work_and_installs_nothing()
    {
        _restart = true;
        await End().End(EndKind.Session);
        Assert.Equal("begin, stop", Done);
    }

    [Fact]
    public async Task A_session_that_ends_during_the_end_refuses_the_update()
    {
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _stop = async _ =>
        {
            stopping.SetResult();
            await go.Task;
        };
        _restart = true;
        var end = End();
        var asked = end.End(EndKind.Asked);
        await stopping.Task;
        var session = end.End(EndKind.Session);
        go.SetResult();
        await Task.WhenAll(asked, session);
        await _exited.Task;
        Assert.Equal("begin, stop, exit", Done);
    }

    [Fact]
    public async Task A_process_that_ends_by_itself_stops_the_work_and_installs_nothing()
    {
        await End().End(EndKind.Process);
        Assert.Equal("begin, stop", Done);
    }

    [Fact]
    public async Task Ends_that_come_together_are_one()
    {
        var end = End();
        var all = Enumerable.Range(0, 8).Select(_ => Task.Run(() => end.End(EndKind.Asked))).ToList();
        await Task.WhenAll(all);
        // The process that then ends joins it
        end.EndAndWait(EndKind.Process, AppEnd.Total);
        await _exited.Task;
        Assert.Equal("begin, stop, hand over, exit", Done);
        Assert.True(end.HasBegun);
    }

    [Fact]
    public async Task A_beginning_that_fails_ends_the_app_all_the_same()
    {
        var end = new AppEnd(() => throw new InvalidOperationException("no"), _ => Task.CompletedTask, () => false, () => false, () => { }, _exited.SetResult);
        await end.End(EndKind.Asked);
        await _exited.Task;
    }

    [Fact]
    public void Waiting_for_it_is_bounded()
    {
        _stop = _ => Task.Delay(Timeout.Infinite, CancellationToken.None);
        var since = System.Diagnostics.Stopwatch.StartNew();
        End().EndAndWait(EndKind.Session, TimeSpan.FromMilliseconds(100));
        Assert.True(since.Elapsed < AppEnd.StopLimit);
    }

    [Fact]
    public void The_parts_fit_in_the_whole() => Assert.True(AppEnd.StopLimit + AppEnd.HandOverLimit <= AppEnd.Total);
}
