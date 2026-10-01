using System.Text.RegularExpressions;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary><c>sof config show | validate | dry-run</c> (CFG-04, CFG-06, CFG-12, TEST-05).</summary>
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
    public async Task Show_with_origin_names_the_layer_of_every_value()
    {
        sof.Write("sof.json", Smallest).Write("sof.prod.json", """{ "run": { "budget": { "time": "2h" } } }""");
        sof.Variables["SOF_ENVIRONMENT"] = "prod";
        sof.Variables["SOF__run__permissionMode"] = "auto";

        var (exitCode, output, _) = await sof.RunAsync("config", "show", "--origin", "--budget", "40");

        Assert.Equal(0, exitCode);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => Spaces().Replace(line, " ")).ToArray();
        Assert.Contains("project.name \"invoice-api\" application file sof.json:2:24", lines);
        Assert.Contains("run.budget.cost 40 run option --budget", lines);
        Assert.Contains("run.budget.time \"2h\" environment file sof.prod.json:1:32", lines);
        Assert.Contains("run.permissionMode \"auto\" environment variable SOF__run__permissionMode", lines);
        Assert.Contains("models.default.model \"claude-opus-5-5\" code default, core 0.1.0", lines);
        Assert.All(lines, line => Assert.Matches(@" (code default, core \d|application file|environment file|environment variable|run option)", line));
    }

    [Fact]
    public async Task Show_for_an_agent_lists_only_what_applies_to_it()
    {
        sof.Write("sof.json", """{ "agents": { "a": { "instructions": "x" }, "b": { "instructions": "y" } } }""");

        var (exitCode, output, _) = await sof.RunAsync("config", "show", "--agent", "a");

        Assert.Equal(0, exitCode);
        Assert.Contains("agents.a.instructions", output, StringComparison.Ordinal);
        Assert.DoesNotContain("agents.b", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_for_an_unknown_agent_is_a_usage_error()
    {
        sof.Write("sof.json", Smallest);

        var (exitCode, _, error) = await sof.RunAsync("config", "show", "--agent", "nobody");

        Assert.Equal(2, exitCode);
        Assert.Contains("There is no agent \"nobody\". Agents: extractor.", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_accepts_a_valid_configuration()
    {
        sof.Write("sof.json", Smallest);

        var (exitCode, output, error) = await sof.RunAsync("config", "validate");

        Assert.Equal(0, exitCode);
        Assert.Equal("The configuration is valid (code defaults, application file sof.json).\n", output);
        Assert.Empty(error);
    }

    [Fact]
    public async Task Validate_lists_every_error_with_its_position_and_fails()
    {
        sof.Write("sof.json", """
            {
              "agents": { "a": { "instructions": "Hi {{caller.id}}", "model": "strong" } },
              "run": { "budget": { "cost": 0 } }
            }
            """);

        var (exitCode, _, error) = await sof.RunAsync("config", "validate", "--set", "run.permissionMode=sometimes");

        Assert.Equal(1, exitCode);
        Assert.Equal(
            """
            error: --set run.permissionMode: run.permissionMode: is text, but must be one of "ask", "auto", "readOnly". Write one of "ask", "auto", "readOnly", such as "ask".
            error: sof.json:2:67: agents.a.model: model profile "strong" does not exist. Add it to models, or use one of: default.
            error: sof.json:2:38: agents.a.instructions: placeholder {{caller.id}} is not allowed in the stable prefix. Move it to context.operatingFacts (CTX-02, CFG-14).
            error: sof.json:3:32: run.budget.cost: is 0, but must be greater than 0. A limit can be high, but never zero, negative or unlimited (INV-07).
            4 errors.

            """.ReplaceLineEndings("\n"),
            error);
    }

    [Fact]
    public async Task Validate_without_a_file_uses_the_code_defaults()
    {
        var (exitCode, output, _) = await sof.RunAsync("config", "validate");

        Assert.Equal(0, exitCode);
        Assert.Contains("code defaults only; no sof.json found", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dry_run_runs_the_agent_against_a_scripted_model()
    {
        sof.Write("sof.json", Smallest);

        var (exitCode, output, _) = await sof.RunAsync("config", "dry-run", "--input", "Invoice A-17", "--reply", """{"total": 12}""");

        Assert.Equal(0, exitCode);
        Assert.Contains("Model call to claude/claude-opus-5-5 (scripted):", output, StringComparison.Ordinal);
        Assert.Contains("    Extract the total for invoice-api. Reply as JSON.", output, StringComparison.Ordinal);
        Assert.Contains("  Input: Invoice A-17", output, StringComparison.Ordinal);
        Assert.Contains("Result: completed\n  Output: {\"total\": 12}", output, StringComparison.Ordinal);
        Assert.Matches(@"Run [0-9a-f-]{36} recorded its configuration \(sha256 [0-9a-f]{12}, core 0\.1\.0\)\.", output);
    }

    [Fact]
    public async Task Dry_run_needs_to_know_which_agent()
    {
        sof.Write("sof.json", """{ "agents": { "a": { "instructions": "x" }, "b": { "instructions": "y" } } }""");

        var (exitCode, _, error) = await sof.RunAsync("config", "dry-run");

        Assert.Equal(2, exitCode);
        Assert.Contains("Choose the agent with --agent: a, b.", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dry_run_does_not_run_an_invalid_configuration()
    {
        sof.Write("sof.json", """{ "agents": { "a": { "instructions": "" } } }""");

        var (exitCode, output, error) = await sof.RunAsync("config", "dry-run");

        Assert.Equal(1, exitCode);
        Assert.Empty(output);
        Assert.Contains("agents.a.instructions: is required but not set.", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("config", "explode")]
    [InlineData("config", "show", "--colour")]
    [InlineData("config", "show", "--set", "nonsense")]
    [InlineData("deploy")]
    public async Task A_wrong_command_line_is_a_usage_error(params string[] args)
    {
        var (exitCode, _, error) = await sof.RunAsync(args);

        Assert.Equal(2, exitCode);
        Assert.StartsWith("error: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Version_prints_the_version()
    {
        var (exitCode, output, _) = await sof.RunAsync("--version");

        Assert.Equal(0, exitCode);
        Assert.StartsWith("0.1.0", output, StringComparison.Ordinal);
    }

    [GeneratedRegex(" {2,}")]
    private static partial Regex Spaces();
}
