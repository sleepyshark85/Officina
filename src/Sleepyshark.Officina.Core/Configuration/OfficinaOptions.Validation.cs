using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Records;
using Sleepyshark.Officina.Core.Tools;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// Validation of the whole configuration (CFG-06). The root sees every section, so it is the one entry point for files
/// and the programmatic form alike (CFG-02): each section's <c>[Required]</c> and <c>[Range]</c> annotations, then the
/// rules across sections.
/// </summary>
public sealed partial record OfficinaOptions : IValidatableObject
{
    /// <summary>The tool that describes the others to an agent whose tool descriptions are loaded on demand (TOOL-12).</summary>
    public const string DescribeTool = "describe_tool";

    /// <summary>Every error, ordered by phase.</summary>
    public IReadOnlyList<ConfigurationError> Validate() => [.. Errors().OrderBy(error => error.Phase)];

    IEnumerable<ValidationResult> IValidatableObject.Validate(ValidationContext validationContext) =>
        Validate().Select(error => new ValidationResult(error.ToString(), [error.Path]));

    private IEnumerable<ConfigurationError> Errors()
    {
        // INV-07: the budget is an invariant, so a value outside its range is an attempt to weaken it.
        var errors = FormatVersionSupported()
            .Concat(Annotations(Run, "run", ValidationPhase.Invariants))
            .Concat(Run.Budget is null ? [] : Annotations(Run.Budget, "run.budget", ValidationPhase.Invariants))
            .Concat(Names(Project.Values.Keys, "project.values"))
            .Concat(Annotations(Operations, "operations"))
            .Concat(Operations.Telemetry is null ? [] : Annotations(Operations.Telemetry, "operations.telemetry"))
            .Concat(Operations.Storage is null ? [] : Annotations(Operations.Storage, "operations.storage"));

        foreach (var (name, provider) in Providers)
        {
            errors = errors.Concat(Names([name], "providers")).Concat(Annotations(provider, $"providers.{name}"))
                .Concat(Annotations(provider.Retry, $"providers.{name}.retry"))
                .Concat(Annotations(provider.Features, $"providers.{name}.features"))
                .Concat(provider.ApiKey is null ? [] : Annotations(provider.ApiKey, $"providers.{name}.apiKey"))
                .Concat(provider.Prices.SelectMany(price => Annotations(price.Value, $"providers.{name}.prices.{price.Key}")));
        }

        foreach (var (name, profile) in Models)
        {
            errors = errors.Concat(Names([name], "models")).Concat(Annotations(profile, $"models.{name}")).Concat(ProviderExists(name, profile))
                .Concat(Priced(name, profile)).Concat(Fallbacks(name, profile));
        }

        foreach (var (name, agent) in Agents)
        {
            errors = errors.Concat(Names([name], "agents")).Concat(Annotations(agent, $"agents.{name}")).Concat(ModelExists(name, agent))
                .Concat(References($"agents.{name}.tools", "tool set", agent.Tools, "toolSets", ToolSets.Keys)).Concat(TurnSettings(name, agent))
                .Concat(References($"agents.{name}.context.retrieval.beforeTurn", "knowledge source", agent.Context?.Retrieval?.BeforeTurn ?? [], "knowledge", Knowledge.Keys))
                .Concat(agent.Pattern is null ? [] : PatternSettings($"agents.{name}.pattern", name, agent.Pattern));
            errors = errors.Concat(HelperSettings(name, agent));
            if (agent.Pattern?.Type == PatternOptions.SingleCall && agent.Tools.Count > 0)
            {
                errors = errors.Append(new(ValidationPhase.Shape, $"agents.{name}.tools", "must be empty: a singleCall agent is offered no tools.",
                    "Remove the tool sets, or use the toolLoop pattern."));
            }

            if (agent.ToolDescriptionsOnDemand && agent.Tools.Where(ToolSets.ContainsKey).Any(set => ToolSets[set].Contains(DescribeTool)))
            {
                errors = errors.Append(new(ValidationPhase.Tools, $"agents.{name}.toolDescriptionsOnDemand", $"needs the tool name {DescribeTool}, which the agent is offered already.",
                    $"Rename the tool {DescribeTool}."));
            }

            // ING-06: masking tokens are numbered per run, so a token in kept history could be restored to a later run's value.
            if (Policies.Masking?.Enabled == true && agent.Context?.History is { Strategy: not HistoryStrategy.None }
                && agent.Tools.Where(ToolSets.ContainsKey).SelectMany(set => ToolSets[set]).Any(tool => Tools.GetValueOrDefault(tool)?.ReceivesMaskedValues == true))
            {
                errors = errors.Append(new(ValidationPhase.Tools, $"agents.{name}.context.history.strategy",
                    "cannot keep history while masking is on and the agent has a tool that receives masked values.",
                    "Set the strategy to none, turn policies.masking.enabled off, or remove receivesMaskedValues from the agent's tools."));
            }
        }

        foreach (var (name, check) in Checks)
        {
            errors = errors.Concat(Names([name], "checks")).Concat(Annotations(check, $"checks.{name}"));
            if (check.Use is not null && check.ExtensionId() is null)
            {
                errors = errors.Append(new(ValidationPhase.Shape, $"checks.{name}.use", $"\"{check.Use}\" is not a check.",
                    "Use extension:<id> for a check the application registers."));
            }

            if ((check.Use is null) == string.IsNullOrWhiteSpace(check.Command))
            {
                errors = errors.Append(new(ValidationPhase.Shape, $"checks.{name}", "needs either use or command.",
                    "Set use to extension:<id> for the application's check, or command to a command that checks a working copy."));
            }
            else if (check.Command is not null)
            {
                // A command checks a working copy: a task's work or the baseline (WS-02), never an agent's output, which has none.
                var checkName = name;
                errors = errors.Concat(Agents.Where(agent => agent.Value.Output?.Checks.Contains(checkName) == true)
                    .Select(agent => CommandOnOutput($"agents.{agent.Key}.output.checks", checkName)))
                    .Concat(Agents.SelectMany(agent => agent.Value.Pattern?.Nested($"agents.{agent.Key}.pattern") ?? [])
                        .Where(nested => nested.Pattern.Checks.Contains(checkName)).Select(nested => CommandOnOutput($"{nested.Path}.checks", checkName)));
            }
        }

        foreach (var (name, server) in ToolServers)
        {
            errors = errors.Concat(Names([name], "toolServers")).Concat(Annotations(server, $"toolServers.{name}"));
            if (server.Transport == ToolServerTransport.Stdio && string.IsNullOrWhiteSpace(server.Command))
            {
                errors = errors.Append(new(ValidationPhase.Shape, $"toolServers.{name}.command", "is required for the stdio transport.", "Name the program that runs the server."));
            }
            else if (server.Transport == ToolServerTransport.Http && !Uri.TryCreate(server.Url, UriKind.Absolute, out _))
            {
                errors = errors.Append(new(ValidationPhase.Shape, $"toolServers.{name}.url", "must be an absolute URL for the http transport.", "Give the server's endpoint, such as https://host/mcp."));
            }
        }

        foreach (var (name, source) in Knowledge)
        {
            errors = errors.Concat(Names([name], "knowledge")).Concat(Annotations(source, $"knowledge.{name}"));
            if (source.Use is not null && source.ExtensionId() is null)
            {
                errors = errors.Append(new(ValidationPhase.Shape, $"knowledge.{name}.use", $"\"{source.Use}\" is not a knowledge source.",
                    "Use extension:<id> for a source the application registers."));
            }
        }

        // CAP-02: the settings of a capability that is off are not checked.
        foreach (var (index, path) in (Capabilities.Workspace is { Enabled: true } workspace ? workspace.ProtectedPaths : []).Index())
        {
            errors = errors.Concat(Annotations(path, $"capabilities.workspace.protectedPaths[{index}]"));
        }

        if (Capabilities.Workspace is { Enabled: true } baseline)
        {
            errors = errors.Concat(References("capabilities.workspace.baselineChecks", "check", baseline.BaselineChecks, "checks", Checks.Keys));
        }

        if (Capabilities.Sandbox is { Enabled: true } sandbox)
        {
            foreach (var (index, rule) in sandbox.CommandRules.Index())
            {
                errors = errors.Concat(Annotations(rule, $"capabilities.sandbox.commandRules[{index}]"));
            }

            errors = errors.Concat(References("capabilities.sandbox.secrets", "agent", sandbox.Secrets.Keys, "agents", Agents.Keys));
        }

        if (Capabilities.TaskBoard is { Enabled: true } board)
        {
            // INV-07: a task's budget is a budget level.
            errors = errors.Concat(Annotations(board, "capabilities.taskBoard", ValidationPhase.Invariants))
                .Concat(Annotations(board.Budget, "capabilities.taskBoard.budget", ValidationPhase.Invariants));
        }

        if (Capabilities.Team is { Enabled: true } team)
        {
            errors = errors.Concat(Annotations(team, "capabilities.team"));
        }

        if (Capabilities.ProjectMemory is { Enabled: true } memory)
        {
            errors = errors.Concat(Annotations(memory, "capabilities.projectMemory"));
        }

        errors = errors.Concat(AdmissionSettings()).Concat(Annotations(Storage.Retention, "storage.retention"))
            .Concat(Storage.Unstored.Where(kind => !EventPayload.Kinds.Contains(kind)).Select(kind => new ConfigurationError(
                ValidationPhase.Shape, "storage.unstoredEvents", $"\"{kind}\" is not a kind of event.",
                $"Use one of: {string.Join(", ", EventPayload.Kinds.Order(StringComparer.Ordinal))}.")))
            .Concat(Capabilities.Checkpoints.Enabled ? Storage.Unstored.Intersect(SpentKinds).Select(kind => new ConfigurationError(
                ValidationPhase.Capabilities, "storage.unstoredEvents", $"\"{kind}\" must be stored while checkpoints are on: a run that resumes reads what it spent from it (INV-07).",
                "Remove it from the list.")) : []);

        return errors.Concat(ToolSettings()).Concat(CapabilitySettings()).Concat(InstructionPlaceholders.Check(this)).Concat(PatternCycles());
    }

