using System;
using System.Globalization;

namespace AlUmmahNow.App.Demo
{
    /// <summary>
    /// "Today" for the demo data (Studio's demo backend and the viewer's demo catalog). CI capture jobs pin it with
    /// <c>AUN_DEMO_TODAY=yyyy-MM-dd</c>, so every frame of every job shows the same dates whenever the job runs; frames
    /// shot either side of midnight UTC no longer disagree. Without the variable the demo follows the real clock.
    /// </summary>
    public static class DemoClock
    {
        public const string Variable = "AUN_DEMO_TODAY";

        /// <summary>The pinned day in <paramref name="value"/> (yyyy-MM-dd, UTC), or null when it is empty or invalid.</summary>
        public static DateTime? Parse(string? value) =>
            DateTime.TryParseExact((value ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day)
                ? DateTime.SpecifyKind(day.Date, DateTimeKind.Utc)
                : (DateTime?)null;

        /// <summary>The demo's today (UTC midnight): the pinned day, else the real one.</summary>
        public static DateTime Today => Parse(Environment.GetEnvironmentVariable(Variable)) ?? DateTime.UtcNow.Date;

        /// <summary>The demo's now: noon of the pinned day, else the real clock.</summary>
        public static DateTimeOffset Now =>
            Parse(Environment.GetEnvironmentVariable(Variable)) is { } day
                ? new DateTimeOffset(day.AddHours(12), TimeSpan.Zero)
                : DateTimeOffset.UtcNow;
    }
}
