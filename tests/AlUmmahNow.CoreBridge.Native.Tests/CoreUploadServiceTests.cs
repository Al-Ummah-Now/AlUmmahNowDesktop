using AlUmmahNow.Core;
using AlUmmahNow.CoreBridge.Native.Studio;
using Xunit;

namespace AlUmmahNow.CoreBridge.Native.Tests;

/// <summary>Stopping the real upload engine: its store is free afterwards.</summary>
public sealed class CoreUploadServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private UploadEngine Engine()
    {
        // No server is there: a file that is queued waits
        var client = new ApiClient(new ApiClientConfig("http://127.0.0.1:9", "test", null, 1_000, 0, AunClient.FromUserAgent("AlUmmahNowStudioWindows/1.0.0")), null);
        return new UploadEngine(client, _dir, "u1", AunCoreMethods.UploadConfigDefault(), new SystemConnectivity());
    }

    [Fact]
    public async Task A_stopped_queue_has_let_go_of_its_store()
    {
        var path = CoreUploadService.LockPath(_dir, "u1");
        Assert.True(CoreUploadService.IsFree(path));
        var service = new CoreUploadService(Engine(), _dir, "u1");
        // The engine's own name of the file, and its own lock on it
        Assert.True(File.Exists(path));
        Assert.False(CoreUploadService.IsFree(path));
        Assert.ThrowsAny<AunException>(Engine);

        await service.StopAsync();
        Assert.True(CoreUploadService.IsFree(path));
        using (var again = Engine())
        {
            Assert.False(CoreUploadService.IsFree(path));
            again.Shutdown();
        }

        // Again, and closing it, are nothing
        await service.StopAsync();
        service.Dispose();
    }

    [Fact]
    public async Task A_store_that_is_not_let_go_of_fails_the_stop_in_time()
    {
        var service = new CoreUploadService(Engine(), _dir, "u2");
        // Somebody else's engine holds the store that this one is to wait for
        var other = CoreUploadService.LockPath(_dir, "u2");
        using var held = File.Open(other, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var late = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StopAsync(late.Token));
    }
}
