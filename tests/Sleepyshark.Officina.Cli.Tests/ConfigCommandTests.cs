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
    public void Show_with_origin_lists_every_setting_with_its_source()
    {
        sof.Write("sof.json", Smallest).Write("sof.prod.json", """{ "run": { "budget": { "time": "02:00:00" } } }""");
        sof.Variables["SOF_ENVIRONMENT"] = "prod";
        sof.Variables["SOF__run__permissionMode"] = "auto";

        var (exitCode, output, _) = sof.Run("config", "show", "--origin", "--budget", "40");

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

    [Fact]
    public void Validate_accepts_a_valid_configuration()
    {
        sof.Write("sof.json", Smallest);

        var (exitCode, output, error) = sof.Run("config", "validate");

        Assert.Equal(0, exitCode);
        Assert.Equal("The configuration is valid.\n", output);
        Assert.Empty(error);
    }

    [Fact]
    public void Validate_lists_every_error_with_where_it_was_set_and_fails()
    {
        sof.Write("sof.json", """
            {
              "agents": { "a": { "instructions": "Hi {{caller.id}}", "model": "strong" } }
            }
            """);

        var (exitCode, _, error) = sof.Run("config", "validate", "--budget", "0");

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

    [Theory]
    [InlineData("config", "explode")]
    [InlineData("config", "show", "--colour")]
    [InlineData("config", "show", "--dir")]
    [InlineData("config", "show", "--agent", "a")]
    [InlineData("deploy")]
    public void A_wrong_command_line_is_a_usage_error(params string[] args) => Assert.Equal(2, sof.Run(args).ExitCode);

    [Fact]
    public void Version_prints_the_version() => Assert.StartsWith("0.1.0", sof.Run("--version").Output, StringComparison.Ordinal);

    [GeneratedRegex(" {2,}")]
    private static partial Regex Spaces();
}
