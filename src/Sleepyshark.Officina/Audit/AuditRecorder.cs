using System.Diagnostics;
using System.Globalization;

namespace Sleepyshark.Officina;

/// <summary>
/// Turns a run's important events into numbered audit entries, written one at a time. Without a sink it records nothing
/// and every record succeeds.
/// </summary>
internal sealed class AuditRecorder(Agent agent, Conversation conversation, Activity? runSpan, string? memoryScope) : IDisposable
{
    /// <summary>The longest text an entry keeps per field, in characters.</summary>
    internal const int MaxTextLength = 4_000;

    private readonly SemaphoreSlim gate = new(1, 1);
    private long sequence;

    /// <summary>Identifies the run in the audit trail and telemetry.</summary>
    public string Run { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Records an entry for the step whose span is <paramref name="step"/>, or for the run; returns whether it was recorded.
    /// The recorder fills in the time, identity, span and kind; <paramref name="details"/> adds the rest, such as the tool
    /// and outcome. A failed write shows in telemetry; only a write tool's attempt depends on it.
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
