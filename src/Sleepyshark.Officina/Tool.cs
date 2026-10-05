using System.Text.Json;

namespace Sleepyshark.Officina;

/// <summary>A tool the model may request: its name, description and JSON input schema, which are part of the cached prefix.</summary>
public sealed record Tool
{
    /// <param name="name">Unique among the agent's tools.</param>
    /// <param name="description">What the tool does, for the model.</param>
    /// <param name="inputSchema">The JSON schema of the tool's input, a JSON object. It is sent as written.</param>
    public Tool(string name, string description, string inputSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(inputSchema);
        using (var schema = JsonDocument.Parse(inputSchema))
        {
            if (schema.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("The input schema must be a JSON object.", nameof(inputSchema));
            }
        }

        Name = name;
        Description = description;
        InputSchema = inputSchema;
    }

    public string Name { get; }

    public string Description { get; }

    public string InputSchema { get; }
}
