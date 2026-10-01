using System.Text.Json;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>A tool call waiting for a human's approval.</summary>
/// <param name="Agent">The agent that makes the call.</param>
/// <param name="Tool">The tool, by its configured name.</param>
/// <param name="Arguments">The arguments to approve.</param>
/// <param name="Reason">Why approval is needed: the rule or gate that asked.</param>
public sealed record HumanRequest(string Agent, string Tool, JsonElement Arguments, string Reason);
