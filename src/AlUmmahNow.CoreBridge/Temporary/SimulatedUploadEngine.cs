using System;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge.Temporary
{
    /// <summary>
    /// TEMPORARY: reports progress but sends nothing. Real TUS arrives with aun-upload and needs sign-in.
    /// </summary>
    public sealed class SimulatedUploadEngine : IUploadEngine
    {
        private readonly TimeSpan _stepDelay;
        private readonly int _steps;

        public SimulatedUploadEngine(TimeSpan stepDelay, int steps = 20)
        {
            _stepDelay = stepDelay;
            _steps = steps;
        }

        public async Task UploadAsync(UploadRequest request, IProgress<long> progress, CancellationToken cancellationToken)
        {
            var chunk = Math.Max(1, request.Size / _steps);
            var sent = request.Offset;
            while (sent < request.Size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(_stepDelay, cancellationToken).ConfigureAwait(false);
                sent = Math.Min(request.Size, sent + chunk);
                progress.Report(sent);
            }
        }
    }
}
