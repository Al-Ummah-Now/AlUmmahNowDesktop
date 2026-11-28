using System;
using System.IO;
using AlUmmahNow.App.Services;
using Xunit;

namespace AlUmmahNow.App.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void Unrelated_settings_save_from_another_instance_never_restores_telemetry_opt_out()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-settings-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var storeA = new SettingsStore(path);
            var storeB = new SettingsStore(path);

            // Default is off.
            Assert.False(storeA.Load().TelemetryEnabled);
            Assert.False(storeA.LoadTelemetryEnabled());

            // Both instances start while telemetry is enabled.
            Assert.True(storeA.SaveTelemetryEnabled(true));
            var cachedInB = storeB.Load();
            Assert.True(cachedInB.TelemetryEnabled);

            // Instance A opts out.
            Assert.True(storeA.SaveTelemetryEnabled(false));
            Assert.False(storeA.LoadTelemetryEnabled());

            // Instance B saves an unrelated theme/language change with its stale cached object (TelemetryEnabled == true).
            cachedInB.Theme = ThemePreference.Light;
            cachedInB.Language = "en";
            storeB.Save(cachedInB);

            // Telemetry MUST remain off on disk and on subsequent loads, while theme/language persisted.
            Assert.False(storeA.LoadTelemetryEnabled());
            var reloaded = storeA.Load();
            Assert.False(reloaded.TelemetryEnabled);
            Assert.Equal(ThemePreference.Light, reloaded.Theme);
            Assert.Equal("en", reloaded.Language);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SaveTelemetryEnabled_false_overwrites_when_delete_is_blocked_and_reports_failure_when_unwritable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-settings-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        var consentPath = Path.Combine(dir, "telemetry-consent");
        try
        {
            var store = new SettingsStore(path);
            Assert.True(store.SaveTelemetryEnabled(true));
            Assert.True(store.LoadTelemetryEnabled());

            if (!OperatingSystem.IsWindows())
            {
                // Make directory read-only while keeping consent file writable: File.Delete fails,
                // so SaveTelemetryEnabled(false) falls back to overwriting the file with "disabled".
                File.SetUnixFileMode(consentPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                try
                {
                    Assert.True(store.SaveTelemetryEnabled(false));
                    Assert.False(store.LoadTelemetryEnabled());
                }
                finally
                {
                    File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }

                // Now make BOTH file and directory read-only while file contains "enabled":
                // neither delete nor overwrite can succeed, so SaveTelemetryEnabled(false) MUST return false.
                Assert.True(store.SaveTelemetryEnabled(true));
                File.SetUnixFileMode(consentPath, UnixFileMode.UserRead);
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                try
                {
                    Assert.False(store.SaveTelemetryEnabled(false));
                    Assert.True(store.LoadTelemetryEnabled());
                }
                finally
                {
                    File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    File.SetUnixFileMode(consentPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                // Also test an unreadable + undeletable sidecar (simulating an exclusive lock where readback fails):
                // SaveTelemetryEnabled(false) MUST still return false instead of trusting LoadTelemetryEnabled() == false.
                Assert.True(store.SaveTelemetryEnabled(true));
                File.SetUnixFileMode(consentPath, UnixFileMode.None);
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                try
                {
                    Assert.False(store.SaveTelemetryEnabled(false));
                }
                finally
                {
                    File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    File.SetUnixFileMode(consentPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                // Once readable again, the original "enabled" marker is still intact on disk.
                Assert.True(store.LoadTelemetryEnabled());

                // Remove search permission on the parent directory so File.Exists returns false even though
                // the "enabled" marker exists: SaveTelemetryEnabled(false) MUST return false, not true.
                File.SetUnixFileMode(dir, UnixFileMode.None);
                try
                {
                    Assert.False(store.SaveTelemetryEnabled(false));
                }
                finally
                {
                    File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                Assert.True(store.LoadTelemetryEnabled());
            }
            else
            {
                // On Windows, hold an exclusive lock on the sidecar: delete, overwrite, and readback all fail,
                // and SaveTelemetryEnabled(false) MUST return false rather than treating readback failure as opted out.
                using (var locked = new FileStream(consentPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.False(store.SaveTelemetryEnabled(false));
                }
                Assert.True(store.LoadTelemetryEnabled());
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SaveTelemetryEnabled_writes_distinct_generation_tokens_and_loads_legacy_marker()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-settings-gen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settingsPath = Path.Combine(dir, "settings.json");
            var consentPath = Path.Combine(dir, "telemetry-consent");
            var storeA = new SettingsStore(settingsPath);
            var storeB = new SettingsStore(settingsPath);

            // Legacy marker containing bare "enabled" still loads as enabled.
            File.WriteAllText(consentPath, "enabled");
            Assert.Equal("enabled", storeB.LoadTelemetryConsentToken());
            Assert.True(storeB.LoadTelemetryEnabled());

            // Opt-in while legacy marker is already active joins the legacy generation;
            // rollback of the joined claim must not disable already-enabled consent.
            Assert.True(storeA.TrySaveTelemetryEnabled(true, out var gen1));
            Assert.False(string.IsNullOrEmpty(gen1));
            Assert.StartsWith("enabled:legacy:", gen1);
            Assert.Equal(gen1, storeB.LoadTelemetryConsentToken());

            Assert.True(storeB.TrySaveTelemetryEnabled(true, out var joinedGen1));
            Assert.NotNull(joinedGen1);
            Assert.StartsWith("enabled:legacy:", joinedGen1);
            Assert.True(storeB.RollbackTelemetryEnabled(joinedGen1));
            Assert.True(storeA.LoadTelemetryEnabled());

            Assert.True(storeA.SaveTelemetryEnabled(false));
            Assert.Null(storeB.LoadTelemetryConsentToken());
            Assert.False(storeB.LoadTelemetryEnabled());

            Assert.True(storeA.TrySaveTelemetryEnabled(true, out var gen2));
            Assert.False(string.IsNullOrEmpty(gen2));
            Assert.NotEqual(gen1, gen2);
            Assert.True(storeB.TrySaveTelemetryEnabled(true, out var joinedGen2));
            Assert.StartsWith(gen2 + ":", joinedGen2);
            Assert.True(storeB.RollbackTelemetryEnabled(joinedGen2));
            Assert.True(storeA.LoadTelemetryEnabled());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SaveTelemetryEnabled_false_succeeds_immediately_while_same_process_holds_consent_lock()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-settings-reentrant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settingsPath = Path.Combine(dir, "settings.json");
            var lockPath = Path.Combine(dir, "telemetry-consent.lock");
            var store = new SettingsStore(settingsPath);
            Assert.True(store.SaveTelemetryEnabled(true));

            using (var flushLease = store.TryAcquireTelemetryConsentLock())
            {
                Assert.NotNull(flushLease);
                // Another process trying to open the OS lock file directly must be blocked while the lease is held.
                Assert.Throws<IOException>(() =>
                    new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));

                // Same-process settings opt-out re-enters the held lease immediately without timing out.
                Assert.True(store.SaveTelemetryEnabled(false));
                Assert.False(store.LoadTelemetryEnabled());

                // OS lock must remain held until the flush lease itself is disposed.
                Assert.Throws<IOException>(() =>
                    new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }

            // Once the flush lease is disposed, the OS lock is released cleanly.
            using var external = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Assert.NotNull(external);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RollbackTelemetryEnabled_is_serialized_against_concurrent_same_process_opt_in()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-settings-rollback-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settingsPath = Path.Combine(dir, "settings.json");
            var storeA = new SettingsStore(settingsPath);
            var storeB = new SettingsStore(settingsPath);
            Assert.True(storeA.TrySaveTelemetryEnabled(true, out var initialToken));
            Assert.False(string.IsNullOrEmpty(initialToken));

            using var flushLease = storeA.TryAcquireTelemetryConsentLock();
            Assert.NotNull(flushLease);

            for (var i = 0; i < 64; i++)
            {
                Assert.True(storeA.TrySaveTelemetryEnabled(true, out var oldToken));
                string? newerToken = null;
                var start = new ManualResetEventSlim(false);
                var tRollback = Task.Run(() =>
                {
                    start.Wait();
                    return storeA.RollbackTelemetryEnabled(oldToken);
                });
                var tEnable = Task.Run(() =>
                {
                    start.Wait();
                    return storeB.TrySaveTelemetryEnabled(true, out newerToken);
                });
                start.Set();
                var results = await Task.WhenAll(tRollback, tEnable);
                Assert.True(results[0]);
                Assert.True(results[1]);
                Assert.False(string.IsNullOrEmpty(newerToken));
                // Regardless of serialization order (rollback->enable or enable->rollback),
                // the newer opt-in token must always survive on disk.
                Assert.Equal(newerToken, storeA.LoadTelemetryConsentToken());
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
