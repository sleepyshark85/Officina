using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace Sleepyshark.Officina;

/// <summary>Whether a tool only reads, or changes something (TOOL-03).</summary>
public enum ToolKind
{
    /// <summary>Reads only: the read calls of one reply run concurrently.</summary>
    Read,

    /// <summary>Changes something: write calls run one at a time, in order, each audited before it runs (AUD-02).</summary>
    Write,
}

/// <summary>What a tool's handler returns: content for the model, and whether it is an error result (TOOL-05).</summary>
public sealed record ToolOutput(string Content, bool IsError = false)
{
    public string Content { get; } = Content ?? throw new ArgumentNullException(nameof(Content));
}

/// <summary>What a tool's handler knows of the run that calls it (ARCHITECTURE §4.2).</summary>
/// <param name="MemoryScope">Whose memory the run sees (MEM-03); null for a run without one.</param>
public sealed record ToolContext(string? MemoryScope);

/// <summary>
/// A tool the model may request (TOOL-01, ARCHITECTURE §4.2). Its name, description and input schema are part of the
/// cached prefix; its kind and approval need are not sent to the model.
/// </summary>
public sealed class Tool
{
    /// <param name="name">Unique among the agent's tools.</param>
    /// <param name="description">What the tool does, for the model.</param>
    /// <param name="inputSchema">
    /// The JSON schema of the tool's input, an object schema in the subset the core validates; it is sent as written.
    /// </param>
    /// <param name="kind">Whether the tool reads or writes.</param>
    /// <param name="handler">
    /// Runs the tool on input that matches the schema. A thrown exception becomes an error result with its message.
    /// </param>
    /// <param name="needsApproval">Whether the run's approver must approve each call before it runs (TOOL-04).</param>
    public Tool(
        string name, string description, string inputSchema, ToolKind kind,
        Func<JsonElement, CancellationToken, Task<ToolOutput>> handler, bool needsApproval = false)
        : this(name, description, inputSchema, kind, Ignoring(handler), needsApproval)
    {
    }

    /// <summary>A tool whose handler also gets the run's <see cref="ToolContext"/>; see the other constructor.</summary>
    public Tool(
        string name, string description, string inputSchema, ToolKind kind,
        Func<JsonElement, ToolContext, CancellationToken, Task<ToolOutput>> handler, bool needsApproval = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(inputSchema);
        ArgumentNullException.ThrowIfNull(handler);
        using (var schema = JsonDocument.Parse(inputSchema))
        {
            if (schema.RootElement.ValueKind != JsonValueKind.Object || !schema.RootElement.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "object")
            {
                throw new ArgumentException("The input schema must be a JSON schema of type object.", nameof(inputSchema));
            }

            SchemaValidator.CheckSubset(schema.RootElement);
            Schema = schema.RootElement.Clone();
        }

        Name = name;
        Description = description;
        InputSchema = inputSchema;
        Kind = kind;
        Handler = handler;
        NeedsApproval = needsApproval;
    }

    public string Name { get; }

    public string Description { get; }

    public string InputSchema { get; }

    public ToolKind Kind { get; }

    public bool NeedsApproval { get; }

    /// <summary>The source the tool comes from, such as an MCP server; null for the application's own tools.</summary>
    public IToolSource? Source { get; init; }

    /// <summary>
    /// Whether this is the memory tool (MEM-01), which a provider with a native memory tool presents as that tool, and
    /// whose runs need a memory scope.
    /// </summary>
    public bool IsMemory { get; internal init; }

    internal JsonElement Schema { get; }

    internal Func<JsonElement, ToolContext, CancellationToken, Task<ToolOutput>> Handler { get; }

    /// <summary>Calls whose input this accepts never need approval, such as the memory tool's views; null exempts none.</summary>
    internal Func<JsonElement, bool>? ExemptFromApproval { get; init; }

