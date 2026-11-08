using System;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge
{
    public sealed class UploadRequest
    {
        public UploadRequest(string filePath, long size, long offset)
        {
            FilePath = filePath;
            Size = size;
            Offset = offset;
        }

        public string FilePath { get; }
        public long Size { get; }

        /// <summary>Bytes already accepted by the server; the upload resumes from here.</summary>
        public long Offset { get; }
    }

    /// <summary>Resumable TUS upload of one file. Backed by aun-upload once AlUmmahNowCore ships.</summary>
    public interface IUploadEngine
    {
        /// <param name="progress">Receives the total bytes accepted so far.</param>
        Task UploadAsync(UploadRequest request, IProgress<long> progress, CancellationToken cancellationToken);
    }
}
