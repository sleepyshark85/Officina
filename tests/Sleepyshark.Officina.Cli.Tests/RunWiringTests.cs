using System.Text.Json;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>
/// <c>sof run</c> connects the tool servers, registers the Claude provider, and gives each agent a working copy and
/// sandbox tools when their capabilities are on (SBX-03, SBX-07, CAP-02, WS-02, WS-09, INV-06).
/// </summary>
public sealed class RunWiringTests : IDisposable
{
    private const string Workspace = """
        {
          "run": { "permissionMode": "auto" },
          "agents": { "dev": { "instructions": "Work.", "tools": ["all"] } },
          "tools": {
            "write": { "source": "extension:workspace.write_file", "gateExemption": "Writes only to the agent's working copy.", "approval": "always" },
            "read": { "source": "extension:workspace.read_file" },
            "run": { "source": "extension:sandbox.run", "gates": ["commands"] },
            "watch": { "source": "extension:sandbox.start_process", "gates": ["commands"] }
          },
          "gates": { "commands": { "use": "extension:sandbox.commandRules" } },
          "toolSets": { "all": ["write", "read", "run", "watch"] },
          "capabilities": {
            "workspace": { "enabled": true },
            "sandbox": { "enabled": true, "commandRules": [{ "match": "dotnet*", "action": "allow" }] }
          }
        }
        """;

    private readonly Sof sof = new();
    private readonly ScriptedModelProvider model = new();

    public RunWiringTests() => sof.Providers["claude"] = model;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => sof.Dispose();

    [Fact]
    public async Task Each_agent_works_in_its_own_working_copy_with_its_own_sandbox_tools_which_end_with_the_run()
    {
        sof.Write("sof.json", Workspace).Commit();
        sof.Sandbox = new FakeSandbox().Reply("built", 0).Reply("watching", exitCode: null);
        model.CallTools(("write", """{ "path": "hello.txt", "content": "hi" }"""))
            .CallTools(("read", """{ "path": "hello.txt" }"""), ("run", """{ "command": "dotnet build" }"""), ("watch", """{ "command": "dotnet watch" }"""))
            .Reply("Done.");

        var run = sof.RunAsync("run", "--input", "Greet.");
        await sof.Out.WaitForAsync("#1 dev asks to run write", Ct);
        sof.In.Type("status");
        await sof.Out.WaitForAsync("integration queue: 0 waiting, longest wait 00:00:00", Ct); // WS-09
        sof.In.Type("approve 1");
        var (exitCode, output, error) = await run;

        Assert.Equal((ExitCodes.Success, ""), (exitCode, error));
        Assert.Contains("Done.\n\nRun ", output, StringComparison.Ordinal);
        var results = model.Requests[^1].History.SelectMany(message => message.Content).OfType<ToolResultContent>().Select(content => content.Text).ToList();
        Assert.Contains(results, text => text.Contains("hi", StringComparison.Ordinal));
        Assert.Contains(results, text => text.Contains("built\n[exit code 0]", StringComparison.Ordinal));
        var processes = sof.Sandbox.Processes;
        var folder = Path.GetDirectoryName(processes[0].Command.Directory)!;
        Assert.Equal(Path.Combine(sof.Directory, ".sof", "worktrees"), folder);
        Assert.EndsWith("-dev", processes[0].Command.Directory, StringComparison.Ordinal);
        Assert.True(processes[1].Stopped); // SBX-03: the background process ends with the run
        Assert.False(Directory.Exists(processes[0].Command.Directory));
        Assert.Equal([processes[0].Command.Directory], sof.Sandbox.Released); // what the sandbox left outside the copy goes with it
        Assert.False(File.Exists(Path.Combine(sof.Directory, "hello.txt"))); // nothing reaches the baseline until integration
    }

