using AlUmmahNow.CoreBridge.Studio;

namespace AlUmmahNow.Studio.Demo;

/// <summary>CI and demo mode: "uploads" that advance on a timer and never touch the network.</summary>
public sealed class SimulatedUploadService : IUploadService
{
    private readonly object _gate = new();
    private readonly List<Item> _items = new();
    private readonly Timer _timer;
    private readonly long _step;

    public SimulatedUploadService(string accountId, TimeSpan tick, long bytesPerTick = 512 * 1024)
    {
        AccountId = accountId;
        _step = bytesPerTick;
        _timer = new Timer(_ => Advance(), null, tick, tick);
    }

    public event EventHandler<string>? Changed;

    public string AccountId { get; }

    public IReadOnlyList<UploadSnapshot> List()
    {
        lock (_gate) return _items.Select(i => i.Snapshot()).ToList();
    }

    public string Enqueue(UploadSpec spec)
    {
        var size = File.Exists(spec.FilePath) ? Math.Max(1, new FileInfo(spec.FilePath).Length) : 1;
        var item = new Item { Id = Guid.NewGuid().ToString("N"), Path = spec.FilePath, Title = spec.Title, Total = size };
        lock (_gate) _items.Add(item);
        Changed?.Invoke(this, item.Id);
        return item.Id;
    }

    public void Pause(string id) => Set(id, i => { if (i.Phase != UploadPhase.Done) i.Phase = UploadPhase.Paused; });
    public void Resume(string id) => Set(id, i => { if (i.Phase == UploadPhase.Paused) i.Phase = UploadPhase.Queued; });
    public void Retry(string id) => Set(id, i => { if (i.Phase == UploadPhase.Failed) i.Phase = UploadPhase.Queued; });

    public Task CancelAsync(string id)
    {
        Set(id, i => i.Phase = UploadPhase.Cancelled);
        return Task.CompletedTask;
    }

    public void Remove(string id)
    {
        lock (_gate) _items.RemoveAll(i => i.Id == id);
        Changed?.Invoke(this, id);
    }

    public void Dispose() => _timer.Dispose();

    private void Set(string id, Action<Item> change)
    {
        lock (_gate)
        {
            if (_items.FirstOrDefault(i => i.Id == id) is { } item) change(item);
        }

        Changed?.Invoke(this, id);
    }

    private void Advance()
    {
        var changed = new List<string>();
        lock (_gate)
        {
            // Three at a time, like the real queue.
            foreach (var i in _items.Where(i => i.Phase is UploadPhase.Queued or UploadPhase.Uploading).Take(3))
            {
                i.Phase = UploadPhase.Uploading;
                i.Sent = Math.Min(i.Total, i.Sent + _step);
                if (i.Sent >= i.Total)
                {
                    i.Phase = UploadPhase.Done;
                    i.VideoId = "sim-" + i.Id[..8];
                }

                changed.Add(i.Id);
            }
        }

        foreach (var id in changed) Changed?.Invoke(this, id);
    }

    private sealed class Item
    {
        public string Id = "";
        public string Path = "";
        public string Title = "";
        public long Total;
        public long Sent;
        public UploadPhase Phase = UploadPhase.Queued;
        public string? VideoId;

        public UploadSnapshot Snapshot() => new(Id, Path, Title, Phase, Sent, Total, VideoId, null);
    }
}
