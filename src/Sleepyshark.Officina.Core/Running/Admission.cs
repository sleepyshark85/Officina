using System.Text.Json;
using System.Text.RegularExpressions;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Tools;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// The step in front of every agent (ING-01): masking when it is on (ING-02), then the admission checks in order, where
/// the first rejection wins: the agent takes work that arrives this way (TRG-01), then the owner's and the tenant's rate
/// limits (ING-03).
/// </summary>
internal sealed class Admission(PolicyOptions policies, TimeProvider time)
{
    private readonly Regex? patterns = policies.Masking.Enabled ? Masker.Expression(policies.Masking.Patterns ?? MaskingOptions.BuiltInPatterns) : null;
    private readonly RateLimits owners = new(policies.RateLimits.PerOwner, time);
    private readonly RateLimits tenants = new(policies.RateLimits.PerTenant, time);

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

    /// <summary>
    /// A fixed window for each key, which starts with the key's first work item after the last window ended. The windows
    /// are kept for every owner and tenant seen, which is a few bytes each.
    /// </summary>
    private sealed class RateLimits(RateLimit? limit, TimeProvider time)
    {
        private readonly Dictionary<string, (long Start, int Used)> windows = new(StringComparer.Ordinal);

        public bool TryAcquire(string? key)
        {
            if (limit is null)
            {
                return true;
            }

            lock (windows)
            {
                var now = time.GetTimestamp();
                var (start, used) = windows.TryGetValue(key ?? "", out var window) && time.GetElapsedTime(window.Start, now) < limit.Window ? window : (now, 0);
                windows[key ?? ""] = (start, Math.Min(used + 1, limit.Permits));
                return used < limit.Permits;
            }
        }
    }
}
