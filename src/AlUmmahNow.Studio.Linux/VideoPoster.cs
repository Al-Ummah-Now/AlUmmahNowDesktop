using System.Collections.Concurrent;
using AlUmmahNow.CoreBridge.Logging;

namespace AlUmmahNow.Studio.Linux;

/// <summary>
/// The first frame of a local video as a small JPEG, read with GStreamer on a worker thread. Each file is read at most once
/// per run. The answer is null when GStreamer can't decode the file; the caller then keeps its night tile.
/// </summary>
public static class VideoPoster
{
    private const int Width = 320;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<string, Task<byte[]?>> Frames = new(StringComparer.Ordinal);

    public static Task<byte[]?> FrameAsync(string path) => Frames.GetOrAdd(path, p => Task.Run(() => Read(p)));

    private static async Task<byte[]?> Read(string path)
    {
        // A fresh directory only this user can open (0700), so no other account can plant a link where the frame is written.
        var dir = Directory.CreateTempSubdirectory("aun-poster-");
        var output = Path.Combine(dir.FullName, "frame.jpg");
        Gst.Element? pipeline = null;
        try
        {
            // snapshot=true: jpegenc ends the stream after the first frame.
            pipeline = Gst.Functions.ParseLaunch(
                $"filesrc location={Quote(path)} ! decodebin ! videoconvert ! videoscale ! video/x-raw,width={Width},pixel-aspect-ratio=1/1 "
                + $"! jpegenc snapshot=true ! filesink location={Quote(output)}");
            if (pipeline?.GetBus() is not { } bus) return null;
            pipeline.SetState(Gst.State.Playing);
            var until = DateTime.UtcNow + Limit;
            while (DateTime.UtcNow < until)
            {
                var message = bus.PopFiltered(Gst.MessageType.Eos | Gst.MessageType.Error);
                if (message?.Type == Gst.MessageType.Error) return null;
                if (message?.Type == Gst.MessageType.Eos) break;
                await Task.Delay(50);
            }

            pipeline.SetState(Gst.State.Null);
            pipeline = null;
            return File.Exists(output) && new FileInfo(output).Length > 0 ? await File.ReadAllBytesAsync(output) : null;
        }
        catch (Exception ex) when (ex is GLib.GException or IOException or InvalidOperationException)
        {
            Logger.Warn("no poster frame for a staged file", ex);
            return null;
        }
        finally
        {
            pipeline?.SetState(Gst.State.Null);
            try
            {
                dir.Delete(true);
            }
            catch (IOException)
            {
                // A private temporary folder left behind is harmless.
            }
        }
    }

    /// <summary>A gst-launch string literal: quoted, with backslashes and quotes escaped.</summary>
    public static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