    /// <summary>
    /// TEAM-07: an agent's helpers are agents that exist and work in turns of their own, keeping no history, since each helper is
    /// an agent of its own whose conversation is neither shared nor kept.
    /// </summary>
    private IEnumerable<ConfigurationError> HelperSettings(string name, AgentDefinition agent)
    {
        foreach (var helper in agent.Helpers ?? [])
        {
            if (Agents.GetValueOrDefault(helper) is not { } definition)
            {
                yield return Missing($"agents.{name}.helpers", "agent", helper, "agents", Agents.Keys);
            }
            else if (definition.Pattern is { IsTurn: false } || definition.Context?.History is { Strategy: not HistoryStrategy.None })
            {
                yield return new(ValidationPhase.Shape, $"agents.{name}.helpers", $"agent \"{helper}\" must work in turns of its own and keep no history to be a helper.",
                    $"Give agents.{helper} the toolLoop or singleCall pattern and the none history strategy.");
            }
            else if (ToolsOf(definition).Except(ToolsOf(agent)).Order(StringComparer.Ordinal).ToList() is { Count: > 0 } more)
            {
                // TEAM-07: a helper acts within its parent's means, so it has no tool its parent does not.
                yield return new(ValidationPhase.Tools, $"agents.{name}.helpers", $"agent \"{helper}\" has tools that {name} does not: {string.Join(", ", more)}.",
                    $"Give agents.{helper} only tools that agents.{name} has.");
            }
        }

        IEnumerable<string> ToolsOf(AgentDefinition definition) => definition.Tools.Where(ToolSets.ContainsKey).SelectMany(set => ToolSets[set]);
    }

