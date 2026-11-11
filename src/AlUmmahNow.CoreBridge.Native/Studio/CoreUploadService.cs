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

    public CoreUploadService(CoreStudioAccounts accounts, Uri videosApi, string userAgent, string storeDir, string accountId)
    {
        AccountId = accountId;
        try
        {
            var client = accounts.Manager.ApiClient(new ApiClientConfig(videosApi.AbsoluteUri.TrimEnd('/'), userAgent, null, 60_000, 4), accountId);
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

    public void Dispose()
    {
        _engine.Shutdown();
        _engine.Dispose();
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
