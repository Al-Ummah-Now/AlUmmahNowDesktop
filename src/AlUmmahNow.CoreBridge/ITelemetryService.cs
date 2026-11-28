using System;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge
{
    /// <summary>
    /// Opt-in diagnostics and crash reports. Off until the person turns it on in Settings. Collection, scrubbing,
    /// the on-disk buffer and delivery all live in aun-core's telemetry; the apps only pass consent, crashes and
    /// a flush. Never throws: reporting a problem must not cause one.
    /// </summary>
    public interface ITelemetryService
    {
        bool Enabled { get; }

        /// <summary>On: crash reports and diagnostics. Off: nothing is kept, and what was buffered is deleted.</summary>
        void SetEnabled(bool enabled);

        /// <summary>Records an unhandled exception and writes it to disk at once, so it survives the process ending.</summary>
        void ReportCrash(Exception exception);

        /// <summary>Sends what is buffered. Safe to call at any time; does nothing while off or offline.</summary>
        Task FlushAsync();
    }

    /// <summary>Used where the core library is not available (Tizen, tests, demo mode).</summary>
    public sealed class NullTelemetryService : ITelemetryService
    {
        public static readonly NullTelemetryService Instance = new NullTelemetryService();

        public bool Enabled => false;
        public void SetEnabled(bool enabled) { }
        public void ReportCrash(Exception exception) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    /// <summary>Hooks the process-wide unhandled-exception events. UI frameworks add their own thread's hook.</summary>
    public static class CrashHooks
    {
        private static ITelemetryService? _installed;

        public static void Install(ITelemetryService telemetry)
        {
            if (_installed != null) return;
            _installed = telemetry;
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex) telemetry.ReportCrash(ex);
            };
            // Fired when a faulted task is collected without being observed. It does not end the process, but it is a
            // bug we want to see.
            TaskScheduler.UnobservedTaskException += (_, e) => telemetry.ReportCrash(e.Exception);
        }
    }
}