    private static ConfigurationError CommandOnOutput(string path, string check) =>
        new(ValidationPhase.References, path, $"check \"{check}\" is a command, which checks a working copy, not output.",
            "Use it as a task's check or in capabilities.workspace.baselineChecks.");

    /// <summary>The events a resumed run reads its spent budget from.</summary>
    private static readonly string[] SpentKinds = ["modelCallEnded", "toolCallEnded", "runResumed", "runRolledBack", "turnStarted", "turnEnded", "budgetWarning"];

    /// <summary>CAP-03: every capability in use is on, and every capability on has the ones it requires.</summary>
    private IEnumerable<ConfigurationError> CapabilitySettings()
    {
        var on = Capabilities.Switches().Where(capability => capability.Value).Select(capability => capability.Key).ToHashSet();
        var errors = CapabilitiesOptions.Dependencies.Where(dependency => on.Contains(dependency.Capability) && !on.Contains(dependency.Requires))
            .Select(dependency => new ConfigurationError(ValidationPhase.Capabilities, $"capabilities.{dependency.Capability}.enabled",
                $"{dependency.Capability} needs the {dependency.Requires} capability, which is off.", $"Set capabilities.{dependency.Requires}.enabled to true."));
        if (Knowledge.Count > 0 && !on.Contains("knowledge"))
        {
            errors = errors.Append(Off("knowledge", "knowledge"));
        }

        foreach (var name in Tools.Where(tool => tool.Value.BuiltinTool()?.StartsWith("tasks.", StringComparison.Ordinal) == true).Select(tool => tool.Key))
        {
            errors = on.Contains("taskBoard") ? errors : errors.Append(Off($"tools.{name}.source", "taskBoard"));
        }

        foreach (var name in Tools.Where(tool => tool.Value.BuiltinTool()?.StartsWith("memory.", StringComparison.Ordinal) == true).Select(tool => tool.Key))
        {
            errors = on.Contains("projectMemory") ? errors : errors.Append(Off($"tools.{name}.source", "projectMemory"));
        }

        foreach (var name in Tools.Where(tool => tool.Value.BuiltinTool()?.StartsWith("team.", StringComparison.Ordinal) == true).Select(tool => tool.Key))
        {
            errors = on.Contains("team") ? errors : errors.Append(Off($"tools.{name}.source", "team"));
        }

        foreach (var name in Agents.Where(agent => agent.Value.Helpers is { Count: > 0 }).Select(agent => agent.Key))
        {
            errors = on.Contains("team") ? errors : errors.Append(Off($"agents.{name}.helpers", "team"));
        }

        foreach (var name in Agents.Where(agent => agent.Value.Pattern?.Type == PatternOptions.Team).Select(agent => agent.Key))
        {
            errors = on.Contains("team") ? errors : errors.Append(Off($"agents.{name}.pattern.type", "team"));
        }


        if (Capabilities.ProjectMemory is { Enabled: true, ApproveBy: MemoryApprover.Owner } && !on.Contains("humanInteraction"))
        {
            errors = errors.Append(Off("capabilities.projectMemory.approveBy", "humanInteraction"));
        }

        foreach (var name in Checks.Where(check => check.Value is { Command: not null, Use: null } && !on.Contains("sandbox")).Select(check => check.Key))
        {
            errors = errors.Append(Off($"checks.{name}.command", "sandbox")); // SBX-07: commands run only in the sandbox
        }

        foreach (var (name, _) in Tools.Where(tool => tool.Value.BuiltinTool() == AskOwnerTool.Name && !on.Contains("humanInteraction")))
        {
            errors = errors.Append(Off($"tools.{name}.source", "humanInteraction"));
        }

        foreach (var (name, agent) in Agents)
        {
            if (agent.Context?.History is { Strategy: not HistoryStrategy.None } && !on.Contains("conversationStore"))
            {
                errors = errors.Append(Off($"agents.{name}.context.history.strategy", "conversationStore"));
            }
        }

        return errors;

        static ConfigurationError Off(string path, string capability) =>
            new(ValidationPhase.Capabilities, path, $"needs the {capability} capability, which is off.", $"Set capabilities.{capability}.enabled to true.");
    }

