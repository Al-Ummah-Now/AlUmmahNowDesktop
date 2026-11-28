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
            Assert.Equal("https://[redacted-email]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/users/alice@example.com"));
            Assert.Equal("https://[redacted-email]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/users/alice%40example.com"));
            Assert.Equal("https://[redacted-email]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/users/alice%team@example.com"));
            Assert.Equal("https://[redacted-email]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/users/alice/bob@example.org"));
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
            Assert.Equal("' ([redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("' (/tmp/Client) Acquisition Plan'"));
            Assert.Equal("\" ([redacted-path]\"",
                CoreTelemetryService.SanitizeLocalPaths("\" (/tmp/Client) Acquisition Plan\""));
            Assert.Equal("\"My ([redacted-path]\"",
                CoreTelemetryService.SanitizeLocalPaths("\"My (/tmp/Client) Acquisition Plan\""));
            Assert.Equal("\"([redacted-path]\"",
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
            Assert.Equal("status \"ready; detail \"[redacted-path]\"",
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
            Assert.Equal("Failed for [redacted-email], [redacted-email], [redacted-email], 「[redacted-email]」, [redacted-email], [redacted-email], [redacted-email], [redacted-email], '[redacted-email], [redacted-email], [redacted-email], [redacted-email], [redacted-email], [redacted-email], [redacted-email], [redacted-email]",
                CoreTelemetryService.SanitizeLocalPaths("Failed for 李@example.com, محمد@مثال.مصر, user@bücher.example, 「李·明@例え.jp」, 李@例え.xn--p1ai, 李!明@example.com, mary'ann@example.com, 李·明@example.com, 'o'brien@example.com, {@example.com, 李，@example.com, 李，明@example.com, \"李·明\"@example.com, \"john doe\"@example.com, user@[192.0.2.1], user@[IPv6:2001:db8::1]"));
            Assert.DoesNotContain("example.com",
                CoreTelemetryService.SanitizeLocalPaths("Failed for \"a\\\"b\"@example.com"));
            Assert.Equal("Failed for [redacted-email], [redacted-email], [redacted-email], [redacted-email], [redacted-email], [redacted-email], [redacted-email], [redacted-email], [redacted-email], [redacted-email]",
                CoreTelemetryService.SanitizeLocalPaths("Failed for \"Private Name user@[IPv6:2001:db8::1]\"@example.com, \"john@doe.example\"@example.com, \"alice@example.com private\"@example.org, \"john\r\n doe\"@example.com, alice=tag@example.com, user@localhost, alice@intranet, \"Private Name alice/bob\"@example.com, alice@x, alice@3com"));
            Assert.Equal("[redacted-path]", CoreTelemetryService.SanitizeLocalPaths("alice/bob@example.com"));
            Assert.Equal("file [redacted-email]", CoreTelemetryService.SanitizeLocalPaths("file \"private name\"@localhost"));
            Assert.Equal("document [redacted-email]", CoreTelemetryService.SanitizeLocalPaths("document \"private name\"%40intranet"));
            Assert.Equal("[redacted-email]",
                CoreTelemetryService.SanitizeLocalPaths("\"Private Name https://example.com/api?q=secret\\\" tail\"@example.com"));
            Assert.Equal("alice@example.123😀", CoreTelemetryService.SanitizeLocalPaths("alice@example.123😀"));
            Assert.Equal("alice@😀", CoreTelemetryService.SanitizeLocalPaths("alice@😀"));
            Assert.Equal("[redacted-email]😀", CoreTelemetryService.SanitizeLocalPaths("alice@x😀"));
            Assert.Equal("[redacted-email]", CoreTelemetryService.SanitizeLocalPaths("alice@example.𐐷"));
            Assert.Equal("[redacted-email]", CoreTelemetryService.SanitizeLocalPaths("alice@123。example.com"));
            Assert.Equal("[redacted-email]", CoreTelemetryService.SanitizeLocalPaths("alice@example。com"));
            Assert.Equal("[redacted-email]", CoreTelemetryService.SanitizeLocalPaths("alice@example．com"));
            Assert.Equal("[redacted-email]", CoreTelemetryService.SanitizeLocalPaths("alice@example｡com"));
            Assert.Equal("[redacted-email]。", CoreTelemetryService.SanitizeLocalPaths("alice@example.com。"));
            Assert.Equal("[redacted-email]", CoreTelemetryService.SanitizeLocalPaths("alice.txt@example.com"));
            Assert.Equal("[redacted-email]", CoreTelemetryService.SanitizeLocalPaths("alice.txt+tag@example.com"));
            Assert.Equal("Failed for [redacted-email]", CoreTelemetryService.SanitizeLocalPaths("Failed for alice.txt@example.com"));
            Assert.Equal("[redacted-path] and [redacted-email]",
                CoreTelemetryService.SanitizeLocalPaths("Private Project.pdf and alice.txt@example.com"));
            Assert.Equal("file [redacted-path]", CoreTelemetryService.SanitizeLocalPaths("file alice.txt@example.com"));
            Assert.Equal("file [redacted-email]", CoreTelemetryService.SanitizeLocalPaths("file alice.txt@localhost"));
            Assert.Equal("file [redacted-path]", CoreTelemetryService.SanitizeLocalPaths("file Private Project alice.txt@example.com"));
            Assert.Equal("[redacted-path]", CoreTelemetryService.SanitizeLocalPaths("'alice.txt'@example.com"));
            Assert.Equal("[redacted-email]", CoreTelemetryService.SanitizeLocalPaths("alice@internal.pdf.example.org"));
            Assert.Equal("[redacted-email]", CoreTelemetryService.SanitizeLocalPaths("alice.txt@example.zip.private"));
            Assert.Equal("Authorization: Basic [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("Authorization: Basic YWxpY2U6c2VjcmV0"));
            Assert.Equal("Proxy-Authorization: Basic [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("Proxy-Authorization: Basic YWxpY2U6c2VjcmV0"));
            Assert.Equal("Authorization: Digest [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("Authorization: Digest username=\"alice\", response=\"deadbeef\""));
            Assert.Equal("{\"Authorization\": \"Basic [redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\": \"Basic YWxpY2U6c2VjcmV0\"}"));
            Assert.Equal("{\"Authorization\":\"[redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\":\"opaqueSecret123\"}"));
            Assert.Equal("{\"Proxy-Authorization\":\"Digest [redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"Proxy-Authorization\":\"Digest username=alice, response=deadbeef\"}"));
            Assert.Equal("{\"Authorization\":\"Digest [redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\":\"Digest username=\\\"alice\\\", response=\\\"deadbeef\\\"\"}"));
            Assert.Equal("{\"Authorization\":\"Bearer [redacted]\",\"status\":401}",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\":\"Bearer testSecret\",\"status\":401}"));
            Assert.Equal("{\"Authorization\":[\"Bearer [redacted]\"],\"status\":401}",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\":[\"Bearer testSecret\"],\"status\":401}"));
            Assert.Equal("{\"Authorization\":[[\"[redacted]\"],\"[redacted]\"]}",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\":[[\"firstValue\"],\"opaqueSecret123\"]}"));
            Assert.Equal("Authorization: [[redacted]]",
                CoreTelemetryService.SanitizeLocalPaths("Authorization: [opaqueSecret123]"));
            Assert.Equal("Authorization: [[redacted]]\r\n\t[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("Authorization: [firstValue]\r\n\topaqueSecret123"));
            Assert.Equal("Authorization: \"[redacted]\"\r\n\t[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("Authorization: \"firstValue\"\r\n\topaqueSecret123"));
            Assert.Equal("Authorization: \"[redacted]\" [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("Authorization: \"firstValue\" opaqueSecret123"));
            Assert.Equal("Authorization: \"[redacted]\" [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("Authorization: \"firstValue\" x opaqueSecret123"));
            Assert.Equal("{\"Authorization\": \"[redacted]\", \"status\": \"safe\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\": \"opaqueSecret123\", \"status\": \"safe\"}"));
            Assert.Equal("{\"Authorization\":\"[redacted]\"\n  , \"status\":\"safe\"\n}",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\":\"opaqueSecret123\"\n  , \"status\":\"safe\"\n}"));
            Assert.Equal("Authorization: \"[redacted]\" [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("Authorization: \"firstValue\" ]opaqueSecret123"));
            Assert.Equal("{\"Authorization\":[\"[redacted]\",\"[redacted]\",null]}",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\":[123,true,null]}"));
            Assert.Equal("{\"Authorization\":\"[redacted]\",\"status\":\"safe\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\":12345,\"status\":\"safe\"}"));
            Assert.Equal("{\"Authorization\":null,\"status\":\"safe\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\":null,\"status\":\"safe\"}"));
            Assert.Equal("\"Authorization\": \"[redacted]\" [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("\"Authorization\": \"firstValue\" ]opaqueSecret123"));
            Assert.Equal("{\"Authorization\":\"[redacted]\",\"status\":\"safe\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\":{\"token\":\"secret\"},\"status\":\"safe\"}"));
            Assert.Equal("{\"Authorization\":\"[redacted]\",\"status\":\"safe\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\":{\n\"token\":\"opaqueSecret123\"\n},\"status\":\"safe\"}"));
            Assert.Equal("{\"Authorization\":[\"[redacted]\"],\"status\":\"safe\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\":[{\"token\":\"start],opaqueSecret123\"}],\"status\":\"safe\"}"));
            var paddedUnmatched = "Authorization: {\nAuthorization: {\nAuthorization: {\nAuthorization: {\n" +
                new string('x', 5000) + "\n{\"Authorization\":{\n\"token\":\"opaqueSecret123\"\n},\"status\":\"safe\"}";
            var scrubbedPadded = CoreTelemetryService.SanitizeLocalPaths(paddedUnmatched);
            Assert.DoesNotContain("opaqueSecret123", scrubbedPadded);
            Assert.EndsWith("{\"Authorization\":\"[redacted]\",\"status\":\"safe\"}", scrubbedPadded);
            Assert.Equal("note \" {\"Authorization\":\"[redacted]\",\"status\":\"safe\"}",
                CoreTelemetryService.SanitizeLocalPaths("note \" {\"Authorization\":{\"token\":\n\"opaqueSecret123\"\n},\"status\":\"safe\"}"));
            Assert.Equal("{\"Authorization\":\"[redacted]\"",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\":{\n\"token\":\"opaqueSecret123\"\n"));
            Assert.Equal("prefix, \"Authorization\": \"[redacted]\" [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("prefix, \"Authorization\": \"firstValue\" }opaqueSecret123"));
            Assert.Equal("{\"status\":401,\"Authorization\":\"[redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"status\":401,\"Authorization\":\"opaqueSecret123\"}"));
            Assert.Equal("{\"Authorization\":\"[redacted]\"[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\":\"firstValue\", \"opaqueSecret123\"}"));
            Assert.Equal("note \"hi\", \"Authorization\": \"[redacted]\" [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("note \"hi\", \"Authorization\": \"firstValue\" } opaqueSecret123"));
            Assert.Equal("note: \"hi\", \"Authorization\": \"[redacted]\" [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("note: \"hi\", \"Authorization\": \"firstValue\" } opaqueSecret123"));
            Assert.Equal("request, \"note\": \"hi\", \"Authorization\": \"[redacted]\" [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("request, \"note\": \"hi\", \"Authorization\": \"firstValue\" } opaqueSecret123"));
            Assert.Equal("{\"bad\tkey\":\"hi\",\"Authorization\":\"[redacted]\" [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("{\"bad\tkey\":\"hi\",\"Authorization\":\"firstValue\" } opaqueSecret123"));
            var badEscapeScrubbed = CoreTelemetryService.SanitizeLocalPaths(
                "{\"bad\\qkey\":\"hi\",\"Authorization\":\"firstValue\" } opaqueSecret123");
            Assert.DoesNotContain("firstValue", badEscapeScrubbed);
            Assert.DoesNotContain("opaqueSecret123", badEscapeScrubbed);
            Assert.Equal("{\"\\q\":\"hi\",\"Authorization\":\"[redacted]\" [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("{\"\\q\":\"hi\",\"Authorization\":\"firstValue\" } opaqueSecret123"));
            Assert.Equal("{\"a\":[{],\"Authorization\":\"[redacted]\" [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("{\"a\":[{],\"Authorization\":\"firstValue\" } opaqueSecret123"));
            Assert.Equal("{\"Authorization\":\"[redacted]\" [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\":\"bad\\qval\" } opaqueSecret123"));
            Assert.Equal("{\"Authorization\":\"[redacted]\" [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("{\"\\Authorization\":\"firstValue\" } opaqueSecret123"));
            Assert.Equal("{\"Authorization\":\"[redacted]\" [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\":\"firstValue\" } opaqueSecret123"));
            Assert.Equal("{\"Authorization\":\"[redacted]\"[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\":\"firstValue\",\"a\":[{]} opaqueSecret123"));
            Assert.Equal("[{\"Authorization\":\"[redacted]\"},{\"status\":\"safe\"}]",
                CoreTelemetryService.SanitizeLocalPaths("[{\"Authorization\":\"opaqueSecret123\"},{\"status\":\"safe\"}]"));
            Assert.Equal("[{\"Authorization\":\"[redacted]\" }\n]",
                CoreTelemetryService.SanitizeLocalPaths("[{\"Authorization\":\"opaqueSecret123\" }\n]"));
            Assert.Equal("[{\"Authorization\":\"[redacted]\" [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("[{\"Authorization\":\"firstValue\" }\n] opaqueSecret123"));
            Assert.Equal("{\"Authorization\":\"[redacted]\"\n[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\":\"firstValue\"\n} opaqueSecret123"));
            Assert.Equal("{\"Authorization\":[\"[redacted]\",\n\"[redacted]\"",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\":[\"firstValue\",\n\"opaqueSecret123\""));
            Assert.Equal("{\"Authorization\":[\n\"[redacted]\"\n",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\":[\n\"opaqueSecret123\"\n"));
            Assert.Equal("{\"\\q\":\"hi\",\"Authorization\":[\"[redacted]\",\n\"[redacted]\"",
                CoreTelemetryService.SanitizeLocalPaths("{\"\\q\":\"hi\",\"Authorization\":[\"firstValue\",\n\"opaqueSecret123\""));
            var badEscapeArray = CoreTelemetryService.SanitizeLocalPaths(
                "{\"bad\\qkey\":\"hi\",\"Authorization\":[\"firstValue\",\n\"opaqueSecret123\"");
            Assert.DoesNotContain("firstValue", badEscapeArray);
            Assert.DoesNotContain("opaqueSecret123", badEscapeArray);
            Assert.Equal("{\"a\":1,\"b\":\"hi\",\"Authorization\":\"[redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"a\":1,\"b\":\"hi\",\"Authorization\":\"opaqueSecret123\"}"));
            Assert.Equal("{\"a\":{\"b\":1},\"Authorization\":\"[redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"a\":{\"b\":1},\"Authorization\":\"opaqueSecret123\"}"));
            var repeatedMalformedBack = "\"\n" + string.Concat(Enumerable.Repeat("}, \"Authorization\": ", 2000));
            Assert.NotNull(CoreTelemetryService.SanitizeLocalPaths(repeatedMalformedBack));
            var nestedJsonAuth = string.Concat(Enumerable.Repeat("{\"a\":", 2000)) + "{}" +
                string.Concat(Enumerable.Repeat(",\"Authorization\":\"secret\"}", 2000));
            var scrubbedNested = CoreTelemetryService.SanitizeLocalPaths(nestedJsonAuth);
            Assert.DoesNotContain("secret", scrubbedNested);
            Assert.EndsWith(",\"Authorization\":\"[redacted]\"}", scrubbedNested);
            Assert.Equal("https://discord.com/api/webhooks/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://discord." + "com/api/webhooks/123456789/fakeWebhookToken"));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken"));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken,remainingSecret123"));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/,remainingSecret123"));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken,(remainingSecret123)"));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken,(tag:opaqueSecret123)"));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken(tag:opaqueSecret123)"));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken(tag:opaqueSecret123,)"));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken,(tag:opaqueSecret123"));
            Assert.Equal("https://example.test/webhook/[redacted]?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken,?token=opaqueSecret123"));
            Assert.Equal("https://example.com/api,?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api,?token=opaqueSecret123"));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken;,(tag:opaqueSecret123)"));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken(:opaqueSecret123)"));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken,;opaqueSecret123"));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken,'opaqueSecret123"));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken,)opaqueSecret123"));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken)opaqueSecret123"));
            Assert.Equal("(https://example.test/webhook/[redacted])",
                CoreTelemetryService.SanitizeLocalPaths("(https://example.test/webhook/fakeToken)"));
            Assert.Equal("'https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("'https://example.test/webhook/fakeToken',)opaqueSecret123"));
            Assert.Equal("'https://example.test/webhook/[redacted]', next",
                CoreTelemetryService.SanitizeLocalPaths("'https://example.test/webhook/fakeToken', next"));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken,/opaqueSecret123"));
            Assert.Equal("https://example.com/api,public?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api,public?token=opaqueSecret123"));
            Assert.Equal("https://example.com/api,public#[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api,public#token=opaqueSecret123"));
            Assert.Equal("https://example.com/api,[redacted-path]?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api,/public?token=opaqueSecret123"));
            Assert.Equal("https://example.com/api,[redacted-path]?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api,private/Secret.mov?token=opaqueSecret123"));
            Assert.Equal("https://example.com([redacted-path])?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com(/Users/alice/Secret.mov)?token=opaqueSecret123"));
            Assert.Equal("https://example.com/api,[redacted-email]?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api,alice@example.com?token=opaqueSecret123"));
            Assert.Equal("https://example.com/api)public?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api)public?token=opaqueSecret123"));
            Assert.Equal("https://example.com/api(tag:val)?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api(tag:val)?token=opaqueSecret123"));
            Assert.Equal("https://example.com/api,[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api,public/webhook/opaqueSecret123"));
            Assert.Equal("https://example.com([redacted-path])[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com(/Users/alice/Secret.mov)/webhook/opaqueSecret123"));
            Assert.Equal("https://api.telegram.org/bot123:[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://api.telegram." + "org/bot123:opaqueSecret123,/Users/alice/Secret.mov"));
            Assert.Equal("https://example.com/api,https://other.com/x?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api,https://other.com/x?token=opaqueSecret123"));
            Assert.Equal("https://example.com/api,ssh://other.com/x?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api,ssh://other.com/x?token=opaqueSecret123"));
            Assert.Equal("https://example.com/api)[redacted-path]?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api)private/Secret?token=opaqueSecret123"));
            Assert.Equal("https://example.com/api][redacted-path]?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api]private/Secret?token=opaqueSecret123"));
            Assert.Equal("https://example.com/api}?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api}?token=opaqueSecret123"));
            Assert.Equal("https://example.com/users/{id}?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/users/{id}?token=opaqueSecret123"));
            Assert.Equal("https://example.com/api}[redacted-path]?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api}private/Secret?token=opaqueSecret123"));
            Assert.Equal("{https://example.test/webhook/[redacted]}",
                CoreTelemetryService.SanitizeLocalPaths("{https://example.test/webhook/fakeToken}"));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken{tag:opaqueSecret123}"));
            Assert.Equal("https://example.com/users/{id}/profile?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/users/{id}/profile?token=opaqueSecret123"));
            Assert.Equal("https://example.com/users/{[redacted-path]?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/users/{private/Secret.mov}?token=opaqueSecret123"));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken{tag:\"opaqueSecret123\"}"));
            Assert.Equal("https://example.com/api{tag:\"value\"}?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api{tag:\"value\"}?token=opaqueSecret123"));
            Assert.Equal("https://example.com/api{tag:\"value)\"}?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api{tag:\"value)\"}?token=opaqueSecret123"));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken{tag:\"first opaqueSecret123\"}"));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken{tag:'first opaqueSecret123'}"));
            Assert.Equal("https://example.com/api{tag:'value)'}?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api{tag:'value)'}?token=opaqueSecret123"));
            Assert.Equal("https://example.com/api{url:\"see https://other.com/x?[redacted]\"}?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api{url:\"see https://other.com/x?token=opaqueSecret123\"}?outer=value"));
            Assert.Equal("https://example.com/api{url:\"see https://other.com/x?[redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api{url:\"see https://other.com/x?token=opaqueSecret123\"}"));
            Assert.Equal("https://example.com/api{url:'see https://other.com/x?[redacted]'}?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api{url:'see https://other.com/x?token=opaqueSecret123'}?outer=value"));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken('a)first opaqueSecret123')"));
            Assert.Equal("'https://example.test/webhook/[redacted]'",
                CoreTelemetryService.SanitizeLocalPaths("'https://example.test/webhook/fakeToken('a)first opaqueSecret123')'"));
            Assert.Equal("\"https://example.test/webhook/[redacted]\"",
                CoreTelemetryService.SanitizeLocalPaths("\"https://example.test/webhook/fakeToken('a)first opaqueSecret123')\""));
            Assert.Equal("https://example.test/wiki/Writer_(O'Reilly)?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/wiki/Writer_(O'Reilly)?private=opaqueSecret123"));
            Assert.Equal("https://example.test/wiki/(Writers')?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/wiki/(Writers')?private=opaqueSecret123"));
            Assert.Equal("https://example.test/wiki/('unclosed)?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/wiki/('unclosed)?private=opaqueSecret123"));
            Assert.Equal("'see https://example.com/api{tag:\"value\"}?[redacted]'",
                CoreTelemetryService.SanitizeLocalPaths("'see https://example.com/api{tag:\"value\"}?outer=value'"));
            Assert.Equal("https://example.com/api{url:\"see https://other.com/webhook/[redacted]\"}?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "https://example.com/api{url:\"see https://other.com/webhook/fakeToken('a)first opaqueSecret123')\"}?outer=value"));
            Assert.Equal("https://example.com/api{url:\"see https://other.com/webhook/[redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths(
                    "https://example.com/api{url:\"see https://other.com/webhook/fakeToken('a)first opaqueSecret123')\"}"));
            Assert.Equal("{'a':'https://example.test/webhook/[redacted]'}",
                CoreTelemetryService.SanitizeLocalPaths("{'a':'https://example.test/webhook/fakeToken('}"));
            Assert.Equal("{\"a\":\"https://example.test/webhook/[redacted]\",\"b\":\"public-text\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"a\":\"https://example.test/webhook/fakeToken(\",\"b\":\"public-text\"}"));
            Assert.Equal("[\"https://a.test/webhook/[redacted]\",\"https://other.com/webhook/[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"https://a.test/webhook/tok(\",\"https://other.com/webhook/tok2('x)secret')\"]"));
            Assert.Equal("'https://example.test/webhook/[redacted]'",
                CoreTelemetryService.SanitizeLocalPaths(
                    "'https://example.test/webhook/fakeToken('a)first opaqueSecret123',https://other.test/x)'"));
            Assert.Equal("'https://example.test/webhook/[redacted]', more)'",
                CoreTelemetryService.SanitizeLocalPaths(
                    "'https://example.test/webhook/fakeToken('a)first opaqueSecret123', more)'"));
            Assert.Equal("[\"https://example.test/webhook/[redacted]\",\")public-text\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"https://example.test/webhook/fakeToken(\",\")public-text\"]"));
            Assert.Equal("'https://example.test/webhook/[redacted]'",
                CoreTelemetryService.SanitizeLocalPaths(
                    "'https://example.test/webhook/fakeToken(' opaqueSecret123')'"));
            Assert.Equal("'https://example.test/webhook/[redacted]'",
                CoreTelemetryService.SanitizeLocalPaths(
                    "'https://example.test/webhook/fakeToken(' )opaqueSecret123')'"));
            Assert.Equal("'https://example.test/webhook/[redacted]'",
                CoreTelemetryService.SanitizeLocalPaths(
                    "'https://example.test/webhook/fakeToken(') opaqueSecret123')'"));
            Assert.Equal("'https://example.test/webhook/[redacted]'",
                CoreTelemetryService.SanitizeLocalPaths(
                    "'https://example.test/webhook/fakeToken(')first opaqueSecret123')'"));
            Assert.Equal("'https://example.test/webhook/[redacted]'",
                CoreTelemetryService.SanitizeLocalPaths(
                    "'https://example.test/webhook/fakeToken('} opaqueSecret123')'"));
            Assert.Equal("[\"https://example.test/webhook/[redacted]\",123,\")public-text\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"https://example.test/webhook/fakeToken(\",123,\")public-text\"]"));
            Assert.Equal("[{\"a\":\"https://example.test/webhook/[redacted]\"},\")public\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[{\"a\":\"https://example.test/webhook/fakeToken(\"},\")public\"]"));
            Assert.Equal("['https://example.test/webhook/[redacted]']",
                CoreTelemetryService.SanitizeLocalPaths(
                    "['https://example.test/webhook/fakeToken(',123, opaqueSecret123')']"));
            Assert.Equal("['https://example.test/webhook/[redacted]']",
                CoreTelemetryService.SanitizeLocalPaths(
                    "['https://example.test/webhook/fakeToken(',{ opaqueSecret123')']"));
            Assert.Equal("[\n  \"https://example.test/webhook/[redacted]\",123,\")public-text\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\n  \"https://example.test/webhook/fakeToken(\",123,\")public-text\"]"));
            Assert.Equal("'https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "'https://example.test/webhook/fakeToken('unclosed opaqueSecret123"));
            Assert.Equal("[\"https://example.test/webhook/[redacted]\",123,\")public\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"https://example.test/webhook/fakeToken('open\",123,\")public\"]"));
            Assert.Equal("[\"https://example.test/webhook/[redacted]\",\")public-text\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"https://example.test/webhook/fakeToken('a\",\")public-text\"]"));
            Assert.Equal("[\"see https://example.test/webhook/[redacted]\",123,\")public\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"see https://example.test/webhook/fakeToken('open\",123,\")public\"]"));
            Assert.Equal("[\"see https://example.test/webhook/[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"see https://example.test/webhook/fakeToken('a)first opaqueSecret123')\"]"));
            Assert.Equal("[\"https://example.test/webhook/[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"https://example.test/webhook/fakeToken(\\\"opaqueSecret123)\"]"));
            Assert.Equal("[\"https://example.test/webhook/[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"https://example.test/webhook/fakeToken(\\\"opaqueSecret123\\\")\"]"));
            Assert.Equal("[\"https://example.test/webhook/[redacted]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"https://example.test/webhook/fakeToken(\\\"first opaqueSecret123\\\")\",\"public\"]"));
            Assert.Equal("[\"https://example.test/webhook/[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"https://example.test/webhook/fakeToken\\\"opaqueSecret123\"]"));
            Assert.Equal("[\"https://example.com/api{1:\\\"see https://other.com/x?[redacted]}?[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"https://example.com/api{1:\\\"see https://other.com/x?token=opaqueSecret123\\\"}?outer=value\"]"));
            Assert.Equal("[\"https://example.com/api?[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"https://example.com/api?token=opaqueSecret123\\\"moreSecret\"]"));
            Assert.Equal("[\"https://example.com/api?[redacted]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"https://example.com/api?token=first\\\",opaqueSecret123\",\"public\"]"));
            Assert.Equal("[\"https://example.com/api{1:\\\"see https://other.com/x?[redacted]}\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"https://example.com/api{1:\\\"see https://other.com/x?token=opaqueSecret123\\\"}\"]"));
            Assert.Equal("https://example.com/api?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api?token=first\\\"opaqueSecret123"));
            Assert.Equal("[\"see \\\"example https://example.com/api?[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"see \\\"example https://example.com/api?token=first\\\",opaqueSecret123\"]"));
            Assert.Equal("[\"https://example.com/api?[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"https://example.com/api?token=first\\\">opaqueSecret123\"]"));
            Assert.Equal("[\"https://example.com/api?[redacted]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"https://example.com/api?token=first<opaqueSecret123\",\"public\"]"));
            Assert.Equal("[\"https://example.com/api(x)?[redacted]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"https://example.com/api(x)?token=first<opaqueSecret123\",\"public\"]"));
            Assert.Equal("[\"see <https://example.com/api?[redacted]> public\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"see <https://example.com/api?token=secret> public\"]"));
            Assert.Equal("[\"[redacted-path]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"Acquisition.mov\"]"));
            Assert.Equal("[\"[redacted-path]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"report.pdf\",\"public\"]"));
            Assert.Equal("[\"https://example.com/api?[redacted]\",\"[redacted-path]\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"https://example.com/api?token=opaqueSecret123\",\"Quarterly Report.pdf\"]"));
            Assert.Equal("[\"see <https://example.com/api?[redacted]> public\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"see <https://example.com/api?token=first<opaqueSecret123> public\"]"));
            Assert.Equal("<https://example.com/api?[redacted]> public",
                CoreTelemetryService.SanitizeLocalPaths(
                    "<https://example.com/api?token=first<opaqueSecret123> public"));
            Assert.Equal("[\"Bearer [redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"Bearer abc\\\"opaqueSecret123\"]"));
            Assert.Equal("[\"Bearer [redacted]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"Bearer abc\\\"opaqueSecret123\",\"public\"]"));
            Assert.Equal("[\"Bearer [redacted]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"Bearer abc\\\\\",\"public\"]"));
            Assert.Equal("[\"[redacted-path]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"C://Secret/Acquisition.mov\",\"public\"]"));
            Assert.Equal("[\"Failed reading [redacted-path]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"Failed reading C://Secret/Acquisition.mov\",\"public\"]"));
            Assert.Equal("[\"[redacted-path]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"/Users/alice/Secret.mov\",\"public\"]"));
            Assert.Equal("[\"[redacted-path]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"relative/Secret.mov\",\"public\"]"));
            Assert.Equal("[\"https://example.com/api?[redacted]\",\"[redacted-path]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths(
                    "[\"https://example.com/api?token=opaqueSecret123\",\"C://Secret/Acquisition.mov\",\"public\"]"));
            Assert.Equal("[\"hello \\\"world\\\"\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"hello \\\"world\\\"\",\"public\"]"));
            Assert.Equal("[\"/\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"/\",\"public\"]"));
            Assert.Equal("[\"[redacted-path]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"\\/private/secret\",\"public\"]"));
            Assert.Equal("[\"\\/\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"\\/\",\"public\"]"));
            Assert.Equal("[\"relative/\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"relative/\",\"public\"]"));
            Assert.Equal("[\"relative\\/\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"relative\\/\",\"public\"]"));
            Assert.Equal("[\"relative\\u002F\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"relative\\u002F\",\"public\"]"));
            Assert.Equal("[\"[redacted-path]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"\\u002Fprivate\",\"public\"]"));
            Assert.Equal("[\"[redacted-path]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"\\u002fAcquisition\",\"public\"]"));
            Assert.Equal("[\"[redacted-path]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"C\\u003A\\u002FSecret\",\"public\"]"));
            Assert.Equal("[\"[redacted-path]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"\\u007E\\u002FSecret\",\"public\"]"));
            Assert.Equal("[\"[redacted-path]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"\\u005C\\u005CServer\\u005CShare\",\"public\"]"));
            Assert.Equal("[\"hello\\nworld\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"hello\\nworld\",\"public\"]"));
            Assert.Equal("[\"path=[redacted-path]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"path=secret\",\"public\"]"));
            Assert.Equal("[\"path=[redacted-path]\",\"path=[redacted-path]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"path=alpha\",\"path=beta\"]"));
            Assert.Equal("[\"hello\\nworld\"] [redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("[\"hello\\nworld\"] relative\\secret"));
            Assert.Equal("[\"hello\\nworld\",\"public\"] [redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("[\"hello\\nworld\",\"public\"] relative\\Secret"));
            Assert.Equal("[\"public\",\"[redacted-path] attached\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"public\",\"report.pdf attached\"]"));
            Assert.Equal("{\"message\":\"[redacted-path] now\",\"keep\":\"public\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"message\":\"see report.pdf now\",\"keep\":\"public\"}"));
            Assert.Equal("{\"status\":\"[redacted-path] pending\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"status\":\"secret.mov pending\"}"));
            Assert.Equal("[\"path=[redacted-path]\",{\"keep\":\"[redacted-path] attached\"}]",
                CoreTelemetryService.SanitizeLocalPaths("[\"path=alpha\",{\"keep\":\"beta.pdf attached\"}]"));
            Assert.Equal("[\"read this\",{\"x\":\"[redacted-path] attached\"}]",
                CoreTelemetryService.SanitizeLocalPaths("[\"read this\",{\"x\":\"report.pdf attached\"}]"));
            Assert.Equal("{\"'\":\"[redacted-path]' pending\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"'\":\"secret.pdf' pending\"}"));
            Assert.Equal("[\"hello\\npath=[redacted-path]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"hello\\npath=Acquisition\"]"));
            Assert.Equal("[\"hello\\npath=[redacted-path]\\npath=[redacted-path]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"hello\\npath=alpha\\npath=beta\"]"));
            Assert.Equal("[\"path\\u003d[redacted-path]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"path\\u003dAcquisition\"]"));
            Assert.Equal("[\"[redacted-path]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"\\\\Secret\"]"));
            Assert.Equal("[\"[redacted-path]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"\\u005CSecret\",\"public\"]"));
            Assert.Equal("[\"\\\\\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"\\\\\",\"public\"]"));
            Assert.Equal("[\"path=[redacted-path]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"path=Project\\u005EAcquisition\"]"));
            Assert.Equal("[\"path=[redacted-path]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"path=alpha\\u001fAcquisition\"]"));
            Assert.Equal("[\"path=[redacted-path]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"path=alpha\\u001eAcquisition\"]"));
            Assert.Equal("{\"file \":\"Acquisition\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"file \":\"Acquisition\"}"));
            Assert.Equal("{\"path \":\"public\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"path \":\"public\"}"));
            Assert.Equal("[\"file [redacted-path]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"file 'Secret'\",\"public\"]"));
            Assert.Equal("read [redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("read 'Top \"Secret\" file.pdf'"));
            Assert.Equal("[\"[redacted-path] pending\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"secret\\u002epdf pending\"]"));
            Assert.Equal("[\"[redacted-path]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"secret\\u002epdf\"]"));
            Assert.Equal("[\"read [redacted-path] pending\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"read Private\\\"Merger.custom pending\"]"));
            Assert.Equal("[\"file [redacted-path]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"file \\u0027Acquisition\\u0027\"]"));
            Assert.Equal("[\"file [redacted-path]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"file \\\"Acquisition\\\"\"]"));
            Assert.Equal("[\"file [redacted-path]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"file \\u0022Acquisition\\u0022\"]"));
            Assert.Equal("[\"file [redacted-path]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"file \\\"Acquisition\\\"\",\"public\"]"));
            Assert.Equal("[\"file [redacted-path] and path [redacted-path]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"file \\\"Acquisition\\\" and path \\\"Secret\\\"\"]"));
            Assert.Equal("[\"[redacted-path] pending\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"\\u0027Secret.custom\\u0027 pending\"]"));
            Assert.Equal("[\"\\\"[redacted-path]\\\" pending\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"\\\"Secret.custom\\\" pending\"]"));
            Assert.Equal("[\"\\u0022[redacted-path]\\u0022 pending\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"\\u0022Secret.custom\\u0022 pending\"]"));
            Assert.Equal("[\"[redacted-path]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"Secret\\\"file.custom\"]"));
            Assert.Equal("[\"file [redacted-path]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"file \\\"O'Brien Acquisition\\\"\"]"));
            Assert.Equal("[\"file [redacted-path]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"file \\\"Acquisition's Plan\\\"\"]"));
            Assert.Equal("[\"file [redacted-path]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"file 'Acquisition\\u0027s Plan'\"]"));
            Assert.Equal("{\"message\":\"open [redacted-path]\",\"status\":\"public\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"message\":\"open file:///Users/Alice/Secret.mov\",\"status\":\"public\"}"));
            Assert.Equal("{\"message\":\"open [redacted-path]\",\"status\":\"public\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"message\":\"open file:\\/\\/\\/Users\\/Alice\\/Secret.mov\",\"status\":\"public\"}"));
            Assert.Equal("[\"https:\\/\\/example.com\\/public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"https:\\/\\/example.com\\/public\"]"));
            Assert.Equal("[\"https://example.com/api?[redacted]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"https:\\/\\/example.com\\/api?token=opaqueSecret123\",\"public\"]"));
            Assert.Equal("[\"https://example.com/api?[redacted]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"https:\\/\\/example.com\\/api\\u003ftoken=opaqueSecret123\",\"public\"]"));
            Assert.Equal("[\"https://example.com/api#[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"https://example.com/api\\u0023opaqueSecret123\"]"));
            Assert.Equal("[\"https://example.com/api?[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"https://example.com/api\\u003ftoken=opaqueSecret123\"]"));
            Assert.Equal("[\"https://example.test/webhook/[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"https://example.test/webhook/fake\\u0054oken\"]"));
            Assert.Equal("[\"https://example.com/api?[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"https://example.com/api\\u003Ftoken=prefix\\u0020opaqueSecret123\"]"));
            Assert.Equal("[\"https://example.com/api?[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"https://example.com/api?token=first\\u0020secondSecret\"]"));
            Assert.Equal("[\"https://example.com/api#[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"https://example.com/api\\u0023prefix\\u0020opaqueSecret123\"]"));
            Assert.Equal("[\"https://example.test/webhook/[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"https://example.test/webhook/\\u00e9PrivateToken\"]"));
            Assert.Equal("[\"HTTP/2\"]", CoreTelemetryService.SanitizeLocalPaths("[\"HTTP/2\"]"));
            Assert.Equal("[\"HTTP/1.1\"]", CoreTelemetryService.SanitizeLocalPaths("[\"HTTP/1.1\"]"));
            Assert.Equal("[\"HTTP\\/2\"]", CoreTelemetryService.SanitizeLocalPaths("[\"HTTP\\/2\"]"));
            Assert.Equal("[\"TLS/1.3\"]", CoreTelemetryService.SanitizeLocalPaths("[\"TLS/1.3\"]"));
            Assert.Equal("[\"SSL/3.0\"]", CoreTelemetryService.SanitizeLocalPaths("[\"SSL/3.0\"]"));
            Assert.Equal("[\"[redacted-path]\"]", CoreTelemetryService.SanitizeLocalPaths("[\"HTTP/2/Secret.mov\"]"));
            var manyApostrophes = "file '" + string.Concat(Enumerable.Repeat("a'", 5000)) + "end'";
            Assert.Equal("file [redacted-path]", CoreTelemetryService.SanitizeLocalPaths(manyApostrophes));
            var manyUnclosedApostrophes = "file '" + string.Concat(Enumerable.Repeat("a'", 5000)) + "end";
            Assert.Equal(manyUnclosedApostrophes, CoreTelemetryService.SanitizeLocalPaths(manyUnclosedApostrophes));
            Assert.Equal("[\"https://example.test/api(v1)?[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"https://example.test/api\\u0028v1\\u0029?token=opaqueSecret123\"]"));
            Assert.Equal("[\"https://example.com/api?[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"https://example.com/api?token=first secondSecret\"]"));
            Assert.Equal("[\"https://example.com/api?[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"https://example.com/api?token=first\u00A0secondSecret\"]"));
            Assert.Equal("[\"https://example.com/api#[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"https://example.com/api#first secondSecret\"]"));
            Assert.Equal("[\"https://example.com/api?[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"https:\\/\\/example.com\\/api?token=first secondSecret\"]"));
            Assert.Equal("[\"https://example.com/webhook/[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"https://example.com/webhook/first\\u0022secondSecret\"]"));
            Assert.Equal("[\"prefix\\n[redacted-email]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"prefix\\nalice@example.com\"]"));
            Assert.Equal("[\"\\u0020[redacted-email] ok\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"\\u0020alice@example.com ok\"]"));
            Assert.Equal("[\"https://a.test\",\"open [redacted-path]\",\"https://b.test\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"https://a.test\",\"open /foo\",\"https://b.test\"]"));
            Assert.Equal("[\"https://example.com/webhook/[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"https://example.com/webhook/first\\u00A0secondSecret\"]"));
            Assert.Equal("[\"https://example.com/webhook/[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"https://example.com/webhook/\\u00A0secondSecret\"]"));
            Assert.Equal("[\" https://example.com/api?[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\" https://example.com/api?token=first secondSecret\"]"));
            Assert.Equal("[\"fetch https://example.com/api?[redacted]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"fetch https://example.com/api?token=first secondSecret\"]"));
            Assert.Equal("[\"fetch https://example.com/api?[redacted]\",\"public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"fetch https://example.com/api?token=first secondSecret\",\"public\"]"));
            Assert.Equal("[\"[redacted-email] ok\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"al\\u0069\\u0063e@example.com ok\"]"));
            Assert.Equal("[\"prefix\\n[redacted-email]\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"prefix\\nal\\u0069\\u0063e@example.com\"]"));
            Assert.Equal("[\"\\u0020[redacted-email] ok\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"\\u0020al\\u0069\\u0063e@example.com ok\"]"));
            Assert.Equal("[\"[redacted-email] ok\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"alice\\u0040example.com ok\"]"));
            Assert.Equal("[\"[redacted-email] ok\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"al\\u0069\\u0063e\\u0040example\\u002e\\u0063om ok\"]"));
            Assert.Equal("[\"see <https://example.com/api?[redacted]> public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"see <https://example.com/api?token=first secondSecret> public\"]"));
            Assert.Equal("[\"https://example.com/docs/[redacted] public\",\"sibling\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"https://example.com/docs/[redacted] public\",\"sibling\"]"));
            Assert.Equal("[\"[redacted-email] ok\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"alice\\u0040exa\\u0301mple.com ok\"]"));
            Assert.Equal("{\"detail\":\"[redacted-email] failed\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"detail\":\"alice@exa\\u0301mple.test failed\"}"));
            Assert.Equal("{\"detail\":\"[redacted-email] failed\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"detail\":\"\\\"Jane Doe\\\"@example.test failed\"}"));
            Assert.Equal("{\"detail\":\"[redacted-email] failed\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"detail\":\"\\u0022Jane Doe\\u0022\\u0040exa\\u0301mple.test failed\"}"));
            Assert.Equal("[\"see \\u003chttps://example.com/api?[redacted]\\u003e public\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"see \\u003chttps://example.com/api?token=first secondSecret\\u003e public\"]"));
            Assert.Equal("{\"detail\":\"<https://example.test/webhook/[redacted]>\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"detail\":\"<https://example.test/webhook/SECRET TAIL>\"}"));
            Assert.Equal("{\"detail\":\"see <https://example.test/webhook/[redacted]> public\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"detail\":\"see <https://example.test/webhook/SECRET TAIL> public\"}"));
            Assert.Equal("{\"detail\":\"see \\u003chttps://example.test/webhook/[redacted]\\u003e public\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"detail\":\"see \\u003chttps://example.test/webhook/SECRET TAIL\\u003e public\"}"));
            Assert.Equal("{\"message\":\"Authorization: Bearer [redacted]\",\"status\":\"ok\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"message\":\"Authorization: Bearer secret\",\"status\":\"ok\"}"));
            Assert.Equal("{\"message\":\"Authorization:\\u0020Bearer [redacted]\",\"status\":\"ok\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"message\":\"Authorization:\\u0020Bearer\\u0020secret\",\"status\":\"ok\"}"));
            Assert.Equal("{\"message\":\"Bearer [redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"message\":\"Bearer\\u0020secret\"}"));
            Assert.Equal("{\"message\":\"Bearer [redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"message\":\"Bearer\\tsecret\"}"));
            Assert.Equal("{\"message\":\"Bearer [redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"message\":\"Bearer\\u00A0secret\"}"));
            Assert.Equal("{\"message\":\"Authorization: Bearer [redacted]\\nX-Public: ok\",\"status\":\"ok\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"message\":\"Authorization: Bearer secret\\nX-Public: ok\",\"status\":\"ok\"}"));
            Assert.Equal("{\"message\":\"Basic [redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"message\":\"Basic\\u0020dXNlcjpwYXNz\"}"));
            Assert.Equal("{\"message\":\"https://example.com/path name?[redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"message\":\"https://example.com/path name?token=private\"}"));
            Assert.Equal("{\"message\":\"https://example.com/path\\u0020name?[redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"message\":\"https://example.com/path\\u0020name?token=private\"}"));
            Assert.Equal("{\"message\":\"see https://example.com/public Why did it fail?\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"message\":\"see https://example.com/public Why did it fail?\"}"));
            Assert.Equal("{\"message\":\"see https://example.com/public issue #123\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"message\":\"see https://example.com/public issue #123\"}"));
            Assert.Equal("\"Authorization\\u003a [redacted]\"",
                CoreTelemetryService.SanitizeLocalPaths("\"Authorization\\u003a secret\""));
            Assert.Equal("\"Authorization\\u003d[redacted]\"",
                CoreTelemetryService.SanitizeLocalPaths("\"Authorization\\u003dsecret\""));
            Assert.Equal("headers=\"Authorization\\u003a [redacted]\"",
                CoreTelemetryService.SanitizeLocalPaths("headers=\"Authorization\\u003a opaqueSecret123\""));
            Assert.Equal("headers=\"Authorization\\u003d [redacted]\" status=ok",
                CoreTelemetryService.SanitizeLocalPaths("headers=\"Authorization\\u003d opaqueSecret123\" status=ok"));
            Assert.Equal("received (\"Authorization\\u003a [redacted]\")",
                CoreTelemetryService.SanitizeLocalPaths("received (\"Authorization\\u003a opaqueSecret123\")"));
            Assert.Equal("[redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("Authorization\\u003a opaqueSecret123"));
            Assert.Equal("{\"msg\":\"Basic [redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"msg\":\"Basic YWI6\\u00632VjcmV0\"}"));
            Assert.Equal("received \"Authorizatio\\u006e: [redacted]\"",
                CoreTelemetryService.SanitizeLocalPaths("received \"Authorizatio\\u006e: opaqueSecret123\""));
            Assert.Equal("Cookie: [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("Cookie: session=opaqueSecret123"));
            Assert.Equal("Set-Cookie: [redacted]\r\nNext: ok",
                CoreTelemetryService.SanitizeLocalPaths("Set-Cookie: session=opaqueSecret123; HttpOnly\r\nNext: ok"));
            Assert.Equal("{\"Cookie\":\"[redacted]\",\"status\":\"ok\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"Cooki\\u0065\":\"session=opaqueSecret123\",\"status\":\"ok\"}"));
            Assert.Equal("{\"m\":\"\\\"Bearer [redacted]\\\"tail\",\"next\":1}",
                CoreTelemetryService.SanitizeLocalPaths("{\"m\":\"\\\"Bearer opaqueSecret123\\\"tail\",\"next\":1}"));
            Assert.Equal("{\"m\":\"Bearer [redacted]\\nX-Public: ok\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"m\":\"Bearer secret\\nX-Public: ok\"}"));
            Assert.Equal("{\"Cookie \":\"[redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"Cookie \":\"session=opaqueSecret123\"}"));
            Assert.Equal("[[\"Cookie\",\"[redacted]\"]]",
                CoreTelemetryService.SanitizeLocalPaths("[[\"Cookie\",\"session=opaqueSecret123\"]]"));
            Assert.Equal("[[\"Authorization\",\"[redacted]\"]]",
                CoreTelemetryService.SanitizeLocalPaths("[[\"Authorization\",\"opaqueSecret123\"]]"));
            Assert.Equal("{\"m\":\"[[\\\"Cookie\\\",\\\"[redacted]\\\"]]\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"m\":\"[[\\\"Cookie\\\",\\\"opaqueSecret123\\\"]]\"}"));
            Assert.Equal("[\"Authorization\",\"Cookie\",\"Accept\"]",
                CoreTelemetryService.SanitizeLocalPaths("[\"Authorization\",\"Cookie\",\"Accept\"]"));
            Assert.Equal("{\\\"Authorization\\\":\\\"[redacted]\\\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\\\"Authorization\\\":\\\"opaqueSecret123\\\"}"));
            Assert.Equal("{\\\"Cookie\\\":\\\"[redacted]\\\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\\\"Cookie\\\":\\\"opaqueSecret123\\\"}"));
            Assert.Equal("{\"m\":\"auth = {\\\"a\\\":1,\\\"Cookie\\\":\\\"[redacted]\\\"}; next\",\"next\":1}",
                CoreTelemetryService.SanitizeLocalPaths("{\"m\":\"auth = {\\\"a\\\":1,\\\"Cookie\\\":\\\"opaqueSecret123\\\"}; next\",\"next\":1}"));
            Assert.Equal("{\"m\":\"Bearer \\\"[redacted]\\\"tail\",\"next\":1}",
                CoreTelemetryService.SanitizeLocalPaths("{\"m\":\"Bearer \\\"opaqueSecret123\\\"tail\",\"next\":1}"));
            Assert.Equal("{\"a\":\"Bearer \",\"b\":\"safe\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"a\":\"Bearer \",\"b\":\"safe\"}"));
            Assert.Equal("{\"m\":\"literal=\\\"[redacted-path]\\\"tail\",\"next\":1}",
                CoreTelemetryService.SanitizeLocalPaths("{\"m\":\"literal=\\\"file:///tmp/private.mov\\\"tail\",\"next\":1}"));
            Assert.Equal("{\"Cookie\\t\":\"[redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"Cookie\\t\":\"opaqueSecret123\"}"));
            Assert.Equal("{\"\\tCookie\":\"[redacted]\",\"Cookie\":\"[redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"\\tCookie\":\"opaqueSecret123\",\"Cookie\":\"otherSecret\"}"));
            Assert.Equal("[[\"Cookie\\t\",\"[redacted]\"]]",
                CoreTelemetryService.SanitizeLocalPaths("[[\"Cookie\\t\",\"opaqueSecret123\"]]"));
            Assert.DoesNotContain("opaqueSecret123",
                CoreTelemetryService.SanitizeLocalPaths("{\"Cookie\t\":\"opaqueSecret123\"}"), StringComparison.Ordinal);
            Assert.Equal("{\\\"\tCookie\t\\\":\\\"[redacted]\\\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\\\"\tCookie\t\\\":\\\"opaqueSecret123\\\"}"));
            Assert.Equal("[[\\\"Authorization\\\",\\\"[redacted]\\\"\\n]]",
                CoreTelemetryService.SanitizeLocalPaths("[[\\\"Authorization\\\",\\\"opaqueSecret123\\\"\\n]]"));
            Assert.Equal("{\"m\":\"{\\\"Authorization\\\":\\\"[redacted]\\\"} after\",\"next\":1}",
                CoreTelemetryService.SanitizeLocalPaths("{\"m\":\"{\\\"Authorization\\\":\\\"opaqueSecret123\\\"} after\",\"next\":1}"));
            Assert.Equal("{\"m\":\"{\\\"Authorization\\\":\\\"[redacted]\\\"}after\",\"next\":1}",
                CoreTelemetryService.SanitizeLocalPaths("{\"m\":\"{\\\"Authorization\\\":\\\"opaqueSecret123\\\"}after\",\"next\":1}"));
            Assert.Equal("{\"cookie\":\"[redacted]\",\"Cookie\":\"[redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"\\u0063ookie\":\"firstSecret\",\"Cookie\":\"secondSecret\"}"));
            var repeatedVaultPrefix = string.Concat(Enumerable.Repeat("\u001EAUNURLx ", 5000)) + "https://example/path";
            Assert.Equal(repeatedVaultPrefix, CoreTelemetryService.SanitizeLocalPaths(repeatedVaultPrefix));
            Assert.Equal("{\\\"Authorization\\\":[\\\"[redacted]\\\"]}",
                CoreTelemetryService.SanitizeLocalPaths("{\\\"Authorization\\\":[123456789]}"));
            Assert.Equal("{\\\"Authorization\\\":\\\"[redacted]\\\",\\\"next\\\":1}",
                CoreTelemetryService.SanitizeLocalPaths("{\\\"Authorization\\\":123456789,\\\"next\\\":1}"));
            Assert.Equal("{\\\"Authorization\\\":\\\"[redacted]\\\",\\\"next\\\":1}",
                CoreTelemetryService.SanitizeLocalPaths("{\\\"Authorization\\\":{\\\"token\\\":\\\"secret\\\"},\\\"next\\\":1}"));
            Assert.Equal("[[\\\"Authorization\\\",[\\\"[redacted]\\\"]]]",
                CoreTelemetryService.SanitizeLocalPaths("[[\\\"Authorization\\\",[\\\"opaqueSecret123\\\"]]]"));
            Assert.Equal("[[\\\"Authorization\\\",[\\\"[redacted]\\\"]]]",
                CoreTelemetryService.SanitizeLocalPaths("[[\\\"Authorization\\\",[123456789]]]"));
            Assert.Equal("[[\\\"Authorization\\\",\\\"[redacted]\\\"]]",
                CoreTelemetryService.SanitizeLocalPaths("[[\\\"Authorization\\\",{\\\"token\\\":\\\"opaqueSecret123\\\"}]]"));
            Assert.Equal("{\\\"Authorization\\\":[\n  \\\"[redacted]\\\"\n],\\\"next\\\":1}",
                CoreTelemetryService.SanitizeLocalPaths("{\\\"Authorization\\\":[\n  123456789\n],\\\"next\\\":1}"));
            Assert.Equal("{\\\"Authorization\\\":\n\\\"[redacted]\\\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\\\"Authorization\\\":\n\\\"opaqueSecret123\\\"}"));
            var paddedCookie = "{\"Cookie" + new string(' ', 60) + "\":\"opaqueSecret123\"}";
            Assert.Equal("{\"Cookie" + new string(' ', 60) + "\":\"[redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths(paddedCookie));
            Assert.Equal("{\"m\":\"before {\\\"Cookie\\\":\\\"[redacted]\\\"} after\",\"next\":1}",
                CoreTelemetryService.SanitizeLocalPaths("{\"m\":\"before {\\\"Cookie\\\":\\\"opaqueSecret123\\\"} after\",\"next\":1}"));
            var malformedEscapedArray = "{\\\"Authorization\\\":[" + string.Concat(Enumerable.Repeat("{,", 2000)) + "]";
            Assert.StartsWith("{\\\"Authorization\\\":[\\\"[redacted]\\\"", CoreTelemetryService.SanitizeLocalPaths(malformedEscapedArray));
            Assert.Equal("Bearer [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("Bearer " + new string('A', 10000)));
            Assert.Equal("{\"m\":\"literal=\\\"[redacted-path]\\\"tail\",\"next\":1}",
                CoreTelemetryService.SanitizeLocalPaths("{\"m\":\"literal=\\\"test\\\\stuff\\\"tail\",\"next\":1}"));
            Assert.Equal("{\"m\":\"{\\\"path\\\":\\\"[redacted-path]\\\"}\",\"next\":1}",
                CoreTelemetryService.SanitizeLocalPaths("{\"m\":\"{\\\"path\\\":\\\"/Users/Alice/Secret.mov\\\"}\",\"next\":1}"));
            var repeatedUnclosedAngles = string.Concat(Enumerable.Repeat("<https://example.test/webhooks/token ", 500));
            var expectedUnclosedAngles = string.Concat(Enumerable.Repeat("<https://example.test/webhooks/[redacted] ", 500));
            Assert.Equal(expectedUnclosedAngles, CoreTelemetryService.SanitizeLocalPaths(repeatedUnclosedAngles));
            var spacedHashes = "\"" + new string(' ', 2000) + "https://example.test/path " + string.Concat(Enumerable.Repeat("# ", 2000)) + "\"";
            Assert.Equal(spacedHashes, CoreTelemetryService.SanitizeLocalPaths(spacedHashes));
            var scrubbedProps = CoreTelemetryService.SerializeProperties(new Dictionary<string, object>
            {
                ["detail"] = "Authorization: Bearer secret",
                ["path"] = "/Users/Alice/Secret.mov",
                ["/Users/Alice/Acquisition.mov"] = 1,
                ["/Users/Bob/Merger.mov"] = 2,
                ["Authorization"] = "opaqueSecret123",
                ["Cookie"] = "session=opaqueSecret123",
                ["Set-Cookie"] = "sid=opaqueSecret456",
                ["uri"] = new Uri("https://example.com/api?token=secret"),
                ["count"] = 2,
                ["ok"] = true
            });
            Assert.NotNull(scrubbedProps);
            Assert.DoesNotContain("secret", scrubbedProps!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("session=", scrubbedProps!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Alice", scrubbedProps!, StringComparison.Ordinal);
            Assert.DoesNotContain("Bob", scrubbedProps!, StringComparison.Ordinal);
            Assert.DoesNotContain("Acquisition", scrubbedProps!, StringComparison.Ordinal);
            Assert.DoesNotContain("Merger", scrubbedProps!, StringComparison.Ordinal);
            Assert.Contains("\"[redacted-path]\":1", scrubbedProps!, StringComparison.Ordinal);
            Assert.Contains("\"[redacted-path]#2\":2", scrubbedProps!, StringComparison.Ordinal);
            Assert.Contains("\"detail\":\"Authorization: Bearer [redacted]\"", scrubbedProps!, StringComparison.Ordinal);
            Assert.Contains("\"path\":\"[redacted-path]\"", scrubbedProps!, StringComparison.Ordinal);
            Assert.Contains("\"Authorization\":\"[redacted]\"", scrubbedProps!, StringComparison.Ordinal);
            Assert.Contains("\"Cookie\":\"[redacted]\"", scrubbedProps!, StringComparison.Ordinal);
            Assert.Contains("\"Set-Cookie\":\"[redacted]\"", scrubbedProps!, StringComparison.Ordinal);
            Assert.Contains("\"uri\":\"https://example.com/api?[redacted]\"", scrubbedProps!, StringComparison.Ordinal);
            Assert.Contains("\"count\":2", scrubbedProps!, StringComparison.Ordinal);
            Assert.Contains("\"ok\":true", scrubbedProps!, StringComparison.Ordinal);
            var manyCollidingKeys = new Dictionary<string, object>(5000);
            for (var k = 0; k < 5000; k++) manyCollidingKeys[$"/home/alice/private-{k}"] = k;
            var manyCollidingJson = CoreTelemetryService.SerializeProperties(manyCollidingKeys);
            Assert.NotNull(manyCollidingJson);
            Assert.DoesNotContain("alice", manyCollidingJson!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"[redacted-path]#5000\":4999", manyCollidingJson!, StringComparison.Ordinal);
            var manyFileUris = string.Join("\n", Enumerable.Repeat("open file:///Users/Alice/Secret.mov", 2000));
            var expectedFileUris = string.Join("\n", Enumerable.Repeat("open [redacted-path]", 2000));
            Assert.Equal(expectedFileUris, CoreTelemetryService.SanitizeLocalPaths(manyFileUris));
            var manyPublic = "[" + string.Join(",", Enumerable.Repeat("\"public\"", 2000)) + "]";
            Assert.Equal(manyPublic, CoreTelemetryService.SanitizeLocalPaths(manyPublic));
            var manyNewlines = "[" + string.Join(",", Enumerable.Repeat("\"hello\\nworld\"", 2000)) + "]";
            Assert.Equal(manyNewlines, CoreTelemetryService.SanitizeLocalPaths(manyNewlines));
            Assert.Equal("Failed reading [redacted-path]",
                CoreTelemetryService.SanitizeLocalPaths("Failed reading /Users/alice/My \"Secret\" Folder/file.mov"));
            var manySlashes = "[\"" + new string('/', 10_000) + "\",\"public\"]";
            Assert.Equal(manySlashes, CoreTelemetryService.SanitizeLocalPaths(manySlashes));
            var repeatedRejectedSchemes = string.Concat(Enumerable.Repeat("https:// ", 2000));
            Assert.Equal("http[redacted-path] ", CoreTelemetryService.SanitizeLocalPaths(repeatedRejectedSchemes));
            var repeatedEqualsQuotedUrls = string.Join("=", Enumerable.Repeat("https://a.test/x{a:\"https://b.test/x\"}", 500));
            Assert.Equal(repeatedEqualsQuotedUrls, CoreTelemetryService.SanitizeLocalPaths(repeatedEqualsQuotedUrls));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken{tag:\"a)opaqueSecret123\"}suffixSecret"));
            Assert.Equal("https://example.test/webhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken{abc://opaqueSecret123}"));
            Assert.Equal("https://example.com/users/[redacted-path]}?[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/users/{Secret.mov}?token=opaqueSecret123"));
            Assert.Equal("https://example.com/api{url:\"https://other.com/x,public?[redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/api{url:\"https://other.com/x,public?token=opaqueSecret123\"}"));
            var nestedParens = "https://example.com/a" + new string('(', 500) + string.Concat(Enumerable.Repeat("b/", 500)) + new string(')', 500);
            Assert.Equal(nestedParens, CoreTelemetryService.SanitizeLocalPaths(nestedParens));
            Assert.Equal("https://api.telegram.org/bot123:[redacted]/sendMessage",
                CoreTelemetryService.SanitizeLocalPaths("https://api.telegram." + "org/bot123:%2FopaqueSecret123/sendMessage"));
            Assert.Equal("https://example.test/webhook%2F[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook%2FfakeToken"));
            var repeatedBot = "https://api.telegram." + "org/bot1:a" + string.Concat(Enumerable.Repeat("%2Fbot2:b", 500));
            Assert.Equal("https://api.telegram.org/bot1:[redacted]", CoreTelemetryService.SanitizeLocalPaths(repeatedBot));
            Assert.Equal("https://example.test/%77ebhook/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/%77ebhook/fakeToken"));
            Assert.Equal("https://example.test/webhook/[redacted], next",
                CoreTelemetryService.SanitizeLocalPaths("https://example.test/webhook/fakeToken, next"));
            Assert.Equal("https://hooks.slack.com/services/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://hooks.slack." + "com/services/T0000/B0000/fakeWebhookToken"));
            Assert.Equal("https://hooks.slack.com:443/services/[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("https://hooks.slack." + "com:443/services/T0000/B0000/fakeWebhookToken"));
            Assert.Equal("https://api.telegram.org/bot123456:[redacted]/getMe",
                CoreTelemetryService.SanitizeLocalPaths("https://api.telegram." + "org/bot123456:fakeBotToken/getMe"));
            Assert.Equal("Authorization: [[redacted]]\r\nNextHeader: safe",
                CoreTelemetryService.SanitizeLocalPaths("Authorization: [firstValue]\r\nNextHeader: safe"));
            Assert.Equal("{\n  \"Authorization\": \"[redacted]\"\n  }",
                CoreTelemetryService.SanitizeLocalPaths("{\n  \"Authorization\": \"opaqueSecret123\"\n  }"));
            Assert.Equal("Authorization:\r\n\t[redacted]",
                CoreTelemetryService.SanitizeLocalPaths("Authorization:\r\n\topaqueSecret123"));
            Assert.Equal("Authorization: Digest [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("Authorization: Digest username=\"alice\",\r\n response=\"deadbeef\""));
            Assert.Equal("Authorization:\r\nNextHeader: safe",
                CoreTelemetryService.SanitizeLocalPaths("Authorization:\r\nNextHeader: safe"));
            Assert.Equal("{\"Authorization\":\"[redacted]\",\"status\":401}",
                CoreTelemetryService.SanitizeLocalPaths("{\"\\u0041uthorization\":\"opaqueSecret123\",\"status\":401}"));
            Assert.Equal("{\"Authorization\":\"[redacted]\"}",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authoriz\\u0061tion\":\"opaqueSecret123\"}"));
            Assert.Equal("{\"Authorization\": \n \"[redacted]\",\"status\":401}",
                CoreTelemetryService.SanitizeLocalPaths("{\"Authorization\": \n \"opaqueSecret123\",\"status\":401}"));
            Assert.Equal("\"Authorization\": [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("\"Authorization\": opaqueSecret123 nextPart"));
            Assert.Equal("Authorization: [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("Authorization: opaqueSecret123 nextPart"));
            Assert.Equal("Authorization: [redacted]",
                CoreTelemetryService.SanitizeLocalPaths("Authorization: X-Secret secondword"));
            Assert.Equal("Basic [redacted]", CoreTelemetryService.SanitizeLocalPaths("Basic YTpi"));
            Assert.Equal("Basic [redacted].", CoreTelemetryService.SanitizeLocalPaths("Basic YTpi."));
            Assert.Equal("Basic [redacted]", CoreTelemetryService.SanitizeLocalPaths("Basic YTo="));
            Assert.Equal("Basic YTp=", CoreTelemetryService.SanitizeLocalPaths("Basic YTp="));
            Assert.Equal("Basic [redacted]", CoreTelemetryService.SanitizeLocalPaths("Basic Og=="));
            Assert.Equal("Basic [redacted]", CoreTelemetryService.SanitizeLocalPaths("Basic ajpqampq"));
            Assert.Equal("BASIC [redacted]", CoreTelemetryService.SanitizeLocalPaths("BASIC YWxpY2U6c2VjcmV0"));
            Assert.Equal("Basic [redacted]", CoreTelemetryService.SanitizeLocalPaths("Basic YWxpY2U6Y2Fm6Q=="));
            Assert.Equal("Basic YWJjZGU=", CoreTelemetryService.SanitizeLocalPaths("Basic YWJjZGU="));
            Assert.Equal("Basic YTpiCQ", CoreTelemetryService.SanitizeLocalPaths("Basic YTpiCQ"));
            Assert.Equal("Basic auth failed", CoreTelemetryService.SanitizeLocalPaths("Basic auth failed"));
            var repeatedFilenameEmails = string.Concat(Enumerable.Repeat("alice.txt@example.org ", 5_000));
            Assert.Equal(string.Concat(Enumerable.Repeat("[redacted-email] ", 5_000)),
                CoreTelemetryService.SanitizeLocalPaths(repeatedFilenameEmails));
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
    public void AddBreadcrumb_category_and_ReportCrash_kind_redact_pii()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-cat-pii-" + Guid.NewGuid().ToString("N"));
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
            service.AddBreadcrumb("alice@example.com", "Opening /Users/alice/Private/video.mp4");
            service.ReportCrash("bob@example.com", "Failed to read /Users/alice/Secret.mov");
            var bufText = File.ReadAllText(bufferPath);
            Assert.DoesNotContain("alice@example.com", bufText);
            Assert.DoesNotContain("bob@example.com", bufText);
            Assert.DoesNotContain("/Users/alice", bufText);
            Assert.Contains("[redacted-email]", bufText);
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

    [Fact]
    public async Task FlushAsync_releases_external_consent_lock_after_dispatch_so_cross_process_opt_out_succeeds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-flush-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var lockPath = Path.Combine(dir, "telemetry-consent.lock");
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        try
        {
            string? sharedToken = "enabled:gen1";
            var nativeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var nativeRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var service = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken),
                acquireExternalConsentLock: () =>
                {
                    try
                    {
                        return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    }
                    catch (IOException)
                    {
                        return null;
                    }
                })
            {
                ExternalPollIntervalMsForTesting = 15,
                FlushOverrideForTesting = () =>
                {
                    nativeStarted.TrySetResult(true);
                    return nativeRelease.Task;
                }
            };

            var flushTask = service.FlushAsync();
            await nativeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            // Cross-process opt-out must acquire the exclusive OS lock immediately even while nativeFlush stays open.
            using (var processBLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Volatile.Write(ref sharedToken, null);
            }

            // Process A's in-flight monitor detects the cross-process opt-out and revokes native consent promptly.
            await flushTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(TelemetryConsent.Off, service.Consent);
            Assert.Equal(AlUmmahNow.Core.TelemetryConsentState.Denied, service.NativeConsentForTesting);

            nativeRelease.TrySetResult(true);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Dispose_during_flush_still_detects_cross_process_opt_out_and_revokes_native_consent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-dispose-monitor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        try
        {
            string? sharedToken = "enabled:gen-dispose";
            var nativeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var nativeRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var service = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken),
                acquireExternalConsentLock: () => new NoOpDisposable())
            {
                ExternalPollIntervalMsForTesting = 15,
                FlushOverrideForTesting = () =>
                {
                    nativeStarted.TrySetResult(true);
                    return nativeRelease.Task;
                }
            };

            var flushTask = service.FlushAsync();
            await nativeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            // Dispose instance A while its native flush is still running, keeping the native client alive.
            service.Dispose();
            await flushTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(service.IsClientDisposedForTesting);

            // Instance B opts out while A's flush is still open; A's monitor must still revoke native consent.
            Volatile.Write(ref sharedToken, null);
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (service.NativeConsentForTesting != AlUmmahNow.Core.TelemetryConsentState.Denied &&
                   DateTime.UtcNow < deadline)
            {
                await Task.Delay(15);
            }
            Assert.Equal(AlUmmahNow.Core.TelemetryConsentState.Denied, service.NativeConsentForTesting);

            nativeRelease.TrySetResult(true);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task FlushAsync_retries_in_same_session_when_external_consent_lock_is_released()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-flush-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        try
        {
            var lockBusy = 1;
            var flushed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var service = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => "enabled:gen-retry",
                acquireExternalConsentLock: () => Volatile.Read(ref lockBusy) == 1 ? null : new NoOpDisposable())
            {
                ExternalLockRetryDelayMsForTesting = 20,
                FlushOverrideForTesting = () =>
                {
                    flushed.TrySetResult(true);
                    return Task.CompletedTask;
                }
            };

            // Startup flush misses the lock while another instance holds it.
            await service.FlushAsync();
            Assert.False(flushed.Task.IsCompleted);

            // Releasing the lock lets the scheduled retry activate and flush in the same session.
            Volatile.Write(ref lockBusy, 0);
            Assert.True(await flushed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(TelemetryConsent.Full, service.Consent);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class BlockingToStringValue
    {
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;

        public BlockingToStringValue(ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            _entered = entered;
            _release = release;
        }

        public override string ToString()
        {
            _entered.Set();
            _release.Wait(TimeSpan.FromSeconds(5));
            return "pre-revoke-value";
        }
    }

    [Fact]
    public async Task Track_drops_event_when_consent_is_revoked_and_reenabled_during_serialization()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-epoch-track-" + Guid.NewGuid().ToString("N"));
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

            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var blocker = new BlockingToStringValue(entered, release);

            var worker = Task.Run(() =>
                service.Track("screen_view", new Dictionary<string, object> { ["detail"] = blocker }));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));

            Assert.True(service.SetConsent(TelemetryConsent.Off));
            Assert.True(service.SetConsent(TelemetryConsent.Full));
            release.Set();
            await worker.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(0, service.PendingCount);

            // Analytics must also be dropped when consent transitions through CrashOnly and back to Full.
            using var entered2 = new ManualResetEventSlim(false);
            using var release2 = new ManualResetEventSlim(false);
            var blocker2 = new BlockingToStringValue(entered2, release2);
            var worker2 = Task.Run(() =>
                service.Track("screen_view", new Dictionary<string, object> { ["detail"] = blocker2 }));
            Assert.True(entered2.Wait(TimeSpan.FromSeconds(2)));

            Assert.True(service.SetConsent(TelemetryConsent.CrashOnly));
            Assert.True(service.SetConsent(TelemetryConsent.Full));
            release2.Set();
            await worker2.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(0, service.PendingCount);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Disabled_startup_purge_preserves_unrelated_json_files_sharing_stem_prefix()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-unrelated-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        var unrelatedBackup = Path.Combine(dir, "telemetry-buffer.backup.json");
        var staleGenBuffer = Path.Combine(dir, "telemetry-buffer.oldgen.abcdef123456.json");
        try
        {
            File.WriteAllText(unrelatedBackup, "{\"user\":\"backup\"}");
            File.WriteAllText(staleGenBuffer, "{\"events\":[]}");
            using (var service = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: false))
            {
                Assert.Equal(TelemetryConsent.Off, service.Consent);
            }
            Assert.True(File.Exists(unrelatedBackup));
            Assert.False(File.Exists(staleGenBuffer));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SanitizeLocalPaths_NormalizesSlackDotSegments_PreservesPathColons_AndRedactsContentUris()
    {
        Assert.Equal(
            "https://hooks.slack.com/./services/[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("https://hooks.slack.com/./services/T000/B000/fakeWebhookSecret"));
        Assert.Equal(
            "https://hooks.slack.com/a/../services/[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("https://hooks.slack.com/a/../services/T000/B000/fakeWebhookSecret"));
        Assert.Equal(
            "https://hooks.slack.com/%2e/services/[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("https://hooks.slack.com/%2e/services/T000/B000/fakeWebhookSecret"));
        Assert.Equal(
            "https://hooks.slack.com//services/[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("https://hooks.slack.com//services/T000/B000/fakeWebhookSecret"));
        Assert.Equal(
            "https://example.com/projectc:/foo",
            CoreTelemetryService.SanitizeLocalPaths("https://example.com/projectc:/foo"));
        Assert.Equal(
            "https://example.com/my-c:/foo",
            CoreTelemetryService.SanitizeLocalPaths("https://example.com/my-c:/foo"));
        Assert.Equal(
            "[redacted-path]",
            CoreTelemetryService.SanitizeLocalPaths(
                "content://com.android.externalstorage.documents/document/primary%3ADownload%2FPrivateAcquisition.mov"));
        Assert.Equal(
            "{\"uri\":\"[redacted-path]\",\"ok\":true}",
            CoreTelemetryService.SanitizeLocalPaths(
                "{\"uri\":\"content://com.android.externalstorage.documents/document/primary%3ADownload%2FPrivateAcquisition.mov\",\"ok\":true}"));
        Assert.Equal(
            "{\"uri\":\"[redacted-path]\",\"ok\":true}",
            CoreTelemetryService.SanitizeLocalPaths(
                "{\"uri\":\"content:\\/\\/com.android.externalstorage.documents\\/document\\/primary%3ADownload%2FPrivateAcquisition.mov\",\"ok\":true}"));
        Assert.Equal(
            "https://proxy.example.com/https://hooks.slack.com/./services/[redacted]",
            CoreTelemetryService.SanitizeLocalPaths(
                "https://proxy.example.com/https://hooks.slack.com/./services/T000/B000/fakeWebhookSecret"));
        var repeatedSlackHosts =
            "https://hooks.slack.com" + string.Concat(Enumerable.Repeat("/https://hooks.slack.com", 2000)) + "/x";
        Assert.Equal(repeatedSlackHosts, CoreTelemetryService.SanitizeLocalPaths(repeatedSlackHosts));
        Assert.Equal("X-API-Key: [redacted]", CoreTelemetryService.SanitizeLocalPaths("X-API-Key: secret123"));
        Assert.Equal("Api-Key: [redacted]", CoreTelemetryService.SanitizeLocalPaths("Api-Key: secret123"));
        Assert.Equal("X-Auth-Token: [redacted]", CoreTelemetryService.SanitizeLocalPaths("X-Auth-Token: secret123"));
        Assert.Equal("X-Access-Token: [redacted]", CoreTelemetryService.SanitizeLocalPaths("X-Access-Token: secret123"));
        Assert.Equal(
            "{\"X-API-Key\":\"[redacted]\",\"Api-Key\":\"[redacted]\",\"X-Auth-Token\":\"[redacted]\"}",
            CoreTelemetryService.SanitizeLocalPaths(
                "{\"X-API-Key\":\"secret123\",\"Api-Key\":\"secret456\",\"X-Auth-Token\":\"tok789\"}"));
        Assert.Equal(
            "https://example.com:/api?[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("https://example.com:/api?token=secret123"));
        Assert.Equal(
            "https://example.com:?[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("https://example.com:?token=secret123"));
        Assert.Equal(
            "https://example.com:#[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("https://example.com:#secret123"));
        var propJson = CoreTelemetryService.SerializeProperties(new Dictionary<string, object>
        {
            ["X-API-Key"] = "apiSecret789",
            ["X-Auth-Token"] = "authTok999",
            ["screen"] = "home",
        });
        Assert.NotNull(propJson);
        Assert.DoesNotContain("apiSecret789", propJson);
        Assert.DoesNotContain("authTok999", propJson);
        Assert.Contains("\"X-API-Key\":\"[redacted]\"", propJson);
        Assert.Contains("\"X-Auth-Token\":\"[redacted]\"", propJson);
        Assert.Equal("[redacted-path]", CoreTelemetryService.SanitizeLocalPaths("HTTP/1/PrivateReorganization"));
        Assert.Equal("([redacted-path])", CoreTelemetryService.SanitizeLocalPaths("(HTTP/1/PrivateReorganization)"));
        Assert.Equal("HTTP/1.1 503 Service Unavailable", CoreTelemetryService.SanitizeLocalPaths("HTTP/1.1 503 Service Unavailable"));
        Assert.Equal(
            "open \"[redacted-path]\" then completed in 3.14 seconds",
            CoreTelemetryService.SanitizeLocalPaths("open \"proposal.pdf\" then completed in 3.14 seconds"));
        Assert.Equal(
            "open failed after 3.14 seconds",
            CoreTelemetryService.SanitizeLocalPaths("open failed after 3.14 seconds"));
        Assert.Equal("failed [redacted-path]", CoreTelemetryService.SanitizeLocalPaths("failed file:/Users/alice/secret.txt"));
        Assert.Equal(
            "{\"uri\":\"[redacted-path]\",\"ok\":true}",
            CoreTelemetryService.SanitizeLocalPaths("{\"uri\":\"file:\\/Users\\/alice\\/secret.txt\",\"ok\":true}"));
        Assert.Equal(
            "https://registry.npmjs.org/@angular/core",
            CoreTelemetryService.SanitizeLocalPaths("https://registry.npmjs.org/@angular/core"));
        Assert.Equal(
            "https://registry.npmjs.org/%40angular/core",
            CoreTelemetryService.SanitizeLocalPaths("https://registry.npmjs.org/%40angular/core"));
        var dottedNumbers = "read " + string.Concat(Enumerable.Repeat("1.", 20000)) + "1";
        Assert.Equal(dottedNumbers, CoreTelemetryService.SanitizeLocalPaths(dottedNumbers));
    }

    [Fact]
    public void Concurrent_same_generation_instances_preserve_each_others_queued_crashes_and_adopt_on_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-multi-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        string? sharedToken = "enabled:gen1";
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
                externalConsentToken: () => Volatile.Read(ref sharedToken));

            string bufferA;
            using (var instanceA = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken)))
            {
                bufferA = instanceA.ActiveBufferPathForTesting!;
                Assert.NotEqual(bufferA, instanceB.ActiveBufferPathForTesting);
                instanceA.ReportCrash("TestCrash", "queued by A");
                Assert.True(File.Exists(bufferA));
                Assert.Contains("TestCrash", File.ReadAllText(bufferA));
            }

            // Instance B has an empty queue; persisting B must NOT erase instance A's queued crash.
            instanceB.Persist();
            Assert.True(File.Exists(bufferA));
            Assert.Contains("TestCrash", File.ReadAllText(bufferA));

            // A malformed sibling orphan in the same generation must NOT poison valid queued crashes.
            var malformedOrphan = Path.Combine(dir, "telemetry-buffer.gen1.invalid01.json");
            File.WriteAllText(malformedOrphan, "{\"events\":[{}],\"breadcrumbs\":[]}");

            // A newly started instance C in the same generation adopts exited A's queued crash without touching live B.
            using var instanceC = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken));
            Assert.Equal(1, instanceC.PendingCount);
            Assert.False(File.Exists(bufferA));
            Assert.False(File.Exists(malformedOrphan));
            Assert.True(File.Exists(instanceB.ActiveBufferPathForTesting!));

            if (!OperatingSystem.IsWindows())
            {
                // An orphan buffer that cannot be read due to permissions must NOT be unlinked.
                var unreadableOrphan = Path.Combine(dir, "telemetry-buffer.gen1.unreadable01.json");
                File.WriteAllText(unreadableOrphan, "{\"events\":[{\"id\":\"keep-me\"}],\"breadcrumbs\":[]}");
                File.SetUnixFileMode(unreadableOrphan, UnixFileMode.None);
                try
                {
                    using var instanceD = new CoreTelemetryService(
                        platform: "linux",
                        appVersion: "1.0.0",
                        locale: "en",
                        videosApiBaseUrl: null,
                        userAgent: "AlUmmahNowLinux/1.0.0",
                        bufferPath: bufferPath,
                        initialEnabled: true,
                        externalConsentToken: () => Volatile.Read(ref sharedToken));
                    Assert.True(File.Exists(unreadableOrphan));
                }
                finally
                {
                    File.SetUnixFileMode(unreadableOrphan, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Second_opt_in_reuses_generation_and_startup_recovers_after_slow_flush_releases_lock()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-second-optin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        string? sharedToken = null;
        try
        {
            using var instanceA = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: false,
                externalConsentToken: () => Volatile.Read(ref sharedToken));
            using var instanceB = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: false,
                externalConsentToken: () => Volatile.Read(ref sharedToken));

            // Instance A enables diagnostics and queues a crash.
            Volatile.Write(ref sharedToken, "enabled:gen1");
            Assert.True(instanceA.SetConsent(TelemetryConsent.Full));
            instanceA.ReportCrash("CrashA", "queued by A");
            var bufferA = instanceA.ActiveBufferPathForTesting!;
            Assert.True(File.Exists(bufferA));

            // Instance B (still showing Off) enables diagnostics: joined-generation claim token keeps gen1 alive.
            Volatile.Write(ref sharedToken, "enabled:gen1:claim2");
            Assert.True(instanceB.SetConsent(TelemetryConsent.Full));
            Assert.True(File.Exists(bufferA));
            Assert.Equal(TelemetryConsent.Full, instanceA.Consent);
            Assert.Equal(1, instanceA.PendingCount);

            // Simulate a slow flush holding the external consent lock while a new instance C starts up.
            var lockBusy = 1;
            using var instanceC = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken),
                acquireExternalConsentLock: () => Volatile.Read(ref lockBusy) == 1 ? null : new NoOpDisposable());
            Assert.Equal(TelemetryConsent.Off, instanceC.Consent);
            await instanceC.FlushAsync();
            Assert.Equal(TelemetryConsent.Off, instanceC.Consent);

            // Once the slow flush releases the lock, instance C activates cleanly on its next check/flush.
            Volatile.Write(ref lockBusy, 0);
            Assert.Equal(TelemetryConsent.Full, instanceC.Consent);
            instanceC.Track("screen_view");
            Assert.Equal(1, instanceC.PendingCount);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SanitizeLocalPaths_redacts_quoted_bearer_basic_slash_emails_and_linear_ip_literals()
    {
        Assert.Equal(
            "received Bearer \"[redacted]\" during handshake",
            CoreTelemetryService.SanitizeLocalPaths("received Bearer \"opaqueSecret123\" during handshake"));
        Assert.Equal(
            "received Bearer '[redacted]' during handshake",
            CoreTelemetryService.SanitizeLocalPaths("received Bearer 'opaqueSecret123' during handshake"));
        Assert.Equal(
            "{\"m\":\"Basic \\\"[redacted]\\\"\"}",
            CoreTelemetryService.SanitizeLocalPaths("{\"m\":\"Basic \\\"dXNlcjpwYXNz\\\"\"}"));
        Assert.Equal(
            "received Basic \"[redacted]\" during handshake",
            CoreTelemetryService.SanitizeLocalPaths("received Basic \"dXNlcjpwYXNz\" during handshake"));
        Assert.Equal(
            "received Basic '[redacted]' during handshake",
            CoreTelemetryService.SanitizeLocalPaths("received Basic 'dXNlcjpwYXNz' during handshake"));
        Assert.Equal(
            "Bearer \"hello world\"",
            CoreTelemetryService.SanitizeLocalPaths("Bearer \"hello world\""));
        Assert.Equal(
            "Bearer Bearer \"[redacted]\"",
            CoreTelemetryService.SanitizeLocalPaths("Bearer Bearer \"opaqueSecret123\""));
        Assert.Equal(
            "received Bearer \"[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("received Bearer \"opaqueSecret123"));
        Assert.Equal(
            "Bearer Bearer \"[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("Bearer Bearer \"opaqueSecret123"));
        Assert.Equal(
            "received Bearer '[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("received Bearer 'opaqueSecret123"));
        Assert.Equal(
            "Bearer Bearer [redacted]",
            CoreTelemetryService.SanitizeLocalPaths("Bearer Bearer opaqueSecret123"));
        Assert.Equal(
            "Bearer Basic \"[redacted]\"",
            CoreTelemetryService.SanitizeLocalPaths("Bearer Basic \"dXNlcjpwYXNz\""));
        Assert.Equal(
            "Bearer [redacted]\n",
            CoreTelemetryService.SanitizeLocalPaths("Bearer Basic\n"));
        Assert.Equal(
            "Bearer [redacted]\n",
            CoreTelemetryService.SanitizeLocalPaths("Bearer Bearer\n"));
        Assert.Equal(
            "Bearer [redacted] auth",
            CoreTelemetryService.SanitizeLocalPaths("Bearer Basic auth"));
        Assert.Equal(
            "Basic Basic \"[redacted]\"",
            CoreTelemetryService.SanitizeLocalPaths("Basic Basic \"dXNlcjpwYXNz\""));
        Assert.Equal(
            "Basic Basic [redacted]",
            CoreTelemetryService.SanitizeLocalPaths("Basic Basic dXNlcjpwYXNz"));
        Assert.Equal(
            "{\"m\":\"Basic Basic \\\"[redacted]\\\"\"}",
            CoreTelemetryService.SanitizeLocalPaths("{\"m\":\"Basic Basic \\\"dXNlcjpwYXNz\\\"\"}"));
        Assert.Equal(
            "{\"msg\":\"Bearer Basic \\u0022[redacted]\\\"\"}",
            CoreTelemetryService.SanitizeLocalPaths("{\"msg\":\"Bearer Basic \\u0022dTpw\\\"\"}"));
        Assert.Equal(
            "Basic \\u0022[redacted]\\\"",
            CoreTelemetryService.SanitizeLocalPaths("Basic \\u0022dTpw\\\""));
        Assert.Equal(
            "received Bearer \"[redacted] ",
            CoreTelemetryService.SanitizeLocalPaths("received Bearer \"opaqueSecret123 "));
        Assert.Equal(
            "Bearer \"hello, world\"",
            CoreTelemetryService.SanitizeLocalPaths("Bearer \"hello, world\""));
        Assert.Equal(
            "{\"msg\":\"Bearer \\\"hello world\\\"\"}",
            CoreTelemetryService.SanitizeLocalPaths("{\"msg\":\"Bearer \\\"hello world\\\"\"}"));
        Assert.Equal(
            "{\"Bearer \":\"ok\"}",
            CoreTelemetryService.SanitizeLocalPaths("{\"Bearer \":\"ok\"}"));
        Assert.Equal(
            "Basic \"[redacted]\nnext line",
            CoreTelemetryService.SanitizeLocalPaths("Basic \"dTpw\nnext line"));
        Assert.Equal(
            "Basic \"[redacted] ",
            CoreTelemetryService.SanitizeLocalPaths("Basic \"dTpw "));
        Assert.Equal(
            "Bearer \"[redacted] \"",
            CoreTelemetryService.SanitizeLocalPaths("Bearer \"opaqueSecret123 \""));
        Assert.Equal(
            "Basic \"[redacted] \"",
            CoreTelemetryService.SanitizeLocalPaths("Basic \"dTpw \""));
        var bearerEscapedNewline = CoreTelemetryService.SanitizeLocalPaths("Bearer \\\"opaqueSecret123\\nnext");
        Assert.DoesNotContain("opaqueSecret123", bearerEscapedNewline);
        Assert.StartsWith("Bearer \\\"", bearerEscapedNewline);
        var basicEscapedNewline = CoreTelemetryService.SanitizeLocalPaths("Basic \\\"dTpw\\nnext");
        Assert.DoesNotContain("dTpw", basicEscapedNewline);
        Assert.StartsWith("Basic \\\"", basicEscapedNewline);
        Assert.Equal(
            "Bearer \\\"[redacted]\\\"",
            CoreTelemetryService.SanitizeLocalPaths("Bearer \\\"opaqueSecret123\\\""));
        Assert.Equal(
            "{\"msg\":\"Bearer [redacted]\\\" next\"}",
            CoreTelemetryService.SanitizeLocalPaths("{\"msg\":\"Bearer opaqueSecret123\\\" next\"}"));
        Assert.Equal(
            "https://[redacted-email]",
            CoreTelemetryService.SanitizeLocalPaths("https://example.org/users/alice/@example.com"));
        Assert.Equal(
            "https://[redacted-email]",
            CoreTelemetryService.SanitizeLocalPaths("https://example.org/users/alice/%40example.com"));
        Assert.Equal(
            "https://registry.npmjs.org/@angular/core",
            CoreTelemetryService.SanitizeLocalPaths("https://registry.npmjs.org/@angular/core"));
        Assert.Equal(
            "https://registry.npmjs.org/%40angular/core",
            CoreTelemetryService.SanitizeLocalPaths("https://registry.npmjs.org/%40angular/core"));
        Assert.Equal(
            "{\"refresh_token\":\"[redacted]\",\"access_token\":\"[redacted]\",\"id_token\":\"[redacted]\"}",
            CoreTelemetryService.SanitizeLocalPaths(
                "{\"refresh_token\":\"opaqueSecret123\",\"access_token\":\"accSecret456\",\"id_token\":\"idSecret789\"}"));
        Assert.Equal(
            "jwt=[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("jwt=eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTYifQ.sigSecret"));
        Assert.Equal(
            "https://example.com/?[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("https://example.com/?access_token=\"super secret\""));
        Assert.Equal(
            "https://example.com/?[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("https://example.com/?session_key=\"super secret\""));
        Assert.Equal(
            "https://example.com/?[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("https://example.com/?session_key='super secret'"));
        Assert.Equal(
            "[redacted-path]",
            CoreTelemetryService.SanitizeLocalPaths(".env"));
        Assert.Equal(
            "[redacted-path]",
            CoreTelemetryService.SanitizeLocalPaths("\\Confidential\\Payroll"));
        Assert.Equal(
            "[redacted-path]",
            CoreTelemetryService.SanitizeLocalPaths("Acquisition.blend"));
        Assert.Equal(
            "[redacted-path]",
            CoreTelemetryService.SanitizeLocalPaths("smb://server/Confidential/Acquisition.mov"));
        Assert.Equal(
            "https://example.com/reset/[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("https://example.com/reset/super-secret-token"));
        Assert.Equal(
            "X-Amz-Security-Token: [redacted]",
            CoreTelemetryService.SanitizeLocalPaths("X-Amz-Security-Token: secretcredential"));
        Assert.Equal(
            "{\"X-Amz-Security-Token\":\"[redacted]\"}",
            CoreTelemetryService.SerializeProperties(new Dictionary<string, object> { ["X-Amz-Security-Token"] = "secretcredential" }));
        Assert.Equal(
            "{\"contact [redacted-phone]\":true}",
            CoreTelemetryService.SerializeProperties(new Dictionary<string, object> { ["contact +1 2025550142"] = true }));
        Assert.Equal(
            "https://[redacted-email]",
            CoreTelemetryService.SanitizeLocalPaths("https://example.com/users/alice%40%65xample.com"));
        Assert.Equal(
            "https://[redacted-email]",
            CoreTelemetryService.SanitizeLocalPaths("https://example.com/users/alice%40b%C3%BCcher.example"));
        Assert.Equal(
            "https://hooks.slack.com:/services/[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("https://hooks.slack.com:/services/T000/B000/Canary"));
        var longProto = "(HTTP/1" + string.Concat(Enumerable.Repeat(".1", 50_000)) + "x)";
        Assert.Equal("([redacted-path])", CoreTelemetryService.SanitizeLocalPaths(longProto));
        var repeatedOpen = string.Concat(Enumerable.Repeat("x@[", 10_000));
        Assert.Equal(repeatedOpen, CoreTelemetryService.SanitizeLocalPaths(repeatedOpen));
    }

    [Fact]
    public void OrphanAdoption_rejects_duplicate_json_keys_and_preserves_sibling_on_failed_opt_out()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-dupkey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        try
        {
            string? sharedToken = "enabled:gen1";
            // Seed one valid orphan crash via instance 1, then exit.
            using (var inst1 = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken),
                acquireExternalConsentLock: () => new NoOpDisposable()))
            {
                inst1.ReportCrash("InvalidOperationException", "valid crash", "at App.Main()");
            }

            // Seed a malformed sibling orphan with duplicate keys, a lone-surrogate orphan, AND temp files.
            var dupOrphan = Path.Combine(dir, "telemetry-buffer.gen1.dupkey01.json");
            File.WriteAllText(
                dupOrphan,
                "{\"events\":[{\"id\":\"bad\",\"kind\":\"crash\",\"name\":\"first\",\"name\":\"second\",\"timestamp_ms\":1,\"session_id\":\"s\"}],\"breadcrumbs\":[]}");
            var dupTmp = Path.Combine(dir, ".telemetry-buffer.gen1.dupkey01.json.999.tmp");
            File.WriteAllText(dupTmp, "{}");
            var surrogateOrphan = Path.Combine(dir, "telemetry-buffer.gen1.surrog01.json");
            File.WriteAllText(
                surrogateOrphan,
                "{\"events\":[{\"id\":\"bad2\",\"kind\":\"crash\",\"name\":\"\\uD800\",\"timestamp_ms\":1,\"session_id\":\"s\"}],\"breadcrumbs\":[]}");
            var surrogateRootKeyOrphan = Path.Combine(dir, "telemetry-buffer.gen1.surrog02.json");
            File.WriteAllText(
                surrogateRootKeyOrphan,
                "{\"events\":[{\"id\":\"ok-with-bad-root-key\",\"kind\":\"crash\",\"name\":\"recovered\",\"timestamp_ms\":2,\"session_id\":\"s\"}],\"\\uD800\":0}");

            // Restarting must adopt both healthy crashes without letting malformed orphans throw or erase them.
            using var instA = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken),
                acquireExternalConsentLock: () => new NoOpDisposable());
            Assert.Equal(TelemetryConsent.Full, instA.Consent);
            Assert.Equal(2, instA.PendingCount);
            Assert.False(File.Exists(dupOrphan));
            Assert.False(File.Exists(dupTmp));
            Assert.False(File.Exists(surrogateOrphan));
            Assert.False(File.Exists(surrogateRootKeyOrphan));

            // Start a second active instance B in the same generation and persist a crash in B's buffer.
            using var instB = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken),
                acquireExternalConsentLock: () => new NoOpDisposable());
            instB.ReportCrash("InvalidOperationException", "sibling crash", "at B.Main()");
            var bufferB = instB.ActiveBufferPathForTesting!;
            Assert.True(File.Exists(bufferB));

            // Simulate a failed opt-out on instance A: shared consent on disk remains enabled:gen1,
            // and instance A calls SetConsent(Off). B's persisted buffer MUST remain intact.
            Assert.True(instA.SetConsent(TelemetryConsent.Off));
            Assert.True(File.Exists(bufferB));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Startup_does_not_activate_on_unconfirmed_pending_claim_and_ReportCrash_persists_under_lock_contention()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-pending-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        try
        {
            string? confirmedToken = null;
            string? pendingToken = "enabled:pendingGen1";

            // Startup with stale initialEnabled=true must NOT activate when externalConsentToken returns null (unconfirmed pending).
            using (var staleStartup = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref confirmedToken),
                acquireExternalConsentLock: () => new NoOpDisposable(),
                externalConsentTokenForClaim: claim => claim == pendingToken ? pendingToken : Volatile.Read(ref confirmedToken)))
            {
                Assert.Equal(TelemetryConsent.Off, staleStartup.Consent);
                Assert.Equal(AlUmmahNow.Core.TelemetryConsentState.Denied, staleStartup.NativeConsentForTesting);
            }

            // Atomic SetConsent(Full, pendingToken, confirm) activates and confirms; subsequent ReportCrash persists even when lock is contended.
            var lockContended = false;
            using (var owner = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: false,
                externalConsentToken: () => Volatile.Read(ref confirmedToken),
                acquireExternalConsentLock: () => Volatile.Read(ref lockContended) ? null : new NoOpDisposable(),
                externalConsentTokenForClaim: claim => claim == pendingToken ? pendingToken : Volatile.Read(ref confirmedToken)))
            {
                Assert.True(owner.SetConsent(TelemetryConsent.Full, pendingToken, () =>
                {
                    Volatile.Write(ref confirmedToken, pendingToken);
                    return true;
                }));
                Assert.Equal(TelemetryConsent.Full, owner.Consent);

                Volatile.Write(ref lockContended, true);
                owner.ReportCrash("InvalidOperationException", "fatal under lock contention", "at App.Main()");
                var ownerBuffer = owner.ActiveBufferPathForTesting!;
                Assert.True(File.Exists(ownerBuffer));
                Assert.Contains("InvalidOperationException", File.ReadAllText(ownerBuffer));
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Different_ingest_environments_do_not_adopt_each_others_buffers_and_opt_out_waits_for_flush_revocation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-env-flush-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        var guardPath = Path.Combine(dir, "telemetry-flush-active.test.lock");
        try
        {
            string? sharedToken = "enabled:envgen1";
            string? qaBufferPath;
            // QA instance persists a crash and exits.
            using (var qa = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: "https://qa.videos.api.alummahnow.net",
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken),
                acquireExternalConsentLock: () => new NoOpDisposable()))
            {
                qa.ReportCrash("InvalidOperationException", "qa crash", "at Qa.Main()");
                qaBufferPath = qa.ActiveBufferPathForTesting;
                Assert.Equal(1, qa.PendingCount);
            }
            Assert.NotNull(qaBufferPath);
            Assert.True(File.Exists(qaBufferPath));

            // Prod instance with the same consent token and bufferPath MUST NOT adopt OR delete the QA crash.
            using (var prod = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: "https://videos.api.alummahnow.net",
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken),
                acquireExternalConsentLock: () => new NoOpDisposable()))
            {
                Assert.Equal(TelemetryConsent.Full, prod.Consent);
                Assert.Equal(0, prod.PendingCount);
                Assert.True(File.Exists(qaBufferPath));
            }

            // Relaunching QA adopts its own preserved QA crash report.
            using (var qaReload = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: "https://qa.videos.api.alummahnow.net",
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken),
                acquireExternalConsentLock: () => new NoOpDisposable()))
            {
                Assert.Equal(1, qaReload.PendingCount);
            }

            // Fallback service when native library is unavailable purges buffered files (including legacy .adopt.tmp)
            // even when external consent lock is absent (e.g. fresh XDG_CONFIG_HOME), and fails closed if buffer lock is held.
            var legacyAdoptTmp = Path.Combine(dir, ".telemetry-buffer.envgen1.123456.json.9999.adopt.tmp");
            File.WriteAllText(legacyAdoptTmp, "{\"events\":[]}");
            Volatile.Write(ref sharedToken, null);
            var bufferLockPath = Path.Combine(dir, ".telemetry-buffer.lock");
            using (new FileStream(bufferLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            using (var contendedFallback = CoreTelemetryService.CreateUnavailableFallbackForTesting(
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken),
                acquireExternalConsentLock: () => null))
            {
                Assert.False(contendedFallback.SetConsent(TelemetryConsent.Off));
                Assert.True(contendedFallback.LastPurgeFailed);
                Assert.True(File.Exists(legacyAdoptTmp));
            }
            using (var unavailableMissingConfig = CoreTelemetryService.CreateUnavailableFallbackForTesting(
                bufferPath: bufferPath,
                initialEnabled: false,
                externalConsentToken: () => Volatile.Read(ref sharedToken),
                acquireExternalConsentLock: () => null))
            {
                Assert.Equal(TelemetryConsent.Off, unavailableMissingConfig.Consent);
                Assert.False(unavailableMissingConfig.LastPurgeFailed);
                Assert.False(File.Exists(legacyAdoptTmp));
                Assert.Empty(Directory.GetFiles(dir, "telemetry-buffer*.json"));
                Assert.False(unavailableMissingConfig.SetConsent(TelemetryConsent.Full));
                Assert.True(unavailableMissingConfig.SetConsent(TelemetryConsent.Off));
            }
            Volatile.Write(ref sharedToken, "enabled:envgen1");

            // Interrupted pre-first-rename temp file in the same generation is cleaned up on next startup,
            // while a live sibling's temp file is preserved; null active-flush guard fails closed without dispatching flush.
            var preRenameOrphanTmp = Path.Combine(dir, ".telemetry-buffer.envgen1.deadinst1234.json.7777.tmp");
            File.WriteAllText(preRenameOrphanTmp, "{\"events\":[]}");
            var flushDispatchedWithNullGuard = false;
            using (var guardFailClosed = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken),
                acquireExternalConsentLock: () => new NoOpDisposable(),
                acquireActiveFlushGuard: () => null))
            {
                Assert.False(File.Exists(preRenameOrphanTmp));
                guardFailClosed.FlushOverrideForTesting = () =>
                {
                    flushDispatchedWithNullGuard = true;
                    return Task.CompletedTask;
                };
                await guardFailClosed.FlushAsync();
                Assert.False(flushDispatchedWithNullGuard);

                // Arabic-Indic phone, JWT, IPv4, compressed IPv6, 9+ digit numeric values, share-link tokens, and identifiers are scrubbed.
                var scrubbed = CoreTelemetryService.SerializeProperties(new Dictionary<string, object>
                {
                    ["+٩٧١٥٠١٢٣٤٥٦"] = true,
                    ["eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig_part-1"] = "failed",
                    ["192.0.2.10"] = true,
                    ["fe80::1"] = true,
                    ["2001:db8::beef:cafe"] = true,
                    ["recipient"] = 12025550148L,
                    ["detail"] = "Peer 192.168.42.17 called +٩٧١٥٠١٢٣٤٥٦٧ from fe80::1 source:2001:db8::1 2001:db8:1234:5678:9abc:def0:192.0.2.128 call +𝟙𝟚𝟛\u2011𝟜𝟝𝟞\u2011𝟟𝟠𝟡𝟘",
                    ["peer"] = System.Net.IPAddress.Parse("2001:db8::1"),
                    ["challenge.verificationCode"] = 123456,
                    ["roundedDecimal"] = 123456789.000000001m,
                    ["throwingObj"] = new ThrowingToStringForTesting(),
                    ["wsJwe"] = "IHsiYWxnIjoiZGlyIiwiZW5jIjoiQTI1NkdDTSJ9..iv1234.cipher5678.tag9012",
                    ["encodedJweUrl"] = "https://example.com/download/eyJhbGciOiJkaXIifQ%2E%2Eiv1234%2Ecipher9999%2Etag8888",
                    ["share"] = "https://share.example.com/s/V8r2Qp6Lm4",
                    ["box"] = "https://app.box.com/s/a1b2c3d4e5f6g7h8i9j0k1l2",
                    ["matrix"] = "https://example.com/a;jsessionid=abc123",
                    ["dropbox"] = "https://www.dropbox.com/scl/fi/a1b2c3d4e5f6g7h8i9j0/PrivateAcquisition.pdf",
                });
                Assert.NotNull(scrubbed);
                Assert.DoesNotContain("٩٧١٥٠١٢٣٤٥٦", scrubbed);
                Assert.DoesNotContain("𝟙𝟚𝟛", scrubbed);
                Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", scrubbed);
                Assert.DoesNotContain("192.0.2.10", scrubbed);
                Assert.DoesNotContain("192.168.42.17", scrubbed);
                Assert.DoesNotContain("192.0.2.128", scrubbed);
                Assert.DoesNotContain("9abc:def0", scrubbed);
                Assert.DoesNotContain("fe80", scrubbed);
                Assert.DoesNotContain("2001:db8", scrubbed);
                Assert.DoesNotContain("beef:cafe", scrubbed);
                Assert.DoesNotContain("12025550148", scrubbed);
                Assert.DoesNotContain("123456", scrubbed);
                Assert.DoesNotContain("123456789", scrubbed);
                Assert.DoesNotContain("cipher5678", scrubbed);
                Assert.DoesNotContain("cipher9999", scrubbed);
                Assert.DoesNotContain("V8r2Qp6Lm4", scrubbed);
                Assert.DoesNotContain("a1b2c3d4e5f6g7h8i9j0k1l2", scrubbed);
                Assert.DoesNotContain("abc123", scrubbed);
                Assert.DoesNotContain("PrivateAcquisition", scrubbed);
                Assert.Contains("https://share.example.com/s/[redacted]", scrubbed);
                Assert.Contains("https://app.box.com/s/[redacted]", scrubbed);
                Assert.Contains("https://example.com/a;jsessionid=[redacted]", scrubbed);
                Assert.Contains("https://www.dropbox.com/scl/[redacted]", scrubbed);

                guardFailClosed.Track("4155550123");
                guardFailClosed.AddBreadcrumb("fe80::1", "Peer 192.168.42.17 called +٩٧١٥٠١٢٣٤٥٦٧");
                guardFailClosed.ReportCrash("4155550123", "Peer 192.168.42.17 called +٩٧١٥٠١٢٣٤٥٦٧");
                var persisted = File.ReadAllText(guardFailClosed.ActiveBufferPathForTesting!);
                Assert.DoesNotContain("4155550123", persisted);
                Assert.DoesNotContain("192.168.42.17", persisted);
                Assert.DoesNotContain("٩٧١٥٠١٢٣٤٥٦٧", persisted);
                Assert.DoesNotContain("fe80", persisted);
            }
            Assert.False(CoreTelemetryService.ShouldClearStaleStartupConsent(NullTelemetryService.Instance));
            using (var contendedStart = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken),
                acquireExternalConsentLock: () => null))
            {
                Assert.Equal(TelemetryConsent.Off, contendedStart.Consent);
                Assert.False(CoreTelemetryService.ShouldClearStaleStartupConsent(contendedStart));
            }

            // Active flush guard stays held until external opt-out triggers native revocation, even while HTTP stalls.
            var flushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseHttp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var guardReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var flusher = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: "https://videos.api.alummahnow.net",
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: true,
                externalConsentToken: () => Volatile.Read(ref sharedToken),
                acquireExternalConsentLock: () => new NoOpDisposable(),
                acquireActiveFlushGuard: () =>
                {
                    var fs = new FileStream(guardPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                    return new CallbackDisposable(() =>
                    {
                        fs.Dispose();
                        try { File.Delete(guardPath); } catch (IOException) { }
                        guardReleased.TrySetResult();
                    });
                });
            flusher.ExternalPollIntervalMsForTesting = 5;
            flusher.FlushOverrideForTesting = async () =>
            {
                flushStarted.TrySetResult();
                await releaseHttp.Task;
            };
            var flushTask = flusher.FlushAsync();
            await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(guardReleased.Task.IsCompleted);

            // Simulate cross-process opt-out while batch 1 HTTP is still stalled.
            Volatile.Write(ref sharedToken, null);
            await guardReleased.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(AlUmmahNow.Core.TelemetryConsentState.Denied, flusher.NativeConsentForTesting);

            releaseHttp.TrySetResult();
            await flushTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Diagnostic_sanitizer_handles_quoted_credentials_userinfo_jose_edge_cases_and_unicode_phones()
    {
        var longHeaderJson = " {\"alg\":\"HS256\",\"pad\":\"" + new string('a', 600) + "\"}";
        var longHeaderB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(longHeaderJson))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var longSignedJwt = $"{longHeaderB64}.eyJzdWIiOiJhbGljZSJ9.sigValue123";

        var json = CoreTelemetryService.SerializeProperties(new Dictionary<string, object>
        {
            ["quotedCred"] = "{\"challenge.verificationCode\":\"482731\"} {\"home\\u0041ddress\":\"12 Cedar Rd\"} {\"userId\":[\"alice42\", \"bob73\"]} status=\"password=first;second\" password=\u201Corchid velvet\u201D pass=\"correct horse battery staple\" {\"pass\":\"multi word phrase\"} pwd=shortsecret token=cobalt-orchard",
            ["user_id"] = "alice123",
            ["p\\u0061ssword"] = "violet-sparrow",
            ["personalText"] = "fullName=\"Alice Wonderland\" displayName=Alice Smith username=Jane Doe address=\"123 Main St, Apt 4\" phone_2025550123 C:ProjectCodename C:秘密 https://example.test/password%3Dshortcredential https%3A%2F%2Fexample.test%2F%3Fcode%3Dorchid-meadow https://example.org/r/Abcdefghijklmno1%41Pqrstuvwxyzabcd2 https://example.org/u/alice%40example.com",
            ["token violet7"] = "ok",
            ["http://u:p@localhost/x"] = "https://1drv.ms/u/s!Aq82F1ZLPx0K",
            ["algNone"] = "IHsiYWxnIjoibm9uZSJ9.eyJzdWIiOiJhbGljZSJ9.",
            ["longJwt"] = longSignedJwt,
            ["safeId"] = "eyJunction.load.failed",
            ["nbText"] = "+33\u202F6\u202F12\u202F34\u202F56\u202F78",
            ["minusText"] = "+1\u2212212\u2212555\u22120123",
            ["detailText"] = "12 34 56 78 and 2026-11-27",
        });

        Assert.NotNull(json);
        Assert.DoesNotContain("482731", json);
        Assert.DoesNotContain("first;second", json);
        Assert.DoesNotContain("second", json);
        Assert.DoesNotContain("alice123", json);
        Assert.DoesNotContain("alice42", json);
        Assert.DoesNotContain("bob73", json);
        Assert.DoesNotContain("violet-sparrow", json);
        Assert.DoesNotContain("秘密", json);
        Assert.DoesNotContain("Abcdefghijklmno1", json);
        Assert.DoesNotContain("Pqrstuvwxyzabcd2", json);
        Assert.DoesNotContain("alice%40example.com", json);
        Assert.Equal("https://example.org/r/[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("https://example.org/r/Abcdefghijklmno1%41Pqrstuvwxyzabcd2"));
        Assert.Equal("https://uploads.example.test/upload/[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("https://uploads.example.test/upload/853876e8-cc19-465d-bafa-7459aba00936"));
        Assert.Equal("alummahnow://watch/[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("alummahnow://watch/v1"));
        Assert.Equal("https://example.test/lookup/[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("https://example.test/lookup/%2B%31%32%30%32%35%35%35%30%31%32%33"));
        Assert.Equal("https://example.test/lookup/[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("https://example.test/lookup/alice%2540example.test"));
        Assert.Equal("https://example.test/contact/[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("https://example.test/contact/%2B%31%20%32%30%32%20%35%35%35%20%30%31%32%33"));
        Assert.Equal("https://example.test/%72eset-password/[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("https://example.test/%72eset-password/AbCdEf123456"));
        Assert.Equal("https://example.test/%2572eset-password/[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("https://example.test/%2572eset-password/AbCdEf123456"));
        Assert.Equal("https://example.test/lookup/[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("https://example.test/lookup/202%C2%A0555%C2%A00123"));
        Assert.Equal("GET https://example.com/users/[redacted] failed",
            CoreTelemetryService.SanitizeLocalPaths("GET https://example.com/users/AliceSmith failed"));
        Assert.Equal("https://example.com/%75sers/[redacted]",
            CoreTelemetryService.SanitizeLocalPaths("https://example.com/%75sers/BobJones"));
        Assert.Equal("en", CoreTelemetryService.SanitizeLocaleTag("/Users/Ada/Private.mov"));
        Assert.Equal("ar-SA", CoreTelemetryService.SanitizeLocaleTag("ar-SA"));
        var multiEnc = CoreTelemetryService.SerializeProperties(new Dictionary<string, object>
        {
            ["u"] = "https%25253A%25252F%25252Fexample.test%25252Flogin%25253Ftoken%25253Ds3cr3t",
            ["pat"] = "ghp_abcdefghijklmnopqrstuvwxyzABCDEFGHIJ",
        });
        Assert.NotNull(multiEnc);
        Assert.DoesNotContain("s3cr3t", multiEnc);
        Assert.DoesNotContain("ghp_abcdefghijklmnopqrstuvwxyzABCDEFGHIJ", multiEnc);
        Assert.DoesNotContain("12 Cedar Rd", json);
        Assert.DoesNotContain("orchid", json);
        Assert.DoesNotContain("velvet", json);
        Assert.DoesNotContain("shortcredential", json);
        Assert.DoesNotContain("horse battery staple", json);
        Assert.DoesNotContain("word phrase", json);
        Assert.DoesNotContain("2025550123", json);
        Assert.DoesNotContain("shortsecret", json);
        Assert.DoesNotContain("cobalt-orchard", json);
        Assert.DoesNotContain("[redacted]]", json);
        Assert.DoesNotContain("Alice", json);
        Assert.DoesNotContain("Wonderland", json);
        Assert.DoesNotContain("Smith", json);
        Assert.DoesNotContain("Jane", json);
        Assert.DoesNotContain("Doe", json);
        Assert.DoesNotContain("ProjectCodename", json);
        Assert.DoesNotContain("violet7", json);
        Assert.DoesNotContain("orchid-meadow", json);
        Assert.DoesNotContain("Main St", json);
        Assert.DoesNotContain("u:p@localhost", json);
        Assert.DoesNotContain("u:[redacted-email]", json);
        Assert.DoesNotContain("Aq82F1ZLPx0K", json);
        Assert.DoesNotContain("eyJzdWIiOiJhbGljZSJ9", json);
        Assert.DoesNotContain("sigValue123", json);
        Assert.Contains("eyJunction.load.failed", json);
        Assert.DoesNotContain("56\u202F78", json);
        Assert.DoesNotContain("0123", json);
        Assert.DoesNotContain("12 34 56 78", json);
        Assert.Contains("2026-11-27", json);
        Assert.Equal("http://[redacted]@localhost/x", CoreTelemetryService.SanitizeLocalPaths("http://u:p@localhost/x"));
    }

    [Fact]
    public void ReportCrash_syncs_cross_process_opt_in_when_not_locally_suppressed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-tel-crash-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bufferPath = Path.Combine(dir, "telemetry-buffer.json");
        string? sharedToken = null;
        var suppressed = false;
        try
        {
            using var service = new CoreTelemetryService(
                platform: "linux",
                appVersion: "1.0.0",
                locale: "en",
                videosApiBaseUrl: null,
                userAgent: "AlUmmahNowLinux/1.0.0",
                bufferPath: bufferPath,
                initialEnabled: false,
                externalConsentToken: () => Volatile.Read(ref sharedToken),
                isLocalOptOutSuppressed: () => Volatile.Read(ref suppressed));

            Assert.Equal(TelemetryConsent.Off, service.Consent);

            // Another process opts in while this instance stays outside Settings.
            Volatile.Write(ref sharedToken, "enabled:genB");
            service.TrackError("playback_failed", new Dictionary<string, object> { ["backend"] = "gstreamer" });
            Assert.Equal(TelemetryConsent.Full, service.Consent);
            service.ReportCrash("TestException", "fatal", "https://example.org/r/Abcdefghijklmno1%41Pqrstuvwxyzabcd2");
            var persisted = File.ReadAllText(service.ActiveBufferPathForTesting!);
            Assert.Contains("playback_failed", persisted);
            Assert.Contains("TestException", persisted);
            Assert.DoesNotContain("Abcdefghijklmno1", persisted);
            Assert.DoesNotContain("Pqrstuvwxyzabcd2", persisted);
            if (!OperatingSystem.IsWindows())
            {
                const UnixFileMode nonOwnerMask =
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(dir) & nonOwnerMask);
                Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(service.ActiveBufferPathForTesting!) & nonOwnerMask);
            }

            // Local failed opt-out suppresses automatic crash-time re-enable for the stale token.
            Volatile.Write(ref suppressed, true);
            service.SetConsent(TelemetryConsent.Off);
            Assert.Equal(TelemetryConsent.Off, service.Consent);
            service.ReportCrash("SuppressedException", "should not record");
            Assert.Equal(TelemetryConsent.Off, service.Consent);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class CallbackDisposable : IDisposable
    {
        private Action? _onDispose;
        public CallbackDisposable(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }

    private sealed class ThrowingToStringForTesting
    {
        public override string ToString() => throw new InvalidOperationException("boom");
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
