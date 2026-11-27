using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Native;
using Xunit;

namespace AlUmmahNow.CoreBridge.Native.Tests;

public class CoreTelemetryServiceTests
{
    [Fact]
    public void Stays_silent_by_default_records_when_opted_in_and_purges_disk_on_revoke()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        try
        {
            using var service = new CoreTelemetryService(
                platform: "windows",
                appVersion: "1.2.3",
                locale: "ar",
                videosApiBaseUrl: null, // NoopSink for deterministic offline tests
                userAgent: "AlUmmahNowStudioWindows/1.2.3",
                bufferPath: bufferPath,
                initialEnabled: false);

            Assert.Equal(TelemetryConsent.Off, service.Consent);
            Assert.False(service.LastPurgeFailed);

            // While off, nothing is recorded or written to disk
            service.Track("screen_view", new Dictionary<string, object> { ["screen"] = "home" });
            service.ReportCrash("InvalidOperationException", "boom", "frame 0");
            Assert.Equal(0, service.PendingCount);
            Assert.False(File.Exists(bufferPath));

            // Opting in enables recording and crash persistence
            Assert.True(service.SetConsent(TelemetryConsent.Full));
            Assert.Equal(TelemetryConsent.Full, service.Consent);
            service.AddBreadcrumb("nav", "opened settings", TelemetryBreadcrumbSeverity.Info);
            service.Track("screen_view", new Dictionary<string, object> { ["screen"] = "settings", ["count"] = 2, ["ok"] = true });
            service.ReportCrash("NullReferenceException", "player crash", "at WatchViewModel.Play()");
            Assert.Equal(2, service.PendingCount);
            Assert.True(File.Exists(bufferPath));

            // Revoking immediately purges memory AND deletes the disk buffer
            Assert.True(service.SetConsent(TelemetryConsent.Off));
            Assert.Equal(TelemetryConsent.Off, service.Consent);
            Assert.False(service.LastPurgeFailed);
            Assert.Equal(0, service.PendingCount);
            Assert.False(File.Exists(bufferPath));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Flush_drains_buffered_events_when_opted_in()
    {
        using var service = new CoreTelemetryService(
            platform: "linux",
            appVersion: "1.0.0",
            locale: "en",
            videosApiBaseUrl: null,
            userAgent: "AlUmmahNowLinux/1.0.0",
            bufferPath: null,
            initialEnabled: true);

        service.TrackError("upload.timeout", new Dictionary<string, object> { ["attempt"] = 2 });
        Assert.Equal(1, service.PendingCount);

        await service.FlushAsync();
        Assert.Equal(0, service.PendingCount);
    }

    [Fact]
    public async Task Concurrent_revoke_and_dispose_during_flush_never_throw_and_leave_buffer_purged()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var bufferPath = Path.Combine(dir, $"buf-{i}.json");
                var service = new CoreTelemetryService(
                    platform: "linux",
                    appVersion: "1.0.0",
                    locale: "en",
                    videosApiBaseUrl: null,
                    userAgent: "AlUmmahNowLinux/1.0.0",
                    bufferPath: bufferPath,
                    initialEnabled: true);

                for (var e = 0; e < 10; e++)
                {
                    service.Track("screen_view", new Dictionary<string, object> { ["idx"] = e });
                }
                service.Persist();

                var flushTask = Task.Run(() => service.FlushAsync());
                var revokeTask = Task.Run(() => service.SetConsent(TelemetryConsent.Off));
                var disposeTask = Task.Run(() => service.Dispose());
                await Task.WhenAll(flushTask, revokeTask, disposeTask);

                // Wait for any cancelled-mid-flight native flush to settle and trigger deferred cleanup/dispose.
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (!service.IsClientDisposedForTesting && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(5);
                }
                Assert.True(service.IsClientDisposedForTesting);

                Assert.Equal(TelemetryConsent.Off, service.Consent);
                Assert.Equal(0, service.PendingCount);
                Assert.False(File.Exists(bufferPath));

                // Subsequent flush with an already-cancelled token or after dispose is a clean no-op
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                await service.FlushAsync(cts.Token);
                await service.FlushAsync();
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Dispose_waits_for_abandoned_native_flush_completion_after_revoke_cancellation()
    {
        var nativeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new CoreTelemetryService(
            platform: "linux",
            appVersion: "1.0.0",
            locale: "en",
            videosApiBaseUrl: null,
            userAgent: "AlUmmahNowLinux/1.0.0",
            bufferPath: null,
            initialEnabled: true)
        {
            FlushOverrideForTesting = () =>
            {
                nativeStarted.TrySetResult(true);
                return nativeRelease.Task;
            }
        };

        var flushTask = service.FlushAsync();
        await nativeStarted.Task;

        // Revoke cancels the awaiter in FlushAsync, and Dispose is called while nativeFlush is still active.
        Assert.True(service.SetConsent(TelemetryConsent.Off));
        await flushTask;
        service.Dispose();

        // Native handle must stay alive until the in-flight native flush actually finishes.
        Assert.False(service.IsClientDisposedForTesting);

        nativeRelease.TrySetResult(true);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!service.IsClientDisposedForTesting && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.True(service.IsClientDisposedForTesting);
    }

    [Fact]
    public async Task Opt_out_enforces_denial_and_surfaces_LastPurgeFailed_when_buffer_cannot_be_removed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-purgefail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        try
        {
            using var service = new CoreTelemetryService(
                platform: "windows",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowWindows/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true);

            Assert.Equal(TelemetryConsent.Full, service.Consent);
            service.Track("screen_view", new Dictionary<string, object> { ["screen"] = "home" });
            service.Persist();
            Assert.True(File.Exists(bufferPath));

            // Replace the buffer file with a non-empty directory so remove_file / File.Delete fails on opt-out.
            File.Delete(bufferPath);
            Directory.CreateDirectory(bufferPath);
            File.WriteAllText(Path.Combine(bufferPath, "keep.txt"), "x");

            // Opting out fails to remove the directory at bufferPath, surfaces LastPurgeFailed,
            // AND still denies all further collection/flushing in the wrapper.
            Assert.False(service.SetConsent(TelemetryConsent.Off));
            Assert.True(service.LastPurgeFailed);
            Assert.Equal(TelemetryConsent.Off, service.Consent);

            service.Track("screen_view", new Dictionary<string, object> { ["screen"] = "home" });
            service.ReportCrash("Exception", "should not record");
            Assert.Equal(0, service.PendingCount);
            await service.FlushAsync();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveIngestUrl_uses_https_authority_and_strips_paths_queries_and_fragments()
    {
        Assert.Equal("https://videos.api.alummahnow.net/api/v1/telemetry/batches",
            CoreTelemetryService.ResolveIngestUrl("https://videos.api.alummahnow.net/"));
        Assert.Equal("https://videos.api.example/api/v1/telemetry/batches",
            CoreTelemetryService.ResolveIngestUrl("https://videos.api.example/some/path?x=1#frag"));
        Assert.Equal("https://videos.api.example:8443/api/v1/telemetry/batches",
            CoreTelemetryService.ResolveIngestUrl("https://videos.api.example:8443/v2?token=abc#top"));
        Assert.Equal("https://videos.api.example/api/v1/telemetry/batches",
            CoreTelemetryService.ResolveIngestUrl("https://videos.api.example?x=1"));
        Assert.Null(CoreTelemetryService.ResolveIngestUrl("http://127.0.0.1:3086"));
        Assert.Null(CoreTelemetryService.ResolveIngestUrl("http://videos.api.alummahnow.net"));
        Assert.Null(CoreTelemetryService.ResolveIngestUrl(""));
        Assert.Null(CoreTelemetryService.ResolveIngestUrl(null));

        var demo = CoreTelemetryService.CreateOrNull("windows", "1.0.0", "en", "https://videos.api.alummahnow.net", "AlUmmahNowWindows/1.0.0", null, initialEnabled: true, demo: true);
        Assert.Same(NullTelemetryService.Instance, demo);
        Assert.Equal(TelemetryConsent.Off, demo.Consent);
    }
}
