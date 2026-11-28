using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Native;
using Xunit;

namespace AlUmmahNow.CoreBridge.Native.Tests;

public class CoreTelemetryServiceTests
{
    [Theory]
    [InlineData("https://videos.api.alummahnow.net", "https://videos.api.alummahnow.net/api/v1/telemetry/batches")]
    [InlineData("https://videos.api.qa.alummahnow.net/", "https://videos.api.qa.alummahnow.net/api/v1/telemetry/batches")]
    [InlineData("http://127.0.0.1:3086", null)]
    public void Reports_go_only_to_an_https_videos_api(string api, string? expected) =>
        Assert.Equal(expected, CoreTelemetryService.IngestUrl(new Uri(api)));

    [Theory]
    [InlineData("Windows", "windows")]
    [InlineData("Linux", "linux")]
    [InlineData("macOS", "macos")]
    public void Platform_names_are_the_ones_the_server_maps(string os, string expected) =>
        Assert.Equal(expected, CoreTelemetryService.Platform(os));

    [Fact]
    public void Off_by_default_and_a_crash_while_off_keeps_nothing()
    {
        if (!CoreLoads()) return;
        var dir = TempDir();
        var buffer = Path.Combine(dir, "telemetry.json");
        using var telemetry = new CoreTelemetryService(new Uri("http://127.0.0.1:9"), "AlUmmahNowLinux/1.0.0", "1.0.0", "ar", buffer, enabled: false);

        Assert.False(telemetry.Enabled);
        telemetry.ReportCrash(new InvalidOperationException("boom"));
        Assert.False(File.Exists(buffer) && new FileInfo(buffer).Length > 2 && File.ReadAllText(buffer).Contains("boom"));
    }

    [Fact]
    public void A_crash_while_on_is_on_disk_at_once_and_turning_off_deletes_it()
    {
        if (!CoreLoads()) return;
        var dir = TempDir();
        var buffer = Path.Combine(dir, "telemetry.json");
        using var telemetry = new CoreTelemetryService(new Uri("http://127.0.0.1:9"), "AlUmmahNowLinux/1.0.0", "1.0.0", "en", buffer, enabled: true);

        Assert.True(telemetry.Enabled);
        telemetry.ReportCrash(new InvalidOperationException("boom for someone@example.com"));
        Assert.True(File.Exists(buffer));
        var saved = File.ReadAllText(buffer);
        Assert.Contains("InvalidOperationException", saved);
        // The core scrubs before anything is stored
        Assert.DoesNotContain("someone@example.com", saved);

        telemetry.SetEnabled(false);
        Assert.False(telemetry.Enabled);
        Assert.False(File.Exists(buffer) && File.ReadAllText(buffer).Contains("InvalidOperationException"));
    }

    [Fact]
    public async Task Flushing_without_an_ingest_url_or_network_never_throws()
    {
        if (!CoreLoads()) return;
        using var telemetry = new CoreTelemetryService(new Uri("http://127.0.0.1:9"), "AlUmmahNowLinux/1.0.0", "1.0.0", "en", Path.Combine(TempDir(), "t.json"), enabled: true);
        telemetry.ReportCrash(new Exception("x"));
        await telemetry.FlushAsync();
    }

    [Fact]
    public void The_null_service_does_nothing() =>
        Assert.False(NullTelemetryService.Instance.Enabled);

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aun-telemetry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // CI runs these where the native core is packaged; a machine without it skips them.
    private static bool CoreLoads()
    {
        try
        {
            CoreRuntime.Version();
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }
}
