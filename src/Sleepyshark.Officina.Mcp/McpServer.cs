using System.Collections.Immutable;

namespace Sleepyshark.Officina.Mcp;

/// <summary>
/// How to reach an MCP server (MCP-01): a program to start, over stdio, or a URL, over Streamable HTTP. Its name prefixes
/// its tools' names. The values of its environment variables and headers are treated as credentials: add
/// <see cref="Secrets"/> to the agent's secrets, so they never reach events, traces or the audit trail (EVT-03).
/// </summary>
public sealed class McpServer
{
    private McpServer(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
        {
            throw new ArgumentException("A server's name may hold only ASCII letters, digits, '_' and '-', as it prefixes tool names.", nameof(name));
        }

        Name = name;
    }

    public string Name { get; }

    /// <summary>The values that may hold credentials: those of the environment variables and headers.</summary>
    public ImmutableArray<string> Secrets => [.. Environment.Values, .. Headers.Values];

    internal string? Command { get; private init; }

    internal ImmutableArray<string> Arguments { get; private init; } = [];

    internal IReadOnlyDictionary<string, string> Environment { get; private init; } = ImmutableDictionary<string, string>.Empty;

    internal Uri? Url { get; private init; }

    internal IReadOnlyDictionary<string, string> Headers { get; private init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>A server the source starts as a child process, speaking over its standard input and output.</summary>
    /// <param name="name">Names the server and prefixes its tools.</param>
    /// <param name="command">The program to start.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="environment">Environment variables to set for it, such as credentials.</param>
    public static McpServer Stdio(string name, string command, IEnumerable<string>? arguments = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return new(name)
        {
            Command = command,
            Arguments = [.. arguments ?? []],
            Environment = environment?.ToImmutableDictionary() ?? ImmutableDictionary<string, string>.Empty,
        };
    }

    /// <summary>A server reached over Streamable HTTP.</summary>
    /// <param name="name">Names the server and prefixes its tools.</param>
    /// <param name="url">Its endpoint.</param>
    /// <param name="headers">Headers sent with every message, such as credentials.</param>
    public static McpServer Http(string name, Uri url, IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        return new(name) { Url = url, Headers = headers?.ToImmutableDictionary() ?? ImmutableDictionary<string, string>.Empty };
    }
}

/// <summary>
/// A tool of an MCP server that the host allows the agent (MCP-03), and how it runs (MCP-02): read or write, and whether
/// each call needs approval. A tool is a write unless the host marks it read: the server's <c>readOnlyHint</c> annotation
/// is a hint, not a guarantee, and a server that wrongly calls a write read-only would let it run alongside other calls,
/// and even when its attempt could not be audited.
/// </summary>
/// <param name="Name">The tool's name on the server.</param>
/// <param name="Kind">Read or write; a write unless the host says otherwise.</param>
/// <param name="NeedsApproval">Whether each call needs the approver's approval (TOOL-04).</param>
public sealed record AllowedTool(string Name, ToolKind Kind = ToolKind.Write, bool NeedsApproval = false);
