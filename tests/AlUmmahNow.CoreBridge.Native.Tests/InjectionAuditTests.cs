using AlUmmahNow.CoreBridge.Native;
using AlUmmahNow.CoreBridge.Native.SelfUpdate;
using Xunit;

namespace AlUmmahNow.CoreBridge.Native.Tests;

public sealed class InjectionAuditTests
{
    [Theory]
    [InlineData("https://alummahnow.net/watch/v1", true)]
    [InlineData("http://127.0.0.1:8787/auth", true)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("ms-msdt:/id", false)]
    [InlineData("javascript:alert(1)", false)]
    public void ShellBrowserLauncher_allows_only_http_and_https_uris(string raw, bool expected)
    {
        var uri = new Uri(raw);
        Assert.Equal(expected, ShellBrowserLauncher.IsAllowedUri(uri));
        if (!expected)
        {
            var launcher = new ShellBrowserLauncher();
            Assert.Throws<ArgumentException>(() => launcher.Open(uri));
        }
    }

    [Theory]
    [InlineData("../escape.nupkg")]
    [InlineData("sub/pkg.nupkg")]
    [InlineData("sub\\pkg.nupkg")]
    [InlineData("..")]
    public void StagedPackage_rejects_path_traversal_filenames(string badName)
    {
        var file = new ReleaseFile("https://github.com/Al-Ummah-Now/AlUmmahNowDesktop/releases/download/v1/a.nupkg", new string('a', 64), 10);
        Assert.Throws<ArgumentException>(() => StagedPackage.Prepare("/tmp/stage", badName, file, "/tmp"));
    }
}
