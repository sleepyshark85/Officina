using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>How the model gateway retries a call that failed for a reason that may pass (REL-01).</summary>
public sealed record RetryOptions
{
    [Setting("How many times a call is made in all, the first included, when it fails as transient or rate-limited. 1 means no retries. When the attempts are used up, the next fallback is tried.",
        Example = "5")]
    [Range(1, int.MaxValue, ErrorMessage = "must be at least 1.")]
    public int MaxAttempts { get; init; } = 5;

    [Setting("How long to wait before the first retry, as `hh:mm:ss`. Each retry waits twice as long as the one before, up to `maxDelay`. If the provider asks for a longer wait, that is used instead.",
        Example = "\"00:00:01\"")]
    [Range(typeof(TimeSpan), "00:00:00", "10675199.02:48:05.4775807", MinimumIsExclusive = true, ErrorMessage = "must be greater than zero.")]
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    [Setting("The longest wait the progression reaches, as `hh:mm:ss`. A wait the provider asks for is not cut short by it.", Example = "\"00:01:00\"")]
    [Range(typeof(TimeSpan), "00:00:00", "10675199.02:48:05.4775807", MinimumIsExclusive = true, ErrorMessage = "must be greater than zero.")]
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(1);
}
