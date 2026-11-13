using System.Net;
using System.Net.Sockets;
using System.Text;
using AlUmmahNow.Admin.Media;
using AlUmmahNow.Admin.Review;
using Xunit;

namespace AlUmmahNow.Admin.Tests;

/// <summary>
/// Owner rule: "never cache or download on phone or native app ever, only encrypted stream".
/// A real loopback server serves review media carrying a random marker; after the app's HTTP
/// stack has fetched it, no file anywhere the process can write may contain that marker.
/// The Windows CI scenario runs the same scan after the player has streamed a video.
/// </summary>
public class MediaNeverOnDiskTests
{
    [Fact]
    public async Task Review_media_and_frames_stay_in_memory()
    {
        var marker = "AUN-MEDIA-MARKER-" + Guid.NewGuid().ToString("N");
        var payload = Encoding.ASCII.GetBytes(marker + new string('x', 200_000) + marker);
        var started = DateTime.UtcNow.AddSeconds(-2);

        using var server = new LoopbackServer(payload);
        var service = new AdminApiReviewService(server.Root, server.Root, _ => Task.FromResult<string?>("token"), "test");

        var bytes = await service.MediaAsync("rev-1", "frames/000042.jpg");
        Assert.Equal(payload, bytes);
        using (var http = PrivateHttp.Create())
        {
            var streamed = await http.GetByteArrayAsync(new Uri(server.Root, "/stream/seg-0.ts"));
            Assert.Equal(payload.Length, streamed.Length);
        }

        Assert.All(server.Requests, r => Assert.Contains("no-store", r));
        var hits = MediaDiskScan.Find(Encoding.ASCII.GetBytes(marker), started);
        Assert.True(hits.Count == 0, "review media found on disk: " + string.Join(", ", hits));
    }

    /// <summary>
    /// CI Admin Windows runs this after the app streamed a loopback HLS video: AUN_SCAN_MARKER_FILE
    /// holds bytes from the middle of a segment, AUN_SCAN_SINCE when the scenarios began.
    /// Without them (a normal test run) there is nothing to look for.
    /// </summary>
    [Fact]
    public void Streamed_video_is_nowhere_on_disk()
    {
        var markerFile = Environment.GetEnvironmentVariable("AUN_SCAN_MARKER_FILE");
        if (string.IsNullOrEmpty(markerFile)) return;
        var since = DateTime.Parse(Environment.GetEnvironmentVariable("AUN_SCAN_SINCE")!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
        var marker = File.ReadAllBytes(markerFile);
        Assert.True(marker.Length >= 128);
        var hits = MediaDiskScan.Find(marker, since).Where(h => !string.Equals(Path.GetFullPath(h), Path.GetFullPath(markerFile), StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(hits.Count == 0, "streamed video found on disk: " + string.Join(", ", hits));
    }

    [Fact]
    public void The_scan_finds_media_that_was_written()
    {
        var marker = Encoding.ASCII.GetBytes("AUN-SCAN-SELFTEST-" + Guid.NewGuid().ToString("N"));
        var started = DateTime.UtcNow.AddSeconds(-2);
        var path = Path.Combine(Path.GetTempPath(), "aun-scan-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, new byte[1000].Concat(marker).Concat(new byte[10]).ToArray());
        try
        {
            Assert.Contains(path, MediaDiskScan.Find(marker, started));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly byte[] _payload;
        private readonly CancellationTokenSource _stop = new();

        public LoopbackServer(byte[] payload)
        {
            _payload = payload;
            _listener.Start();
            Root = new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/");
            _ = Task.Run(LoopAsync);
        }

        public Uri Root { get; }

        public List<string> Requests { get; } = new();

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        var head = new StringBuilder();
                        var buf = new byte[1];
                        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(buf) == 1) head.Append((char)buf[0]);
                        lock (Requests) Requests.Add(head.ToString().ToLowerInvariant());
                        var header = "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nCache-Control: private, no-store\r\nContent-Length: " + _payload.Length + "\r\nConnection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                        await stream.WriteAsync(_payload);
                    }
                });
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}

/// <summary>Looks for a byte marker in files changed since <c>since</c> under every place a process writes caches or temp files.</summary>
public static class MediaDiskScan
{
    /// <summary>
    /// Temp, the working and app folders and the HTTP caches always; the whole per-user app data
    /// and cache trees when AUN_FULL_DISK_SCAN=1 (CI sets it: a clean runner scans in seconds,
    /// a developer's home folder takes minutes).
    /// </summary>
    public static IEnumerable<string> Roots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new List<string?>
        {
            Path.GetTempPath(),
            Environment.CurrentDirectory,
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.InternetCache),
            Path.Combine(local, "AlUmmahNowAdmin"),
            Path.Combine(local, "Microsoft", "Windows", "INetCache"),
        };
        if (Environment.GetEnvironmentVariable("AUN_FULL_DISK_SCAN") == "1")
        {
            roots.Add(local);
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            roots.Add(Environment.GetEnvironmentVariable("XDG_CACHE_HOME"));
            roots.Add(Path.Combine(home, ".cache"));
            roots.Add(Path.Combine(home, "Library", "Caches"));
        }

        return roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Select(r => Path.GetFullPath(r!)).Distinct();
    }

    public static List<string> Find(byte[] marker, DateTime sinceUtc)
    {
        var hits = new List<string>();
        foreach (var root in Roots()) Walk(root, marker, sinceUtc, hits, 0);
        return hits.Distinct().ToList();
    }

    private static void Walk(string dir, byte[] marker, DateTime since, List<string> hits, int depth)
    {
        if (depth > 12) return;
        IEnumerable<string> files, dirs;
        try
        {
            files = Directory.EnumerateFiles(dir);
            dirs = Directory.EnumerateDirectories(dir);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var f in files)
        {
            try
            {
                var info = new FileInfo(f);
                if (info.LastWriteTimeUtc < since || info.Length < marker.Length || info.Length > 512L * 1024 * 1024) continue;
                if (Contains(f, marker)) hits.Add(f);
            }
            catch (Exception)
            {
                // Locked or vanished: not ours.
            }
        }

        foreach (var d in dirs)
        {
            try
            {
                if ((new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) != 0) continue;
            }
            catch (Exception)
            {
                continue;
            }

            Walk(d, marker, since, hits, depth + 1);
        }
    }

    private static bool Contains(string path, byte[] marker)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[1 << 16];
        var carry = 0;
        int read;
        while ((read = s.Read(buffer, carry, buffer.Length - carry)) > 0)
        {
            var len = carry + read;
            if (buffer.AsSpan(0, len).IndexOf(marker) >= 0) return true;
            carry = Math.Min(marker.Length - 1, len);
            Array.Copy(buffer, len - carry, buffer, 0, carry);
        }

        return false;
    }
}