    /// <summary>
    /// The tool settings that can be checked without the tools themselves. The tool pipeline checks the rest when it is
    /// built with the application's tools (TOOL-02).
    /// </summary>
    private IEnumerable<ConfigurationError> ToolSettings()
    {
        var errors = Names(Tools.Keys, "tools").Concat(Names(ToolSets.Keys, "toolSets")).Concat(Names(Gates.Keys, "gates"))
            .Concat(References("policies.gates", "gate", Policies.Gates, "gates", Gates.Keys));
        foreach (var (name, tool) in Tools)
        {
            errors = errors.Concat(Annotations(tool, $"tools.{name}")).Concat(References($"tools.{name}.gates", "gate", tool.Gates, "gates", Gates.Keys))
                .Concat(tool.Limits is null ? [] : Annotations(tool.Limits, $"tools.{name}.limits"));
            if (tool.McpTool() is { } mcp)
            {
                errors = mcp.Split('/') is [var server, { Length: > 0 }] && server.Length > 0
                    ? errors.Concat(References($"tools.{name}.source", "tool server", [server], "toolServers", ToolServers.Keys))
                    : errors.Append(new(ValidationPhase.Shape, $"tools.{name}.source", $"\"{tool.Source}\" does not name a server and a tool.", "Write mcp:<server>/<tool>."));
            }
            else if (tool.KnowledgeSource() is { } knowledge)
            {
                errors = errors.Concat(References($"tools.{name}.source", "knowledge source", [knowledge], "knowledge", Knowledge.Keys));
            }
            else if (tool.BuiltinTool() is { } builtin)
            {
                errors = ToolCatalog.Builtins.Contains(builtin) ? errors : errors.Append(new(ValidationPhase.References, $"tools.{name}.source",
                    $"built-in tool \"{builtin}\" does not exist.", $"Use one of: {string.Join(", ", ToolCatalog.Builtins)}."));
            }
            else if (tool.Source is not null && tool.ExtensionId() is null && tool.ProviderTool() is null)
            {
                errors = errors.Append(new(ValidationPhase.Shape, $"tools.{name}.source", $"\"{tool.Source}\" is not a tool source.",
                    "Use extension:<id>, mcp:<server>/<tool>, knowledge:<name>, builtin:<name>, or provider:<name> for a provider's own tool."));
            }
            else if (tool.ProviderTool() is not null && string.IsNullOrWhiteSpace(tool.Reason))
            {
                // TOOL-13: a provider tool skips the per-call steps, so it is enabled only explicitly, with a reason.
                errors = errors.Append(new(ValidationPhase.Tools, $"tools.{name}.reason", "is required for a provider tool.",
                    "Say why the agents need it; the provider runs it without gates or approval."));
            }

            if ((tool.ProviderTool() is not null || tool.BuiltinTool() is not null) && (tool.MaskResults || tool.ReceivesMaskedValues))
            {
                // ING-02, ING-06: the provider runs its tools and gives the model their results, so the core never sees either;
                // the core's own tools work on what the model has seen, masked already.
                errors = errors.Append(new(ValidationPhase.Tools, $"tools.{name}.{(tool.MaskResults ? "maskResults" : "receivesMaskedValues")}",
                    tool.ProviderTool() is null ? "has no effect on a built-in tool." : "has no effect on a provider tool.",
                    tool.ProviderTool() is null
                        ? "Remove it; built-in tools work on what the agent has seen, which is masked already."
                        : "Remove it; the provider runs the call, so the core cannot mask or restore its values."));
            }

            if (tool.ProviderTool() is null && tool.Limits is not null)
            {
                errors = errors.Append(new(ValidationPhase.Tools, $"tools.{name}.limits", "applies only to a provider tool.", "Remove it."));
            }
        }

        foreach (var (name, tools) in ToolSets)
        {
            errors = errors.Concat(References($"toolSets.{name}", "tool", tools, "tools", Tools.Keys));
        }

        foreach (var (name, gate) in Gates)
        {
            errors = errors.Concat(Annotations(gate, $"gates.{name}"));
            if (gate.Use is not null and not GateOptions.RequireApproval and not GateOptions.Deny and not GateOptions.UntrustedContentApproval && gate.ExtensionId() is null)
            {
                errors = errors.Append(new(ValidationPhase.Shape, $"gates.{name}.use", $"\"{gate.Use}\" is not a gate.",
                    $"Use {GateOptions.RequireApproval}, {GateOptions.Deny}, {GateOptions.UntrustedContentApproval}, or extension:<id> for a gate the application registers."));
            }
        }

        foreach (var (index, rule) in Policies.PermissionRules.Index())
        {
            var path = $"policies.permissionRules[{index}]";
            errors = errors.Concat(Annotations(rule, path)).Concat(rule.Tool is null ? [] : References($"{path}.tool", "tool", [rule.Tool], "tools", Tools.Keys));
            if (rule.Action == PolicyAction.Route)
            {
                errors = errors.Concat(rule.To is null
                    ? [new(ValidationPhase.Shape, $"{path}.to", "is required to route.", "Name the agent the turn is handed to.")]
                    : References($"{path}.to", "agent", [rule.To], "agents", Agents.Keys));
            }
        }

        return errors;
    }

