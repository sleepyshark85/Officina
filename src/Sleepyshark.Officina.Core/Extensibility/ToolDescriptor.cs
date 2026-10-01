using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>What a tool declares about itself. Configuration can narrow it, never loosen it (TOOL-04).</summary>
/// <param name="Description">What the tool does, for the model.</param>
/// <param name="InputSchema">The JSON Schema its arguments must match (MSG-07).</param>
/// <param name="Kind">Whether it changes anything.</param>
/// <param name="ParallelSafe">Whether calls may run at the same time as other calls (LOOP-08).</param>
public sealed record ToolDescriptor(string Description, JsonElement InputSchema, ToolKind Kind, bool ParallelSafe = false);
