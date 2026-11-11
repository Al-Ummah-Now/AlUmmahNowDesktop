using System.Text.Json;
using AlUmmahNow.CoreBridge.Logging;

namespace AlUmmahNow.Studio.Videos;

public sealed class DeletedVideo
{
    public string VideoId { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string Title { get; set; } = "";
    public bool WasPublic { get; set; }
    public DateTimeOffset DeletedAt { get; set; }
}

/// <summary>
/// Studio's "Recently deleted": the videos API has no creator soft delete yet, so a delete makes the
/// video private and lists it here for 30 days; restore puts its visibility back; after 30 days, or
/// on "Delete now", the video is deleted on the server. One file per account.
/// </summary>
public sealed class RecentlyDeleted
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly Logger Logger = Log.For("trash");
    private readonly string _path;
    private List<DeletedVideo> _items;

    public RecentlyDeleted(string path)
    {
        _path = path;
        _items = Load();
    }

    public IReadOnlyList<DeletedVideo> Items => _items.OrderByDescending(i => i.DeletedAt).ToList();

    public bool Contains(string videoId) => _items.Any(i => i.VideoId == videoId);

    public void Add(DeletedVideo v)
    {
        _items.RemoveAll(i => i.VideoId == v.VideoId);
        _items.Add(v);
        Save();
    }

    public void Remove(string videoId)
    {
        if (_items.RemoveAll(i => i.VideoId == videoId) > 0) Save();
    }

    public IReadOnlyList<DeletedVideo> Due(DateTimeOffset now) => _items.Where(i => now - i.DeletedAt >= Retention).ToList();

    private List<DeletedVideo> Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<List<DeletedVideo>>(File.ReadAllText(_path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException)
        {
            Logger.Warn("recently deleted list unreadable", ex);
            return new();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_items));
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (IOException ex)
        {
            Logger.Warn("recently deleted list not saved", ex);
        }
    }
}
