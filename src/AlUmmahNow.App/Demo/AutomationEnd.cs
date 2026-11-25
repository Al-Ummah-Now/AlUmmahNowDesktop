using System;
using System.IO;

namespace AlUmmahNow.App.Demo
{
    /// <summary>
    /// When a CI scenario ends that waits for something outside the app (<c>AUN_END_FILE</c>): its own time has to be over,
    /// and the script has written the file. A limit ends it too, and then it is judged as it is. Never in a normal launch.
    /// </summary>
    public sealed class AutomationEnd
    {
        public static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

        private readonly string? _file;
        private readonly Func<DateTimeOffset> _now;
        private DateTimeOffset? _over;

        public AutomationEnd(string? file, Func<DateTimeOffset> now)
        {
            _file = file;
            _now = now;
        }

        public static AutomationEnd FromEnvironment() =>
            new AutomationEnd(Environment.GetEnvironmentVariable("AUN_END_FILE"), () => DateTimeOffset.UtcNow);

        /// <summary>Asked when the scenario's own time is over, and again until it is false.</summary>
        public bool Waits()
        {
            if (string.IsNullOrEmpty(_file) || File.Exists(_file)) return false;
            _over ??= _now();
            return _now() - _over.Value < Limit;
        }
    }
}
