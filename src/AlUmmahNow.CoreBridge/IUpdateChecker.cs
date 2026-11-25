using System;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge
{
    /// <summary>What the core says to show for one answer of the server (aun-updates).</summary>
    public enum UpdatePromptKind
    {
        None,

        /// <summary>A newer version: a bar that closes for that version.</summary>
        Notice,

        /// <summary>Below the minimum with nothing to update to: a bar that stays.</summary>
        RequiredBar,

        /// <summary>Below the minimum, and there is a version to update to: the whole window.</summary>
        Forced,
    }

    /// <summary>
    /// One answer of <c>GET /api/v1/app-config</c>, as the core decided it. There is no link in it: where a new version
    /// is downloaded is compiled into the app (wiki AUN-APP-UPDATES), and the network cannot change it.
    /// </summary>
    public sealed class UpdateAnswer
    {
        public static readonly UpdateAnswer Off = new UpdateAnswer();

        /// <summary>The server's <c>appUpdates</c> switch.</summary>
        public bool Enabled { get; set; }

        public UpdatePromptKind Kind { get; set; }

        /// <summary>The newest version; null when there is none to tell of.</summary>
        public string? Version { get; set; }

        public string? MinimumVersion { get; set; }

        /// <summary>What is new, as plain text.</summary>
        public string? NotesEn { get; set; }

        public string? NotesAr { get; set; }

        /// <summary>The admins' words on the screen of an unsupported version.</summary>
        public string? MessageEn { get; set; }

        public string? MessageAr { get; set; }

        public int CheckHours { get; set; } = 6;
    }

    /// <summary>The server could not be reached, or its answer could not be read.</summary>
    public sealed class UpdateCheckFailedException : Exception
    {
        public UpdateCheckFailedException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>Asks for the newest version and keeps what must outlive the app (aun-core's <c>UpdateChecker</c>).</summary>
    public interface IUpdateChecker
    {
        /// <exception cref="UpdateCheckFailedException">Offline, or an unreadable answer.</exception>
        Task<UpdateAnswer> CheckAsync(CancellationToken cancellationToken = default);

        /// <summary>What an earlier answer still asks for before the first check, without the network.</summary>
        UpdatePromptKind LastKnown();

        /// <summary>What to save for the next run (opaque).</summary>
        string State();
    }
}
