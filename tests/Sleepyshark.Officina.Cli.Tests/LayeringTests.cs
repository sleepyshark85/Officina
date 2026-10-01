using System.Text.Json;
using Sleepyshark.Officina.Core;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>Layers merge as in configuration reference §13, and every value names its source (CFG-04, TEST-05).</summary>
public sealed class LayeringTests : IDisposable
{
    private readonly ConfigurationFolder folder = new();

    public void Dispose() => folder.Dispose();

    [Fact]
    public void Each_layer_overrides_the_ones_below_it()
    {
        folder.Write("sof.json", """{ "run": { "budget": { "cost": 10, "time": "01:00:00" }, "permissionMode": "readOnly" }, "project": { "name": "app" } }""")
            .Write("sof.prod.json", """{ "run": { "budget": { "cost": 20, "time": "02:00:00" } } }""");
        var variables = new Dictionary<string, string> { ["SOF__run__budget__cost"] = "30", ["PATH"] = "/bin" };

        var configuration = folder.Load("prod", variables, ("run.budget.cost", "40", "--budget"));

        Assert.Empty(configuration.Errors);
        Assert.Equal(40m, configuration.Options.Run.Budget.Cost);
        Assert.Equal(TimeSpan.FromHours(2), configuration.Options.Run.Budget.Time);
        Assert.Equal(PermissionMode.ReadOnly, configuration.Options.Run.PermissionMode);
        Assert.Equal("option --budget", configuration.SourceOf("run.budget.cost"));
        Assert.Equal("sof.prod.json", configuration.SourceOf("run.budget.time"));
        Assert.Equal("sof.json", configuration.SourceOf("run.permissionMode"));
        Assert.Equal($"code default, core {CoreVersion.Value}", configuration.SourceOf("models.default.model"));
    }

    [Fact]
    public void Environment_variables_are_a_layer_between_the_files_and_the_options()
    {
        folder.Write("sof.json", """{ "run": { "budget": { "cost": 10 } } }""");

        var configuration = folder.Load(variables: new() { ["SOF__run__budget__cost"] = "30", ["SOF__RUN__PERMISSIONMODE"] = "auto" });

        Assert.Empty(configuration.Errors);
        Assert.Equal(30m, configuration.Options.Run.Budget.Cost);
        Assert.Equal(PermissionMode.Auto, configuration.Options.Run.PermissionMode);
        Assert.Equal("environment variable SOF__run__budget__cost", configuration.SourceOf("run.budget.cost"));
    }

    [Fact]
    public void Every_setting_is_listed_with_its_default_and_the_core_version()
    {
        folder.Write("sof.json", "{}");

        var settings = folder.Load().Settings();

        Assert.Contains(("run.permissionMode", "\"ask\""), settings);
        Assert.Contains(("run.budget.time", "\"08:00:00\""), settings);
        Assert.Equal("code default, core 0.1.0", folder.Load().SourceOf("run.permissionMode"));
    }

    [Fact]
    public void Objects_merge_key_by_key_and_named_maps_by_name()
    {
        folder.Write("sof.json", """{ "project": { "name": "app", "values": { "build": "make", "test": "make test" } }, "models": { "strong": { "effort": "high" } } }""")
            .Write("sof.ci.json", """{ "project": { "values": { "test": "make check" } }, "models": { "fast": {}, "strong": { "maxOutputTokens": 1000 } } }""");

        var options = folder.Load("ci").Options;

        Assert.Equal("app", options.Project.Name);
        Assert.Equal(new Dictionary<string, string> { ["build"] = "make", ["test"] = "make check" }, options.Project.Values);
        Assert.Equal(["default", "fast", "strong"], options.Models.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("high", 1000), (options.Models["strong"].Effort, options.Models["strong"].MaxOutputTokens));
    }

