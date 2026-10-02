using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Records;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Tasks;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>
/// The storage extension point (STO-01): one store per kind of data, and the rules across them (PRIV-01, PRIV-02).
/// Everything stored carries its tenant, and every read and write is limited to one tenant (SEC-02). The slices that
/// store memory and checkpoints add their stores.
/// </summary>
public interface IStorage
{
    IRunStore Runs { get; }

    IConversationStore Conversations { get; }

    IEventLog Events { get; }

    IAuditLog Audit { get; }

    IRecordStore Records { get; }

    IArtifactStore Artifacts { get; }

    ITaskStore Tasks { get; }

    /// <summary>Everything stored about an owner's runs and conversations, for an export on request (PRIV-02).</summary>
    ValueTask<OwnerData> ExportAsync(string? tenant, string owner, CancellationToken ct);

    /// <summary>
    /// Deletes an owner's runs, with their events, records, artifacts and task boards, and the owner's conversations on request
    /// (PRIV-02). Audit entries stay until their retention period, which always exists, ends: the audit log keeps only
    /// what its retention rules require.
    /// </summary>
    ValueTask DeleteAsync(string? tenant, string owner, CancellationToken ct);

    /// <summary>Deletes, in every tenant, the data older than its kind's retention period (PRIV-01).</summary>
    ValueTask DeleteExpiredAsync(RetentionOptions retention, DateTimeOffset now, CancellationToken ct);
}

/// <summary>An owner's stored data (PRIV-02).</summary>
public sealed record OwnerData(
    IReadOnlyList<RunStarted> Runs,
    IReadOnlyList<CoreEvent> Events,
    IReadOnlyList<AuditEntry> Audit,
    IReadOnlyList<ConversationTurn> Conversations,
    IReadOnlyList<RecordEntry> Records,
    IReadOnlyList<Artifact> Artifacts,
    IReadOnlyList<TaskChange> Tasks);
