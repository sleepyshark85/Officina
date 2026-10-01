namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>An external tool server, reached over MCP (TOOL-01). Tools use its tools with <c>mcp:&lt;server&gt;/&lt;tool&gt;</c> sources.</summary>
public sealed record ToolServerOptions
{
    [Setting("How the server is reached: `stdio` starts `command` and talks over its standard input and output; `http` posts to `url` (Streamable HTTP).",
        Example = "\"http\"")]
    public ToolServerTransport Transport { get; init; } = ToolServerTransport.Stdio;

    [Setting("For `stdio`: the program that runs the server.", Example = "\"github-mcp-server\"")]
    public string? Command { get; init; }

    [Setting("For `stdio`: the program's arguments.", Example = """["stdio"]""")]
    public IReadOnlyList<string> Args { get; init; } = [];

    [Setting("For `stdio`: environment variables of the program, each a secret read when the server starts.",
        Example = """{ "GITHUB_TOKEN": { "secret": "GITHUB_TOKEN" } }""")]
    public IReadOnlyDictionary<string, SecretReference> Env { get; init; } = new Dictionary<string, SecretReference>();

    [Setting("For `http`: the server's endpoint.", Example = "\"https://tracker.example.com/mcp\"")]
    public string? Url { get; init; }

    [Setting("For `http`: headers sent with every request, each a secret read when the server is first reached.",
        Example = """{ "Authorization": { "secret": "TRACKER_AUTH" } }""")]
    public IReadOnlyDictionary<string, SecretReference> Headers { get; init; } = new Dictionary<string, SecretReference>();
}

public enum ToolServerTransport
{
    Stdio,
    Http,
}
