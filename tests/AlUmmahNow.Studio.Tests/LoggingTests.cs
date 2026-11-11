using AlUmmahNow.CoreBridge.Logging;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

public sealed class LoggingTests
{
    private sealed class ListSink : ILogSink
    {
        public readonly List<LogEntry> Entries = new();
        public void Write(LogEntry entry) => Entries.Add(entry);
    }

    [Theory]
    [InlineData("Authorization: Bearer abc.def-ghi", "Authorization: Bearer [redacted]")]
    [InlineData("token eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiJ1In0.c2ln here", "token [redacted] here")]
    [InlineData("from amina@example.com failed", "from [email] failed")]
    [InlineData("POST token=rt-123&token_type_hint=refresh_token", "POST token=[redacted]&token_type_hint=refresh_token")]
    [InlineData("http://127.0.0.1:5/auth/callback?code=abc&state=xyz", "http://127.0.0.1:5/auth/callback?code=[redacted]&state=[redacted]")]
    [InlineData("{\"refresh_token\":\"r1\"}", "{\"refresh_token\":\"[redacted]\"}")]
    [InlineData("/api/videos?q=تفسير&limit=8", "/api/videos?q=[redacted]&limit=8")]
    [InlineData("upload 3 of 7 finished", "upload 3 of 7 finished")]
    public void ScrubsSecretsAndPersonalData(string input, string expected) => Assert.Equal(expected, LogScrubber.Scrub(input));

    [Fact]
    public void LoggerScrubsMessagesAndExceptionsAndHonoursTheLevel()
    {
        var sink = new ListSink();
        Log.AddSink(sink);
        try
        {
            var log = Log.For("test");
            log.Debug("hidden");
            log.Warn("refresh failed for a@b.co", new InvalidOperationException("Bearer secret"));
            var entry = Assert.Single(sink.Entries, e => e.Category == "test");
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.DoesNotContain("a@b.co", entry.Message);
            Assert.DoesNotContain("secret", entry.Exception);
        }
        finally
        {
            Log.RemoveSink(sink);
        }
    }

    [Fact]
    public void FileSinkRollsOver()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var sink = new FileLogSink(Path.Combine(dir, "studio.log"), maxBytes: 200, keep: 2);
            for (var i = 0; i < 20; i++) sink.Write(new LogEntry(DateTimeOffset.Now, LogLevel.Info, "t", "line " + i, null));
            Assert.True(File.Exists(Path.Combine(dir, "studio.log")));
            Assert.True(File.Exists(Path.Combine(dir, "studio.1.log")));
            Assert.True(File.Exists(Path.Combine(dir, "studio.2.log")));
            Assert.False(File.Exists(Path.Combine(dir, "studio.3.log")));
            Assert.Contains("line 19", File.ReadAllText(Path.Combine(dir, "studio.log")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>#22: code logs through Log; only the stderr sink may touch the console.</summary>
    [Fact]
    public void SourceNeverWritesToTheConsoleDirectly()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src"))) root = Path.GetDirectoryName(root)!;
        var allowed = Path.Combine(root, "src", "AlUmmahNow.CoreBridge", "Logging", "Sinks.cs");
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => f != allowed && File.ReadAllText(f).Contains("Console."))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        Assert.Empty(offenders);
    }
}
