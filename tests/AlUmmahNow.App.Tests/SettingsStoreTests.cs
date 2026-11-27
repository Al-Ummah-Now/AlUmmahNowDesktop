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
            storeA.SaveTelemetryEnabled(true);
            var cachedInB = storeB.Load();
            Assert.True(cachedInB.TelemetryEnabled);

            // Instance A opts out.
            storeA.SaveTelemetryEnabled(false);
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
}
