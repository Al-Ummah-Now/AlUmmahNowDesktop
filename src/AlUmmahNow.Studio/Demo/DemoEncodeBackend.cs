using AlUmmahNow.Studio.Encode;

namespace AlUmmahNow.Studio.Demo;

/// <summary>
/// Review frames (<c>AUN_DEMO=1</c>, scene <c>active</c>): an encode queue with one finished job and one
/// running at 62 % with a time left. Never used in a normal launch. <see cref="Seed"/> queues the jobs;
/// <see cref="Clock"/> moves four minutes per reading, so the running job shows a steady estimate.
/// </summary>
public sealed class DemoEncodeBackend : IEncodeBackend
{
    public const double RunningProgress = 0.62;
    public const string FinishedFile = "friday-khutbah.mp4";
    public const string RunningFile = "seerah-part-4.mov";

    private readonly EncoderChoice _encoder;

    /// <param name="encoderName">The platform's hardware encoder, as the frames should name it.</param>
    public DemoEncodeBackend(string encoderName) => _encoder = new EncoderChoice(encoderName, EncoderKind.Hardware);

    public IReadOnlyList<EncoderChoice> AvailableEncoders() => new[] { _encoder };

    public async Task EncodeAsync(EncodeRequest request, EncoderChoice encoder, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Path.GetFileName(request.Source) != RunningFile)
        {
            progress.Report(1);
            return;
        }

        progress.Report(RunningProgress);
        // Stays running until the app closes.
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A clock that moves four minutes each time it is read (start, then each progress report).</summary>
    public static Func<DateTimeOffset> Clock()
    {
        var at = new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);
        var gate = new object();
        return () =>
        {
            lock (gate)
            {
                at = at.AddMinutes(4);
                return at;
            }
        };
    }

    /// <summary>Queues the finished job, then the running one.</summary>
    public static void Seed(EncodeQueue queue, string folder)
    {
        queue.Add(new EncodeRequest(Path.Combine(folder, FinishedFile), Path.Combine(folder, "friday-khutbah.encoded.mp4"), EncodePreset.Lecture));
        queue.Add(new EncodeRequest(Path.Combine(folder, RunningFile), Path.Combine(folder, "seerah-part-4.encoded.mp4"), EncodePreset.Conference));
    }
}
