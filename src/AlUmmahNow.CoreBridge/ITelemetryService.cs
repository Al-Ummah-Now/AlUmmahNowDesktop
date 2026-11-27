using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge
{
    /// <summary>The user's telemetry consent choice. Defaults to <see cref="Off"/>.</summary>
    public enum TelemetryConsent
    {
        Off = 0,
        CrashOnly = 1,
        Full = 2,
    }

    /// <summary>Breadcrumb severity level attached to crash reports.</summary>
    public enum TelemetryBreadcrumbSeverity
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
    }

    /// <summary>
    /// Opt-in, consent-gated telemetry and crash reporting backed by <c>aun-telemetry</c>.
    /// Nothing is recorded, persisted, or sent while <see cref="Consent"/> is <see cref="TelemetryConsent.Off"/>.
    /// Turning consent off immediately blocks all collection and purges any buffered events from memory and disk.
    /// </summary>
    public interface ITelemetryService : IDisposable
    {
        TelemetryConsent Consent { get; }
        int PendingCount { get; }

        /// <summary>True when the most recent opt-out could not purge the on-disk buffer (collection is still blocked).</summary>
        bool LastPurgeFailed { get; }

        /// <summary>Raised when <see cref="LastPurgeFailed"/> changes (including after deferred in-flight flush cleanup).</summary>
        event Action<bool>? PurgeFailureChanged;

        /// <summary>
        /// Updates telemetry consent. Returns <c>true</c> when the native state change (and disk purge on opt-out)
        /// succeeded. Opting out always disables collection immediately even if disk purge fails.
        /// </summary>
        bool SetConsent(TelemetryConsent consent);
        void Track(string name, IReadOnlyDictionary<string, object>? properties = null);
        void TrackError(string code, IReadOnlyDictionary<string, object>? properties = null);
        void AddBreadcrumb(string category, string message, TelemetryBreadcrumbSeverity level = TelemetryBreadcrumbSeverity.Info);
        void ReportCrash(string kind, string message, string? stack = null, bool fromPreviousSession = false);
        void Persist();
        Task FlushAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>No-op telemetry service used in demo mode, tests, or platforms without native core.</summary>
    public sealed class NullTelemetryService : ITelemetryService
    {
        public static readonly NullTelemetryService Instance = new NullTelemetryService();

        private NullTelemetryService() { }

        public TelemetryConsent Consent => TelemetryConsent.Off;
        public int PendingCount => 0;
        public bool LastPurgeFailed => false;
        public event Action<bool>? PurgeFailureChanged { add { } remove { } }

        public bool SetConsent(TelemetryConsent consent) => consent == TelemetryConsent.Off;
        public void Track(string name, IReadOnlyDictionary<string, object>? properties = null) { }
        public void TrackError(string code, IReadOnlyDictionary<string, object>? properties = null) { }
        public void AddBreadcrumb(string category, string message, TelemetryBreadcrumbSeverity level = TelemetryBreadcrumbSeverity.Info) { }
        public void ReportCrash(string kind, string message, string? stack = null, bool fromPreviousSession = false) { }
        public void Persist() { }
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }
}
