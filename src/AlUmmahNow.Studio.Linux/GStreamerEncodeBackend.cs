using System.Runtime.InteropServices;
using AlUmmahNow.Studio.Encode;

namespace AlUmmahNow.Studio.Linux;

/// <summary>Executes encode jobs with GStreamer on a worker thread.</summary>
public sealed class GStreamerEncodeBackend : IEncodeBackend
{
    public IReadOnlyList<EncoderChoice> AvailableEncoders() =>
        GstPipeline.H264Encoders.Where(e => Gst.ElementFactory.Find(e.Name) != null).ToList();

    public Task EncodeAsync(EncodeRequest request, EncoderChoice encoder, IProgress<double> progress, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            var pipeline = Gst.Functions.ParseLaunch(GstPipeline.Build(request, encoder))
                ?? throw new InvalidOperationException("Could not build the pipeline");
            var bus = pipeline.GetBus() ?? throw new InvalidOperationException("Pipeline has no bus");
            try
            {
                pipeline.SetState(Gst.State.Playing);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var message = bus.PopFiltered(Gst.MessageType.Eos | Gst.MessageType.Error);
                    if (message?.Type == Gst.MessageType.Eos) break;
                    if (message?.Type == Gst.MessageType.Error) throw new InvalidOperationException(ReadError(message));

                    if (pipeline.QueryPosition(Gst.Format.Time, out var position) && pipeline.QueryDuration(Gst.Format.Time, out var duration) && duration > 0)
                    {
                        progress.Report(Math.Clamp((double)position / duration, 0, 1));
                    }

                    await Task.Delay(100, cancellationToken);
                }
            }
            finally
            {
                pipeline.SetState(Gst.State.Null);
            }

            if (!File.Exists(request.Output) || new FileInfo(request.Output).Length == 0)
            {
                throw new InvalidOperationException("Encoder produced no output");
            }
        }, cancellationToken);

    // Gir.Core 0.8.1 does not bind gst_message_parse_error (docs/D9-FINDING.md).
    [DllImport("libgstreamer-1.0.so.0")]
    private static extern void gst_message_parse_error(IntPtr message, out IntPtr error, out IntPtr debug);

    private static string ReadError(Gst.Message message)
    {
        gst_message_parse_error(message.Handle.DangerousGetHandle(), out var error, out _);
        return error != IntPtr.Zero ? Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "Encode failed" : "Encode failed";
    }
}