    // SBX-05, WS-01: an agent of a team works in its task's working copy, and its commands get its role's secrets, by its definition.
    [Fact]
    public async Task An_agent_of_a_team_runs_commands_in_its_tasks_copy_with_its_roles_secrets()
    {
        sof.Write("sof.json", """
            {
              "run": { "permissionMode": "auto" },
              "agents": {
                "team": { "instructions": "A team.", "pattern": { "type": "team", "lead": "lead", "roles": { "developer": { "max": 2 } } } },
                "lead": { "instructions": "Lead.", "tools": ["planning"] },
                "developer": { "instructions": "Develop.", "tools": ["work"] }
              },
              "tools": {
                "create": { "source": "builtin:tasks.create" },
                "submit": { "source": "builtin:tasks.submit_for_review" },
                "run": { "source": "extension:sandbox.run", "gates": ["commands"] }
              },
              "gates": { "commands": { "use": "extension:sandbox.commandRules" } },
              "toolSets": { "planning": ["create"], "work": ["run", "submit"] },
              "capabilities": {
                "taskBoard": { "enabled": true }, "team": { "enabled": true }, "workspace": { "enabled": true },
                "sandbox": { "enabled": true, "commandRules": [{ "match": "dotnet*", "action": "allow" }], "secrets": { "developer": ["NUGET_TOKEN"] } }
              }
            }
            """).Commit();
        sof.Variables["NUGET_TOKEN"] = "s3cret";
        sof.Sandbox = new FakeSandbox().Reply("built", 0);
        model.When(request => ScriptedModelProvider.WorkOf(request).StartsWith("You lead a team", StringComparison.Ordinal))
            .CallTools(("create", """{ "id": "a", "title": "Build", "reason": "plan" }""")).Reply("Planned.");
        model.When(request => ScriptedModelProvider.WorkOf(request).StartsWith("Every task is done", StringComparison.Ordinal)).Reply("Built.");
        model.When(request => ScriptedModelProvider.WorkOf(request).Contains("Do task a,", StringComparison.Ordinal))
            .CallTools(("run", """{ "command": "dotnet build" }""")).CallTools(("submit", """{ "id": "a" }""")).Reply("Submitted.");

        var (exitCode, _, error) = await sof.RunAsync("run", "--agent", "team", "--input", "Build it.");

        Assert.Equal((ExitCodes.Success, ""), (exitCode, error));
        var command = Assert.Single(sof.Sandbox.Processes).Command;
        Assert.EndsWith("-task.a", command.Directory, StringComparison.Ordinal);
        Assert.Equal("s3cret", command.Environment["NUGET_TOKEN"]);
        Assert.False(Directory.Exists(command.Directory)); // the task's copy went when it was done
    }

