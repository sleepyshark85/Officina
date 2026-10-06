namespace Sleepyshark.Officina;

/// <summary>
/// Where tools that hold a connection come from, such as an MCP server (ARCHITECTURE §7). Its tools name it as their
/// <see cref="Tool.Source"/>. Each run connects every source its agent's tools come from before its first model call,
/// and fails if one cannot connect (MCP-04); a source that fails later gives error results for its calls. What happens
/// to a source's connection is written to the audit trail (AUD-01).
/// </summary>
public interface IToolSource
{
    /// <summary>Names the source in the audit trail and telemetry.</summary>
    string Name { get; }

    /// <summary>
    /// Makes sure the source is connected, connecting again if it lost its connection. Throws when it cannot, with a
    /// message that says why and holds no secret. Several runs may call it at once.
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// What happened to the connection since the last call, oldest first; each change is taken once. Runs take changes
    /// when they connect and after each batch of tool calls, so a source that several runs share has each change
    /// recorded in the audit trail of the run that takes it first, which may not be the run whose call met it.
    /// </summary>
    IReadOnlyList<ToolSourceChange> TakeChanges();
}

/// <summary>What happened to a tool source's connection; <paramref name="Detail"/> says why it failed or was lost.</summary>
public sealed record ToolSourceChange(ToolSourceState State, string? Detail = null);

/// <summary>A tool source's connection state, as the audit trail records it.</summary>
public enum ToolSourceState
{
    Connected,

    /// <summary>An attempt to connect failed.</summary>
    Failed,

    /// <summary>A connection was lost.</summary>
    Disconnected,
}

/// <summary>Connects an agent's tool sources and records what happens to their connections.</summary>
internal static class ToolSources
{
    /// <summary>Connects each tool source of the agent's tools; returns why the run cannot start, or null when all are connected.</summary>
    public static async Task<string?> ConnectAsync(AgentDefinition agent, AuditRecorder audit, CancellationToken cancellationToken)
    {
        string? error = null;
        foreach (var source in Of(agent))
        {
            try
            {
                await source.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The run then stops as cancelled, before its first model call.
                break;
            }
#pragma warning disable CA1031 // A source that cannot connect fails the run, with its reason (MCP-04).
            catch (Exception exception)
#pragma warning restore CA1031
            {
                error = agent.Redact($"The tool source '{source.Name}' is not available: {exception.Message}");
                break;
            }
        }

        await RecordChangesAsync(agent, audit).ConfigureAwait(false);
        return error;
    }

    /// <summary>Writes the connection changes of the agent's tool sources to the audit trail.</summary>
    public static async Task RecordChangesAsync(AgentDefinition agent, AuditRecorder audit)
    {
        foreach (var source in Of(agent))
        {
            foreach (var change in source.TakeChanges())
            {
                var outcome = change.State switch
                {
                    ToolSourceState.Connected => "connected",
                    ToolSourceState.Failed => "failed",
                    _ => "disconnected",
                };
                await audit.RecordAsync(AuditKind.ToolSource, tool: source.Name, outcome: outcome, detail: change.Detail).ConfigureAwait(false);
            }
        }
    }

    private static IEnumerable<IToolSource> Of(AgentDefinition agent) => agent.Tools.Select(tool => tool.Source).OfType<IToolSource>().Distinct();
}
