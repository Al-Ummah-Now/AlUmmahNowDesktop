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
            var siblingTmp = Path.Combine(dir, ".telemetry-buffer.json.4242.tmp");
            File.WriteAllText(siblingTmp, "stale");
            Assert.True(File.Exists(bufferPath));
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
            Assert.False(File.Exists(bufferPath));
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
            Assert.True(File.Exists(bufferPath));

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
            Assert.False(File.Exists(bufferPath));

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
            Assert.Equal(1, instanceB.PendingCount);
            Assert.True(File.Exists(bufferPath));

            // Instance A opts out and immediately opts back in with a new consent generation token.
            Volatile.Write(ref sharedToken, null);
            Volatile.Write(ref sharedToken, "enabled:gen2");

            // Instance B must detect the generation change, revoke + purge its pre-opt-out queue, and NOT flush.
            await instanceB.FlushAsync();

            Assert.False(flushInvoked);
            Assert.Equal(TelemetryConsent.Off, instanceB.Consent);
            Assert.Equal(0, instanceB.PendingCount);
            Assert.False(File.Exists(bufferPath));
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
                "Failed reading /Private (Client).mov\n/Private Client\n/private-key\n/Users/alice/Secret<Project>.mov\n/Users/alice/Draft:77 in open() Acquisition.mov\nFailed /home/alice/Secret\"Acquisition.mov\nFailed to open \"/ Secret/Acquisition.mov\"\nFailed to open \"/ SecretFile\"\n/ SecretFile.mov\n/ SecretFile\n/ SecretFile.abcdefghijkl\n// SecretFile\n42:/ Secret/Acquisition.mov:77\nerror 42://SecretHost/Acquisition.mov\nFailed to open path\\\\Server\\Share\\Secret.mov\nopen(pathC:\\Secret\\Acquisition.mov)\nopen(C://Secret/Acquisition.mov)\nC://Users\\Alice\\Secret.mov\nhttps://example.com(/Users/alice/Secret.mov)\nCould not find file 'Secret.mov'\nCould not find file 'private/Secret.mov'\nCould not find file './Secret.mov'\nCould not find file 'O'Brien.mov'\nCould not find file 'SecretBareName'\nCould not find file Secret.xyz\nCould not find file Secret.2026\nSecret Project.mov\nopen Acquisition.custom\nopen Secret Project.mov\nopen //storage/emulated/0/Private.mov\n/workspace/alice/Project|Acquisition.mov\nerror 42:/home/alice/private.mov\n/Client Files/Private.mov\n/workspace/alice/Project;Acquisition.mov\n'/workspace/alice/Project:2026;Acquisition.mov'\n/!Acquisition.mov\n\"/!Acquisition.mov\"\n\"C:\\Users\\Alice Smith\\Private Draft.mp4\"",
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
            Assert.Equal("https://example.com/?path=/api/items",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/?path=/api/items"));
            Assert.Equal("https://[2001:db8::1]/v1/items",
                CoreTelemetryService.SanitizeLocalPaths("https://[2001:db8::1]/v1/items"));
            Assert.Equal("https://example.com/a(b)/items",
                CoreTelemetryService.SanitizeLocalPaths("https://example.com/a(b)/items"));
            Assert.Equal("open(https://example.com/a(b)/items)",
                CoreTelemetryService.SanitizeLocalPaths("open(https://example.com/a(b)/items)"));
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
}
