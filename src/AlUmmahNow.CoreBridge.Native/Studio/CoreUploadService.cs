using System.Security.Cryptography;
using System.Text;
using AlUmmahNow.Core;
using AlUmmahNow.CoreBridge.Studio;
using CoreUploadRequest = AlUmmahNow.Core.UploadRequest;

namespace AlUmmahNow.CoreBridge.Native.Studio;

/// <summary>
/// <see cref="IUploadService"/> on aun-upload's UploadEngine: resumable TUS through the videos API,
/// three at a time, state in <c>storeDir</c> so uploads continue after a restart. The engine locks
/// its store, so a second Studio window for the same account gets a clear error instead of racing.
/// </summary>
public sealed class CoreUploadService : IUploadService
{
    private readonly UploadEngine _engine;
    private readonly string _lock;
    private int _stopped;

    public CoreUploadService(CoreStudioAccounts accounts, Uri videosApi, string userAgent, string storeDir, string accountId)
    {
        AccountId = accountId;
        _lock = LockPath(storeDir, accountId);
        try
        {
            var client = accounts.Manager.ApiClient(new ApiClientConfig(videosApi.AbsoluteUri.TrimEnd('/'), userAgent, null, 60_000, 4, AunClient.FromUserAgent(userAgent)), accountId);
            var config = AunCoreMethods.UploadConfigDefault();
            Directory.CreateDirectory(storeDir);
            _engine = new UploadEngine(client, storeDir, accountId, config, new SystemConnectivity());
            _engine.AddListener(new Listener(this));
        }
        catch (AunException ex)
        {
            throw CoreApiTransport.Map(ex, "upload queue");
        }
    }

    /// <summary>On an engine of the tests.</summary>
    internal CoreUploadService(UploadEngine engine, string storeDir, string accountId)
    {
        AccountId = accountId;
        _lock = LockPath(storeDir, accountId);
        _engine = engine;
    }

    public event EventHandler<string>? Changed;

    public string AccountId { get; }

    public IReadOnlyList<UploadSnapshot> List() => Call(() => _engine.List().Select(ToSnapshot).ToList());

    public string Enqueue(UploadSpec spec) => Call(() => _engine.Enqueue(new CoreUploadRequest(
        spec.FilePath,
        spec.Title,
        spec.Description,
        null,
        spec.ChannelId,
        spec.IsPublic,
        spec.Tags.Count == 0 ? null : string.Join(",", spec.Tags),
        spec.Category,
        new Dictionary<string, string>(),
        new Dictionary<string, string>(),
        spec.Priority)));

    public void Pause(string id) => Call(() => _engine.Pause(id));
    public void Resume(string id) => Call(() => _engine.Resume(id));
    public void Retry(string id) => Call(() => _engine.Retry(id));
    public void Remove(string id) => Call(() => _engine.Remove(id));

    public async Task CancelAsync(string id)
    {
        try
        {
            await _engine.Cancel(id).ConfigureAwait(false);
        }
        catch (AunException ex)
        {
            throw CoreApiTransport.Map(ex, "cancel upload");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        // It returns at once; the engine gives its store's lock back when the last transfer has written what it has
        _engine.Shutdown();
        try
        {
            while (!IsFree(_lock)) await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _engine.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _engine.Shutdown();
        _engine.Dispose();
    }

    /// <summary>The file the engine locks (aun-upload, Store::lock_path).</summary>
    internal static string LockPath(string storeDir, string accountId) =>
        Path.Combine(storeDir, ".account-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(accountId)).AsSpan(0, 8)) + ".lock");

    /// <summary>Nobody holds the engine's lock on it.</summary>
    internal static bool IsFree(string path)
    {
        if (!File.Exists(path)) return true;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // A lock on the file's bytes
                using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                file.Lock(0, long.MaxValue);
                file.Unlock(0, long.MaxValue);
            }
            else
            {
                // flock, which is also what an unshared open takes
                using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    internal static UploadSnapshot ToSnapshot(UploadInfo u) => new(
        u.Id, u.FilePath, u.Title, Phase(u.State), (long)u.SentBytes, (long)u.TotalBytes, u.VideoId, u.LastError);

    private static UploadPhase Phase(UploadState s) => s switch
    {
        UploadState.Queued => UploadPhase.Queued,
        UploadState.Waiting => UploadPhase.Waiting,
        UploadState.Hashing => UploadPhase.Hashing,
        UploadState.Initiating => UploadPhase.Starting,
        UploadState.Uploading => UploadPhase.Uploading,
        UploadState.Completing => UploadPhase.Finishing,
        UploadState.Paused => UploadPhase.Paused,
        UploadState.Done => UploadPhase.Done,
        UploadState.Failed => UploadPhase.Failed,
        _ => UploadPhase.Cancelled,
    };

    private static T Call<T>(Func<T> f)
    {
        try
        {
            return f();
        }
        catch (AunException ex)
        {
            throw CoreApiTransport.Map(ex, "upload");
        }
    }

    private static void Call(Action f) => Call(() =>
    {
        f();
        return 0;
    });

    private sealed class Listener : UploadListener
    {
        private readonly CoreUploadService _owner;

        public Listener(CoreUploadService owner) => _owner = owner;

        public void OnProgress(string id, ulong sentBytes, ulong totalBytes) => _owner.Changed?.Invoke(_owner, id);

        public void OnState(string id, UploadState state, AunException? error)
        {
            if (error != null) Logging.Log.For("upload").Warn("upload " + state + ": " + error.GetType().Name);
            _owner.Changed?.Invoke(_owner, id);
        }
    }
}
