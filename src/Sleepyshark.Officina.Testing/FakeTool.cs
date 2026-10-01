using System.Collections.Concurrent;
using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Testing;

/// <summary>A tool whose behaviour a test supplies. It remembers every call it receives.</summary>
/// <param name="kind">Whether the tool writes.</param>
/// <param name="inputSchema">The arguments' JSON Schema; by default any object.</param>
/// <param name="parallelSafe">Whether it declares itself safe to run in parallel.</param>
/// <param name="run">What a call does; by default it returns <c>ok</c>.</param>
public sealed class FakeTool(
    ToolKind kind,
    string inputSchema = """{ "type": "object" }""",
    bool parallelSafe = false,
    Func<ToolCall, CancellationToken, ValueTask<ToolResult>>? run = null) : ITool
{
    private readonly ConcurrentQueue<ToolCall> calls = new();

    public ToolDescriptor Descriptor { get; } = new("A tool for tests.", JsonDocument.Parse(inputSchema).RootElement.Clone(), kind, parallelSafe);

    /// <summary>The calls received so far, in order.</summary>
    public IReadOnlyList<ToolCall> Calls => [.. calls];

    public ValueTask<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken ct)
    {
        calls.Enqueue(toolCall);
        return run?.Invoke(toolCall, ct) ?? ValueTask.FromResult(ToolResult.Success("ok"));
    }
}
