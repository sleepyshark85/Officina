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

    [Setting("What history a request starts with, and how it is shortened.", Example = """{ "strategy": "shortened" }""")]
    [Required(ErrorMessage = Messages.Required)]
    public HistoryOptions History { get; init; } = new();
}

/// <summary>What history a request starts with (CTX-06), and how it is shortened (HIST-01).</summary>
public sealed record HistoryOptions
{
    /// <summary>The shortening the model provider does itself.</summary>
    public const string Provider = "provider";

    [Setting("`none`: each request starts a new conversation. `full`: the agent's conversation with the caller continues, and is never shortened, so once it is too long for the model the turn is handed off. `shortened`: it continues, and is shortened once when the model reports it too long. `lastTurns`: it continues with the last `lastTurns` turns only. All but `none` need the conversation store.",
        Example = "\"shortened\"")]
    public HistoryStrategy Strategy { get; init; } = HistoryStrategy.None;

    [Setting("How `shortened` history is shortened: `provider` by the model provider's own mechanism, or `extension:<id>` by a shortener the application registers. The current turn is never shortened.",
        Example = "\"extension:Acme.Summarizer\"")]
    [Required(ErrorMessage = Messages.Required)]
    public string Shortening { get; init; } = Provider;

    [Setting("For `lastTurns`: how many earlier turns a request starts with.", Example = "5")]
    [Range(1, int.MaxValue, ErrorMessage = "must be at least 1.")]
    public int LastTurns { get; init; } = 10;

    /// <summary>The id of the application's shortener, for an <c>extension:</c> shortening.</summary>
    public string? ExtensionId() => ToolOptions.After(Shortening, "extension:");
}

public enum HistoryStrategy
{
    None,
    Full,
    Shortened,
    LastTurns,
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
