using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge
{
    /// <summary>Public catalog reads. Backed by aun-api once AlUmmahNowCore ships.</summary>
    public interface ICatalogClient
    {
        Task<VideoPage> ListVideosAsync(VideoQuery query, CancellationToken cancellationToken = default);
        Task<Video> GetVideoAsync(string id, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<SubtitleTrack>> GetSubtitleTracksAsync(string videoId, CancellationToken cancellationToken = default);

        /// <summary>Fetches a text resource such as an HLS playlist or a VTT file.</summary>
        Task<string> GetTextAsync(string url, CancellationToken cancellationToken = default);
    }

    public sealed class CatalogException : System.Exception
    {
        public CatalogException(string message, System.Exception? inner = null) : base(message, inner) { }
    }
}
