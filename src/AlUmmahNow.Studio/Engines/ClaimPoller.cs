using AlUmmahNow.CoreBridge.Studio;

namespace AlUmmahNow.Studio.Engines;

public enum ClaimResult { Ready, Failed, Cancelled }

/// <summary>
/// After an on-device transcript is queued, the draft appears in the drafts list once the server's claim job
/// finishes. Polls the list with backoff (2 s, 4 s, 8 s … at most 30 s between checks).
/// </summary>
public sealed class ClaimPoller
{
    private readonly ICaptionsApi _api;

    public ClaimPoller(ICaptionsApi api) => _api = api;

    public Func<TimeSpan, CancellationToken, Task> Pause { get; set; } = Task.Delay;

    /// <summary>Stops after this long; the draft can still appear later and the list shows it then.</summary>
    public TimeSpan GiveUpAfter { get; set; } = TimeSpan.FromMinutes(15);

    public static TimeSpan Delay(int attempt) => TimeSpan.FromSeconds(Math.Min(30, 2 << Math.Min(attempt, 4)));

    /// <summary>
    /// Waits for the language's draft to be newer than <paramref name="since"/> (or to exist) with no queued claim.
    /// Returns the list it last saw, or null when cancelled.
    /// </summary>
    public async Task<(ClaimResult Result, CaptionDraftList? List, string? Error)> WaitAsync(string videoId, string language, string? channelId, DateTimeOffset? since, CancellationToken cancellationToken = default)
    {
        var waited = TimeSpan.Zero;
        CaptionDraftList? last = null;
        for (var attempt = 0; ; attempt++)
        {
            var delay = Delay(attempt);
            try
            {
                await Pause(delay, cancellationToken).ConfigureAwait(false);
                waited += delay;
                last = await _api.DraftsAsync(videoId, channelId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (ClaimResult.Cancelled, last, null);
            }
            catch (Exception ex)
            {
                // A failed check is retried at the next step, until the time runs out.
                if (waited >= GiveUpAfter) return (ClaimResult.Failed, last, ex.Message);
                continue;
            }

            var claim = last.Claims.FirstOrDefault(c => c.Language == language);
            if (claim?.Status == CaptionClaimStatus.Failed) return (ClaimResult.Failed, last, claim.Error);
            var draft = last.Drafts.FirstOrDefault(d => d.Language == language);
            var fresh = draft != null && (since == null || draft.UpdatedAt == null || draft.UpdatedAt > since);
            if (claim == null && fresh) return (ClaimResult.Ready, last, null);
            if (waited >= GiveUpAfter) return (ClaimResult.Failed, last, "timeout");
        }
    }
}
