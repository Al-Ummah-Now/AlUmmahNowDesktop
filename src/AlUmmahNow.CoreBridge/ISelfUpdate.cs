using System;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge
{
    /// <summary>An update that must not be installed. <see cref="Reason"/> is the code the server is told.</summary>
    public sealed class UpdateRefusedException : Exception
    {
        public UpdateRefusedException(string reason) : base("update refused: " + reason) => Reason = reason;

        public string Reason { get; }
    }

    /// <summary>A release whose signed manifest was checked: its version and notes are the manifest's, not the answer's.</summary>
    public sealed class VerifiedUpdate
    {
        public VerifiedUpdate(string version, string tag, string notesEn, string notesAr)
        {
            Version = version;
            Tag = tag;
            NotesEn = notesEn;
            NotesAr = notesAr;
        }

        public string Version { get; }
        public string Tag { get; }
        public string NotesEn { get; }
        public string NotesAr { get; }
    }

    /// <summary>
    /// Installing in place (wiki AUN-APP-UPDATES §5.3). Nothing is installed that was not verified by the core under the keys
    /// trusted at that moment, and what is staged is verified again right before it is applied.
    /// </summary>
    public interface ISelfUpdate
    {
        /// <summary>The release's manifest, signature and contents checked.</summary>
        /// <exception cref="UpdateRefusedException">It must not be installed.</exception>
        /// <exception cref="UpdateCheckFailedException">It could not be reached.</exception>
        Task<VerifiedUpdate> VerifyAsync(string tag, string version, CancellationToken cancellationToken = default);

        /// <summary>Downloads the release's package, checks it and stages it. Installs nothing.</summary>
        /// <exception cref="UpdateRefusedException">It must not be installed.</exception>
        /// <exception cref="UpdateCheckFailedException">The network, or the disk.</exception>
        Task<VerifiedUpdate> StageAsync(string tag, string version, IProgress<double>? progress, CancellationToken cancellationToken = default);

        /// <summary>What is staged, checked again from the start: its manifest under the keys trusted now, and the file.</summary>
        /// <exception cref="UpdateRefusedException">It is no longer to be installed.</exception>
        void VerifyStaged();

        /// <summary>What is staged is not installed, now or later.</summary>
        void Discard();

        /// <summary>The app is open a second time. The updater would end that one too, whatever it is doing.</summary>
        bool IsOpenElsewhere { get; }

        /// <summary>
        /// The app is closing and its work has stopped: what is staged is installed once it has ended, and the app then started
        /// again if asked. Only right after <see cref="VerifyStaged"/>.
        /// </summary>
        void ApplyOnExit(bool restart);

        /// <summary>Tells the server that an update was refused. Never throws.</summary>
        Task ReportAsync(string tag, string reason, CancellationToken cancellationToken = default);
    }
}
