using System;

namespace AlUmmahNow.App.Media
{
    public enum PlayerState { Idle, Loading, Playing, Paused, Ended, Failed }

    /// <summary>Implemented per platform: Windows.Media.Playback, GStreamer, Tizen player.</summary>
    /// <remarks>Implementations raise events on the UI thread.</remarks>
    public interface IMediaPlayer
    {
        event EventHandler? StateChanged;
        event EventHandler? PositionChanged;

        PlayerState State { get; }
        double Position { get; }
        double Duration { get; }
        double Rate { get; set; }

        /// <summary>Opens a URL, starting at the given position and playing automatically if asked.</summary>
        void Open(string url, double startPosition, bool autoPlay);
        void Play();
        void Pause();
        void Seek(double position);
        void Stop();
    }

    public enum MediaCommand { Play, Pause, Toggle, Next, Previous, SkipForward, SkipBack }

    /// <summary>OS media-session integration: SMTC on Windows, MPRIS on Linux.</summary>
    public interface IMediaSession
    {
        event EventHandler<MediaCommand>? Command;
        void SetMetadata(string title, string artist, string? artworkUrl, double duration);
        void SetPlayback(PlayerState state, double position, double rate);
        void Clear();
    }

    public sealed class NullMediaSession : IMediaSession
    {
        public event EventHandler<MediaCommand>? Command { add { } remove { } }
        public void SetMetadata(string title, string artist, string? artworkUrl, double duration) { }
        public void SetPlayback(PlayerState state, double position, double rate) { }
        public void Clear() { }
    }
}
