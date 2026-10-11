namespace Sleepyshark.Officina;

/// <summary>Connects an agent's tool sources and records their connection changes.</summary>
internal static class ToolSources
{
    /// <summary>Connects each of the agent's tool sources; returns why the run cannot start, or null when all connected.</summary>
    public static async Task<string?> ConnectAsync(Agent agent, AuditRecorder audit, CancellationToken cancellationToken)
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
#pragma warning disable CA1031 // A source that cannot connect fails the run, with its reason.
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
    public static async Task RecordChangesAsync(Agent agent, AuditRecorder audit)
    {
        foreach (var source in Of(agent))
        {
            IReadOnlyList<ToolSourceChange> changes;
            try
            {
                changes = source.TakeChanges();
            }
#pragma warning disable CA1031 // A source that cannot report its changes must not end the run; the failure is recorded.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                changes = [new ToolSourceChange(ToolSourceState.Failed, agent.Redact($"The tool source '{source.Name}' could not report its connection changes: {exception.Message}"))];
            }

            foreach (var change in changes)
            {
                var outcome = change.State switch
                {
                    ToolSourceState.Connected => "connected",
                    ToolSourceState.Failed => "failed",
                    _ => "disconnected",
                };
                await audit.RecordAsync(AuditKind.ToolSource, entry => entry with { Tool = source.Name, Outcome = outcome, Detail = change.Detail }).ConfigureAwait(false);
            }
        }
    }

    private static IEnumerable<IToolSource> Of(Agent agent) => agent.Tools.Select(tool => tool.Source).OfType<IToolSource>().Distinct();
}
