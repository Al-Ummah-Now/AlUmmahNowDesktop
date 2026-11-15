using AlUmmahNow.Studio.Timeline;
using Xunit;

namespace AlUmmahNow.Platform.Windows.Tests;

/// <summary>Project sources must look local before any file system call (runs on the Windows and Linux runners).</summary>
public class EditorSourcePathTests
{
    [Theory]
    [InlineData(@"\\attacker.example\share\cam.mp4")]
    [InlineData("//attacker.example/share/cam.mp4")]
    [InlineData(@"\/attacker.example/share/cam.mp4")]
    [InlineData(@"/\attacker.example\share\cam.mp4")]
    [InlineData(@"\\?\UNC\attacker.example\share\cam.mp4")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData("file://attacker.example/cam.mp4")]
    [InlineData("cam.mp4")]
    [InlineData("")]
    public void Network_device_uri_and_relative_paths_are_refused(string path) => Assert.False(EdlProject.HasLocalShape(path));

    [Fact]
    public void Platform_local_paths_are_accepted()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(EdlProject.HasLocalShape(@"C:\Users\me\Videos\cam.mp4"));
            Assert.True(EdlProject.HasLocalShape("D:/clips/cam.mp4"));
            Assert.False(EdlProject.HasLocalShape(@"\Videos\cam.mp4"));
            Assert.False(EdlProject.HasLocalShape("C:cam.mp4"));
        }
        else
        {
            Assert.True(EdlProject.HasLocalShape("/home/me/Videos/cam.mp4"));
            Assert.False(EdlProject.HasLocalShape(@"C:\Users\me\cam.mp4"));
        }
    }
}
