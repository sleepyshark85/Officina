using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Tools;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// The step in front of every agent (ING-01): masking when it is on (ING-02), then the admission checks in order, where
/// the first rejection wins: the agent takes work that arrives this way (TRG-01), then the owner's and the tenant's rate
/// limits (ING-03).
/// </summary>
internal sealed class Admission(PolicyOptions policies)
{
    private readonly Regex? patterns = policies.Masking.Enabled ? Masker.Expression(policies.Masking.Patterns ?? MaskingOptions.BuiltInPatterns) : null;
    private readonly RateLimits owners = new(policies.RateLimits.PerOwner);
    private readonly RateLimits tenants = new(policies.RateLimits.PerTenant);

    /// <returns>The work as the agent sees it, the run's masking when it is on, and why the work is rejected, if it is.</returns>
    public (Work Work, Masker? Masker, string? Rejection) Admit(Work work, AgentDefinition agent)
    {
        var masker = patterns is null ? null : new Masker(patterns);
        var admitted = masker is null ? work : work with { Input = masker.Mask(work.Input) };
        var rejection = agent.Triggers?.Contains(work.Trigger) == false
                ? $"agent {work.Agent} does not take work by {JsonNamingPolicy.CamelCase.ConvertName(work.Trigger.ToString())}"
            : !owners.TryAcquire(work.Caller.Id) ? "the owner's rate limit is reached"
            : !tenants.TryAcquire(work.Caller.Tenant) ? "the tenant's rate limit is reached"
            : null;
        return (admitted, masker, rejection);
    }

    /// <summary>A fixed-window limit for each key. A limiter replenishes when it is next used, so none needs a timer.</summary>
    private sealed class RateLimits(RateLimit? limit)
    {
        private readonly ConcurrentDictionary<string, FixedWindowRateLimiter> limiters = new(StringComparer.Ordinal);

        public bool TryAcquire(string? key)
        {
            if (limit is null)
            {
                return true;
            }

            var limiter = limiters.GetOrAdd(key ?? "", _ => new(new()
            {
                PermitLimit = limit.Permits, Window = limit.Window, QueueLimit = 0, AutoReplenishment = false,
            }));
            limiter.TryReplenish();
            using var lease = limiter.AttemptAcquire();
            return lease.IsAcquired;
        }
    }
}
