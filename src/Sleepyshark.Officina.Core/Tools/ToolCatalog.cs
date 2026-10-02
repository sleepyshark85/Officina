using Json.Schema;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Records;
using Sleepyshark.Officina.Core.Tasks;

namespace Sleepyshark.Officina.Core.Tools;

/// <summary>A configured tool joined with what its implementation declares: the settings that apply to its calls.</summary>
/// <param name="Name">The configured name.</param>
/// <param name="Options">Its configuration.</param>
/// <param name="Implementation">The application's tool; null for a provider tool.</param>
/// <param name="Schema">The arguments' schema, built once; null for a provider tool.</param>
internal sealed record CatalogTool(string Name, ToolOptions Options, ITool? Implementation, JsonSchema? Schema)
{
    /// <summary>
    /// A tool is a write tool if it declares itself one, is configured as one, or is irreversible. A tool server's tools
    /// are writes unless configured as reads: what a server says about its tools is only a hint.
    /// </summary>
    public ToolKind Kind { get; } =
        Implementation?.Descriptor.Kind == ToolKind.Write || (Options.Kind ?? (Options.McpTool() is null ? ToolKind.Read : ToolKind.Write)) == ToolKind.Write
        || Options.Irreversible ? ToolKind.Write : ToolKind.Read;

    /// <summary>
    /// Configuration can make a tool unsafe to run in parallel, never safe. An irreversible tool never is, so a second
    /// identical call in the same reply finds the first one's intent (TOOL-10).
    /// </summary>
    public bool ParallelSafe { get; } = Implementation?.Descriptor.ParallelSafe == true && Options.ParallelSafe != false && !Options.Irreversible;

    public Approval Approval { get; } = Options.Approval ?? (Options.Irreversible ? Approval.Always : Approval.Never);
}

/// <summary>
/// The configured tools joined with the application's implementations, and the tools each agent is offered. Building it
/// checks what only the implementations reveal (TOOL-02): every source is registered, every input schema is valid,
/// every write tool has a gate or an exemption (INV-04), and every condition fits the arguments it reads (CFG-13).
/// A tool server's tools join exactly like the application's, so the same steps govern them (TEST-17).
/// </summary>
internal sealed class ToolCatalog
{
    /// <summary>The built-in tools, by the name <c>builtin:</c> sources use.</summary>
    public static readonly IReadOnlyList<string> Builtins = [.. RecordTool.All.Keys, ArtifactTool.Name, AskOwnerTool.Name, .. TaskTool.Names];

    private readonly Dictionary<string, Dictionary<string, CatalogTool>> byAgent;

    private ToolCatalog(Dictionary<string, Dictionary<string, CatalogTool>> byAgent) => this.byAgent = byAgent;

    /// <exception cref="ConfigurationException">The configuration has errors.</exception>
    public static ToolCatalog Create(
        OfficinaOptions options, IReadOnlyDictionary<string, ITool> tools, IReadOnlyDictionary<string, IGate> gates, IReadOnlyDictionary<string, IKnowledgeSource> knowledge,
        IReadOnlyDictionary<string, ICheck> checks, IArtifactStore artifacts)
    {
        var errors = options.Validate().ToList();
        var catalog = new Dictionary<string, CatalogTool>();
        if (errors.Count == 0)
        {
            foreach (var (name, tool) in options.Tools)
            {
                catalog[name] = Join(name, tool, Implementation(name, tool, options, tools, knowledge, checks, artifacts, errors), errors);
            }

            errors.AddRange(options.Knowledge
                .Where(source => !knowledge.ContainsKey(source.Value.ExtensionId()!))
                .Select(source => Unregistered($"knowledge.{source.Key}.use", "knowledge source", source.Value.ExtensionId()!)));

            errors.AddRange(options.Gates
                .Where(gate => gate.Value.ExtensionId() is { } id && !gates.ContainsKey(id))
                .Select(gate => Unregistered($"gates.{gate.Key}.use", "gate", gate.Value.ExtensionId()!)));
            errors.AddRange(options.Checks
                .Where(check => !checks.ContainsKey(check.Value.ExtensionId()!))
                .Select(check => Unregistered($"checks.{check.Key}.use", "check", check.Value.ExtensionId()!)));
            errors.AddRange(catalog.Values.Where(tool => tool.Schema is not null).SelectMany(tool => Conditions(tool, options)));
        }

        if (errors.Count > 0)
        {
            throw new ConfigurationException([.. errors.OrderBy(error => error.Phase)]);
        }

        return new(options.Agents.ToDictionary(agent => agent.Key, agent => AgentTools(agent.Value, options, catalog)));
    }

    public bool TryGet(string agent, string name, out CatalogTool tool) => Tools(agent).TryGetValue(name, out tool!);

    /// <summary>
    /// The tools an agent is offered, sorted by name. They depend only on its tool sets, never on the caller (TOOL-03).
    /// When its tool descriptions are loaded on demand, its tools are offered by name only (TOOL-12).
    /// </summary>
    public IReadOnlyList<ToolDefinition> Offered(string agent)
    {
        var tools = Tools(agent);
        var onDemand = tools.TryGetValue(OfficinaOptions.DescribeTool, out var describe) && describe.Implementation is DescribeTool;
        return [.. tools.Values.OrderBy(tool => tool.Name, StringComparer.Ordinal).Select(tool =>
            onDemand && tool.Implementation is not (null or DescribeTool)
                ? new ToolDefinition(tool.Name, DescribeTool.Stub, DescribeTool.AnyArguments, null)
                : new ToolDefinition(tool.Name, tool.Implementation?.Descriptor.Description, tool.Implementation?.Descriptor.InputSchema, tool.Options.ProviderTool()))];
    }

