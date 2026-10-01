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

    [Setting("Knowledge retrieved before each turn.", Example = """{ "beforeTurn": ["handbook"], "handOffWhenNotCovered": true }""")]
    [Required(ErrorMessage = Messages.Required)]
    public RetrievalOptions Retrieval { get; init; } = new();
}

/// <summary>Knowledge retrieved before each turn (CTX-04, CTX-05). Agents search when they choose through <c>knowledge:</c> tools.</summary>
public sealed record RetrievalOptions
{
    [Setting("Knowledge sources, by name in `knowledge`, searched with the turn's work before the turn starts. Their passages are given to the model as data in the volatile context.",
        Example = """["handbook"]""")]
    public IReadOnlyList<string> BeforeTurn { get; init; } = [];

    [Setting("Whether the turn ends in a handoff for a policy gap, without calling the model, when no source searched before the turn covers the work.",
        Example = "true")]
    public bool HandOffWhenNotCovered { get; init; }
}
