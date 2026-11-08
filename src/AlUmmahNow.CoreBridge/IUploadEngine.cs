using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge
{
    /// <summary>Server-side upload state, saved so an upload can resume after a restart.</summary>
    public sealed class UploadSession
    {
        public string VideoId { get; set; } = "";
        public string Location { get; set; } = "";
        public int ChunkSize { get; set; }

        /// <summary>True once the upload bypasses the expired proxy session and goes straight to storage.</summary>
        public bool Direct { get; set; }
    }

    public sealed class UploadRequest
    {
        public UploadRequest(string filePath, long size, long offset, UploadSession? session = null, Action<UploadSession>? sessionChanged = null)
        {
            FilePath = filePath;
            Size = size;
            Offset = offset;
            Session = session;
            SessionChanged = sessionChanged;
        }

        public string FilePath { get; }
        public long Size { get; }

        /// <summary>Bytes the queue believes are accepted. The server's offset wins when they differ.</summary>
        public long Offset { get; }

        public UploadSession? Session { get; }

        /// <summary>Called whenever the session should be saved.</summary>
        public Action<UploadSession>? SessionChanged { get; }
    }

    /// <summary>Resumable TUS upload of one file. Backed by aun-upload once AlUmmahNowCore exposes it.</summary>
    public interface IUploadEngine
    {
        /// <param name="progress">Receives the total bytes accepted so far.</param>
        Task UploadAsync(UploadRequest request, IProgress<long> progress, CancellationToken cancellationToken);
    }

    public sealed class ChannelInfo
    {
        public ChannelInfo(string id, string name, bool isMain)
        {
            Id = id;
            Name = name;
            IsMain = isMain;
        }

        public string Id { get; }
        public string Name { get; }
        public bool IsMain { get; }
    }

    public interface IChannelClient
    {
        Task<IReadOnlyList<ChannelInfo>> ListMineAsync(CancellationToken cancellationToken = default);
    }
}
