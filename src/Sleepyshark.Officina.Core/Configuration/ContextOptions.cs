using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>How an agent's model input is built (DESIGN.md §3).</summary>
public sealed record ContextOptions
{
    // CTX-09: rebuilt for every model call, so they may use the time, unlike instructions.
    [Setting("Facts added after the history for every model call, in this order, such as limits or the date. Placeholders may use `{{now}}` and `{{now:date}}`, and the project and agent values.",
        Example = """["Today is {{now:date}}.", "Replies are limited to 300 words."]""")]
    public IReadOnlyList<string> OperatingFacts { get; init; } = [];

    // CTX-11: the boundaries before it last an hour, and a longer lifetime may not follow a shorter one.
    [Setting("How long the provider keeps the conversation cached between model calls, as `hh:mm:ss`, at most one hour. Agents that often wait for approvals benefit from longer.",
        Example = "\"01:00:00\"")]
    [Range(typeof(TimeSpan), "00:00:00", "01:00:00", MinimumIsExclusive = true, ErrorMessage = "must be more than zero and at most one hour.")]
    public TimeSpan HistoryCacheLifetime { get; init; } = TimeSpan.FromMinutes(5);

    // COST-01.
    [Setting("The share of a model call's input read from the provider's cache, from 0 to 1, below which a warning is raised. The first call of a turn is not checked.",
        Example = "0.7")]
    [Range(0d, 1d, ErrorMessage = "must be between 0 and 1.")]
    public double CacheHitWarning { get; init; } = 0.7;
}
