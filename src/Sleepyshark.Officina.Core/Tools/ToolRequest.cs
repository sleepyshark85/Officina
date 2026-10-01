using System.Text.Json;

namespace Sleepyshark.Officina.Core.Tools;

/// <summary>A tool call the model asked for, before any checks.</summary>
/// <param name="Name">The tool, by the name the model was offered.</param>
/// <param name="Arguments">The arguments as the model wrote them.</param>
public sealed record ToolRequest(string Name, JsonElement Arguments);
