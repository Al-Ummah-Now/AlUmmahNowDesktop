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
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
