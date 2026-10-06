using System.Diagnostics;
using System.Globalization;

namespace Sleepyshark.Officina;

/// <summary>
/// Where the audit trail goes (AUD-04, ARCHITECTURE §4.5). Entries of one run arrive one at a time, in sequence order.
/// A write that returns is durable; a write that fails throws, and is never swallowed by the sink.
/// </summary>
public interface IAuditSink
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken);
}

/// <summary>What an audit entry records (AUD-01).</summary>
public enum AuditKind
{
    RunStarted,

    /// <summary>The run's result, refusals, provider failures and prefix mismatches included, with its usage.</summary>
    RunEnded,

    /// <summary>A tool call is about to run; a write tool's call runs only once this is recorded (AUD-02).</summary>
    ToolStarted,

    /// <summary>A tool call's outcome: every call has one, whether it ran or not.</summary>
    ToolEnded,

    ApprovalAsked,

    ApprovalAnswered,

    /// <summary>
    /// A tool source connected, failed to connect, or lost its connection; the entry's tool names the source (AUD-01). It
    /// is recorded by the run that noticed it: with a shared source, not always the run whose call met it.
    /// </summary>
    ToolSource,

    /// <summary>The provider compacted the conversation during a model call (HIST-04): the detail says how much.</summary>
    Compacted,

    /// <summary>The provider cleared old tool results for a model call (HIST-04): the detail says how many.</summary>
    Cleared,
}

/// <summary>
/// One durable record of a run (AUD-03): when, in which order, of which run, conversation, agent and memory scope, in
/// which trace and span, and what. Text is truncated with its size noted, and the agent's secrets are redacted (AUD-05).
/// </summary>
public sealed record AuditEntry
{
    public required DateTimeOffset Time { get; init; }

    /// <summary>The entry's place in its run, from 1. A gap means an entry the sink failed to write.</summary>
    public required long Sequence { get; init; }

    public required string Run { get; init; }

    public required string Conversation { get; init; }

    public required string Agent { get; init; }

    /// <summary>Whose memory the run sees, as the host named it in the run input; null for a run without one.</summary>
    public string? MemoryScope { get; init; }

    /// <summary>The run's trace (EVT-02), in W3C hex form; null when nothing listens to the core's telemetry.</summary>
    public string? TraceId { get; init; }

    /// <summary>The span of the step recorded: the run's for its start and end, the tool call's for the others.</summary>
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

    /// <summary>What the run's tokens cost, in US dollars, for its end.</summary>
    public decimal? Cost { get; init; }
}

/// <summary>
/// Turns a run's important events into audit entries (ARCHITECTURE §3), numbered and written one at a time. Without a
/// sink it records nothing and every record succeeds (GEN-02).
/// </summary>
internal sealed class AuditRecorder(AgentDefinition agent, Conversation conversation, Activity? runSpan, string? memoryScope) : IDisposable
{
    /// <summary>The longest text an entry keeps per field, in characters.</summary>
    internal const int MaxTextLength = 4_000;

    private readonly SemaphoreSlim gate = new(1, 1);
    private long sequence;

    /// <summary>Identifies the run in the audit trail and its telemetry.</summary>
    public string Run { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Records an entry of the step whose span is <paramref name="step"/>, or else of the run; returns whether it was
    /// recorded. The recorder fills in when, which run, conversation, agent, scope and span, and the kind;
    /// <paramref name="details"/> adds the rest to that entry, such as its tool and outcome. A failed write shows in
    /// telemetry (AUD-06); only a write tool's attempt depends on it.
    /// </summary>
    public async Task<bool> RecordAsync(AuditKind kind, Func<AuditEntry, AuditEntry>? details = null, Activity? step = null)
    {
        if (agent.AuditSink is not { } sink)
        {
            return true;
        }

        // The trail is written even for a cancelled run, so the host's cancellation does not reach the sink.
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var entry = new AuditEntry
            {
                Time = agent.Time.GetUtcNow(),
                Sequence = ++sequence,
                Run = Run,
                Conversation = conversation.Id,
                Agent = agent.Name,
                MemoryScope = memoryScope,
                TraceId = (step ?? runSpan)?.TraceId.ToHexString(),
                SpanId = (step ?? runSpan)?.SpanId.ToHexString(),
                Kind = kind,
            };
            entry = details?.Invoke(entry) ?? entry;
            await sink.WriteAsync(entry with { Input = Clean(entry.Input), Detail = Clean(entry.Detail) }, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
#pragma warning disable CA1031 // A sink may fail in any way; the caller decides what a missing entry means.
        catch (Exception)
#pragma warning restore CA1031
        {
            Telemetry.AuditFailed(step ?? runSpan, agent, kind);
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();

    private string? Clean(string? text)
    {
        if (text is null)
        {
            return null;
        }

        text = agent.Redact(text);
        return text.Length <= MaxTextLength
            ? text
            : string.Create(CultureInfo.InvariantCulture, $"{Strings.Cut(text, MaxTextLength)}… [truncated: {text.Length} characters]");
    }
}
