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

            // Also verify that revoking when the buffer's parent directory denies search permission
            // surfaces LastPurgeFailed instead of falsely reporting success via File.Exists == false.
            if (!OperatingSystem.IsWindows())
            {
                var restrictedDir = Path.Combine(dir, "restricted");
                Directory.CreateDirectory(restrictedDir);
                var restrictedBuf = Path.Combine(restrictedDir, "telemetry-buffer.json");
                using var restrictedSvc = new CoreTelemetryService(
                    platform: "linux",
                    appVersion: "1.0.0",
                    locale: "en",
                    videosApiBaseUrl: null,
                    userAgent: "AlUmmahNowLinux/1.0.0",
                    bufferPath: restrictedBuf,
                    initialEnabled: true);
                restrictedSvc.Track("screen_view", new Dictionary<string, object> { ["screen"] = "home" });
                restrictedSvc.Persist();
                Assert.True(File.Exists(restrictedBuf));

                File.SetUnixFileMode(restrictedDir, UnixFileMode.None);
                try
                {
                    Assert.False(restrictedSvc.SetConsent(TelemetryConsent.Off));
                    Assert.True(restrictedSvc.LastPurgeFailed);
                    Assert.Equal(TelemetryConsent.Off, restrictedSvc.Consent);
                }
                finally
                {
                    File.SetUnixFileMode(restrictedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                Assert.True(File.Exists(restrictedBuf));
            }
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
        Assert.Equal("https://videos.api.example/api/v1/telemetry/batches",
            CoreTelemetryService.ResolveIngestUrl("https://videos.api.example:443/v1"));
        Assert.Equal("https://[::1]:8443/api/v1/telemetry/batches",
            CoreTelemetryService.ResolveIngestUrl("https://[::1]:8443/path?a=1#frag"));
        Assert.Null(CoreTelemetryService.ResolveIngestUrl("https://user:password@videos.api.example/path"));
        Assert.Null(CoreTelemetryService.ResolveIngestUrl("https://user@videos.api.example"));
        Assert.Null(CoreTelemetryService.ResolveIngestUrl("http://127.0.0.1:3086"));
        Assert.Null(CoreTelemetryService.ResolveIngestUrl("http://videos.api.alummahnow.net"));
        Assert.Null(CoreTelemetryService.ResolveIngestUrl(""));
        Assert.Null(CoreTelemetryService.ResolveIngestUrl(null));

        var demo = CoreTelemetryService.CreateOrNull("windows", "1.0.0", "en", "https://videos.api.alummahnow.net", "AlUmmahNowWindows/1.0.0", null, initialEnabled: true, demo: true);
        Assert.Same(NullTelemetryService.Instance, demo);
        Assert.Equal(TelemetryConsent.Off, demo.Consent);
    }

    [Fact]
    public async Task Dispose_while_opted_in_flush_in_flight_preserves_retryable_buffer()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-dispose-keep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        try
        {
            var nativeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var nativeRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true)
            {
                FlushOverrideForTesting = () =>
                {
                    nativeStarted.TrySetResult(true);
                    return nativeRelease.Task;
                }
            };

            service.Track("screen_view", new Dictionary<string, object> { ["screen"] = "home" });
            service.Persist();
            Assert.True(File.Exists(bufferPath));

            var flushTask = service.FlushAsync();
            await nativeStarted.Task;

            // Ordinary shutdown (Dispose without revoking consent) must preserve the persisted buffer for retry.
            service.Dispose();
            await flushTask;
            Assert.False(service.IsClientDisposedForTesting);

            nativeRelease.TrySetResult(true);
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!service.IsClientDisposedForTesting && DateTime.UtcNow < deadline)
            {
                await Task.Delay(5);
            }
            Assert.True(service.IsClientDisposedForTesting);
            Assert.True(File.Exists(bufferPath));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task External_opt_out_revokes_other_instance_before_crash_persist_or_flush()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-multi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        string? sharedToken = "enabled:gen1";
        try
        {
            using var otherInstance = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken));

            otherInstance.Track("screen_view", new Dictionary<string, object> { ["screen"] = "home" });
            otherInstance.Persist();
            var activeBufferPath = otherInstance.ActiveBufferPathForTesting!;
            var siblingTmp = Path.Combine(dir, ".telemetry-buffer.gen1.json.4242.tmp");
            File.WriteAllText(siblingTmp, "stale");
            Assert.True(File.Exists(activeBufferPath));
            Assert.True(File.Exists(siblingTmp));

            // Simulate another running instance turning diagnostics off in shared settings.
            Volatile.Write(ref sharedToken, null);

            // Crash, persist, and flush on this instance must all revoke instead of writing or sending,
            // and clean up both the buffer and any sibling temp files.
            otherInstance.ReportCrash("IOException", "boom", "at Foo.Bar()");
            otherInstance.Persist();
            await otherInstance.FlushAsync();

            Assert.Equal(TelemetryConsent.Off, otherInstance.Consent);
            Assert.False(otherInstance.LastPurgeFailed);
            Assert.Equal(0, otherInstance.PendingCount);
            Assert.False(File.Exists(activeBufferPath));
            Assert.False(File.Exists(siblingTmp));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task External_opt_out_mid_flush_revokes_native_and_managed_consent_immediately()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-midflush-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        string? sharedToken = "enabled:gen1";
        try
        {
            var nativeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var nativeRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken))
            {
                ExternalPollIntervalMsForTesting = 15,
                FlushOverrideForTesting = () =>
                {
                    nativeStarted.TrySetResult(true);
                    return nativeRelease.Task;
                }
            };

            service.Track("screen_view", new Dictionary<string, object> { ["screen"] = "home" });
            service.Persist();
            var activeBufferPath = service.ActiveBufferPathForTesting!;
            Assert.True(File.Exists(activeBufferPath));

            var flushTask = service.FlushAsync();
            await nativeStarted.Task;

            // Another instance opts out while B's flush is ALREADY in flight.
            Volatile.Write(ref sharedToken, null);

            // FlushAsync must observe the external opt-out and revoke BOTH managed and native consent
            // before nativeRelease even completes.
            var completed = await Task.WhenAny(flushTask, Task.Delay(2000));
            Assert.Same(flushTask, completed);
            Assert.Equal(TelemetryConsent.Off, service.Consent);
            Assert.Equal(AlUmmahNow.Core.TelemetryConsentState.Denied, service.NativeConsentForTesting);
            Assert.False(File.Exists(activeBufferPath));

            nativeRelease.TrySetResult(true);
            service.Dispose();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task External_opt_out_and_reenable_aba_revokes_stale_instance_before_flushing_old_events()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-aba-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        string? sharedToken = "enabled:gen1";
        var flushInvoked = false;
        try
        {
            using var instanceB = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken))
            {
                FlushOverrideForTesting = () =>
                {
                    flushInvoked = true;
                    return Task.CompletedTask;
                }
            };

            instanceB.Track("screen_view", new Dictionary<string, object> { ["screen"] = "pre-optout" });
            instanceB.Persist();
            var gen1BufferPath = instanceB.ActiveBufferPathForTesting!;
            Assert.Equal(1, instanceB.PendingCount);
            Assert.True(File.Exists(gen1BufferPath));

            // Instance A opts out and immediately opts back in with a new consent generation token.
            Volatile.Write(ref sharedToken, null);
            Volatile.Write(ref sharedToken, "enabled:gen2");

            // Instance B must detect the generation change, revoke + purge its pre-opt-out queue, and NOT flush.
            await instanceB.FlushAsync();

            Assert.False(flushInvoked);
            Assert.Equal(TelemetryConsent.Off, instanceB.Consent);
            Assert.Equal(0, instanceB.PendingCount);
            Assert.False(File.Exists(gen1BufferPath));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Fresh_instance_purges_stale_generation_buffer_on_startup_but_keeps_same_generation_buffer()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-gen-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        string? sharedToken = "enabled:gen1";
        try
        {
            string gen1BufferPath;
            using (var gen1Instance = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken)))
            {
                gen1BufferPath = gen1Instance.ActiveBufferPathForTesting!;
                gen1Instance.Track("screen_view", new Dictionary<string, object> { ["screen"] = "gen1-old" });
                gen1Instance.Persist();
                Assert.True(File.Exists(gen1BufferPath));
            }

            // Simulate opt-out + re-enable creating a new generation while a stale gen1 buffer remained on disk
            // (or was written late by a paused gen1 instance).
            Volatile.Write(ref sharedToken, "enabled:gen2");

            string gen2BufferPath;
            using (var freshGen2 = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken)))
            {
                gen2BufferPath = freshGen2.ActiveBufferPathForTesting!;
                Assert.NotEqual(gen1BufferPath, gen2BufferPath);
                Assert.Equal(TelemetryConsent.Full, freshGen2.Consent);
                Assert.Equal(0, freshGen2.PendingCount);
                Assert.False(File.Exists(gen1BufferPath));

                // Persist a new event under gen2; a normal restart within gen2 must keep it.
                freshGen2.Track("screen_view", new Dictionary<string, object> { ["screen"] = "gen2-new" });
                freshGen2.Persist();
                Assert.Equal(1, freshGen2.PendingCount);
                Assert.True(File.Exists(gen2BufferPath));
            }

            // Even if a paused gen1 process wrote a late file to gen1BufferPath after gen2 started,
            // gen2's isolated file is untouched and the next gen2 startup sweeps gen1BufferPath without loading it.
            File.WriteAllText(gen1BufferPath, "{\"stale\":\"gen1\"}");

            using var restartedGen2 = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken));
            Assert.Equal(TelemetryConsent.Full, restartedGen2.Consent);
            Assert.Equal(1, restartedGen2.PendingCount);
            Assert.False(File.Exists(gen1BufferPath));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Revoked_gen1_flush_completion_and_stale_off_startup_preserve_newer_gen2_buffer()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-gen2-keep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        string? sharedToken = "enabled:gen1";
        try
        {
            var nativeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var nativeRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var gen1 = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken))
            {
                ExternalPollIntervalMsForTesting = 15,
                FlushOverrideForTesting = () =>
                {
                    nativeStarted.TrySetResult(true);
                    return nativeRelease.Task;
                }
            };

            var gen1BufferPath = gen1.ActiveBufferPathForTesting!;
            gen1.Track("screen_view", new Dictionary<string, object> { ["screen"] = "gen1" });
            gen1.Persist();
            Assert.True(File.Exists(gen1BufferPath));

            var consentEvents = new TaskCompletionSource<TelemetryConsent>(TaskCreationOptions.RunContinuationsAsynchronously);
            gen1.ConsentChanged += consent =>
            {
                if (consent == TelemetryConsent.Off) consentEvents.TrySetResult(consent);
            };
            var flushTask = gen1.FlushAsync();
            await nativeStarted.Task;

            // Simulate opt-out followed by re-enable to gen2 while gen1's flush is still in flight.
            Volatile.Write(ref sharedToken, null);
            var completed = await Task.WhenAny(flushTask, Task.Delay(2000));
            Assert.Same(flushTask, completed);
            Assert.Equal(TelemetryConsent.Off, await consentEvents.Task.WaitAsync(TimeSpan.FromSeconds(2)));

            Volatile.Write(ref sharedToken, "enabled:gen2");
            string gen2BufferPath;
            using (var gen2 = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken)))
            {
                gen2BufferPath = gen2.ActiveBufferPathForTesting!;
                gen2.Track("screen_view", new Dictionary<string, object> { ["screen"] = "gen2" });
                gen2.Persist();
                Assert.True(File.Exists(gen2BufferPath));
            }

            // Simulate a late persist rename from gen1 right before its revoked flush completes, then
            // deterministically wait for ReleaseInFlightFlush to delete gen1BufferPath while preserving gen2BufferPath.
            File.WriteAllText(gen1BufferPath, "{\"late\":\"gen1\"}");
            nativeRelease.TrySetResult(true);
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (File.Exists(gen1BufferPath) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.False(File.Exists(gen1BufferPath));
            Assert.True(File.Exists(gen2BufferPath));
            using (var staleOffStartup = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: false,
                externalConsentToken: () => Volatile.Read(ref sharedToken)))
            {
                Assert.Equal(TelemetryConsent.Off, staleOffStartup.Consent);
            }
            Assert.True(File.Exists(gen2BufferPath));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReportCrash_and_breadcrumb_redact_local_file_paths()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        try
        {
            using var service = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true);

            service.AddBreadcrumb("io", "Opened /workspace/E\u0301lodie/Very Secret Client Project Name.mov\nFailed '/data/user/0/O'Brien.mov'\nRead \"/Users/O'Brien/Final.mov\"");
            service.ReportCrash(
                "IOException",
                "Failed reading /Private (Client).mov\n/Private Client\n/private-key\n/Users/alice/Secret<Project>.mov\n/Users/alice/Draft:77 in open() Acquisition.mov\nFailed /home/alice/Secret\"Acquisition.mov\nFailed to open \"/ Secret/Acquisition.mov\"\nFailed to open \"/ SecretFile\"\n/ SecretFile.mov\n/ SecretFile\n/ SecretFile.abcdefghijkl\n// SecretFile\n42:/ Secret/Acquisition.mov:77\nerror 42://SecretHost/Acquisition.mov\nFailed to open path\\\\Server\\Share\\Secret.mov\nopen(pathC:\\Secret\\Acquisition.mov)\nopen(C://Secret/Acquisition.mov)\nC://Users\\Alice\\Secret.mov\nhttps://example.com(/Users/alice/Secret.mov)\n(https://example.com)(private/Secret.mov)\nhttps://example.com/api,open(C://Secret/Acquisition.mov)\nCould not find file 'Secret.mov'\nCould not find file 'private/Secret.mov'\nCould not find file './Secret.mov'\nCould not find file 'O'Brien.mov'\nCould not find file 'SecretBareName'\nCould not find file Secret.xyz\nCould not find file Secret.2026\nSecret Project.mov\nSecret Project.mov (missing)\nSecret.Final.mov\nopen Acquisition.custom\nopen Secret.123\nopen Secret Project.mov\nopen //storage/emulated/0/Private.mov\n/workspace/alice/Project|Acquisition.mov\nerror 42:/home/alice/private.mov\n/Client Files/Private.mov\n/workspace/alice/Project;Acquisition.mov\n'/workspace/alice/Project:2026;Acquisition.mov'\n/!Acquisition.mov\n\"/!Acquisition.mov\"\n\"C:\\Users\\Alice Smith\\Private Draft.mp4\"",
                "System.IO.IOException: /Users/alice/Draft:99 in open()\n123 files failed under /home/alice/Draft:99\rCaused by: /Users/alice/Cause:99\n   at Foo.Open() in /workspace/Élodie/src/App.cs:line 42\n   at \"/home/alice/private:66\" while opening file\n   at file:///Users/alice/Player.cs:55\n   at Bar.Run() in \\\\fileserver\\share\\secret.mp4\n~/private/key.pem\nhttps://videos.api.alummahnow.net/v1/items");

            Assert.True(File.Exists(bufferPath));
            var json = File.ReadAllText(bufferPath);
            Assert.DoesNotContain("alice", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("smith", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("lodie", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("brien", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("client", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("acquisition", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("abcdefghijkl", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("project>.mov", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("storage", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("player.cs", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(":99", json, StringComparison.Ordinal);
            Assert.DoesNotContain(":66", json, StringComparison.Ordinal);
            Assert.DoesNotContain(":55", json, StringComparison.Ordinal);
            Assert.DoesNotContain("name.mov", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("draft.mp4", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("workspace", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"C:\Users", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fileserver", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private/key.pem", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Foo.Open()", json, StringComparison.Ordinal);
            Assert.Contains("Bar.Run()", json, StringComparison.Ordinal);
            Assert.Contains("[redacted-path]", json, StringComparison.Ordinal);
            Assert.Equal("https://videos.api.alummahnow.net/v1/items",
                CoreTelemetryService.SanitizeLocalPaths("https://videos.api.alummahnow.net/v1/items"));
            Assert.Equal("https://example.com/api?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api?page=2&sort=asc"));
            Assert.Equal("https://example.com/api?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api?source=%2FUsers%2Falice%2FPrivateProject"));
            Assert.Equal("https://example.com/api?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api?token=abc[secret]&next=private"));
            Assert.Equal("https://example.com/api?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api?q=(PrivateProject)"));
            Assert.Equal("(https://example.com/api?[redacted])",
                CoreTelemetryService.SanitizeLocalPaths("(https://example.com/api?q=(PrivateProject))"));
            Assert.Equal("https://example.com/api#[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api#first,PrivateProject"));
            Assert.Equal("https://example.com/users/[redacted-email]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/users/alice@example.com"));
            Assert.Equal("https://example.com/users/[redacted-email]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/users/alice%40example.com"));
            Assert.Equal("https://example.com/users/[redacted-email]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/users/alice%team@example.com"));
            Assert.Equal("[redacted-email]",
                CoreTelemetryService.SanitizeLocalPaths("alice%notes@example.com"));
            Assert.Equal("https://example.com/?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/?token=abc{Secret}xyz"));
            Assert.Equal("(https://example.com/?[redacted]).",
                CoreTelemetryService.SanitizeLocalPaths("(https://example.com/?q=secret)."));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("file:///private/(https://Secret/Acquisition.mov)"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("file:///private/My Folder/(https://Secret/Acquisition.mov)"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("file:///private/My Folder/( https://Secret/Acquisition.mov)"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("file:///private/Project Folder/ (https://Secret/Acquisition.mov)"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("file:///private/My\u00A0Acquisition"));
            Assert.Equal("\"[redacted-path]\" and ( https://example.com/api)",
                CoreTelemetryService.SanitizeLocalPaths("\"file:///a.mov\" and ( https://example.com/api)"));
            Assert.Equal("\"[redacted-path]\" and (((https://example.com/api)))",
                CoreTelemetryService.SanitizeLocalPaths("\"file:///a.mov\" and (((https://example.com/api)))"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("file:// Secret Acquisition"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("file:///private/Secret\u001EAcquisition"));
            Assert.Equal("[redacted-path]   https://example.com/api",
                CoreTelemetryService.SanitizeLocalPaths("file://   https://example.com/api"));
            Assert.Equal("[redacted-path] https://example.com/ (https://other.example/api)",
                CoreTelemetryService.SanitizeLocalPaths("file:///a.mov; https://example.com/ (https://other.example/api)"));
            Assert.Equal("[redacted-path] https://example.com/ [redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("file:///a.mov; https://example.com/ /private/foo(https://Secret/Acquisition.mov)"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("/private/foo(https://Secret/Acquisition.mov)"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("/private/Client Files (https://Secret/Acquisition.mov)"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("/private/Transfer to (https://Secret/Acquisition.mov)"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("relative/Transfer to (https://Secret/Acquisition.mov)"));
            Assert.Equal("path=[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("path=/private/Transfer to (https://Secret/Acquisition.mov)"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("/private/Secret\u001Ehttps://Secret/Acquisition.mov"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("/private/Secret\u001EAcquisition"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("/private/Secret^Acquisition"));
            Assert.Equal("TLS/1.3(https://example.com/api)",
                CoreTelemetryService.SanitizeLocalPaths("TLS/1.3(https://example.com/api)"));
            Assert.Equal("HTTP/2(https://example.com/api)",
                CoreTelemetryService.SanitizeLocalPaths("HTTP/2(https://example.com/api)"));
            var manyXs = "\u001EAUNURL" + new string('X', 5000);
            Assert.Equal(manyXs, CoreTelemetryService.SanitizeLocalPaths(manyXs));
            Assert.Equal("\"[redacted-path]\"",
                CoreTelemetryService.SanitizeLocalPaths("\"file:///private/My Folder (https://Secret/Acquisition.mov)\""));
            Assert.Equal("\"[redacted-path]\"",
                CoreTelemetryService.SanitizeLocalPaths("\"file:///private/Client \\\"Internal\\\" Acquisition\""));
            Assert.Contains("https://example.com/api",
                CoreTelemetryService.SanitizeLocalPaths("\"relative/My Clip.mov\" and (https://example.com/api)"));
            Assert.Equal("progress 1/2 (https://example.com/api)",
                CoreTelemetryService.SanitizeLocalPaths("progress 1/2 (https://example.com/api)"));
            Assert.Equal("progress 1/2.",
                CoreTelemetryService.SanitizeLocalPaths("progress 1/2."));
            Assert.Equal("progress (1/2.)",
                CoreTelemetryService.SanitizeLocalPaths("progress (1/2.)"));
            Assert.Equal("progress (1/2 )",
                CoreTelemetryService.SanitizeLocalPaths("progress (1/2 )"));
            Assert.Equal("progress (1/2...) (https://example.com/api)",
                CoreTelemetryService.SanitizeLocalPaths("progress (1/2...) (https://example.com/api)"));
            Assert.Equal("https://example.com/a(b/c)",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/a(b/c)"));
            Assert.Equal("https://example.com/a(b(c/d))",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/a(b(c/d))"));
            Assert.Equal("https://example.com/a([redacted-path])",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/a(/storage/PrivateProject)"));
            Assert.Equal("'[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("'file:///tmp/O'Brien Secret'"));
            Assert.Equal("'[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("'file:///tmp/O'Brien Secret"));
            Assert.Equal("'[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("'file:///tmp/James' Private Name'"));
            Assert.Equal("'[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("'file:///tmp/Project'2 Secret'"));
            Assert.Equal("\"[redacted-path]\" progress \"done\"",
                CoreTelemetryService.SanitizeLocalPaths("\"file:///tmp/Secret\" progress \"done\""));
            Assert.Equal("\"[redacted-path]\" progress 1/2",
                CoreTelemetryService.SanitizeLocalPaths("\"file:///tmp/Secret\" progress 1/2"));
            Assert.Equal("'[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("'file:///tmp/O' Connor Private"));
            Assert.Equal("'[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("'file:///tmp/O' Connor Private'"));
            Assert.Equal("'[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("'file:///tmp/O'Connor' Private'Collection Plan"));
            Assert.Equal("'[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("'file:///tmp/Artists' Private 'Collection'"));
            Assert.Equal("'[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("'file:///tmp/Artists' Private 'Collection"));
            Assert.Equal("'[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("'file:///tmp/O' Connor Private (https://private.example/Acquisition.mov)"));
            Assert.DoesNotContain("private.example",
                CoreTelemetryService.SanitizeLocalPaths("input'file:///tmp/O' Connor Private https://private.example/Acquisition.mov"));
            Assert.DoesNotContain("private.example",
                CoreTelemetryService.SanitizeLocalPaths("input'/tmp/O' Connor Private https://private.example/Acquisition.mov"));
            Assert.DoesNotContain("private.example",
                CoreTelemetryService.SanitizeLocalPaths("'relative/Artists' Private (https://private.example/Acquisition.mov)'"));
            Assert.Equal("'([redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("'(/tmp/Client) Acquisition Plan'"));
            Assert.Equal("\"([redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("\"(relative/Client) Acquisition Plan\""));
            Assert.Equal("status 'ready' (https://example.com/api)",
                CoreTelemetryService.SanitizeLocalPaths("status 'ready' (https://example.com/api)"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("/tmp/Private\u2028Acquisition Plan"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("'Private.qzx'2 Acquisition.qzx'"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("'Studio 2's Acquisition.qzx'"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("'Studio 2' Acquisition.qzx'"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("'Studio 'Private Acquisition.qzx'"));
            Assert.Equal("status \"ready; detail [redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("status \"ready; detail \"Private Project.qzx\""));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("private]folder\\secret"));
            Assert.Equal("progress 1/2. (https://example.com/api)",
                CoreTelemetryService.SanitizeLocalPaths("progress 1/2. (https://example.com/api)"));
            Assert.Equal("opening [redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("opening 1/2.mov"));
            Assert.Equal("opening\u00A0[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("opening\u00A0Acquisition.qzx"));
            Assert.Contains("https://example.com/api",
                CoreTelemetryService.SanitizeLocalPaths("progress \"1/2\" (https://example.com/api)"));
            Assert.DoesNotContain("private.example",
                CoreTelemetryService.SanitizeLocalPaths("opening \"1/2 (https://private.example/Acquisition.mov)\""));
            Assert.DoesNotContain("private.example",
                CoreTelemetryService.SanitizeLocalPaths("opening \"HTTP/2 (https://private.example/Acquisition.mov)\""));
            Assert.DoesNotContain("private.example",
                CoreTelemetryService.SanitizeLocalPaths("opening \"My Project/Clip https://private.example/Acquisition.mov\""));
            Assert.DoesNotContain("private.example",
                CoreTelemetryService.SanitizeLocalPaths("opening \"(relative/My Clip.mov) https://private.example/Acquisition.mov\""));
            Assert.DoesNotContain("private.example",
                CoreTelemetryService.SanitizeLocalPaths("opening \"/tmp/Project 'draft' (https://private.example/Acquisition.mov)\""));
            Assert.DoesNotContain("private.example",
                CoreTelemetryService.SanitizeLocalPaths("opening /tmp/Project 'draft' (https://private.example/Acquisition.mov)"));
            Assert.DoesNotContain("private.example",
                CoreTelemetryService.SanitizeLocalPaths("opening \"relative/My's (https://private.example/Acquisition.mov)\""));
            Assert.Contains("https://example.com/api",
                CoreTelemetryService.SanitizeLocalPaths("opening \"relative/My's Clip.mov\" (https://example.com/api)"));
            Assert.DoesNotContain("private.example",
                CoreTelemetryService.SanitizeLocalPaths("opening relative/My(1) (https://private.example/Acquisition.mov)"));
            Assert.Equal("can't reach https://example.com/api",
                CoreTelemetryService.SanitizeLocalPaths("can't reach https://example.com/api"));
            // Linear scaling regression tests: long alphanumeric runs and repeated keywords before a trailing dot.
            var longAlpha = new string('a', 100_000);
            Assert.Equal(longAlpha, CoreTelemetryService.SanitizeLocalPaths(longAlpha));
            var repeatedKeywords = string.Concat(Enumerable.Repeat("open ", 20_000)) + ".";
            Assert.Equal(repeatedKeywords, CoreTelemetryService.SanitizeLocalPaths(repeatedKeywords));
            var repeatedQuotes = string.Concat(Enumerable.Repeat("'a ", 20_000)) + ".";
            Assert.Equal(repeatedQuotes, CoreTelemetryService.SanitizeLocalPaths(repeatedQuotes));
            var repeatedEquals = new string('=', 50_000);
            Assert.Equal(repeatedEquals, CoreTelemetryService.SanitizeLocalPaths(repeatedEquals));
            var leadingSpaces = new string(' ', 50_000) + "x.qzx";
            Assert.Equal(leadingSpaces, CoreTelemetryService.SanitizeLocalPaths(leadingSpaces));
            var repeatedBrackets = string.Concat(Enumerable.Repeat("https://[", 20_000));
            Assert.Contains("[redacted-path]", CoreTelemetryService.SanitizeLocalPaths(repeatedBrackets));
            var longQuotedFile = "'" + new string('b', 100_000) + ".pdf'";
            Assert.Equal("[redacted-path]", CoreTelemetryService.SanitizeLocalPaths(longQuotedFile));
            var repeatedClosers = string.Concat(Enumerable.Repeat("x.qzx' ", 20_000));
            Assert.Equal(repeatedClosers, CoreTelemetryService.SanitizeLocalPaths(repeatedClosers));
            var embeddedQueries = "/tmp;" + string.Concat(Enumerable.Repeat("https://host?x;", 5_000));
            Assert.Equal("[redacted-path]", CoreTelemetryService.SanitizeLocalPaths(embeddedQueries));
            var longWindowsPath = "C:\\Private" + string.Concat(Enumerable.Repeat(" folder", 20_000));
            Assert.Equal("[redacted-path]", CoreTelemetryService.SanitizeLocalPaths(longWindowsPath));
            var longFractionUrls = new string('1', 50_000) + "/" + string.Concat(Enumerable.Repeat("https://a;", 2_000));
            Assert.Equal("[redacted-path]", CoreTelemetryService.SanitizeLocalPaths(longFractionUrls));
            Assert.Equal("\"[redacted-path]\" (https://example.com/api)",
                CoreTelemetryService.SanitizeLocalPaths("\"file:///a.mov\" (https://example.com/api)"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("file:///private/a" + new string(' ', 5000) + "Secret.mov"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("file:///storage/emulated/0/Secret.mov and (https://example.com/api)"));
            Assert.Equal("[redacted-path] https://example.com/api",
                CoreTelemetryService.SanitizeLocalPaths("file:///private/Folder/ https://example.com/api"));
            Assert.Equal("https://example.com/ (https://other.com/api)",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/ (https://other.com/api)"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("/private/My Folder/(https://Secret/Acquisition.mov)"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("/private/My Folder/( https://Secret/Acquisition.mov)"));
            var repeated = string.Concat(Enumerable.Repeat("(https://example.com)", 500));
            Assert.Equal(repeated, CoreTelemetryService.SanitizeLocalPaths(repeated));
            Assert.Equal("([redacted-path] https://example.com/api)",
                CoreTelemetryService.SanitizeLocalPaths("(relative/Secret.mov and https://example.com/api)"));
            Assert.Equal("Failed for [redacted-email] with Bearer [redacted] and Bearer [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("Failed for alice@example.com with Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig and Bearer abcdefg"));
            Assert.Equal("https://example.com/?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/?path=/Users/alice/Secret.mov"));
            Assert.Equal("https://example.com/api?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api?path=/storage/emulated/0/Secret.mov"));
            Assert.Equal("https://example.com/api;[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api;C://Secret/Acquisition.mov"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("file:///storage/emulated/0/https://Secret/Acquisition.mov"));
            Assert.Equal("([redacted-path] https://example.com/api)",
                CoreTelemetryService.SanitizeLocalPaths("(/storage/emulated/0/Secret.mov and https://example.com/api)"));
            Assert.Equal("[redacted-path] https://example.com/api",
                CoreTelemetryService.SanitizeLocalPaths("file:///storage/emulated/0/Secret.mov and https://example.com/api"));
            Assert.Equal("Failed for [redacted-email] with Bearer [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("Failed for alice@example.com with Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig"));
            Assert.Equal("Failed for [redacted-email], [redacted-email], [redacted-email], 「[redacted-email]」",
                CoreTelemetryService.SanitizeLocalPaths("Failed for 李@example.com, محمد@مثال.مصر, user@bücher.example, 「李@例え.jp」"));
            Assert.Equal("https://[2001:db8::1]/v1/items",
                CoreTelemetryService.SanitizeLocalPaths("https://[2001:db8::1]/v1/items"));
            Assert.Equal("https://example.com/a(b)/items",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/a(b)/items"));
            Assert.Equal("https://example.com/a(b;c)/items",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/a(b;c)/items"));
            Assert.Equal("https://example.com/api;[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api;file:///storage/emulated/0/Secret.mov"));
            Assert.Equal("Failure: Main.ts:42",
                CoreTelemetryService.SanitizeLocalPaths("Failure: Main.ts:42"));
            Assert.Equal("open(https://example.com/a(b)/items)",
                CoreTelemetryService.SanitizeLocalPaths("open(https://example.com/a(b)/items)"));
            Assert.Equal("https://example.com/a'b/items",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/a'b/items"));
            Assert.Equal("https://foo!bar.example/api",
                CoreTelemetryService.SanitizeLocalPaths("https://foo!bar.example/api"));
            Assert.Equal("https://example.com/v1;version=2/items",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/v1;version=2/items"));
            Assert.Equal("https://one;two.example/api",
                CoreTelemetryService.SanitizeLocalPaths("https://one;two.example/api"));
            Assert.Equal("https://example.com/api,[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api,private/Secret.mov"));
            Assert.Equal("open [redacted-path]:42",
                CoreTelemetryService.SanitizeLocalPaths("open Secret.mov:42"));
            Assert.Equal("open [redacted-path] from https://example.com/api",
                CoreTelemetryService.SanitizeLocalPaths("open Secret.mov from https://example.com/api"));
            Assert.Equal("open failed at MainActivity.kt:42",
                CoreTelemetryService.SanitizeLocalPaths("open failed at MainActivity.kt:42"));
            Assert.Equal("open([redacted-path]) Foo.Open()",
                CoreTelemetryService.SanitizeLocalPaths("open(/Volumes/Secret/Acquisition.mov) Foo.Open()"));
            Assert.Equal("[redacted-path] (missing)",
                CoreTelemetryService.SanitizeLocalPaths("Secret Project.mov (missing)"));
            Assert.DoesNotContain("Secret", CoreTelemetryService.SanitizeLocalPaths("https://example.com/api,open(C://Secret/Acquisition.mov)"));
            Assert.NotEqual("https://[:::]/api", CoreTelemetryService.SanitizeLocalPaths("https://[:::]/api"));
            Assert.NotEqual("https://bad%zz/api", CoreTelemetryService.SanitizeLocalPaths("https://bad%zz/api"));
            Assert.Equal("git+http2://example.com/repo",
                CoreTelemetryService.SanitizeLocalPaths("git+http2://example.com/repo"));
            Assert.Equal("profile://example.com/repo",
                CoreTelemetryService.SanitizeLocalPaths("profile://example.com/repo"));
            Assert.Equal("https://[::1]:8443/api",
                CoreTelemetryService.SanitizeLocalPaths("https://[::1]:8443/api"));
            Assert.Equal("a123456789012345678901234567890123://example.com/repo",
                CoreTelemetryService.SanitizeLocalPaths("a123456789012345678901234567890123://example.com/repo"));
            Assert.Equal("HTTP/1.1 503 Service Unavailable",
                CoreTelemetryService.SanitizeLocalPaths("HTTP/1.1 503 Service Unavailable"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Reenable_after_failed_opt_out_purge_deletes_leftover_buffer_before_granting_consent()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-reenable-" + Guid.NewGuid().ToString("N"));
        var lockedDir = Path.Combine(dir, "locked");
        Directory.CreateDirectory(lockedDir);
        var bufferPath = Path.Combine(lockedDir, "telemetry-buffer.json");
        try
        {
            using var service = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true);

            var events = new List<bool>();
            service.PurgeFailureChanged += failed => events.Add(failed);

            service.Track("screen_view", new Dictionary<string, object> { ["screen"] = "revoked" });
            service.Persist();
            Assert.True(File.Exists(bufferPath));

            // Make parent directory read-only so opt-out cannot unlink the buffer file.
            File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                Assert.False(service.SetConsent(TelemetryConsent.Off));
                Assert.True(service.LastPurgeFailed);
                Assert.Contains(true, events);
                Assert.True(File.Exists(bufferPath));

                // While buffer still cannot be removed, re-enable must fail closed.
                Assert.False(service.SetConsent(TelemetryConsent.Full));
                Assert.Equal(TelemetryConsent.Off, service.Consent);
                Assert.True(service.LastPurgeFailed);
            }
            finally
            {
                File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            // Once permissions recover, re-enable must purge the stale revoked buffer first so 0 old events reload.
            Assert.True(service.SetConsent(TelemetryConsent.Full));
            Assert.Equal(TelemetryConsent.Full, service.Consent);
            Assert.False(service.LastPurgeFailed);
            Assert.Contains(false, events);
            Assert.Equal(0, service.PendingCount);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Reenable_is_blocked_until_pre_revocation_flush_finishes_and_purges()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-reenable-flush-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        try
        {
            var nativeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var nativeRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var service = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
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

            // Opt out while the pre-revocation flush is still running.
            Assert.True(service.SetConsent(TelemetryConsent.Off));
            await flushTask;

            // Re-enabling must fail closed while the pre-revocation flush could still persist a late rename.
            Assert.False(service.SetConsent(TelemetryConsent.Full));
            Assert.Equal(TelemetryConsent.Off, service.Consent);

            // Simulate the late persist rename finishing right before the flush completes.
            File.WriteAllText(bufferPath, "{\"late\":true}");
            nativeRelease.TrySetResult(true);

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (File.Exists(bufferPath) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }
            Assert.False(File.Exists(bufferPath));

            // Once the pre-revocation flush finishes and cleans up, re-enabling succeeds cleanly.
            Assert.True(service.SetConsent(TelemetryConsent.Full));
            Assert.Equal(TelemetryConsent.Full, service.Consent);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Failed_enable_transition_rolls_back_native_and_managed_consent_to_off()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-enable-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        try
        {
            // Place a non-empty directory at bufferPath so native SetConsent(Analytics) fails on load/persist,
            // plus a sibling temp file to confirm rollback cleans up sibling temps as well.
            Directory.CreateDirectory(bufferPath);
            File.WriteAllText(Path.Combine(bufferPath, "keep.txt"), "x");
            var siblingTmp = Path.Combine(dir, ".telemetry-buffer.json.7777.tmp");
            File.WriteAllText(siblingTmp, "stale");

            using var service = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: false);

            Assert.False(service.SetConsent(TelemetryConsent.Full));
            Assert.Equal(TelemetryConsent.Off, service.Consent);
            Assert.Equal(AlUmmahNow.Core.TelemetryConsentState.Denied, service.NativeConsentForTesting);
            Assert.Equal(0, service.PendingCount);
            Assert.False(File.Exists(siblingTmp));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Revoked_flush_still_deletes_late_buffer_when_LastPurgeFailed_was_already_true()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-revoked-after-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        var siblingTmp = Path.Combine(dir, ".telemetry-buffer.json.7777.tmp");
        try
        {
            var nativeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var nativeRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var service = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
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

            // Plant a sibling temp file and make dir read-only so initial opt-out sets LastPurgeFailed = true.
            File.WriteAllText(siblingTmp, "stale");
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                Assert.False(service.SetConsent(TelemetryConsent.Off));
                Assert.True(service.LastPurgeFailed);
                await flushTask;
            }
            finally
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            // Plant the late buffer written by the in-flight flush and release it; ReleaseInFlightFlush
            // must still delete the buffer AND preserve the prior LastPurgeFailed = true flag.
            File.WriteAllText(bufferPath, "{\"late\":true}");
            nativeRelease.TrySetResult(true);

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (File.Exists(bufferPath) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }
            Assert.False(File.Exists(bufferPath));
            Assert.True(service.LastPurgeFailed);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SetConsent_enable_acquires_external_consent_lock_and_fails_closed_when_unavailable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-enable-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        try
        {
            var allowLock = false;
            var lockCalls = 0;
            using var service = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: false,
                externalConsentToken: () => "gen-1",
                acquireExternalConsentLock: () =>
                {
                    lockCalls++;
                    return allowLock ? new NoOpDisposable() : null;
                });

            Assert.False(service.SetConsent(TelemetryConsent.Full));
            Assert.Equal(1, lockCalls);
            Assert.Equal(TelemetryConsent.Off, service.Consent);
            Assert.Equal(AlUmmahNow.Core.TelemetryConsentState.Denied, service.NativeConsentForTesting);

            allowLock = true;
            Assert.True(service.SetConsent(TelemetryConsent.Full));
            Assert.Equal(2, lockCalls);
            Assert.Equal(TelemetryConsent.Full, service.Consent);
            Assert.Equal(AlUmmahNow.Core.TelemetryConsentState.Analytics, service.NativeConsentForTesting);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
