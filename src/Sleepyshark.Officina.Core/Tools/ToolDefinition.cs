using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Core.Tools;

/// <summary>A tool as it is offered to a model (TOOL-03).</summary>
/// <param name="Name">The configured name.</param>
/// <param name="Description">What the tool does; null for a provider tool, which the provider describes.</param>
/// <param name="InputSchema">The arguments' JSON Schema; null for a provider tool.</param>
/// <param name="ProviderTool">The provider's name for its own tool, which the provider runs (TOOL-13); null for the application's tools.</param>
/// <param name="Limits">The provider's limits on its own tool; null when none are configured.</param>
public sealed record ToolDefinition(string Name, string? Description, JsonElement? InputSchema, string? ProviderTool, ProviderToolLimits? Limits = null);
