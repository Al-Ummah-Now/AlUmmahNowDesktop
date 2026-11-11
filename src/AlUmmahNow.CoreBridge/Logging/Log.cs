using System;
using System.Collections.Generic;
using System.Threading;

namespace AlUmmahNow.CoreBridge.Logging
{
    public enum LogLevel { Debug, Info, Warning, Error }

    public sealed class LogEntry
    {
        public LogEntry(DateTimeOffset time, LogLevel level, string category, string message, string? exception)
        {
            Time = time;
            Level = level;
            Category = category;
            Message = message;
            Exception = exception;
        }

        public DateTimeOffset Time { get; }
        public LogLevel Level { get; }
        public string Category { get; }

        /// <summary>Already scrubbed of tokens, codes and email addresses.</summary>
        public string Message { get; }

        /// <summary>The scrubbed exception text, if any.</summary>
        public string? Exception { get; }

        public override string ToString() =>
            Time.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture)
            + " " + Level.ToString().ToUpperInvariant() + " [" + Category + "] " + Message
            + (Exception == null ? "" : Environment.NewLine + Exception);
    }

    /// <summary>A destination for log entries: a file, stderr, the debugger, a test list.</summary>
    public interface ILogSink
    {
        void Write(LogEntry entry);
    }

    /// <summary>
    /// The app's one logger. Shared code logs through <see cref="For"/>; each platform adds its sinks
    /// at startup. Messages are scrubbed before any sink sees them. Never log typed search text,
    /// titles the user is typing, tokens or email addresses on purpose: the scrubber is a safety net.
    /// </summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static ILogSink[] _sinks = Array.Empty<ILogSink>();

        public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

        public static void AddSink(ILogSink sink)
        {
            lock (Gate)
            {
                var list = new List<ILogSink>(_sinks) { sink };
                _sinks = list.ToArray();
            }
        }

        public static void RemoveSink(ILogSink sink)
        {
            lock (Gate)
            {
                var list = new List<ILogSink>(_sinks);
                list.Remove(sink);
                _sinks = list.ToArray();
            }
        }

        public static Logger For(string category) => new Logger(category);

        internal static void Write(LogLevel level, string category, string message, Exception? exception)
        {
            if (level < MinimumLevel) return;
            var sinks = Volatile.Read(ref _sinks);
            if (sinks.Length == 0) return;
            var entry = new LogEntry(DateTimeOffset.Now, level, category, LogScrubber.Scrub(message),
                exception == null ? null : LogScrubber.Scrub(exception.ToString()));
            foreach (var sink in sinks)
            {
                try
                {
                    sink.Write(entry);
                }
#pragma warning disable CA1031 // A broken sink must never break the app.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }
        }
    }

    public sealed class Logger
    {
        internal Logger(string category) => Category = category;

        public string Category { get; }

        public void Debug(string message) => Log.Write(LogLevel.Debug, Category, message, null);
        public void Info(string message) => Log.Write(LogLevel.Info, Category, message, null);
        public void Warn(string message, Exception? exception = null) => Log.Write(LogLevel.Warning, Category, message, exception);
        public void Error(string message, Exception? exception = null) => Log.Write(LogLevel.Error, Category, message, exception);
    }
}
