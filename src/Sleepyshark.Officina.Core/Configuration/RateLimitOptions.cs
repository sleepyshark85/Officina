using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>Rate limits on admitted work (ING-03). S19 adds the per-run limit, once work can join a running run.</summary>
public sealed record RateLimitOptions
{
    [Setting("The limit for each owner. Anonymous callers share one. No limit when unset.", Example = """{ "permits": 20, "window": "01:00:00" }""")]
    public RateLimit? PerOwner { get; init; }

    [Setting("The limit for each tenant. Callers without a tenant share one. No limit when unset.", Example = """{ "permits": 500, "window": "01:00:00" }""")]
    public RateLimit? PerTenant { get; init; }
}

/// <summary>A fixed-window rate limit.</summary>
public sealed record RateLimit
{
    [Setting("How many work items are admitted in each window.", Example = "20")]
    [Required(ErrorMessage = Messages.Required)]
    [Range(1, int.MaxValue, ErrorMessage = "must be at least 1.")]
    public int? Permits { get; init; }

    [Setting("How long a window lasts, as `hh:mm:ss`.", Example = "\"01:00:00\"")]
    [Required(ErrorMessage = Messages.Required)]
    [Range(typeof(TimeSpan), "00:00:00", "10675199.02:48:05.4775807", MinimumIsExclusive = true, ErrorMessage = "must be greater than zero.")]
    public TimeSpan? Window { get; init; }
}
