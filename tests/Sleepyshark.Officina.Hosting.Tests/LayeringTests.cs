using Sleepyshark.Officina.Core;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Hosting.Configuration;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Hosting.Tests;

/// <summary>Layers merge as in configuration reference §13, and every value names its origin (CFG-04, CFG-05, TEST-05).</summary>
public sealed class LayeringTests : IDisposable
{
    private readonly ConfigurationFolder folder = new();

    public void Dispose() => folder.Dispose();

    [Fact]
    public void Each_layer_overrides_the_ones_below_it()
    {
        folder.Write("sof.json", """{ "run": { "budget": { "cost": 10, "time": "1h" }, "permissionMode": "readOnly" }, "project": { "name": "app" } }""")
            .Write("sof.prod.json", """{ "run": { "budget": { "cost": 20, "time": "2h" } } }""");
        var variables = new Dictionary<string, string> { ["SOF__run__budget__cost"] = "30", ["PATH"] = "/bin" };

        var configuration = folder.Load("prod", variables, new RunOption("run.budget.cost", "40", "--budget"));

        Assert.True(configuration.IsValid, string.Join('\n', configuration.Errors));
        Assert.Equal(40m, configuration.Options.Run.Budget.Cost);
        Assert.Equal(TimeSpan.FromHours(2), configuration.Options.Run.Budget.Time);
        Assert.Equal(PermissionMode.ReadOnly, configuration.Options.Run.PermissionMode);
        Assert.Equal("run option --budget", configuration.OriginOf("run.budget.cost").ToString());
        Assert.Equal("environment file sof.prod.json:1:44", configuration.OriginOf("run.budget.time").ToString());
        Assert.Equal("application file sof.json:1:70", configuration.OriginOf("run.permissionMode").ToString());
        Assert.Equal($"code default, core {CoreVersion.Value}", configuration.OriginOf("models.default.model").ToString());
        Assert.Equal(["application file sof.json", "environment file sof.prod.json", "environment variables", "run options"], configuration.Layers);
    }

    [Fact]
    public void Environment_variables_are_a_layer_between_the_files_and_run_options()
    {
        folder.Write("sof.json", """{ "run": { "budget": { "cost": 10 } } }""");

        var configuration = folder.Load(variables: new() { ["SOF__run__budget__cost"] = "30", ["SOF__run__permissionMode"] = "auto", ["SOF__models__default__fallbacks"] = """["fast"]""", ["SOF__models__fast__model"] = "claude-haiku-4-5" });

        Assert.True(configuration.IsValid, string.Join('\n', configuration.Errors));
        Assert.Equal(30m, configuration.Options.Run.Budget.Cost);
        Assert.Equal(PermissionMode.Auto, configuration.Options.Run.PermissionMode);
        Assert.Equal(["fast"], configuration.Options.Models["default"].Fallbacks);
        Assert.Equal("environment variable SOF__run__budget__cost", configuration.OriginOf("run.budget.cost").ToString());
    }

