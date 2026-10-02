using System.Diagnostics.CodeAnalysis;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Tools;

namespace Sleepyshark.Officina.Core.Checkpoints;

/// <summary>
/// An attempt of a write tool, from the audit log (TOOL-11): an effect that may have reached beyond the core's state.
/// </summary>
/// <param name="Agent">The agent that made the call.</param>
/// <param name="Tool">The tool, by its configured name.</param>
/// <param name="Arguments">The arguments, as JSON.</param>
/// <param name="Irreversible">Whether the tool is irreversible, so its effect cannot be taken back or repeated (TOOL-10).</param>
/// <param name="Finished">Whether the attempt has a recorded outcome; if not, the call may or may not have taken effect (RUN-07).</param>
public sealed record ToolEffect(string Agent, string Tool, string Arguments, bool Irreversible, bool Finished);

/// <summary>What a rollback did (RUN-08).</summary>
/// <param name="To">The checkpoint the run is back at.</param>
/// <param name="NotUndone">The effects outside the core's state made since the checkpoint, which a rollback cannot undo.</param>
public sealed record RollbackReport(Checkpoint To, IReadOnlyList<ToolEffect> NotUndone);

/// <summary>
/// Takes a run's checkpoints and returns the run to one (RUN-03, RUN-04, RUN-08). A checkpoint holds a position in each
/// append-only store and the commit of each working copy; going back truncates the stores and resets the working copies
/// together (DESIGN.md §8). Events and the audit log are history, so they are never truncated (RUN-09).
/// </summary>
[SuppressMessage("Reliability", "CA1001", Justification = "The semaphore never creates a wait handle, so it holds nothing to dispose.")]
internal sealed class Checkpointer(OfficinaOptions options, IStorage storage, IWorkspace? workspace, EventBus events, ToolPipeline pipeline, TimeProvider time)
{
    private readonly SemaphoreSlim one = new(1, 1);

    /// <summary>Takes a checkpoint of the run if its configuration asks for one at this point; null if not.</summary>
    public async Task<Checkpoint?> TakeAsync(ToolContext context, CheckpointPoint point, CancellationToken ct)
    {
        if (!options.Capabilities.Checkpoints.TakenAt(point))
        {
            return null;
        }

        // One at a time, so the numbers are in order and each position is read between the same writes.
        await one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (tenant, runId) = (context.Caller.Tenant, context.RunId);
            var number = (await storage.Checkpoints.ReadAsync(tenant, runId, ct).ConfigureAwait(false)).Count;
            var conversations = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var name in Conversing())
            {
                conversations[name] = await storage.Conversations.CountAsync(tenant, name, context.Caller.Id, ct).ConfigureAwait(false);
            }

            var record = await storage.Records.ReadAsync(tenant, runId, ct).ConfigureAwait(false);
            var board = await storage.Tasks.ReadAsync(tenant, runId, ct).ConfigureAwait(false);
            var memory = pipeline.Memory(context) is { } project ? (await project.ReadAsync(ct).ConfigureAwait(false)).Log is { Count: > 0 } log ? log[^1].Revision : 0 : 0;
            var audit = await storage.Audit.ReadAsync(tenant, runId, ct).ConfigureAwait(false);
            var copies = workspace is null ? [] : await workspace.SnapshotAsync(ct).ConfigureAwait(false);
            var checkpoint = new Checkpoint(
                runId, number, time.GetUtcNow(), point, conversations, record.Count == 0 ? 0 : record[^1].Revision, board.Count == 0 ? 0 : board[^1].Revision,
                memory, audit.Count, copies);
            await storage.Checkpoints.AppendAsync(tenant, checkpoint, ct).ConfigureAwait(false);
            await events.PublishAsync(context, new CheckpointTaken(number, point), ct).ConfigureAwait(false);
            return checkpoint;
        }
        finally
        {
            one.Release();
        }
    }

    /// <summary>
    /// Returns the run's state and working copies to a checkpoint: each store is truncated to its position, and the
    /// checkpoints after it are deleted. The effects it cannot undo are returned (RUN-08).
    /// </summary>
    public async Task<IReadOnlyList<ToolEffect>> RestoreAsync(ToolContext context, Checkpoint to, CancellationToken ct)
    {
        await one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (tenant, runId) = (context.Caller.Tenant, context.RunId);
            var audit = await storage.Audit.ReadAsync(tenant, runId, ct).ConfigureAwait(false);
            var notUndone = Outside(Effects(audit.Skip(to.Audit)));
            foreach (var (name, count) in to.Conversations)
            {
                await storage.Conversations.TruncateAsync(tenant, name, context.Caller.Id, count, ct).ConfigureAwait(false);
            }

            await storage.Records.TruncateAsync(tenant, runId, to.Record, ct).ConfigureAwait(false);
            await storage.Tasks.TruncateAsync(tenant, runId, to.Board, ct).ConfigureAwait(false);
            if (pipeline.Memory(context) is { } memory)
            {
                await storage.Memory.TruncateAsync(tenant, memory.Scope, to.Memory, ct).ConfigureAwait(false);
            }

            await storage.Checkpoints.TruncateAsync(tenant, runId, to.Number, ct).ConfigureAwait(false);
            if (workspace is not null)
            {
                await workspace.RestoreAsync(to.Workspace, ct).ConfigureAwait(false);
            }

            return notUndone;
        }
        finally
        {
            one.Release();
        }
    }

    /// <summary>The attempts of the run's write tools whose outcome is not recorded, outside the core's state (RUN-07).</summary>
    public async Task<IReadOnlyList<ToolEffect>> InterruptedAsync(ToolContext context, CancellationToken ct) =>
        [.. Outside(Effects(await storage.Audit.ReadAsync(context.Caller.Tenant, context.RunId, ct).ConfigureAwait(false))).Where(effect => !effect.Finished)];

    /// <summary>The agents whose conversations are stored, so a checkpoint counts their turns.</summary>
    private IEnumerable<string> Conversing() =>
        options.Agents.Where(agent => agent.Value.Context.History.Strategy != HistoryStrategy.None).Select(agent => agent.Key);

    /// <summary>One effect for each attempt the audit log records an intent for, in order, marked finished when its outcome follows.</summary>
    private List<ToolEffect> Effects(IEnumerable<AuditEntry> entries)
    {
        var effects = new List<ToolEffect>();
        var unfinished = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Outcome == AuditOutcome.Intent)
            {
                unfinished[entry.IdempotencyKey] = effects.Count;
                effects.Add(new ToolEffect(entry.Agent, entry.Tool, entry.Arguments, options.Tools.TryGetValue(entry.Tool, out var tool) && tool.Irreversible, Finished: false));
            }
            else if (entry.Outcome is AuditOutcome.Completed or AuditOutcome.Failed && unfinished.Remove(entry.IdempotencyKey, out var at))
            {
                effects[at] = effects[at] with { Finished = true };
            }
        }

        return effects;
    }

    /// <summary>
    /// What a rollback does not undo: the core's own state is restored, which is the built-in tools' (the record, the task
    /// board, memory) and the working copy's files; anything else a tool did, such as a command or a call to a server, is not.
    /// </summary>
    private List<ToolEffect> Outside(List<ToolEffect> effects) =>
        [.. effects.Where(effect => !options.Tools.TryGetValue(effect.Tool, out var tool)
            || (tool.BuiltinTool() is null && tool.ExtensionId()?.StartsWith("workspace.", StringComparison.Ordinal) != true))];
}