    // SBX-03, TASK-05: a background process stops when its task is submitted, before the checks run, so it cannot change the copy
    // after them; and an agent runs at most four at once.
    [Fact]
    public async Task Background_processes_stop_when_the_task_is_submitted_and_are_capped()
    {
        sof.Write("sof.json", """
            {
              "run": { "permissionMode": "auto" },
              "agents": {
                "team": { "instructions": "A team.", "pattern": { "type": "team", "lead": "lead", "roles": { "developer": { "max": 1 } } } },
                "lead": { "instructions": "Lead.", "tools": ["planning"] },
                "developer": { "instructions": "Develop.", "tools": ["work"] }
              },
              "tools": {
                "create": { "source": "builtin:tasks.create" },
                "submit": { "source": "builtin:tasks.submit_for_review" },
                "start": { "source": "extension:sandbox.start_process", "gates": ["commands"] }
              },
              "gates": { "commands": { "use": "extension:sandbox.commandRules" } },
              "toolSets": { "planning": ["create"], "work": ["start", "submit"] },
              "checks": { "tests": { "command": "dotnet test" } },
              "capabilities": {
                "taskBoard": { "enabled": true }, "team": { "enabled": true }, "workspace": { "enabled": true },
                "sandbox": { "enabled": true, "commandRules": [{ "match": "*", "action": "allow" }] }
              }
            }
            """).Commit();
        bool? stoppedBeforeChecks = null;
        FakeSandbox sandbox = null!;
        sandbox = new FakeSandbox
        {
            Answer = command =>
            {
                if (command.CommandLine == "dotnet test")
                {
                    stoppedBeforeChecks ??= sandbox.Processes.Where(process => process.Command.CommandLine.StartsWith("sleep", StringComparison.Ordinal)).All(process => process.Stopped);
                    return ("passed", 0);
                }

                return ("", null); // runs until it is stopped
            },
        };
        sof.Sandbox = sandbox;
        model.When(request => ScriptedModelProvider.WorkOf(request).StartsWith("You lead a team", StringComparison.Ordinal))
            .CallTools(("create", """{ "id": "a", "title": "Build", "checks": ["tests"], "reason": "plan" }""")).Reply("Planned.");
        model.When(request => ScriptedModelProvider.WorkOf(request).StartsWith("Every task is done", StringComparison.Ordinal)).Reply("Built.");
        model.When(request => ScriptedModelProvider.WorkOf(request).Contains("Do task a,", StringComparison.Ordinal))
            .CallTools([.. Enumerable.Range(1, 5).Select(n => ("start", $$"""{ "command": "sleep 300; echo planted >> src/{{n}}.cs" }"""))])
            .CallTools(("submit", """{ "id": "a" }""")).CallTools(("start", """{ "command": "sleep 1; echo planted >> src/late.cs" }""")).Reply("Submitted.");

        var (exitCode, _, error) = await sof.RunAsync("run", "--agent", "team", "--input", "Build it.");

        Assert.Equal((ExitCodes.Success, ""), (exitCode, error));
        Assert.True(stoppedBeforeChecks);
        Assert.DoesNotContain(sandbox.Processes, process => process.Command.CommandLine.Contains("late.cs", StringComparison.Ordinal)); // refused after submit
        Assert.Equal(4, sandbox.Processes.Count(process => process.Command.CommandLine.StartsWith("sleep", StringComparison.Ordinal)));
        var results = model.Requests.SelectMany(request => request.History).SelectMany(message => message.Content).OfType<ToolResultContent>().Select(content => content.Text);
        Assert.Contains(results, text => text.Contains("4 background processes already run; stop one with stop_process first", StringComparison.Ordinal));
        Assert.Contains(results, text => text.Contains("task a is InReview, so no command runs in its working copy until it is in progress again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_machine_that_cannot_sandbox_commands_is_reported_and_nothing_runs()
    {
        sof.Write("sof.json", Workspace).Commit();
        sof.Sandbox = new FakeSandbox { Problem = "bubblewrap is not installed." };

        var (exitCode, _, error) = await sof.RunAsync("run", "--input", "Go.");

        Assert.Equal((ExitCodes.Invalid, "error: commands cannot run, because this machine cannot sandbox them: bubblewrap is not installed.\n"), (exitCode, error));
        Assert.Empty(model.Requests);
    }

    [Theory]
    [InlineData("workspace", "tools.read.source")]
    [InlineData("sandbox", "tools.run.source")]
    public async Task A_tool_of_a_capability_that_is_off_is_an_error(string capability, string path)
    {
        // The sandbox needs the workspace, so turning the workspace off turns the sandbox off too.
        var configuration = Workspace.Replace("\"sandbox\": { \"enabled\": true", "\"sandbox\": { \"enabled\": false", StringComparison.Ordinal);
        sof.Write("sof.json", capability == "workspace" ? configuration.Replace("\"workspace\": { \"enabled\": true", "\"workspace\": { \"enabled\": false", StringComparison.Ordinal) : configuration);

        var (exitCode, _, error) = await sof.RunAsync("run", "--input", "Go.");

        Assert.Equal(ExitCodes.Invalid, exitCode);
        Assert.Contains($"{path}: needs the {capability} capability, which is off.", error, StringComparison.Ordinal);

        // CFG-06: validate reports it too, before anything runs.
        var (validateCode, _, validateError) = await sof.RunAsync("config", "validate");
        Assert.Equal(ExitCodes.Invalid, validateCode);
        Assert.Contains($"{path}: needs the {capability} capability, which is off.", validateError, StringComparison.Ordinal);
    }

    // WS-01: agents of one run work apart, and an agent whose name is not fit for a branch still gets a copy.
    [Fact]
    public async Task Two_agents_of_one_run_each_work_in_their_own_working_copy()
    {
        sof.Write("sof.json", """
            {
              "run": { "permissionMode": "auto" },
              "agents": {
                "release": { "instructions": "Run both.", "pattern": { "type": "workflow", "steps": [{ "id": "one", "agent": "alice" }, { "id": "two", "agent": "bob smith" }] } },
                "alice": { "instructions": "Write.", "tools": ["files"] },
                "bob smith": { "instructions": "Read.", "tools": ["files"] }
              },
              "tools": {
                "write": { "source": "extension:workspace.write_file", "gateExemption": "Writes only to the agent's working copy." },
                "read": { "source": "extension:workspace.read_file" }
              },
              "toolSets": { "files": ["write", "read"] },
              "capabilities": { "workspace": { "enabled": true } }
            }
            """).Commit();
        model.CallTools(("write", """{ "path": "a.txt", "content": "alice" }""")).Reply("Wrote.")
            .CallTools(("read", """{ "path": "a.txt" }""")).Reply("Read.").Reply("Done.");

        var (exitCode, _, error) = await sof.RunAsync("run", "--agent", "release", "--input", "Go.");

        Assert.Equal((ExitCodes.Success, ""), (exitCode, error));
        var results = model.Requests.SelectMany(request => request.History).SelectMany(message => message.Content).OfType<ToolResultContent>().Select(content => content.Text);
        Assert.Contains(results, text => text.Contains("a.txt does not exist.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tool_servers_tools_are_offered_and_the_secrets_it_was_given_are_removed_from_its_results()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var program = Path.Combine(AppContext.BaseDirectory, "Sleepyshark.Officina.Mcp.TestServer.dll");
        sof.Write("sof.json", $$"""
            {
              "agents": { "dev": { "instructions": "Work.", "tools": ["all"] } },
              "toolServers": { "ref": { "command": {{JsonSerializer.Serialize(host)}}, "args": [{{JsonSerializer.Serialize(program)}}], "env": { "MCP_TEST_TOKEN": { "secret": "TOKEN" } } } },
              "tools": { "echo": { "source": "mcp:ref/echo", "kind": "read" } },
              "toolSets": { "all": ["echo"] }
            }
            """);
        sof.Variables["TOKEN"] = "s3cret";
        model.CallTools(("echo", """{ "text": "the token is s3cret" }""")).Reply("Done.");

        var (exitCode, _, error) = await sof.RunAsync("run", "--input", "Echo.");

        Assert.Equal((ExitCodes.Success, ""), (exitCode, error));
        var result = Assert.IsType<ToolResultContent>(model.Requests[^1].History[^1].Content[0]);
        Assert.Contains("the token is [secret]", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tool_server_whose_secret_is_not_set_ends_the_command_with_the_reason()
    {
        sof.Write("sof.json", """
            {
              "agents": { "dev": { "instructions": "Work." } },
              "toolServers": { "ref": { "command": "dotnet", "args": ["missing.dll"], "env": { "MCP_TEST_TOKEN": { "secret": "TOKEN" } } } }
            }
            """);

        var (exitCode, _, error) = await sof.RunAsync("run", "--input", "Go.");

        Assert.Equal((ExitCodes.Invalid, "error: The secret TOKEN is not set: set the environment variable TOKEN.\n"), (exitCode, error));
    }

    // CFG-09: the key is a secret the configuration names, read from the secret source when it is used.
    [Fact]
    public async Task The_claude_provider_is_registered_without_the_host_and_reads_its_key_from_the_environment()
    {
        sof.Write("sof.json", """{ "agents": { "dev": { "instructions": "Work." } } }""");
        sof.Providers.Clear();

        var (exitCode, output, _) = await sof.RunAsync("run", "--input", "Go.");

        Assert.Equal(ExitCodes.NotCompleted, exitCode); // no ANTHROPIC_API_KEY is set, so the first call fails without reaching the network
        Assert.Contains("dev: HandedOff (ProviderFail", output, StringComparison.Ordinal);
    }
}
