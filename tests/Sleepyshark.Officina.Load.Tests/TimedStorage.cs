using System.Collections.Concurrent;
using System.Diagnostics;
using Sleepyshark.Officina.Core.Checkpoints;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Memory;
using Sleepyshark.Officina.Core.Records;
using Sleepyshark.Officina.Core.Tasks;

namespace Sleepyshark.Officina.Load.Tests;

/// <summary>
/// Storage that times the writes a turn makes on every iteration (events, audit entries, conversation turns) and passes everything
/// to the real storage underneath, so the durable writes LAT-01 leaves out are measured on their own (TEST-30).
/// </summary>
internal sealed class TimedStorage : IStorage
{
    private readonly IStorage inner;

    public TimedStorage(IStorage inner)
    {
        this.inner = inner;
        Events = new TimedEvents(inner.Events, Writes);
        Audit = new TimedAudit(inner.Audit, Writes);
        Conversations = new TimedConversations(inner.Conversations, Writes);
    }

    /// <summary>Each write, by kind, in milliseconds.</summary>
    public ConcurrentQueue<(string Kind, double Milliseconds)> Writes { get; } = new();

    public IRunStore Runs => inner.Runs;

    public IConversationStore Conversations { get; }

    public IEventLog Events { get; }

    public IAuditLog Audit { get; }

    public IRecordStore Records => inner.Records;

    public IArtifactStore Artifacts => inner.Artifacts;

    public ITaskStore Tasks => inner.Tasks;

    public IMemoryStore Memory => inner.Memory;

    public ICheckpointStore Checkpoints => inner.Checkpoints;

    public ValueTask<OwnerData> ExportAsync(string? tenant, string owner, CancellationToken ct) => inner.ExportAsync(tenant, owner, ct);

    public ValueTask DeleteAsync(string? tenant, string owner, CancellationToken ct) => inner.DeleteAsync(tenant, owner, ct);

    public ValueTask DeleteExpiredAsync(RetentionOptions retention, DateTimeOffset now, CancellationToken ct) => inner.DeleteExpiredAsync(retention, now, ct);

    private static async ValueTask TimeAsync(ConcurrentQueue<(string, double)> writes, string kind, Func<ValueTask> write)
    {
        var started = Stopwatch.GetTimestamp();
        await write().ConfigureAwait(false);
        writes.Enqueue((kind, Measure.Milliseconds(Stopwatch.GetTimestamp() - started)));
    }

    private sealed class TimedEvents(IEventLog inner, ConcurrentQueue<(string, double)> writes) : IEventLog
    {
        public ValueTask AppendAsync(string? tenant, CoreEvent coreEvent, CancellationToken ct) =>
            TimeAsync(writes, "event", () => inner.AppendAsync(tenant, coreEvent, ct));

        public ValueTask<IReadOnlyList<CoreEvent>> ReadAsync(string? tenant, string runId, long after, CancellationToken ct) => inner.ReadAsync(tenant, runId, after, ct);
    }

    private sealed class TimedAudit(IAuditLog inner, ConcurrentQueue<(string, double)> writes) : IAuditLog
    {
        public ValueTask AppendAsync(string? tenant, AuditEntry entry, CancellationToken ct) =>
            TimeAsync(writes, "audit entry", () => inner.AppendAsync(tenant, entry, ct));

        public ValueTask<IReadOnlyList<AuditEntry>> ReadAsync(string? tenant, string runId, CancellationToken ct) => inner.ReadAsync(tenant, runId, ct);
    }

    private sealed class TimedConversations(IConversationStore inner, ConcurrentQueue<(string, double)> writes) : IConversationStore
    {
        public ValueTask AppendAsync(string? tenant, ConversationTurn turn, CancellationToken ct) =>
            TimeAsync(writes, "conversation turn", () => inner.AppendAsync(tenant, turn, ct));

        public ValueTask<IReadOnlyList<ConversationTurn>> ReadAsync(string? tenant, string agent, string? owner, CancellationToken ct) => inner.ReadAsync(tenant, agent, owner, ct);

        public ValueTask<int> CountAsync(string? tenant, string agent, string? owner, CancellationToken ct) => inner.CountAsync(tenant, agent, owner, ct);

        public ValueTask<int> CountOtherRunsAfterAsync(string? tenant, string agent, string? owner, int count, string runId, CancellationToken ct) =>
            inner.CountOtherRunsAfterAsync(tenant, agent, owner, count, runId, ct);

        public ValueTask TruncateAsync(string? tenant, string agent, string? owner, int count, string runId, CancellationToken ct) =>
            inner.TruncateAsync(tenant, agent, owner, count, runId, ct);
    }
}
