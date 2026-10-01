using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Tools;

/// <summary>Who makes a batch of tool calls: the run, the agent and the caller it acts for. The host sets it, never the model (INV-03).</summary>
public sealed record ToolContext(string RunId, string Agent, Caller Caller);
