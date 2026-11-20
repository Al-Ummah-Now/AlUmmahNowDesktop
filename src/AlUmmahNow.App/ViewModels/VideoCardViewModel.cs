using System.Linq;
using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge;

namespace AlUmmahNow.App.ViewModels
{
    /// <summary>One video tile. Text getters read the active language; shells rebuild on language change.</summary>
    public sealed class VideoCardViewModel
    {
        private readonly Localizer _localizer;

        public VideoCardViewModel(Video video, Localizer localizer)
        {
            Video = video;
            _localizer = localizer;
        }

        public Video Video { get; }
        public string Id => Video.Id;
        public string Title => Video.Title.For(_localizer.Language);
        public string Channel => Video.ChannelName;
        public string? ThumbnailUrl => Video.ThumbnailUrl;
        public string Duration => Video.DurationSeconds is { } d ? _localizer.Duration(d) : "";

        /// <summary>Channel, views and duration, each isolated so numbers cannot reorder across segments.</summary>
        // Join isolates each part (direction), so the parts go in as they are.
        public string Meta => _localizer.Join(new[] { Channel, Views, Duration }.Where(x => x.Length > 0));

        public string Views => _localizer.Format("video.views", _localizer.Number(Video.Views));
    }
}
