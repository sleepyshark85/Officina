namespace Sleepyshark.Officina.Mcp;

/// <summary>The server answered with a JSON-RPC error: it is reachable, but refused or failed the request.</summary>
internal sealed class McpErrorException(string message) : Exception(message);
