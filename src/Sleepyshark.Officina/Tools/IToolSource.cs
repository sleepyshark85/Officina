namespace Sleepyshark.Officina;

/// <summary>
/// A source of tools that holds a connection, such as an MCP server; its tools name it as their <see cref="Tool.Source"/>.
/// Each run connects its sources before the first model call and fails if one cannot; a source that fails later gives
/// error results for its calls. Connection changes go to the audit trail.
/// </summary>
public interface IToolSource
{
    /// <summary>Names the source in the audit trail and telemetry.</summary>
    string Name { get; }

    /// <summary>
    /// Makes sure the source is connected, reconnecting if it was lost. Throws when it cannot, with a message that says why
    /// and holds no secret. Several runs may call it at once.
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Connection changes since the last call, oldest first; each is taken once. Runs take them when they connect and after
    /// each batch of tool calls, so with a shared source a change is recorded by whichever run takes it first.
    /// </summary>
    IReadOnlyList<ToolSourceChange> TakeChanges();
}
