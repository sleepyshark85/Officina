using System.Text.Json;
using System.Text.RegularExpressions;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>Where <c>sof</c> keeps its storage: <c>operations.storage</c>, <c>.sof/</c> by default (STO-01).</summary>
public sealed partial class LocalStorageTests : IDisposable
{
    private readonly Sof sof = new();
    private readonly ScriptedModelProvider model = new();

    public LocalStorageTests() => sof.Providers["claude"] = model;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => sof.Dispose();

    // STO-01, TOOL-09: the database is where operations.storage.path says, the full text of a trimmed result is a file in the
    // folder artifacts beside it, and sof report reads the run from there.
    [Fact]
    public async Task A_run_keeps_its_database_and_artifact_files_where_operations_storage_says()
    {
        sof.Write("sof.json", Configuration(""" "operations": { "storage": { "path": ".sof/data/run.db" } }, """));
        model.CallTools(("ask", """{ "question": "Which database?" }""")).Reply("Done.");

        var run = sof.RunAsync("run", "--input", "Pick a database.");
        await sof.Out.WaitForAsync("#1 dev asks: Which database?", Ct);
        sof.In.Type("answer 1 Postgres, version 17.");
        var (exitCode, output, _) = await run;

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.True(File.Exists(Path.Combine(sof.Directory, ".sof", "data", "run.db")));
        Assert.False(File.Exists(Path.Combine(sof.Directory, ".sof", "sof.db")));
        Assert.Equal("Postgres, version 17.", await File.ReadAllTextAsync(Path.Combine(sof.Directory, ".sof", "data", "artifacts", "1"), Ct));
        var (reported, report, _) = await sof.RunAsync("report", RunId().Match(output).Groups[1].Value);
        Assert.Equal(ExitCodes.Success, reported);
        Assert.StartsWith($"Run {RunId().Match(output).Groups[1].Value}: Complete", report, StringComparison.Ordinal);
    }

    // STO-01: an absolute path outside the project is the owner's choice, and the artifacts' folder goes with the database.
    [Fact]
    public async Task An_absolute_path_can_lead_out_of_the_project_with_the_artifacts_beside_the_database()
    {
        var outside = Directory.CreateTempSubdirectory("officina-storage-").FullName;
        try
        {
            var database = JsonSerializer.Serialize(Path.Combine(outside, "sof.db"));
            sof.Write("sof.json", Configuration($$""" "operations": { "storage": { "path": {{database}} } }, """));
            model.CallTools(("ask", """{ "question": "Which database?" }""")).Reply("Done.");

            var run = sof.RunAsync("run", "--input", "Pick a database.");
            await sof.Out.WaitForAsync("#1 dev asks: Which database?", Ct);
            sof.In.Type("answer 1 Postgres, version 17.");
            var (exitCode, _, _) = await run;

            Assert.Equal(ExitCodes.Success, exitCode);
            Assert.True(File.Exists(Path.Combine(outside, "sof.db")));
            Assert.True(File.Exists(Path.Combine(outside, "artifacts", "1")));
            Assert.False(Directory.Exists(Path.Combine(sof.Directory, ".sof", "artifacts")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    // STO-01, CFG-06: a path that would put the storage where agents can see it, or that leaves the project by a relative path,
    // is refused by config validate, and so by every command that opens the storage.
    [Theory]
    [InlineData("path", "data/sof.db", "\"data/sof.db\" leads out of .sof/: a relative path must stay in it, where agents cannot see the storage.")]
    [InlineData("path", "../sof.db", "\"../sof.db\" leads out of .sof/: a relative path must stay in it, where agents cannot see the storage.")]
    [InlineData("path", ".sof", "\".sof\" leads out of .sof/: a relative path must stay in it, where agents cannot see the storage.")]
    [InlineData("path", "{project}/data/sof.db", "\"{project}/data/sof.db\" is in the project but not in .sof/, so agents could see the storage.")]
    public async Task Config_validate_refuses_a_storage_path_agents_could_reach_or_that_leaves_the_project_unasked(string setting, string value, string message)
    {
        var path = value.Replace("{project}", sof.Directory, StringComparison.Ordinal);
        sof.Write("sof.json", Configuration($$""" "operations": { "storage": { "{{setting}}": {{JsonSerializer.Serialize(path)}} } }, """));

        var (exitCode, _, error) = await sof.RunAsync("config", "validate");
        var (ran, _, refused) = await sof.RunAsync("run", "--input", "Pick a database.");

        var expected = $"error: sof.json: operations.storage.{setting}: {message.Replace("{project}", sof.Directory, StringComparison.Ordinal)}";
        Assert.Equal(ExitCodes.Invalid, exitCode);
        Assert.StartsWith(expected, error, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Invalid, ran);
        Assert.StartsWith(expected, refused, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(sof.Directory, "data")));
    }

    // STO-01, CFG-06: the database's path is required.
    [Fact]
    public async Task Config_validate_refuses_an_empty_database_path()
    {
        sof.Write("sof.json", Configuration(""" "operations": { "storage": { "path": "" } }, """));

        var (exitCode, _, error) = await sof.RunAsync("config", "validate");

        Assert.Equal(ExitCodes.Invalid, exitCode);
        Assert.Contains("operations.storage.path: is required but not set.", error, StringComparison.Ordinal);
    }

    private static string Configuration(string storage) => $$"""
        {
          {{storage}}
          "agents": { "dev": { "instructions": "Work.", "tools": ["owner"] } },
          "tools": { "ask": { "source": "builtin:human.ask_owner", "maxResultLength": 5 } },
          "toolSets": { "owner": ["ask"] },
          "capabilities": { "humanInteraction": { "enabled": true } }
        }
        """;

    [GeneratedRegex(@"^run (\S+)$", RegexOptions.Multiline)]
    private static partial Regex RunId();
}
