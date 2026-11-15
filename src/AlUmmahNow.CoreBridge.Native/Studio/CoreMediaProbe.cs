using AlUmmahNow.Core;
using AlUmmahNow.CoreBridge.Studio;

namespace AlUmmahNow.CoreBridge.Native.Studio;

/// <summary><see cref="IMediaProbe"/> on Core's <c>media_probe_json</c>.</summary>
public sealed class CoreMediaProbe : IMediaProbe
{
    public async Task<MediaSourceInfo> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var json = await AunCoreMethods.MediaProbeJson(path).WaitAsync(cancellationToken).ConfigureAwait(false);
            return MediaSourceInfo.FromProbeJson(json);
        }
        catch (AunException ex)
        {
            throw new InvalidOperationException("Could not read " + Path.GetFileName(path) + ": " + ex.GetType().Name, ex);
        }
    }
}
