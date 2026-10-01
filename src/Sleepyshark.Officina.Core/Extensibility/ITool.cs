namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>
/// An application tool. The core calls it only through the tool pipeline, after argument validation, permissions,
/// gates and approval (TOOL-05, TOOL-07). It has no access to configuration, budgets or other agents (INV-10).
/// </summary>
public interface ITool
{
    ToolDescriptor Descriptor { get; }

    ValueTask<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken ct);
}
