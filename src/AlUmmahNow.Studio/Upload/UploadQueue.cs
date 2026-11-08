using System.Text.Json;
using System.Text.Json.Serialization;
using AlUmmahNow.CoreBridge;

namespace AlUmmahNow.Studio.Upload;

public enum UploadStatus { Queued, Uploading, Paused, Completed, Failed }

public sealed class UploadItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; set; } = "";
    public long Size { get; set; }
    public long BytesSent { get; set; }
    public UploadStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }

    [JsonIgnore]
    public double Fraction => Size > 0 ? (double)BytesSent / Size : 0;
}

/// <summary>Batch upload queue: bounded parallelism, retry with backoff, and state that survives restarts.</summary>
public sealed class UploadQueue
{
    private readonly IUploadEngine _engine;
    private readonly string _statePath;
    private readonly int _maxParallel;
    private readonly int _maxAttempts;
    private readonly Func<int, TimeSpan> _backoff;
    private readonly object _gate = new();
    private readonly List<UploadItem> _items = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = new();

    public UploadQueue(IUploadEngine engine, string statePath, int maxParallel = 3, int maxAttempts = 5, Func<int, TimeSpan>? backoff = null)
    {
        _engine = engine;
        _statePath = statePath;
        _maxParallel = Math.Max(1, maxParallel);
        _maxAttempts = maxAttempts;
        _backoff = backoff ?? (attempt => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt))));
        Load();
    }

    /// <summary>Raised from worker threads when any item changes.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<UploadItem> Snapshot()
    {
        lock (_gate) return _items.ToList();
    }

    /// <summary>Adds a file unless the same path and size is already queued. Returns the item, or null.</summary>
    public UploadItem? Enqueue(string path, long size)
    {
        UploadItem item;
        lock (_gate)
        {
            if (_items.Any(i => i.FilePath == path && i.Size == size)) return null;
            item = new UploadItem { FilePath = path, Size = size };
            _items.Add(item);
            Save();
        }

        Notify();
        Pump();
        return item;
    }

    public void Pause(string id)
    {
        lock (_gate)
        {
            var item = _items.First(i => i.Id == id);
            if (item.Status == UploadStatus.Completed) return;
            item.Status = UploadStatus.Paused;
            if (_running.TryGetValue(id, out var cts)) cts.Cancel();
            Save();
        }

        Notify();
    }

    public void Resume(string id)
    {
        lock (_gate)
        {
            var item = _items.First(i => i.Id == id);
            if (item.Status != UploadStatus.Paused && item.Status != UploadStatus.Failed) return;
            item.Status = UploadStatus.Queued;
            item.Attempts = 0;
            item.Error = null;
            Save();
        }

        Notify();
        Pump();
    }

    /// <summary>Starts as many queued items as the parallel limit allows.</summary>
    public void Pump()
    {
        lock (_gate)
        {
            foreach (var item in _items.Where(i => i.Status == UploadStatus.Queued).ToList())
            {
                if (_running.Count >= _maxParallel) break;
                var cts = new CancellationTokenSource();
                _running[item.Id] = cts;
                item.Status = UploadStatus.Uploading;
                _ = Task.Run(() => RunAsync(item, cts));
            }

            Save();
        }
    }

    /// <summary>Completes when nothing is running or waiting.</summary>
    public async Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_running.Count == 0 && _items.All(i => i.Status != UploadStatus.Queued)) return;
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    private async Task RunAsync(UploadItem item, CancellationTokenSource cts)
    {
        var retry = false;
        try
        {
            var request = new UploadRequest(item.FilePath, item.Size, item.BytesSent);
            var progress = new SynchronousProgress(sent =>
            {
                lock (_gate) item.BytesSent = sent;
                Notify();
            });
            await _engine.UploadAsync(request, progress, cts.Token);
            lock (_gate)
            {
                item.BytesSent = item.Size;
                item.Status = UploadStatus.Completed;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Pause set the status; progress so far is kept for resume.
        }
#pragma warning disable CA1031 // Any engine failure must fail this item, never the queue.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            lock (_gate)
            {
                item.Attempts++;
                item.Error = ex.Message;
                if (item.Attempts >= _maxAttempts) item.Status = UploadStatus.Failed;
                else retry = true;
            }
        }

        if (retry) await Task.Delay(_backoff(item.Attempts));

        lock (_gate)
        {
            _running.Remove(item.Id);
            cts.Dispose();
            if (retry && item.Status == UploadStatus.Uploading) item.Status = UploadStatus.Queued;
            Save();
        }

        Notify();
        Pump();
    }

    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);

    private void Load()
    {
        if (!File.Exists(_statePath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<UploadItem>>(File.ReadAllText(_statePath)) ?? new List<UploadItem>();
            // Anything mid-flight when the app stopped resumes from its saved offset.
            foreach (var item in loaded.Where(i => i.Status == UploadStatus.Uploading)) item.Status = UploadStatus.Queued;
            _items.AddRange(loaded);
        }
        catch (JsonException)
        {
            // A corrupt state file starts an empty queue; files on disk are untouched.
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = _statePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_items));
        File.Move(temp, _statePath, overwrite: true);
    }

    private sealed class SynchronousProgress : IProgress<long>
    {
        private readonly Action<long> _report;
        public SynchronousProgress(Action<long> report) => _report = report;
        public void Report(long value) => _report(value);
    }
}
