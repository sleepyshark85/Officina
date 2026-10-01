using System.Text.Json;
using Sleepyshark.Officina.Core.Records;
using Sleepyshark.Officina.Core.Tasks;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>What a gate sees of a call, with the run record and the task board so it can require prerequisites (TOOL-06).</summary>
/// <param name="Agent">The agent that makes the call.</param>
/// <param name="Tool">The tool, by its configured name.</param>
/// <param name="Arguments">The validated arguments.</param>
/// <param name="Caller">Who the call acts for.</param>
/// <param name="ReadUntrusted">Whether the agent has read untrusted content, such as a fetched web page (SEC-04).</param>
/// <param name="Record">The run record, to read.</param>
/// <param name="Board">The task board, to read, with the task the agent works on; null when the task board is off.</param>
public sealed record GateContext(string Agent, string Tool, JsonElement Arguments, Caller Caller, bool ReadUntrusted, RunRecord Record, TaskBoard? Board);
