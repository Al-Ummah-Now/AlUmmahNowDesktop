using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge.Studio
{
    public enum UploadPhase { Queued, Waiting, Hashing, Starting, Uploading, Finishing, Paused, Done, Failed, Cancelled }

    /// <summary>A file to upload and the metadata sent when the upload starts.</summary>
    public sealed class UploadSpec
    {
        public string FilePath { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public string ChannelId { get; set; } = "";
        public bool IsPublic { get; set; }
        public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
        public string? Category { get; set; }

        /// <summary>Higher uploads first.</summary>
        public int Priority { get; set; }
    }

    /// <summary>One upload's state (no secrets).</summary>
    public sealed class UploadSnapshot
    {
        public UploadSnapshot(string id, string filePath, string title, UploadPhase phase, long sentBytes, long totalBytes, string? videoId, string? error)
        {
            Id = id;
            FilePath = filePath;
            Title = title;
            Phase = phase;
            SentBytes = sentBytes;
            TotalBytes = totalBytes;
            VideoId = videoId;
            Error = error;
        }

        public string Id { get; }
        public string FilePath { get; }
        public string Title { get; }
        public UploadPhase Phase { get; }
        public long SentBytes { get; }
        public long TotalBytes { get; }

        /// <summary>Set once the server created the video.</summary>
        public string? VideoId { get; }

        public string? Error { get; }

        public double Fraction => TotalBytes > 0 ? Math.Min(1, (double)SentBytes / TotalBytes) : 0;

        public bool IsActive => Phase != UploadPhase.Done && Phase != UploadPhase.Failed && Phase != UploadPhase.Cancelled && Phase != UploadPhase.Paused;
    }

    /// <summary>
    /// The resumable upload queue of one account (aun-upload). Uploads survive app restarts: a new
    /// service for the same account and store continues where they stopped. <see cref="Changed"/>
    /// fires on a background thread with the upload id.
    /// </summary>
    public interface IUploadService : IDisposable
    {
        event EventHandler<string>? Changed;

        string AccountId { get; }

        IReadOnlyList<UploadSnapshot> List();

        /// <returns>The upload id.</returns>
        string Enqueue(UploadSpec spec);

        void Pause(string id);
        void Resume(string id);
        void Retry(string id);

        /// <summary>Stops the upload and discards the partial upload on the server.</summary>
        Task CancelAsync(string id);

        /// <summary>Forgets a finished, failed or cancelled upload.</summary>
        void Remove(string id);
    }
}