    /// <summary>The admission settings (ING-02, ING-03): masking patterns that are valid expressions, and real rate limits.</summary>
    private IEnumerable<ConfigurationError> AdmissionSettings()
    {
        var errors = Annotations(Policies, "policies");
        foreach (var (name, limit) in new[] { ("perOwner", Policies.RateLimits?.PerOwner), ("perTenant", Policies.RateLimits?.PerTenant), ("perRun", Policies.RateLimits?.PerRun) })
        {
            errors = errors.Concat(limit is null ? [] : Annotations(limit, $"policies.rateLimits.{name}"));
        }

        foreach (var (name, pattern) in Policies.Masking?.Patterns ?? new Dictionary<string, string>())
        {
            try
            {
                _ = new Regex($"(?<{name}>{pattern})");
            }
            catch (ArgumentException exception)
            {
                errors = errors.Append(new(ValidationPhase.Shape, $"policies.masking.patterns.{name}", $"is not a valid pattern: {exception.Message}",
                    "Name the pattern with letters, digits and underscores, and write a .NET regular expression."));
            }
        }

        return errors;
    }

    /// <summary>
    /// The settings of an agent's turns (LOOP-05, LOOP-06, LOOP-07), their input and their output (OUT). The turn budget
    /// is an invariant (INV-07).
    /// </summary>
    private IEnumerable<ConfigurationError> TurnSettings(string name, AgentDefinition agent)
    {
        var path = $"agents.{name}";
        var errors = (agent.Budget is null ? [] : Annotations(agent.Budget, $"{path}.budget", ValidationPhase.Invariants))
            .Concat(agent.Budget?.Turn is null ? [] : Annotations(agent.Budget.Turn, $"{path}.budget.turn", ValidationPhase.Invariants))
            .Concat(agent.Budget?.Total is null ? [] : Annotations(agent.Budget.Total, $"{path}.budget.total", ValidationPhase.Invariants))
            .Concat(agent.Stall is null ? [] : Annotations(agent.Stall, $"{path}.stall"))
            .Concat(agent.Context is null ? [] : Annotations(agent.Context, $"{path}.context"))
            .Concat(agent.Context?.History is null ? [] : Annotations(agent.Context.History, $"{path}.context.history"))
            .Concat((agent.Context?.Record ?? []).Where(kind => !RecordItem.Kinds.Contains(kind)).Select(kind => new ConfigurationError(
                ValidationPhase.Shape, $"{path}.context.record", $"\"{kind}\" is not a kind of record entry.",
                $"Use one of: {string.Join(", ", RecordItem.Kinds.Order(StringComparer.Ordinal))}.")));
        if (agent.Context?.History is { Shortening: { } shortening } history && shortening != HistoryOptions.Provider && history.ExtensionId() is null)
        {
            errors = errors.Append(new(ValidationPhase.Shape, $"{path}.context.history.shortening", $"\"{shortening}\" is not a way to shorten history.",
                $"Use {HistoryOptions.Provider}, or extension:<id> for a shortener the application registers."));
        }

        if (agent.Output is { } output)
        {
            errors = errors.Concat(Annotations(output, $"{path}.output"))
                .Concat(References($"{path}.output.checks", "check", output.Checks, "checks", Checks.Keys))
                .Concat(OutputSchema($"{path}.output", output));
        }

        if (agent.StopWhen is { } stop)
        {
            errors = errors.Concat(Annotations(stop, $"{path}.stopWhen"))
                .Concat(stop.FinishTool is null ? [] : References($"{path}.stopWhen.finishTool", "tool", [stop.FinishTool], "tools", Tools.Keys));
            if (!stop.Finished && stop.FinishTool is null && stop.MaxIterations is null && !stop.ChecksPass)
            {
                errors = errors.Append(new(ValidationPhase.Shape, $"{path}.stopWhen", "has no condition, so no turn can complete.",
                    "Keep \"finished\": true, or set \"finishTool\", \"checksPass\" or \"maxIterations\"."));
            }

            if (stop.ChecksPass && agent.Output?.Checks.Count == 0)
            {
                errors = errors.Append(new(ValidationPhase.Shape, $"{path}.stopWhen.checksPass", "needs checks to pass, but output.checks is empty.",
                    "Name the checks in output.checks."));
            }
        }

        return errors;
    }

