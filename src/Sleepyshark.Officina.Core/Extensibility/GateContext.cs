using System.Text.Json;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>What a gate sees of a call. The run record (S06) and the task board (S18) are added by their slices (TOOL-06).</summary>
/// <param name="Agent">The agent that makes the call.</param>
/// <param name="Tool">The tool, by its configured name.</param>
/// <param name="Arguments">The validated arguments.</param>
/// <param name="Caller">Who the call acts for.</param>
/// <param name="ReadUntrusted">Whether the agent has read untrusted content, such as a fetched web page (SEC-04).</param>
public sealed record GateContext(string Agent, string Tool, JsonElement Arguments, Caller Caller, bool ReadUntrusted);
