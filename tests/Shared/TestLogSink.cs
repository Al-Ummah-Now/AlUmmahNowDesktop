using AlUmmahNow.CoreBridge.Logging;

namespace AlUmmahNow.Tests.Shared;

/// <summary>
/// The log is one for the whole process and test classes run at the same time, so a plain list gets
/// the entries of every test, while it is read. This sink keeps only what the test that made it
/// writes (its own flow, with the tasks it starts), and is safe to read at any time.
/// </summary>
internal sealed class TestLogSink : ILogSink, IDisposable
{
    private static readonly AsyncLocal<TestLogSink?> Current = new();
    private readonly List<LogEntry> _entries = new();

    public TestLogSink()
    {
        Current.Value = this;
        Log.AddSink(this);
    }

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries) return _entries.ToArray();
        }
    }

    public void Write(LogEntry entry)
    {
        if (Current.Value != this) return;
        lock (_entries) _entries.Add(entry);
    }

    public void Dispose()
    {
        Log.RemoveSink(this);
        Current.Value = null;
    }
}