    /// <summary>Structured output needs a schema, and the schema must be valid JSON Schema (OUT-01).</summary>
    private static IEnumerable<ConfigurationError> OutputSchema(string path, OutputOptions output)
    {
        if (output.Schema is null)
        {
            return output.Format == OutputFormat.Structured
                ? [new(ValidationPhase.Shape, $"{path}.schema", "is required for structured output.", "Give the JSON Schema the output must match.")]
                : [];
        }

        try
        {
            JsonSchema.FromText(output.Schema);
            return [];
        }
        catch (Exception exception) when (exception is JsonSchemaException or JsonException or InvalidOperationException)
        {
            return [new(ValidationPhase.Shape, $"{path}.schema", $"is not valid JSON Schema: {exception.Message}", "")];
        }
    }

    private IEnumerable<ConfigurationError> FormatVersionSupported() =>
        FormatVersion == CurrentFormatVersion
            ? []
            : [new(ValidationPhase.Parse, "formatVersion", $"format version {FormatVersion} is not supported.",
                $"This core reads format version {CurrentFormatVersion}.")];

    /// <summary>The section's <c>[Required]</c> and <c>[Range]</c> annotations; each message is the problem and the fix.</summary>
    private static IEnumerable<ConfigurationError> Annotations(object section, string path, ValidationPhase phase = ValidationPhase.Shape)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(section, new ValidationContext(section), results, validateAllProperties: true);
        return results.Select(result => new ConfigurationError(
            phase, $"{path}.{JsonNamingPolicy.CamelCase.ConvertName(result.MemberNames.First())}", result.ErrorMessage!, ""));
    }

    private IEnumerable<ConfigurationError> ProviderExists(string name, ModelProfile profile) =>
        profile.Provider is null || Providers.ContainsKey(profile.Provider)
            ? []
            : [Missing($"models.{name}.provider", "provider", profile.Provider, "providers", Providers.Keys)];

    /// <summary>MDL-04: a fallback is another profile.</summary>
    private IEnumerable<ConfigurationError> Fallbacks(string name, ModelProfile profile) =>
        References($"models.{name}.fallbacks", "model profile", profile.Fallbacks.Where(fallback => fallback != name), "models", Models.Keys)
            .Concat(profile.Fallbacks.Contains(name)
                ? [new ConfigurationError(ValidationPhase.References, $"models.{name}.fallbacks", "a profile cannot be its own fallback.", "Name another profile.")]
                : []);

    /// <summary>MDL-09: cost budgets always exist (INV-07), so each model needs a price to be counted against them.</summary>
    private IEnumerable<ConfigurationError> Priced(string name, ModelProfile profile) =>
        profile.Provider is null || profile.Model is null || !Providers.TryGetValue(profile.Provider, out var provider) || provider.Prices.ContainsKey(profile.Model)
            ? []
            : [new(ValidationPhase.Provider, $"models.{name}.model", $"model \"{profile.Model}\" has no price, which the cost budgets need.",
                $"Add its prices to providers.{profile.Provider}.prices.")];

    private IEnumerable<ConfigurationError> ModelExists(string name, AgentDefinition agent) =>
        agent.Model is null || Models.ContainsKey(agent.Model)
            ? []
            : [Missing($"agents.{name}.model", "model profile", agent.Model, "models", Models.Keys)];

    /// <summary>Names of named entries cannot contain the characters of setting paths.</summary>
    private static IEnumerable<ConfigurationError> Names(IEnumerable<string> names, string section) =>
        names.Where(name => name.IndexOfAny(['.', '[', ']']) >= 0)
            .Select(name => new ConfigurationError(
                ValidationPhase.Shape, $"{section}.{name}", $"\"{name}\" is not a valid name.", "Leave out '.', '[' and ']'."));

    private static IEnumerable<ConfigurationError> References(string path, string kind, IEnumerable<string> names, string section, IEnumerable<string> known) =>
        names.Where(name => !known.Contains(name)).Select(name => Missing(path, kind, name, section, known));

    private static ConfigurationError Missing(string path, string kind, string name, string section, IEnumerable<string> known) =>
        new(ValidationPhase.References, path, $"{kind} \"{name}\" does not exist.",
            $"Add it to {section}, or use one of: {string.Join(", ", known.Order(StringComparer.Ordinal))}.");
}
