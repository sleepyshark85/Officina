using System.Text.Json;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// Validation of the agents' patterns (PAT-01 to PAT-05). Branches, routes and votes read only fields of a turn's
/// structured output, which validation checks against its schema (PAT-03, CFG-13).
/// </summary>
public sealed partial record OfficinaOptions
{
    /// <summary>The problems of a pattern, its steps and the patterns nested in it.</summary>
    /// <param name="path">The pattern's setting path.</param>
    /// <param name="owner">The agent the pattern belongs to, whose turn a step that names no agent is.</param>
    /// <param name="pattern">The pattern.</param>
    private IEnumerable<ConfigurationError> PatternSettings(string path, string owner, PatternOptions pattern)
    {
        var errors = Annotations(pattern, path).ToList();
        if (!PatternOptions.BuiltIn.Contains(pattern.Type) && pattern.ExtensionId() is null)
        {
            errors.Add(new(ValidationPhase.Shape, $"{path}.type", $"\"{pattern.Type}\" is not a pattern.",
                $"Use one of: {string.Join(", ", PatternOptions.BuiltIn)}, or extension:<id> for a pattern the application registers."));
        }

        foreach (var (at, step) in pattern.Children(path))
        {
            if (step is { Agent: not null, Pattern: not null })
            {
                errors.Add(new(ValidationPhase.Shape, at, "names both an agent and a pattern.", "Keep one of them."));
            }

            if (step.Pattern is { IsTurn: true })
            {
                errors.Add(new(ValidationPhase.Shape, $"{at}.pattern", $"\"{step.Pattern.Type}\" is a turn, not a nested pattern.",
                    "A step that is a turn names an agent, or neither."));
            }

            errors.AddRange(Annotations(step, at));
            errors.AddRange(step.Agent is null ? [] : References($"{at}.agent", "agent", [step.Agent], "agents", Agents.Keys));
            errors.AddRange(step.Pattern is null ? [] : PatternSettings($"{at}.pattern", owner, step.Pattern));
        }

        var needed = pattern.Type switch
        {
            PatternOptions.Workflow when pattern.Steps.Count == 0 => "steps",
            PatternOptions.Router when pattern.Routes.Count == 0 => "routes",
            PatternOptions.Router when pattern.On is null => "on",
            PatternOptions.FanOut when pattern.Branches.Count == 0 => "branches",
            PatternOptions.FanOut when pattern is { Combine: FanOutCombine.Majority, On: null } => "on",
            PatternOptions.FanOut when pattern is { Combine: FanOutCombine.Step, Combiner: null } => "combiner",
            PatternOptions.EvaluateAndRevise when pattern.Checks.Count == 0 => "checks",
            PatternOptions.PlanAndExecute when pattern.Executor is null => "executor",
            PatternOptions.Team when pattern.Lead is null => "lead",
            _ => null,
        };
        if (needed is not null)
        {
            return errors.Append(new(ValidationPhase.Shape, $"{path}.{needed}", $"is required for the {pattern.Type} pattern.", ""));
        }

        return errors.Concat(pattern.Type switch
        {
            PatternOptions.Workflow => WorkflowSettings(path, owner, pattern),
            PatternOptions.Router => Reads($"{path}.on", owner, pattern.Classify, new() { Field = pattern.On, Exists = true })
                .Concat(pattern.Otherwise is null || pattern.Routes.ContainsKey(pattern.Otherwise)
                    ? [] : [Missing($"{path}.otherwise", "route", pattern.Otherwise, $"{path}.routes", pattern.Routes.Keys)]),
            PatternOptions.FanOut => FanOutSettings(path, owner, pattern),
            PatternOptions.EvaluateAndRevise => References($"{path}.checks", "check", pattern.Checks, "checks", Checks.Keys),
            PatternOptions.PlanAndExecute => Reads($"{path}.planner", owner, pattern.Planner, new() { Field = "output.steps", Exists = true }),
            PatternOptions.Team => References($"{path}.lead", "agent", [pattern.Lead!], "agents", Agents.Keys)
                .Concat(References($"{path}.roles", "agent", pattern.Roles.Keys, "agents", Agents.Keys))
                .Concat(pattern.Roles.SelectMany(role => Annotations(role.Value, $"{path}.roles.{role.Key}"))),
            _ => [],
        });
    }

