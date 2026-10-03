using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Sandbox;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>
/// Definitions that build on others (CFG-05) and the presets shipped with the core (CFG-11), loaded by the CLI's loader: a
/// file's <c>extends</c> puts presets and other files below it, and an agent's builds it on another agent.
/// </summary>
public sealed class ExtendsAndPresetTests : IDisposable
{
    private readonly ConfigurationFolder folder = new();

    public void Dispose() => folder.Dispose();

    // CFG-05: an agent builds on another: it inherits what it does not set, and a list it sets replaces the other's.
    [Fact]
    public void An_agent_builds_on_another_and_overrides_parts_of_it()
    {
        folder.Write("sof.json", """
            {
              "agents": {
                "base": { "instructions": "Work.", "model": "strong", "tools": ["a", "b"], "context": { "operatingFacts": ["Today is {{now:date}}."] } },
                "derived": { "extends": "base", "instructions": "Work well.", "tools": ["a"] },
                "deeper": { "extends": "derived", "description": "Deeper." }
              },
              "models": { "strong": { "effort": "high" } },
              "tools": { "a": { "source": "builtin:record.propose_fact" }, "b": { "source": "builtin:record.propose_finding" } },
              "toolSets": { "a": ["a"], "b": ["b"] }
            }
            """);

        var configuration = folder.Load();

        Assert.Empty(configuration.Errors);
        var deeper = configuration.Options.Agents["deeper"];
        Assert.Equal(("Work well.", "strong", "Deeper."), (deeper.Instructions, deeper.Model, deeper.Description));
        Assert.Equal(["a"], deeper.Tools);
        Assert.Equal(["Today is {{now:date}}."], deeper.Context.OperatingFacts);
        Assert.Equal(["a", "b"], configuration.Options.Agents["base"].Tools); // the base stays as it is
        Assert.Equal("sof.json", configuration.SourceOf("agents.derived.instructions"));
        Assert.Equal("inherited by agents.deeper through extends", configuration.SourceOf("agents.deeper.model"));
    }

    // Merge phase (configuration reference §14): a missing agent or a cycle is an error.
    [Fact]
    public void An_agent_that_builds_on_a_missing_agent_or_on_itself_is_an_error()
    {
        folder.Write("sof.json", """
            {
              "agents": {
                "one": { "extends": "two", "instructions": "Work." },
                "two": { "extends": "one", "instructions": "Work." },
                "three": { "extends": "nobody", "instructions": "Work." }
              }
            }
            """);

        Assert.Equal(
            [
                (ValidationPhase.Merge, "agents.two.extends", "builds on itself: one → two → one."),
                (ValidationPhase.Merge, "agents.three.extends", "agent \"nobody\" does not exist."),
            ],
            folder.Load().Errors.Select(error => (error.Phase, error.Path, error.Problem)));
    }

    // CFG-11, CFG-04: a preset is a layer below the file, which overrides it; a list in the file replaces the preset's.
    [Fact]
    public void A_file_builds_on_a_preset_and_another_file_and_overrides_them()
    {
        folder.Write("shared.json", """{ "run": { "budget": { "cost": 50 } } }""");
        folder.Write("sof.json", """
            {
              "extends": ["preset:coding-team", "shared.json"],
              "project": { "name": "calc", "values": { "buildCommand": "dotnet build", "testCommand": "dotnet test" } },
              "capabilities": { "humanInteraction": { "signOffs": ["runBudgetExceeded"] } }
            }
            """);

        var configuration = folder.Load();

        Assert.Empty(configuration.Errors);
        var options = configuration.Options;
        Assert.Equal(50m, options.Run.Budget.Cost);
        Assert.Equal([SignOff.RunBudgetExceeded], options.Capabilities.HumanInteraction.SignOffs); // replaced, not merged by position
        Assert.Equal(("preset:coding-team", "sof.json"), (configuration.SourceOf("capabilities.team.enabled"), configuration.SourceOf("project.name")));
        Assert.EndsWith("shared.json", configuration.SourceOf("run.budget.cost"), StringComparison.Ordinal);
    }

    // INV-10: a file sof.json extends is read-only to agents, as sof.json is, whatever its name, such as one starting with "..".
    [Fact]
    public void The_files_sof_json_extends_are_read_only_to_agents()
    {
        Directory.CreateDirectory(Path.Combine(folder.Directory, "team"));
        folder.Write("..base.json", "{}").Write(Path.Combine("team", "roles.json"), "{}");
        folder.Write("sof.json", """{ "extends": ["..base.json", "team/roles.json", "preset:single-call-extractor"] }""");

        var paths = folder.Load().Options.Capabilities.Workspace.ProtectedPaths;

        Assert.Equal(
            [("..base.json", PathAccess.ReadOnly), ("team/roles.json", PathAccess.ReadOnly)],
            paths.Select(path => (path.Path, path.Access)).Order());
    }

    [Theory]
    [InlineData("""{ "extends": ["preset:nothing"] }""", "preset \"preset:nothing\" does not exist.")]
    [InlineData("""{ "extends": ["other.json"] }""", "other.json builds on itself through sof.json.")]
    public void A_missing_preset_or_a_cycle_of_files_is_an_error(string text, string problem)
    {
        folder.Write("other.json", """{ "extends": ["sof.json"] }""");
        folder.Write("sof.json", text);

        var error = Assert.Single(folder.Load().Errors, error => error.Phase == ValidationPhase.Merge);

        Assert.EndsWith(problem, error.Problem, StringComparison.Ordinal);
    }

