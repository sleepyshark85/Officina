namespace Sleepyshark.Officina;

/// <summary>
/// One durable record of a run: when, in which order, of which run, conversation, agent and memory scope, in which trace
/// and span, and what. Text is truncated with its size noted, and secrets are redacted.
/// </summary>
public sealed record AuditEntry
{
    public required DateTimeOffset Time { get; init; }

    /// <summary>The entry's place in its run, from 1. A gap means an entry the sink failed to write.</summary>
    public required long Sequence { get; init; }

    public required string Run { get; init; }

    public required string Conversation { get; init; }

    public required string Agent { get; init; }

    /// <summary>Whose memory the run sees; null for a run without one.</summary>
    public string? MemoryScope { get; init; }

    /// <summary>The run's trace, in W3C hex form; null when nothing listens to the core's telemetry.</summary>
    public string? TraceId { get; init; }

    /// <summary>The step's span: the run's for its start and end, the tool call's for the others.</summary>
    public string? SpanId { get; init; }

    public required AuditKind Kind { get; init; }

    public string? Tool { get; init; }

    public string? CallId { get; init; }

    /// <summary>The tool call's input, as JSON text.</summary>
    public string? Input { get; init; }

    /// <summary>A short word for how it went: a result kind, a tool outcome or an approval answer.</summary>
    public string? Outcome { get; init; }

    /// <summary>More about the outcome: a tool's result or error, a denial's reason, a run's error.</summary>
    public string? Detail { get; init; }

    public TimeSpan? Duration { get; init; }

    public Usage? Usage { get; init; }

    /// <summary>What the run's tokens cost, in US dollars, on its end entry.</summary>
    public decimal? Cost { get; init; }
}
