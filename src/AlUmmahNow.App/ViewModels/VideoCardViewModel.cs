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

        public string Views => _localizer.Views(Video.Views);

        /// <summary>
        /// The card's second meta line under the channel: views and duration («١٢٫٨ ألف مشاهدة • ٤:٠٠»). The channel
        /// gets its own line so a compact Arabic count, a word longer than the full number, never pushes the duration
        /// out of a narrow card. Join isolates each part so numbers cannot reorder across them.
        /// </summary>
        public string Stats => _localizer.Join(new[] { Views, Duration }.Where(x => x.Length > 0));
    }
}