    [Fact]
    public void The_code_default_names_the_core_version()
    {
        var settings = folder.Load().Settings();

        var permissionMode = Assert.Single(settings, setting => setting.Path == "run.permissionMode");
        Assert.Equal(("\"ask\"", "code default, core 0.1.0"), (permissionMode.Value, permissionMode.Origin.ToString()));
        Assert.All(settings, setting => Assert.Equal(LayerKind.CodeDefault, setting.Origin.Layer));
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
    public void Lists_are_replaced_as_a_whole()
    {
        folder.Write("sof.json", """{ "models": { "a": { "fallbacks": ["default", "b"] }, "b": {} } }""")
            .Write("sof.ci.json", """{ "models": { "a": { "fallbacks": ["b"] } } }""");

        var configuration = folder.Load("ci");

        Assert.Equal(["b"], configuration.Options.Models["a"].Fallbacks);
        Assert.Equal("environment file sof.ci.json:1:35", configuration.OriginOf("models.a.fallbacks").ToString());
    }

    [Fact]
    public void Null_removes_a_value_so_the_code_default_applies()
    {
        folder.Write("sof.json", """{ "run": { "permissionMode": "auto" }, "models": { "default": { "effort": "high", "model": "other" } }, "agents": { "a": { "instructions": "x" }, "b": { "instructions": "y" } } }""")
            .Write("sof.ci.json", """{ "run": { "permissionMode": null }, "models": { "default": { "effort": null, "model": null } }, "agents": { "b": null } }""");

        var configuration = folder.Load("ci");

        Assert.True(configuration.IsValid, string.Join('\n', configuration.Errors));
        Assert.Equal(PermissionMode.Ask, configuration.Options.Run.PermissionMode);
        Assert.Equal((null, "claude-opus-5-5"), (configuration.Options.Models["default"].Effort, configuration.Options.Models["default"].Model));
        Assert.Equal(["a"], configuration.Options.Agents.Keys);
        Assert.Equal(LayerKind.CodeDefault, configuration.OriginOf("run.permissionMode").Layer);
        Assert.Equal(LayerKind.CodeDefault, configuration.OriginOf("models.default.model").Layer);
    }

    [Fact]
    public void A_file_builds_on_the_files_it_extends_and_later_entries_win()
    {
        folder.Write("base/one.json", """{ "project": { "name": "one", "values": { "a": "1" } } }""")
            .Write("base/two.json", """{ "extends": ["one.json"], "project": { "name": "two" } }""")
            .Write("sof.json", """{ "extends": ["base/one.json", "base/two.json"], "project": { "values": { "b": "2" } } }""");

        var configuration = folder.Load();

        Assert.True(configuration.IsValid, string.Join('\n', configuration.Errors));
        Assert.Equal("two", configuration.Options.Project.Name);
        Assert.Equal(new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" }, configuration.Options.Project.Values);
        Assert.Equal("extended file base/two.json:1:49", configuration.OriginOf("project.name").ToString());
        Assert.Equal(["extended file base/one.json", "extended file base/one.json", "extended file base/two.json", "application file sof.json"], configuration.Layers);
    }

    [Fact]
    public void A_definition_builds_on_the_one_it_extends()
    {
        folder.Write("sof.json", """
            {
              "models": { "strong": {} },
              "agents": {
                "base": { "description": "Writes code.", "instructions": "Be careful.", "model": "strong" },
                "developer": { "extends": "base", "instructions": "Implement the task." },
                "junior": { "extends": "developer", "description": null }
              }
            }
            """);

        var configuration = folder.Load();

        Assert.True(configuration.IsValid, string.Join('\n', configuration.Errors));
        Assert.Equal(new AgentDefinition { Instructions = "Implement the task.", Model = "strong" }, configuration.Options.Agents["junior"]);
        Assert.Equal("Writes code.", configuration.Options.Agents["developer"].Description);
        Assert.Equal("application file sof.json:5:55, through extends from agents.developer", configuration.OriginOf("agents.junior.instructions").ToString());
        Assert.Equal("application file sof.json:4:86, through extends from agents.base", configuration.OriginOf("agents.junior.model").ToString());
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

        Assert.True(configuration.IsValid, string.Join('\n', configuration.Errors));
        Assert.Equal("Extract.", configuration.Options.Agents["extractor"].Instructions);
    }

    [Fact]
    public async Task Configuration_changes_apply_to_new_runs_without_a_rebuild_and_never_to_a_running_one()
    {
        var ct = TestContext.Current.CancellationToken;
        folder.Write("sof.json", """{ "agents": { "a": { "instructions": "First." } } }""");
        var running = new TestKit(folder.Load().ValidOptions());

        folder.Write("sof.json", """{ "agents": { "a": { "instructions": "Second." } } }""");
        var next = new TestKit(folder.Load().ValidOptions());
        running.Model.Reply("1");
        next.Model.Reply("2");
        await running.RunAsync("a", "go", ct);
        await next.RunAsync("a", "go", ct);

        Assert.Equal("First.", Assert.Single(running.Model.Requests).Instructions);
        Assert.Equal("Second.", Assert.Single(next.Model.Requests).Instructions);
    }

    [Fact]
    public void A_file_and_the_programmatic_form_give_the_same_configuration()
    {
        folder.Write("sof.json", """
            {
              "project": { "name": "app" },
              "models": { "strong": { "effort": "high", "fallbacks": ["default"] } },
              "agents": { "dev": { "instructions": "Implement.", "model": "strong" } },
              "run": { "budget": { "cost": 40, "time": "2h" } }
            }
            """);

        var built = new OfficinaOptions
        {
            Project = new ProjectOptions { Name = "app" },
            Models = new Dictionary<string, ModelProfile> { ["default"] = new(), ["strong"] = new() { Effort = "high", Fallbacks = ["default"] } },
            Agents = new Dictionary<string, AgentDefinition> { ["dev"] = new() { Instructions = "Implement.", Model = "strong" } },
            Run = new RunDefaults { Budget = new RunBudget { Cost = 40, Time = TimeSpan.FromHours(2) } },
        };

        Assert.Equal(OfficinaJson.Write(built), OfficinaJson.Write(folder.Load().ValidOptions()));
    }

    [Fact]
    public void Show_for_an_agent_lists_what_applies_to_it()
    {
        folder.Write("sof.json", """{ "models": { "strong": {} }, "agents": { "dev": { "instructions": "x", "model": "strong" }, "other": { "instructions": "y" } } }""");

        var paths = folder.Load().Settings("dev").Select(setting => setting.Path).ToArray();

        Assert.Contains("agents.dev.instructions", paths);
        Assert.Contains("models.strong.model", paths);
        Assert.Contains("providers.claude.apiKey.secret", paths);
        Assert.Contains("run.budget.cost", paths);
        Assert.DoesNotContain(paths, path => path.StartsWith("agents.other", StringComparison.Ordinal) || path.StartsWith("models.default", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => folder.Load().Settings("nobody"));
    }

    [Fact]
    public async Task A_run_stores_a_configuration_that_loads_back_to_the_same_options()
    {
        folder.Write("sof.json", """
            {
              "project": { "name": "app", "values": { "test": "dotnet test" } },
              "models": { "strong": { "effort": "high", "settings": { "temperature": 0.2, "nested": { "a": [1, "b"] } }, "fallbacks": ["default"] } },
              "agents": { "dev": { "instructions": "Run {{project.values.test}}.", "model": "strong", "description": "Dev." } },
              "run": { "budget": { "time": "90m" }, "permissionMode": "auto" }
            }
            """);
        var options = folder.Load().ValidOptions();
        var kit = new TestKit(options);
        kit.Model.Reply("ok");
        await kit.RunAsync("dev", "go", TestContext.Current.CancellationToken);

        using var replay = new ConfigurationFolder();
        var stored = Assert.Single(kit.Runs.Runs).Configuration;
        replay.Write("sof.json", stored);

        Assert.Equal(stored, OfficinaJson.Write(replay.Load().ValidOptions()));
    }
}
