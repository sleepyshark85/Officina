namespace Sleepyshark.Officina.Mcp;

/// <summary>
/// A tool of an MCP server the host allows the agent, and how it runs. It is a write unless the host marks it read: the
/// server's <c>readOnlyHint</c> is not trusted, as a write wrongly marked read-only would run alongside other calls, and
/// even when its attempt could not be audited.
/// </summary>
/// <param name="Name">The tool's name on the server.</param>
/// <param name="Kind">Read or write; a write unless the host says otherwise.</param>
/// <param name="NeedsApproval">Whether each call needs the approver's approval.</param>
public sealed record AllowedTool(string Name, ToolKind Kind = ToolKind.Write, bool NeedsApproval = false);