    private Dictionary<string, CatalogTool> Tools(string agent) =>
        byAgent.TryGetValue(agent, out var tools) ? tools : throw ConfigurationException.UnknownAgent(agent, byAgent.Keys);

    /// <summary>The tools of an agent's tool sets, with <c>describe_tool</c> when their descriptions are loaded on demand.</summary>
    private static Dictionary<string, CatalogTool> AgentTools(AgentDefinition agent, OfficinaOptions options, Dictionary<string, CatalogTool> catalog)
    {
        var tools = agent.Tools.SelectMany(set => options.ToolSets[set]).Distinct().ToDictionary(name => name, name => catalog[name]);
        if (agent.ToolDescriptionsOnDemand)
        {
            var describe = new DescribeTool(tools);
            tools[OfficinaOptions.DescribeTool] = new(
                OfficinaOptions.DescribeTool, new() { Source = $"builtin:{OfficinaOptions.DescribeTool}" }, describe, JsonSchema.Build(describe.Descriptor.InputSchema));
        }

        return tools;
    }

    /// <summary>What runs a tool's calls; null for a provider tool, or one that is missing.</summary>
    private static ITool? Implementation(
        string name, ToolOptions tool, OfficinaOptions options, IReadOnlyDictionary<string, ITool> tools, IReadOnlyDictionary<string, IKnowledgeSource> knowledge,
        IReadOnlyDictionary<string, ICheck> checks, IArtifactStore artifacts, List<ConfigurationError> errors)
    {
        if (tool.BuiltinTool() is { } builtin)
        {
            return builtin switch
            {
                ArtifactTool.Name => new ArtifactTool(artifacts),
                AskOwnerTool.Name => AskOwnerTool.Instance,
                _ => RecordTool.All.GetValueOrDefault(builtin) ?? TaskTool.Create(builtin, checks),
            };
        }

        if (tool.KnowledgeSource() is { } source)
        {
            // An unregistered source is reported once, for its entry in knowledge.
            return knowledge.TryGetValue(options.Knowledge[source].ExtensionId()!, out var found) ? new KnowledgeTool(source, found, options.Knowledge[source].Mask) : null;
        }

        if ((tool.ExtensionId() ?? tool.McpTool()) is not { } id)
        {
            return null;
        }

        if (tools.TryGetValue(id, out var implementation))
        {
            return implementation;
        }

        errors.Add(tool.McpTool() is null
            ? Unregistered($"tools.{name}.source", "tool", id)
            : new(ValidationPhase.References, $"tools.{name}.source", $"tool server \"{id.Split('/')[0]}\" has no tool \"{id[(id.IndexOf('/') + 1)..]}\".",
                "Use a name from the server's tool list."));
        return null;
    }

    private static CatalogTool Join(string name, ToolOptions options, ITool? implementation, List<ConfigurationError> errors)
    {
        if (implementation is null)
        {
            return new(name, options, null, null);
        }

        JsonSchema? schema = null;
        try
        {
            schema = JsonSchema.Build(implementation.Descriptor.InputSchema);
        }
        catch (Exception exception) when (exception is JsonSchemaException or System.Text.Json.JsonException or InvalidOperationException)
        {
            errors.Add(new(ValidationPhase.Tools, $"tools.{name}", $"the input schema of {options.ExtensionId() ?? options.Source} is not valid JSON Schema: {exception.Message}",
                "Fix the tool's declared input schema."));
        }

        var tool = new CatalogTool(name, options, implementation, schema);
        if (tool.Kind == ToolKind.Write && options.Gates.Count == 0 && string.IsNullOrWhiteSpace(options.GateExemption))
        {
            errors.Add(new(ValidationPhase.Tools, $"tools.{name}", "write tool has no gate of its own.",
                "Add \"gates\": [...] or \"gateExemption\": \"<reason>\"."));
        }

        return tool;
    }

    /// <summary>The problems of the conditions that read a tool's arguments: its permission rules and its gates.</summary>
    private static IEnumerable<ConfigurationError> Conditions(CatalogTool tool, OfficinaOptions options)
    {
        var schema = tool.Implementation!.Descriptor.InputSchema;
        var rules = options.Policies.PermissionRules.Index()
            .Where(rule => rule.Item.Tool == tool.Name && rule.Item.When is not null)
            .Select(rule => ($"policies.permissionRules[{rule.Index}].when", rule.Item.When!));
        var gates = options.Policies.Gates.Concat(tool.Options.Gates)
            .Where(gate => options.Gates[gate].When is not null)
            .Select(gate => ($"gates.{gate}.when", options.Gates[gate].When!));
        return rules.Concat(gates).SelectMany(condition => condition.Item2.Check(schema)
            .Select(problem => new ConfigurationError(ValidationPhase.Conditions, condition.Item1, $"for tool {tool.Name}: {problem}", "")));
    }

    internal static ConfigurationError Unregistered(string path, string kind, string id) =>
        new(ValidationPhase.References, path, $"{kind} extension \"{id}\" is not registered.", $"Register the application's {kind} under the id \"{id}\".");
}
