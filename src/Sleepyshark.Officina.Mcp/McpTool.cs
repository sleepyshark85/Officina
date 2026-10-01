using System.Text.Json;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Mcp;

/// <summary>A tool server's tool as a core tool. The tool pipeline governs its calls like any other tool's (TEST-17).</summary>
/// <param name="server">The connection to its server.</param>
/// <param name="name">Its name on the server.</param>
/// <param name="descriptor">Its description and input schema, from the server's list.</param>
internal sealed class McpTool(McpConnection server, string name, ToolDescriptor descriptor) : ITool
{
    public ToolDescriptor Descriptor => descriptor;

    public async ValueTask<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken ct)
    {
        JsonElement result;
        try
        {
            result = await server.CallToolAsync(name, toolCall.Arguments, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            return ToolResult.Failed(ToolErrorCategory.Unavailable);
        }

        var text = string.Join("\n", result.GetProperty("content").EnumerateArray().Select(content =>
            content.GetProperty("type").GetString() is "text" ? content.GetProperty("text").GetString() : $"[{content.GetProperty("type").GetString()} content]"));

        // The server writes an error result for the model to read, so its text goes to the model.
        return result.TryGetProperty("isError", out var isError) && isError.GetBoolean() ? ToolResult.Failed(ToolErrorCategory.Failed, text) : ToolResult.Success(text);
    }
}