    [Fact]
    public void Tools_gates_policies_and_conditions_bind_from_a_file()
    {
        folder.Write("sof.json", """
            {
              "agents": { "dev": { "instructions": "x", "tools": ["issues"] } },
              "tools": { "create_issue": { "source": "extension:Acme.CreateIssue", "kind": "write", "gates": ["no-main"], "timeout": "00:00:30" } },
              "toolSets": { "issues": ["create_issue"] },
              "gates": {
                "no-main": { "use": "builtin:deny", "when": { "all": [ { "field": "args.branch", "in": ["main"] }, { "not": { "field": "args.force", "is": "true" } } ] } }
              },
              "policies": { "permissionRules": [ { "tool": "create_issue", "when": { "field": "args.count", "gte": 2 }, "action": "ask" } ] }
            }
            """);

        var configuration = folder.Load();
        var options = configuration.Options;

        Assert.Empty(configuration.Errors);
        Assert.Equal((ToolKind.Write, TimeSpan.FromSeconds(30)), (options.Tools["create_issue"].Kind, options.Tools["create_issue"].Timeout));
        Assert.Equal(PolicyAction.Ask, Assert.Single(options.Policies.PermissionRules).Action);
        var when = options.Gates["no-main"].When!;
        Assert.True(when.Holds(JsonDocument.Parse("""{ "branch": "main", "force": false }""").RootElement));
        Assert.False(when.Holds(JsonDocument.Parse("""{ "branch": "main", "force": true }""").RootElement));
        Assert.True(options.Policies.PermissionRules[0].When!.Holds(JsonDocument.Parse("""{ "count": 2 }""").RootElement));
    }

    [Fact]
    public void Null_unsets_a_setting_that_may_be_unset()
    {
        folder.Write("sof.json", """{ "agents": { "a": { "instructions": "x", "description": "Writes." } } }""")
            .Write("sof.ci.json", """{ "agents": { "a": { "description": null } } }""");

        var configuration = folder.Load("ci");

        Assert.Empty(configuration.Errors);
        Assert.Null(configuration.Options.Agents["a"].Description);
    }

    [Fact]
    public void Unknown_settings_are_ignored()
    {
        folder.Write("sof.json", """{ "agents": { "a": { "instructions": "x", "instruction": "y" } }, "capabilities": { "sandbox": true } }""");

        Assert.Empty(folder.Load().Errors);
    }

    [Fact]
    public void Configuration_files_accept_comments_and_trailing_commas()
    {
        folder.Write("sof.json", """
            {
              // the extractor
              "agents": {
                "extractor": { "instructions": "Extract.", /* inline */ },
              },
            }
            """);

        var configuration = folder.Load();

        Assert.Empty(configuration.Errors);
        Assert.Equal("Extract.", configuration.Options.Agents["extractor"].Instructions);
    }

    [Fact]
    public async Task Configuration_changes_apply_to_new_runs_without_a_rebuild_and_never_to_a_running_one()
    {
        var ct = TestContext.Current.CancellationToken;
        folder.Write("sof.json", """{ "agents": { "a": { "instructions": "First." } } }""");
        var running = new TestKit(folder.Load().Options);

        folder.Write("sof.json", """{ "agents": { "a": { "instructions": "Second." } } }""");
        var next = new TestKit(folder.Load().Options);
        running.Model.Reply("1");
        next.Model.Reply("2");
        await running.RunAsync("a", "go", ct);
        await next.RunAsync("a", "go", ct);

        Assert.StartsWith("First.\n", Assert.Single(running.Model.Requests).Instructions);
        Assert.StartsWith("Second.\n", Assert.Single(next.Model.Requests).Instructions);
    }

    [Fact]
    public void A_file_and_the_programmatic_form_give_the_same_configuration()
    {
        folder.Write("sof.json", """
            {
              "project": { "name": "app" },
              "models": { "strong": { "effort": "high", "toolChoice": "none" } },
              "agents": { "dev": { "instructions": "Implement.", "model": "strong" } },
              "run": { "budget": { "cost": 40, "time": "02:00:00" } }
            }
            """);

        var built = new OfficinaOptions
        {
            Project = new ProjectOptions { Name = "app" },
            Models = new Dictionary<string, ModelProfile>
            {
                ["default"] = new(),
                ["strong"] = new() { Effort = "high", ToolChoice = ToolChoice.None },
            },
            Agents = new Dictionary<string, AgentDefinition> { ["dev"] = new() { Instructions = "Implement.", Model = "strong" } },
            Run = new RunDefaults { Budget = new RunBudget { Cost = 40, Time = TimeSpan.FromHours(2) } },
        };

        Assert.Equal(Json(built), Json(folder.Load().Options));
    }

    [Fact]
    public async Task A_run_records_the_configuration_it_was_started_with()
    {
        folder.Write("sof.json", """{ "agents": { "dev": { "instructions": "Run the tests." } } }""");
        var options = folder.Load().Options;
        var kit = new TestKit(options);
        kit.Model.Reply("ok");

        await kit.RunAsync("dev", "go", TestContext.Current.CancellationToken);

        Assert.Same(options, Assert.Single(kit.Storage.Runs.Runs).Configuration);
    }

    private static string Json(OfficinaOptions options) => JsonSerializer.Serialize(options, ConfigurationJson.Options);
}