    private List<ConfigurationError> WorkflowSettings(string path, string owner, PatternOptions pattern)
    {
        var ids = pattern.Steps.Select(step => step.Id).OfType<string>().ToList();
        var errors = new List<ConfigurationError>();
        foreach (var (index, step) in pattern.Steps.Index())
        {
            var at = $"{path}.steps[{index}]";
            if (step.Id is null or "input" or BranchRule.End || ids.Count(id => id == step.Id) > 1)
            {
                errors.Add(new(ValidationPhase.Shape, $"{at}.id", step.Id is null ? "is required in a workflow." : $"\"{step.Id}\" is taken.",
                    "Give each step an id of its own, other than input and end."));
            }

            errors.AddRange((step.Input ?? []).Where(from => from != "input" && !pattern.Steps.Take(index).Any(earlier => earlier.Id == from))
                .Select(from => new ConfigurationError(ValidationPhase.References, $"{at}.input", $"\"{from}\" is neither input nor an earlier step.",
                    "Use input for the workflow's input, or the id of a step before this one.")));
            var actions = step.OnOutcome is { } on ? new[] { ("completed", on.Completed), ("handedOff", on.HandedOff), ("failed", on.Failed) } : [];
            foreach (var (outcome, action) in actions)
            {
                var (kind, target) = OutcomeActions.Parse(action ?? "");
                if (kind is null)
                {
                    errors.Add(new(ValidationPhase.Shape, $"{at}.onOutcome.{outcome}", $"\"{action}\" is not an action.", "Use continue, retry:<n>, goto:<step> or handoff."));
                }
                else if (kind == "goto")
                {
                    errors.AddRange(Target($"{at}.onOutcome.{outcome}", target!));
                }
            }
        }

        foreach (var (index, rule) in pattern.Next.Index())
        {
            var at = $"{path}.next[{index}]";
            var from = pattern.Steps.FirstOrDefault(step => step.Id is not null && step.Id == rule.From);
            errors.AddRange(Annotations(rule, at).Concat(rule.Goto is null ? [] : Target($"{at}.goto", rule.Goto)));
            errors.AddRange(rule.From is not null && from is null ? [Missing($"{at}.from", "step", rule.From, $"{path}.steps", ids)]
                : from is not null && rule.When is not null ? Reads($"{at}.when", owner, from, rule.When)
                : []);
        }

        return errors;

        IEnumerable<ConfigurationError> Target(string at, string target) =>
            target == BranchRule.End || ids.Contains(target) ? [] : [Missing(at, "step", target, $"{path}.steps", ids.Append(BranchRule.End))];
    }

    private List<ConfigurationError> FanOutSettings(string path, string owner, PatternOptions pattern)
    {
        var errors = new List<ConfigurationError>();
        if (pattern.Over is not null && pattern.Branches.Count != 1)
        {
            errors.Add(new(ValidationPhase.Shape, $"{path}.branches", "needs exactly one step with over, which runs it on each item.", ""));
        }

        if (pattern.Over is { } list && !Condition.IsPath(list, "input"))
        {
            errors.Add(new(ValidationPhase.Shape, $"{path}.over", $"{list} is not a path into the input, such as input.files.", ""));
        }

        if (pattern.Combine == FanOutCombine.Majority)
        {
            errors.AddRange(pattern.Branches.Index().SelectMany(branch =>
                Reads($"{path}.branches[{branch.Index}]", owner, branch.Item, new() { Field = pattern.On, Exists = true })));
        }

        return errors;
    }

    /// <summary>
    /// PAT-03, INV-01: a pattern decides only on the structured output of a turn. The problems of a condition on the
    /// output of a step, or a field it reads, given as a condition that it exists.
    /// </summary>
    private IEnumerable<ConfigurationError> Reads(string path, string owner, StepOptions? step, Condition condition)
    {
        var agent = Agents.GetValueOrDefault(step?.Agent ?? owner);
        var turn = step?.Pattern is null && (step?.Agent is null || agent?.Pattern?.IsTurn != false);
        JsonElement? schema = null;
        try
        {
            schema = turn && agent?.Output is { Format: OutputFormat.Structured, Schema: { } text } ? JsonElement.Parse(text) : null;
        }
        catch (JsonException)
        {
            // Reported with the agent's output settings.
        }

        return schema is { } parsed
            ? condition.Check(parsed, "output").Select(problem => new ConfigurationError(ValidationPhase.Conditions, path, problem, ""))
            : [new(ValidationPhase.Conditions, path, "reads the output of a step that is not a turn with structured output.",
                "Make the step a turn of an agent whose output format is structured, with a schema.")];
    }

    /// <summary>PAT-02: patterns nest, but no agent is a step of its own pattern, directly or through other agents.</summary>
    private IEnumerable<ConfigurationError> PatternCycles() =>
        Agents.Keys.Where(name =>
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var next = new Queue<string>(StepAgents(name));
            while (next.TryDequeue(out var agent))
            {
                if (agent == name)
                {
                    return true;
                }

                if (seen.Add(agent))
                {
                    foreach (var step in StepAgents(agent))
                    {
                        next.Enqueue(step);
                    }
                }
            }

            return false;
        }).Select(name => new ConfigurationError(ValidationPhase.References, $"agents.{name}.pattern", "has the agent as a step of its own pattern.",
            "A step that names no agent is a turn of the pattern's agent; break the loop of agents."));

    private IEnumerable<string> StepAgents(string agent) => Agents.GetValueOrDefault(agent)?.Pattern?.Agents() ?? [];
}