    /// <summary>Whether a call with <paramref name="input"/> needs the approver's approval (TOOL-04).</summary>
    internal bool NeedsApprovalFor(JsonElement input) => NeedsApproval && ExemptFromApproval?.Invoke(input) != true;

    /// <summary>
    /// Builds a tool from an ordinary typed function (TOOL-01). Each parameter is a property of the input, with its schema
    /// exported from its type and a <see cref="DescriptionAttribute"/> as its description; a parameter with a default
    /// value is optional, and a <see cref="CancellationToken"/> gets the run's. The function may be async; a
    /// <see cref="ToolOutput"/> or string it returns is the result as is, anything else is sent as JSON.
    /// </summary>
    /// <remarks>
    /// Known limit: it uses reflection, for the schema and for JSON, so it is neither trim nor AOT safe, and it fails
    /// where reflection-based JSON is disabled. The constructor works everywhere.
    /// </remarks>
    public static Tool FromFunction(string name, string description, ToolKind kind, Delegate function, bool needsApproval = false)
    {
        ArgumentNullException.ThrowIfNull(function);
        var parameters = function.Method.GetParameters();
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var parameter in parameters.Where(parameter => parameter.ParameterType != typeof(CancellationToken)))
        {
            properties[parameter.Name!] = TypedJson.Options.GetJsonSchemaAsNode(parameter.ParameterType, TypedJson.Exporter);
            TypedJson.Describe(properties[parameter.Name!], parameter.GetCustomAttribute<DescriptionAttribute>());
            if (!parameter.HasDefaultValue)
            {
                required.Add(parameter.Name);
            }
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
        return new Tool(name, description, schema.ToJsonString(), kind, InvokeAsync, needsApproval);

        async Task<ToolOutput> InvokeAsync(JsonElement input, CancellationToken cancellationToken)
        {
            var arguments = parameters.Select(parameter =>
                parameter.ParameterType == typeof(CancellationToken) ? cancellationToken
                : input.TryGetProperty(parameter.Name!, out var value) ? value.Deserialize(parameter.ParameterType, TypedJson.Options)
                : parameter.HasDefaultValue ? parameter.DefaultValue
                : null).ToArray();
            var returned = function.Method.Invoke(function.Target, BindingFlags.DoNotWrapExceptions, null, arguments, null);
            if (returned is not null && returned.GetType() is { IsGenericType: true } type && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
            {
                returned = type.GetMethod(nameof(ValueTask<int>.AsTask))!.Invoke(returned, null);
            }
            else if (returned is ValueTask valueTask)
            {
                returned = valueTask.AsTask();
            }

            if (returned is Task task)
            {
                await task.ConfigureAwait(false);
                returned = function.Method.ReturnType.IsGenericType ? task.GetType().GetProperty(nameof(Task<int>.Result))!.GetValue(task) : null;
            }

            return returned switch
            {
                ToolOutput output => output,
                string text => new ToolOutput(text),
                null => new ToolOutput(""),
                _ => new ToolOutput(JsonSerializer.Serialize(returned, returned.GetType(), TypedJson.Options)),
            };
        }
    }

    private static Func<JsonElement, ToolContext, CancellationToken, Task<ToolOutput>> Ignoring(Func<JsonElement, CancellationToken, Task<ToolOutput>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return (input, _, cancellationToken) => handler(input, cancellationToken);
    }
}

/// <summary>Answers approval requests (TOOL-04, ARCHITECTURE §4.3): a person or a policy. A run without one is unattended (GEN-04).</summary>
public interface IApprover
{
    /// <summary>Decides whether <paramref name="toolCall"/> of <paramref name="tool"/>, which needs approval, may run.</summary>
    Task<Approval> ApproveAsync(Tool tool, ToolCall toolCall, CancellationToken cancellationToken);
}

/// <summary>An approver's answer; a denial's reason is told to the model.</summary>
public sealed record Approval(bool Approved, string? Reason = null)
{
    public static Approval Granted { get; } = new(true);

    public static Approval Denied(string reason) => new(false, reason);
}