    // CFG-11, ING-02, HITL-04, TASK-06, CFG-05: the coding team needs only the project's commands. Masking is off, the plan, the run
    // budget and irreversible actions are signed off, the reviewer has no tool that changes files or runs commands, and the
    // roles share a base.
    [Fact]
    public void The_coding_team_preset_needs_only_the_projects_commands()
    {
        folder.Write("sof.json", """{ "extends": ["preset:coding-team"] }""");
        Assert.Equal(
            ["agents.developer.instructions", "agents.developer.instructions", "checks.build.command", "checks.tests.command"],
            folder.Load().Errors.Select(error => error.Path).Order(StringComparer.Ordinal));

        folder.Write("sof.json", """{ "extends": ["preset:coding-team"], "project": { "values": { "buildCommand": "dotnet build", "testCommand": "dotnet test" } } }""");
        var configuration = folder.Load();

        Assert.Empty(configuration.Errors);
        var options = configuration.Options;
        Assert.False(options.Policies.Masking!.Enabled);
        Assert.Equal([SignOff.PlanApproval, SignOff.RunBudgetExceeded, SignOff.IrreversibleAction], options.Capabilities.HumanInteraction.SignOffs);
        var reviewerTools = options.Agents["reviewer"].Tools.SelectMany(set => options.ToolSets[set]).Select(tool => options.Tools[tool].Source).ToList();
        Assert.DoesNotContain(reviewerTools, source => source!.StartsWith("extension:sandbox.", StringComparison.Ordinal) || source == "extension:workspace.write_file");
        Assert.All(["lead", "developer", "reviewer"], role => Assert.Equal(["Today is {{now:date}}."], options.Agents[role].Context.OperatingFacts));
    }

    // SBX-02: the coding team trusts the sandbox. Every command runs in it, however a line joins them, but git push and git
    // remote; a line with a substitution is still asked about, as no rule sees the command it runs.
    [Theory]
    [InlineData("dotnet build 2>&1 | tail -5", PolicyAction.Allow)]
    [InlineData("cat x", PolicyAction.Allow)]
    [InlineData("ls; pwd", PolicyAction.Allow)]
    [InlineData("cd src && dotnet test", PolicyAction.Allow)]
    [InlineData("find . -name '*.cs' | xargs grep -n Divide", PolicyAction.Allow)]
    [InlineData("git push origin", PolicyAction.Deny)]
    [InlineData("dotnet build && git remote add x https://example.com", PolicyAction.Deny)]
    [InlineData("echo $(cat x)", PolicyAction.Ask)]
    [InlineData("echo `cat x`", PolicyAction.Ask)]
    public async Task The_coding_team_allows_every_command_but_git_push_and_git_remote(string command, PolicyAction expected)
    {
        folder.Write("sof.json", """{ "extends": ["preset:coding-team"], "project": { "values": { "buildCommand": "dotnet build", "testCommand": "dotnet test" } } }""");
        var options = folder.Load().Options;

        // The gate reads only the command, so the call's run record is not needed.
        var context = new GateContext("developer[1]", "run_command", JsonSerializer.SerializeToElement(new { command }), Caller.Anonymous, false, null!, null);
        var decision = await new CommandRules(options.Capabilities.Sandbox).EvaluateAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(expected, decision.Action);
        Assert.Equal(["run_command", "start_process"], options.Tools.Where(tool => tool.Value.Gates.Contains("commands")).Select(tool => tool.Key).Order(StringComparer.Ordinal));
    }

    // TEST-31: the benchmark's goals are fixed, in tiers, each with a hidden test suite, for a team its configuration sets up.
    [Fact]
    public void The_benchmark_has_ten_goals_in_tiers_each_with_hidden_tests_for_a_valid_team()
    {
        var benchmark = Path.Combine(GeneratedDocumentationTests.Root, "benchmark");
        var goals = Directory.GetDirectories(Path.Combine(benchmark, "goals")).Select(Path.GetFileName).ToList();

        Assert.Equal((4, 4, 2), (goals.Count(goal => goal![0] == 's'), goals.Count(goal => goal![0] == 'm'), goals.Count(goal => goal![0] == 'l')));
        Assert.All(goals, goal =>
        {
            Assert.Single(File.ReadAllText(Path.Combine(benchmark, "goals", goal!, "goal.md")).Trim().Split("\n\n")); // one paragraph
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(benchmark, "goals", goal!, "hidden"), "test_*.py"));
        });
        Assert.Empty(SofConfiguration.Load(benchmark, null, new Dictionary<string, string>(), []).Errors);
    }

    [Theory]
    [InlineData("single-call-extractor", """{ "agents": { "extractor": { "instructions": "Extract the total.", "output": { "schema": "{ \"type\": \"object\" }" } } } }""")]
    [InlineData("tool-using-assistant", """{ "agents": { "assistant": { "instructions": "Help." } } }""")]
    public void The_other_presets_need_only_the_agents_job(string preset, string application)
    {
        var text = application.Insert(1, $""" "extends": ["preset:{preset}"], """);
        folder.Write("sof.json", text);

        Assert.Empty(folder.Load().Errors);
    }
}
