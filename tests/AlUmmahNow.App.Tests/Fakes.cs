using AlUmmahNow.App.Media;
using AlUmmahNow.CoreBridge;

namespace AlUmmahNow.App.Tests;

public sealed class FakeCatalog : ICatalogClient
{
    public List<Video> Videos { get; } = new();
    public Dictionary<string, string> Texts { get; } = new();
    public List<SubtitleTrack> Tracks { get; } = new();
    public Func<VideoQuery, bool> FailWhen { get; set; } = _ => false;

    public Task<VideoPage> ListVideosAsync(VideoQuery q, CancellationToken ct = default)
    {
        if (FailWhen(q)) throw new CatalogException("boom");
        return Task.FromResult(new VideoPage(Videos.Where(v => q.Category == null || v.Category == q.Category).ToList(), null));
    }

    public Task<Video> GetVideoAsync(string id, CancellationToken ct = default) =>
        Videos.FirstOrDefault(v => v.Id == id) is { } v ? Task.FromResult(v) : throw new CatalogException("404");

    public Task<IReadOnlyList<SubtitleTrack>> GetSubtitleTracksAsync(string id, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SubtitleTrack>>(Tracks);

    public Task<string> GetTextAsync(string url, CancellationToken ct = default) =>
        Texts.TryGetValue(url, out var t) ? Task.FromResult(t) : throw new CatalogException("404");

    public static Video Ready(string id, string? category = null, string title = "t") => new()
    {
        Id = id, Title = new LocalizedText(title), IsPublic = true, UploadStatus = "ready",
        PlayUrl = "https://cdn/" + id + "/playlist.m3u8", Category = category,
    };
}

public sealed class FakePlayer : IMediaPlayer
{
    public event EventHandler? StateChanged;
    public event EventHandler? PositionChanged;
    public PlayerState State { get; private set; } = PlayerState.Idle;
    public double Position { get; set; }
    public double Duration { get; set; } = 100;
    public double Rate { get; set; } = 1;
    public List<(string Url, double Start, bool Auto)> Opened { get; } = new();
    public bool Stopped { get; private set; }

    public void Open(string url, double start, bool auto)
    {
        Opened.Add((url, start, auto));
        Position = start;
        Set(auto ? PlayerState.Playing : PlayerState.Paused);
    }

    public void Play() => Set(PlayerState.Playing);
    public void Pause() => Set(PlayerState.Paused);
    public void Seek(double p) => Tick(p);
    public void Stop() { Stopped = true; Set(PlayerState.Idle); }
    public void Tick(double p) { Position = p; PositionChanged?.Invoke(this, EventArgs.Empty); }
    public void Set(PlayerState s) { State = s; StateChanged?.Invoke(this, EventArgs.Empty); }
}

public sealed class FakeSession : IMediaSession
{
    public event EventHandler<MediaCommand>? Command;
    public string? Title { get; private set; }
    public PlayerState? LastState { get; private set; }
    public bool Cleared { get; private set; }
    public void Raise(MediaCommand c) => Command?.Invoke(this, c);
    public void SetMetadata(string title, string artist, string? art, double d) => Title = title;
    public void SetPlayback(PlayerState s, double p, double r) => LastState = s;
    public void Clear() => Cleared = true;
}
