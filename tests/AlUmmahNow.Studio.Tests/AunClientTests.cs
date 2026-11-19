using AlUmmahNow.CoreBridge;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

public class AunClientTests
{
    [Theory]
    [InlineData("AlUmmahNowStudioWindows/1.4.0", "Windows", "10.0", "aun-studio/1.4.0 (Windows 10.0)")]
    [InlineData("AlUmmahNowStudioLinux/1.4.0", "Linux", "6.8", "aun-studio/1.4.0 (Linux 6.8)")]
    [InlineData("AlUmmahNowAdminWindows/2.0.1", "Windows", "10.0", "aun-admin/2.0.1 (Windows 10.0)")]
    [InlineData("AlUmmahNowWindows/1.0 (beta)", "Windows", "10.0", "aun-desktop/1.0beta (Windows 10.0)")]
    [InlineData(null, "Linux", "", "aun-desktop/0 (Linux)")]
    public void NamesTheAppVersionAndOs(string? userAgent, string os, string osVersion, string expected) =>
        Assert.Equal(expected, AunClient.FromUserAgent(userAgent, (os, osVersion)));

    [Fact]
    public void TheCurrentOsIsOneTheServersKnow() =>
        Assert.Contains(AunClient.CurrentOs().Name, new[] { "Windows", "Linux", "macOS", "Tizen" });
}
