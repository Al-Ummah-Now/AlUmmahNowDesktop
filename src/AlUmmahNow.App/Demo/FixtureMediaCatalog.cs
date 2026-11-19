using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AlUmmahNow.CoreBridge;

namespace AlUmmahNow.App.Demo
{
    /// <summary>
    /// CI's watch scenario (<c>AUN_FIXTURE_MEDIA=&lt;folder&gt;</c>, automation <c>watch:fixture</c>): the video
    /// <see cref="VideoId"/> plays <c>watch.mp4</c> with captions from <c>watch.vtt</c> in that folder, so the
    /// playback check never depends on a remote stream opening. Everything else (the home rails, any
    /// other video) still comes from the real catalog. Never used in a normal launch.
    /// </summary>
    public sealed class FixtureMediaCatalog : ICatalogClient
    {
        public const string VideoId = "fixture";

        private readonly ICatalogClient _inner;
        private readonly string _video;
        private readonly string _captions;

        public FixtureMediaCatalog(ICatalogClient inner, string folder)
        {
            _inner = inner;
            var root = Path.GetFullPath(folder);
            _video = new Uri(Path.Combine(root, "watch.mp4")).AbsoluteUri;
            _captions = new Uri(Path.Combine(root, "watch.vtt")).AbsoluteUri;
        }

        public Task<VideoPage> ListVideosAsync(VideoQuery query, CancellationToken cancellationToken = default) =>
            _inner.ListVideosAsync(query, cancellationToken);

        public Task<Video> GetVideoAsync(string id, CancellationToken cancellationToken = default) =>
            id == VideoId
                ? Task.FromResult(new Video
                {
                    Id = VideoId,
                    Title = new LocalizedText("Watch scenario", new Dictionary<string, string> { ["en"] = "Watch scenario", ["ar"] = "مشهد المشاهدة" }),
                    Description = new LocalizedText(""),
                    ChannelName = "Al Ummah Now",
                    PlayUrl = _video,
                    DurationSeconds = 40,
                    IsPublic = true,
                    UploadStatus = "ready",
                })
                : _inner.GetVideoAsync(id, cancellationToken);

        public Task<IReadOnlyList<SubtitleTrack>> GetSubtitleTracksAsync(string videoId, CancellationToken cancellationToken = default) =>
            videoId == VideoId
                ? Task.FromResult<IReadOnlyList<SubtitleTrack>>(new[] { new SubtitleTrack("en", "English", _captions, true) })
                : _inner.GetSubtitleTracksAsync(videoId, cancellationToken);

        public Task<string> GetTextAsync(string url, CancellationToken cancellationToken = default)
        {
            if (url == _captions) return Task.FromResult(File.ReadAllText(new Uri(url).LocalPath));
            // The fixture is a plain MP4, not an HLS master: no renditions to list (Auto only).
            if (url == _video) return Task.FromException<string>(new CatalogException("fixture video has no renditions"));
            return _inner.GetTextAsync(url, cancellationToken);
        }
    }
}
