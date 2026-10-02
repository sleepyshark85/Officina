using System.Text.RegularExpressions;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary><c>sof config show | validate</c> (CFG-04, CFG-06, TEST-05).</summary>
public sealed partial class ConfigCommandTests : IDisposable
{
    private const string Smallest = """
        {
          "project": { "name": "invoice-api" },
          "agents": {
            "extractor": { "instructions": "Extract the total for {{project.name}}. Reply as JSON." }
          }
        }
        """;

    private readonly Sof sof = new();

    public void Dispose() => sof.Dispose();

    [Fact]
    public async Task Show_with_origin_lists_every_setting_with_its_source()
    {
        sof.Write("sof.json", Smallest).Write("sof.prod.json", """{ "run": { "budget": { "time": "02:00:00" } } }""");
        sof.Variables["SOF_ENVIRONMENT"] = "prod";
        sof.Variables["SOF__run__permissionMode"] = "auto";

        var (exitCode, output, _) = await sof.RunAsync("config", "show", "--origin", "--budget", "40");

        Assert.Equal(0, exitCode);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => Spaces().Replace(line, " ")).ToArray();
        Assert.Contains("project.name \"invoice-api\" sof.json", lines);
        Assert.Contains("run.budget.cost 40 option --budget", lines);
        Assert.Contains("run.budget.time \"02:00:00\" sof.prod.json", lines);
        Assert.Contains("run.permissionMode \"auto\" environment variable SOF__run__permissionMode", lines);
        Assert.Contains("models.default.model \"claude-opus-5-5\" code default, core 0.1.0", lines);
        const string AnyLayer = @" (code default, core \d|sof\.json|sof\.prod\.json|environment variable|option)";
        Assert.All(lines, line => Assert.Matches(AnyLayer, line));
    }

    // A list has no value of its own in the configuration, only its items, so its source is found through them.
    [Fact]
    public async Task Show_with_origin_gives_a_list_the_file_preset_or_variable_that_set_it()
    {
        sof.Write("sof.json", """
            {
              "extends": ["preset:coding-team"],
              "project": { "values": { "buildCommand": "dotnet build", "testCommand": "dotnet test" } },
              "capabilities": { "checkpoints": { "at": ["turn"] } }
            }
            """);
        sof.Variables["SOF__capabilities__humanInteraction__signOffs__0"] = "planApproval";

        var (exitCode, output, _) = await sof.RunAsync("config", "show", "--origin");

        Assert.Equal(0, exitCode);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => Spaces().Replace(line, " ")).ToArray();
        Assert.Contains("capabilities.checkpoints.at [\"turn\"] sof.json", lines);
        Assert.Contains("toolSets.reviewing [\"review_task\",\"message\",\"hand_off\"] preset:coding-team", lines);
        Assert.Contains(lines, line => line.StartsWith("capabilities.sandbox.commandRules [{", StringComparison.Ordinal) && line.EndsWith(" preset:coding-team", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("capabilities.humanInteraction.signOffs ", StringComparison.Ordinal) && line.EndsWith(" environment variable SOF__capabilities__humanInteraction__signOffs__0", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("toolSets.", StringComparison.Ordinal) && line.Contains("code default", StringComparison.Ordinal));
    }

    // INV-04: sof run refuses a write tool with no gate and no exemption, so validate reports it, with the same message.
    [Fact]
    public async Task Validate_reports_a_write_tool_with_no_gate_and_no_exemption()
    {
        sof.Write("sof.json", """
            {
              "agents": { "a": { "instructions": "Write.", "tools": ["all"] } },
              "tools": {
                "save": { "source": "extension:workspace.write_file" },
                "peek": { "source": "extension:workspace.read_file" },
                "exempt": { "source": "extension:workspace.edit_file", "gateExemption": "It changes only the working copy." }
              },
              "toolSets": { "all": ["save", "peek", "exempt"] },
              "capabilities": { "workspace": { "enabled": true } }
            }
            """);

        var (exitCode, output, error) = await sof.RunAsync("config", "validate");

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("valid", output, StringComparison.Ordinal);
        Assert.Equal(
            """
            error: tools.save: write tool has no gate of its own. Add "gates": [...] or "gateExemption": "<reason>".
            1 error.

            """.ReplaceLineEndings("\n"),
            error);
    }

    // The model provider runs its own tools, so sof run does not ask them for a gate; validate agrees.
    [Fact]
    public async Task Validate_does_not_ask_a_provider_tool_for_a_gate()
    {
        sof.Write("sof.json", """
            {
              "agents": { "a": { "instructions": "Search.", "tools": ["all"] } },
              "tools": { "web": { "source": "provider:web_search", "kind": "write", "reason": "The model searches the web itself." } },
              "toolSets": { "all": ["web"] }
            }
            """);

        var (_, _, error) = await sof.RunAsync("config", "validate");

        Assert.DoesNotContain("no gate", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_accepts_a_valid_configuration()
    {
        sof.Write("sof.json", Smallest);

        var (exitCode, output, error) = await sof.RunAsync("config", "validate");

        Assert.Equal(0, exitCode);
        Assert.Equal("The configuration is valid.\n", output);
        Assert.Empty(error);
    }

    [Fact]
    public async Task Validate_lists_every_error_with_where_it_was_set_and_fails()
    {
        sof.Write("sof.json", """
            {
              "agents": { "a": { "instructions": "Hi {{caller.id}}", "model": "strong" } }
            }
            """);

        var (exitCode, _, error) = await sof.RunAsync("config", "validate", "--budget", "0");

        Assert.Equal(1, exitCode);
        Assert.Equal(
            """
            error: sof.json: agents.a.model: model profile "strong" does not exist. Add it to models, or use one of: default.
            error: sof.json: agents.a.instructions: placeholder {{caller.id}} is not allowed in instructions. Instructions are the same for every call, so they cannot use caller, work or time values.
            error: option --budget: run.budget.cost: must be greater than zero. A limit can be high, but never zero, negative or unlimited.
            3 errors.

            """.ReplaceLineEndings("\n"),
            error);
    }

    // CFG-06: validate reports what sof run refuses as it starts: an extension sof does not register, and a feature the model's
    // provider does not have for it (MDL-06).
    [Fact]
    public async Task Validate_reports_what_sof_run_refuses_as_it_starts()
    {
        sof.Write("sof.json", """
            {
              "providers": { "claude": { "features": { "refusalFallback": true } } },
              "models": { "default": { "model": "claude-haiku-4-5" } },
              "agents": { "a": { "instructions": "Look things up.", "tools": ["all"] } },
              "tools": { "lookup": { "source": "extension:acme.lookup", "gates": ["acme"] } },
              "toolSets": { "all": ["lookup"] },
              "gates": { "acme": { "use": "extension:acme.gate" } }
            }
            """);

        var (exitCode, _, error) = await sof.RunAsync("config", "validate");

        Assert.Equal(1, exitCode);
        Assert.Equal(
            """
            error: tools.lookup.source: tool extension "acme.lookup" is not one sof registers. sof registers the workspace.* and sandbox.* tools and the sandbox.commandRules gate, and runs command checks; use one of them, a built-in, or a tool server.
            error: gates.acme.use: gate extension "acme.gate" is not one sof registers. sof registers the workspace.* and sandbox.* tools and the sandbox.commandRules gate, and runs command checks; use one of them, a built-in, or a tool server.
            error: providers.claude.features.refusalFallback: model "claude-haiku-4-5" of agent "a" does not have the feature. Switch it off, or use a model that has it.
            3 errors.

            """.ReplaceLineEndings("\n"),
            error);
    }

    // CFG-06: a model whose provider this build has not is refused by sof run, so validate reports it too.
    [Fact]
    public async Task Validate_reports_a_model_whose_provider_is_not_in_this_build()
    {
        sof.Write("sof.json", """
            {
              "providers": { "other": { "prices": { "x": { "input": 1, "output": 2 } } } },
              "models": { "default": { "provider": "other", "model": "x" } },
              "agents": { "a": { "instructions": "Answer." } }
            }
            """);

        var (exitCode, _, error) = await sof.RunAsync("config", "validate");

        Assert.Equal(1, exitCode);
        Assert.StartsWith("error: models.default.provider: provider \"other\" is not available in this build of sof. Use one it has: claude.", error, StringComparison.Ordinal);
    }

    // CFG-06, TASK-09: the task's budget was its cost, a number; it is an object now, and a number is not silently ignored.
    [Fact]
    public async Task Validate_reports_a_task_budget_written_as_a_number()
    {
        sof.Write("sof.json", """
            {
              "agents": { "a": { "instructions": "Answer." } },
              "capabilities": { "taskBoard": { "enabled": true, "budget": 3 } }
            }
            """);

        var (exitCode, _, error) = await sof.RunAsync("config", "validate");

        Assert.Equal(1, exitCode);
        Assert.Contains("error: sof.json: capabilities.taskBoard.budget: is a number, but it is now an object. Write { \"cost\": 3 }.", error, StringComparison.Ordinal);
    }

    // CFG-12.
    [Fact]
    public async Task Dry_run_shows_the_configuration_and_runs_the_agent_against_scripted_replies()
    {
        sof.Write("sof.json", Smallest);

        var (exitCode, output, error) = await sof.RunAsync("config", "dry-run", "--input", "Invoice A-17", "--reply", """{"total":42}""");

        Assert.Equal((0, ""), (exitCode, error));
        Assert.Contains("project.name", output, StringComparison.Ordinal);
        Assert.EndsWith("\nextractor: Completed\n{\"total\":42}\n", output, StringComparison.Ordinal);
    }

    // CFG-12: a configuration with the workspace's and the sandbox's tools, a command gate and a command check dry-runs too, over
    // files in memory and a sandbox that runs nothing.
    [Fact]
    public async Task Dry_run_runs_a_configuration_with_the_workspace_and_the_sandbox()
    {
        sof.Write("sof.json", """
            {
              "agents": { "dev": { "instructions": "Develop.", "tools": ["code"] } },
              "tools": {
                "write_file": { "source": "extension:workspace.write_file", "gateExemption": "It changes only the working copy." },
                "run_command": { "source": "extension:sandbox.run", "gates": ["commands"] }
              },
              "toolSets": { "code": ["write_file", "run_command"] },
              "gates": { "commands": { "use": "extension:sandbox.commandRules" } },
              "checks": { "build": { "command": "dotnet build" } },
              "capabilities": { "workspace": { "enabled": true, "baselineChecks": ["build"] }, "sandbox": { "enabled": true } }
            }
            """);

        var (exitCode, output, error) = await sof.RunAsync("config", "dry-run", "--input", "Fix it.", "--reply", "Done.");

        Assert.Equal((0, ""), (exitCode, error));
        Assert.EndsWith("\ndev: Completed\nDone.\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dry_run_runs_nothing_on_an_invalid_configuration()
    {
        sof.Write("sof.json", Smallest);

        var (exitCode, output, _) = await sof.RunAsync("config", "dry-run", "--budget", "0", "--reply", "never");

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("extractor:", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("config", "explode")]
    [InlineData("config", "show", "--colour")]
    [InlineData("config", "show", "--dir")]
    [InlineData("config", "show", "--agent", "a")]
    [InlineData("config", "dry-run")]
    [InlineData("deploy")]
    public async Task A_wrong_command_line_is_a_usage_error(params string[] args) => Assert.Equal(2, (await sof.RunAsync(args)).ExitCode);

    [Fact]
    public async Task Version_prints_the_version() => Assert.StartsWith("0.1.0", (await sof.RunAsync("--version")).Output, StringComparison.Ordinal);

    [GeneratedRegex(" {2,}")]
    private static partial Regex Spaces();
}
