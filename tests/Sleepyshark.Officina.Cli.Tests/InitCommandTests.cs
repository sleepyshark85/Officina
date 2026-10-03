using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>
/// CFG-17: <c>sof init</c> writes the coding team's <c>sof.json</c>, which needs only the project's build and test commands. It
/// detects them where it can, asks the owner to confirm or edit each, and validates what it wrote. Real folders and files;
/// only the console is a stand-in.
/// </summary>
public sealed class InitCommandTests : IDisposable
{
    private static readonly string Python = OperatingSystem.IsWindows() ? "python" : "python3";
    private static readonly (string, CommandAction)[] Rules = [("git push*", CommandAction.Deny), ("git remote*", CommandAction.Deny), ("*", CommandAction.Allow)];

    private readonly Sof sof = new();

    public InitCommandTests() => Directory.CreateDirectory(Path.Combine(sof.Directory, ".git"));

    public void Dispose() => sof.Dispose();

    public static TheoryData<string[], string, string, string[]> Projects => new()
    {
        { ["App.slnx", "src/App.csproj"], "dotnet build", "dotnet test", ["api.nuget.org", "*.nuget.org"] },
        { ["App.sln", "App.slnx"], "dotnet build App.sln", "dotnet test App.sln", ["api.nuget.org", "*.nuget.org"] },
        { ["package.json"], "npm run build", "npm test", ["registry.npmjs.org"] },
        { ["package.json", "package-lock.json"], "npm ci && npm run build", "npm test", ["registry.npmjs.org"] },
        { ["package.json", "pnpm-lock.yaml"], "pnpm install --frozen-lockfile && pnpm run build", "pnpm test", ["registry.npmjs.org"] },
        { ["package.json", "yarn.lock"], "yarn install --frozen-lockfile && yarn run build", "yarn test", ["registry.yarnpkg.com", "registry.npmjs.org"] },
        { ["package.json", "yarn.lock", ".yarnrc.yml"], "yarn install --immutable && yarn run build", "yarn test", ["registry.yarnpkg.com", "registry.npmjs.org"] },
        { ["pyproject.toml"], $"{Python} -m compileall -q .", $"{Python} -m unittest", [] },
        { ["pyproject.toml", "pytest.ini"], $"{Python} -m compileall -q .", $"{Python} -m pytest", [] },
        { ["test_calc.py"], $"{Python} -m compileall -q .", $"{Python} -m unittest", [] },
        { ["Cargo.toml"], "cargo build", "cargo test", ["crates.io", "index.crates.io", "static.crates.io"] },
        { ["go.mod"], "go build ./...", "go test ./...", ["proxy.golang.org", "sum.golang.org"] },
        { ["Makefile"], "make build", "make test", [] },
        // The most certain first: a .NET solution with a Makefile beside it is a .NET project.
        { ["Makefile", "App.slnx"], "dotnet build", "dotnet test", ["api.nuget.org", "*.nuget.org"] },
    };

    // CFG-17: each detector finds the commands; with the input at its end, the suggestions are taken, and the file validates.
    // The rules are the preset's, written out: every command but git push and git remote, whatever the commands are.
    [Theory]
    [MemberData(nameof(Projects))]
    public async Task Each_toolchain_s_commands_are_detected_and_the_file_written_validates(
        string[] files, string build, string test, string[] hosts)
    {
        foreach (var file in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(sof.Directory, file))!);
            sof.Write(file, Content(file));
        }

        sof.In.Dispose(); // the end of the input: each suggestion is taken

        var (exitCode, output, error) = await sof.EndedAsync(sof.RunAsync("init"));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("", error);
        Assert.Contains("The configuration is valid.", output, StringComparison.Ordinal);
        Assert.Contains($"Build command [{build}]: \n", output, StringComparison.Ordinal);
        var sandbox = Load().Capabilities.Sandbox;
        Assert.Equal((build, test), Commands());
        Assert.Equal(hosts, sandbox.AllowedHosts);
        Assert.Equal(Rules, sandbox.CommandRules.Select(rule => (rule.Match, rule.Action)));
        Assert.Empty(sandbox.Toolchains);
    }

    // CFG-17: the owner confirms one suggestion with an empty line and edits the other; declining, .gitignore stays as it was.
    [Fact]
    public async Task The_owner_confirms_or_edits_each_command_at_the_console()
    {
        sof.Write("App.slnx", "<Solution />");
        sof.In.Type("");
        sof.In.Type("dotnet test --no-build");
        sof.In.Type("n");

        var (exitCode, output, error) = await sof.EndedAsync(sof.RunAsync("init"));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("", error);
        Assert.Contains("Found .NET (App.slnx): dotnet build and dotnet test.", output, StringComparison.Ordinal);
        Assert.Contains("Build command [dotnet build]: \nTest command [dotnet test]: \n", output, StringComparison.Ordinal);
        Assert.Equal(("dotnet build", "dotnet test --no-build"), Commands());
        Assert.Equal(Rules, Load().Capabilities.Sandbox.CommandRules.Select(rule => (rule.Match, rule.Action)));
        Assert.False(File.Exists(Path.Combine(sof.Directory, ".gitignore")));
        Assert.Contains("note: add .sof/ to .gitignore yourself", output, StringComparison.Ordinal);
    }

    // CFG-17: with several projects and no solution, sof init says so and asks, rather than taking one of them.
    [Fact]
    public async Task With_several_projects_and_no_solution_it_asks()
    {
        sof.Write("App.csproj", "<Project />").Write("App.Tests.csproj", "<Project />");
        sof.In.Type("dotnet build App.csproj");
        sof.In.Type("dotnet test App.Tests.csproj");
        sof.In.Type("n");

        var (exitCode, output, _) = await sof.EndedAsync(sof.RunAsync("init"));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Found .NET (App.Tests.csproj, App.csproj: several projects and no solution, so name one in each command).", output, StringComparison.Ordinal);
        Assert.Contains("Build command: \n", output, StringComparison.Ordinal);
        Assert.Equal(("dotnet build App.csproj", "dotnet test App.Tests.csproj"), Commands());
        Assert.Equal(["api.nuget.org", "*.nuget.org"], Load().Capabilities.Sandbox.AllowedHosts);
    }

    // CFG-17: for scripts, --build and --test give the commands; nothing is detected or asked, and the console is never read.
    [Fact]
    public async Task With_build_and_test_options_nothing_is_detected_or_asked()
    {
        sof.Write("go.mod", "module calc").Write("package.json", "{ not json");

        var (exitCode, output, error) = await sof.EndedAsync(sof.RunAsync("init", "--build", "make all && make lint", "--test", "make check"));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("", error);
        Assert.DoesNotContain("Found", output, StringComparison.Ordinal);
        Assert.DoesNotContain("command [", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[Y/n]", output, StringComparison.Ordinal);
        Assert.Contains("note: add .sof/ to .gitignore: runs are stored there.", output, StringComparison.Ordinal);
        Assert.Equal(("make all && make lint", "make check"), Commands());
        var sandbox = Load().Capabilities.Sandbox;
        Assert.Equal(Rules, sandbox.CommandRules.Select(rule => (rule.Match, rule.Action)));
        Assert.Empty(sandbox.AllowedHosts); // make needs no registry, whatever go.mod says
    }

    // CFG-17: a package.json that cannot be read, or is not an object, is skipped with a note, and the next detector is used.
    [Theory]
    [InlineData("{ \"scripts\": ", "package.json cannot be read")]
    [InlineData("[1, 2]", "package.json is not a JSON object")]
    public async Task A_package_json_that_cannot_be_read_is_skipped_with_a_note(string package, string note)
    {
        sof.Write("package.json", package).Write("Makefile", Content("Makefile"));
        sof.In.Dispose();

        var (exitCode, output, _) = await sof.EndedAsync(sof.RunAsync("init"));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains($"note: {note}", output, StringComparison.Ordinal);
        Assert.Contains("Found make (Makefile with build and test targets): make build and make test.", output, StringComparison.Ordinal);
        Assert.Equal(("make build", "make test"), Commands());
    }

    // CFG-17: with nothing detected, the owner is asked; an empty line asks again. Agreeing adds .sof/ to .gitignore.
    [Fact]
    public async Task With_nothing_detected_the_owner_is_asked_and_gitignore_gets_the_state_folder()
    {
        sof.Write(".gitignore", "bin/");
        sof.In.Type("");
        sof.In.Type("./build.sh");
        sof.In.Type("./test.sh");
        sof.In.Type("y");

        var (exitCode, output, _) = await sof.EndedAsync(sof.RunAsync("init"));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Found no build or test commands to suggest.", output, StringComparison.Ordinal);
        Assert.Contains("Build command: \nBuild command: \nTest command: \n", output, StringComparison.Ordinal);
        Assert.Equal(("./build.sh", "./test.sh"), Commands());
        Assert.Equal("bin/\n.sof/\n", File.ReadAllText(Path.Combine(sof.Directory, ".gitignore")));
        Assert.Contains("The configuration is valid.", output, StringComparison.Ordinal);
    }

    // CFG-17: with nothing detected, no options and no input to ask, sof init says what to give, and writes nothing.
    [Fact]
    public async Task With_nothing_detected_and_no_input_it_fails_saying_what_to_give()
    {
        sof.In.Dispose();

        var (exitCode, _, error) = await sof.EndedAsync(sof.RunAsync("init"));

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("error: no build command, and the input ended. Give both with --build and --test.", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(sof.Directory, "sof.json")));
    }

    // CFG-17: an existing sof.json is the owner's, so it is replaced only with --force, which says it keeps no backup.
    [Fact]
    public async Task An_existing_sof_json_is_replaced_only_with_force()
    {
        sof.Write("sof.json", """{ "agents": { "mine": { "instructions": "Mine." } } }""");

        var (refused, _, error) = await sof.EndedAsync(sof.RunAsync("init", "--build", "make", "--test", "make test"));
        Assert.Equal(ExitCodes.Usage, refused);
        Assert.Contains("exists already, and sof init does not replace it. Give --force to replace it.", error, StringComparison.Ordinal);
        Assert.Contains("mine", File.ReadAllText(Path.Combine(sof.Directory, "sof.json")), StringComparison.Ordinal);

        var (replaced, output, _) = await sof.EndedAsync(sof.RunAsync("init", "--build", "make build", "--test", "make test", "--force"));
        Assert.Equal(ExitCodes.Success, replaced);
        Assert.Contains("sof.json, with no backup: git has the old one if you committed it.", output, StringComparison.Ordinal);
        Assert.Equal(("make build", "make test"), Commands());
    }

    // CFG-17: sof works in a git repository's top folder, so sof init warns elsewhere, and still writes the file.
    [Fact]
    public async Task Outside_a_repository_s_top_folder_it_warns()
    {
        var (_, _, top) = await sof.EndedAsync(sof.RunAsync("init", "--build", "make build", "--test", "make test"));
        Assert.DoesNotContain("warning:", top, StringComparison.Ordinal);

        var sub = Directory.CreateDirectory(Path.Combine(sof.Directory, "sub")).FullName;
        var (inSub, _, subfolder) = await sof.EndedAsync(sof.RunAsync("init", "--dir", "sub", "--build", "make build", "--test", "make test"));
        Assert.Equal(ExitCodes.Success, inSub);
        Assert.Contains($"warning: {sub} is a subfolder of the git repository {sof.Directory}, and sof needs sof.json in the repository's top folder.", subfolder, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(sub, "sof.json")));

        Directory.Delete(Path.Combine(sof.Directory, ".git"));
        File.Delete(Path.Combine(sof.Directory, "sof.json"));
        var (_, _, outside) = await sof.EndedAsync(sof.RunAsync("init", "--build", "make build", "--test", "make test"));
        Assert.Contains("is not a git repository, and the coding team works in one: run git init and commit before you run sof.", outside, StringComparison.Ordinal);
    }

    // CFG-17: a program on PATH outside the folders the Linux sandbox shows gets a note naming the exact folder that holds it,
    // never one above it such as the home folder, and nothing machine-specific is written. A program given with its folder is
    // not looked up on PATH.
    [Fact]
    public async Task A_program_outside_the_system_folders_gets_a_note_and_nothing_is_written_for_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Only the Linux sandbox hides the folders outside the system folders.");
        var home = Directory.CreateTempSubdirectory("officina-home-").FullName;
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(home, "bin")).FullName;
            var dotnet = Directory.CreateDirectory(Path.Combine(home, ".dotnet")).FullName;
            File.WriteAllText(Path.Combine(bin, "npm"), "");
            File.WriteAllText(Path.Combine(bin, "make"), "");
            File.WriteAllText(Path.Combine(dotnet, "dotnet"), "");
            sof.Variables["PATH"] = $"{bin}:{dotnet}:/usr/bin";

            var (exitCode, output, _) = await sof.EndedAsync(sof.RunAsync("init", "--build", "dotnet build && /usr/bin/make build", "--test", "npm test"));

            Assert.Equal(ExitCodes.Success, exitCode);
            Assert.Contains(
                $"note: your dotnet is at {dotnet}, where the sandbox doesn't look. Add {dotnet} to capabilities.sandbox.toolchains, so that commands in the sandbox run it.",
                output, StringComparison.Ordinal);
            Assert.Contains($"note: your npm is at {bin}, where", output, StringComparison.Ordinal);
            Assert.DoesNotContain("your make", output, StringComparison.Ordinal); // /usr/bin/make is given with its folder
            Assert.DoesNotContain($"{home},", output, StringComparison.Ordinal);
            Assert.Empty(Load().Capabilities.Sandbox.Toolchains);
            Assert.DoesNotContain("toolchains", File.ReadAllText(Path.Combine(sof.Directory, "sof.json")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // CFG-17: the sandbox runs a program only from the toolchains or its own path, and sees only its system folders and the
    // toolchains. A program the sandbox runs from a toolchain gets no note; one whose link leads outside the folders the sandbox
    // sees gets the folder the link leads to, which is the one to add.
    [Fact]
    public void A_program_counts_as_found_only_where_the_sandbox_looks_and_after_its_links()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Only the Linux sandbox searches a path of its own.");
        var home = Directory.CreateTempSubdirectory("officina-home-").FullName;
        try
        {
            var dotnet = Directory.CreateDirectory(Path.Combine(home, "dotnet-sdk")).FullName;
            var bin = Directory.CreateDirectory(Path.Combine(home, "bin")).FullName;
            File.WriteAllText(Path.Combine(dotnet, "dotnet-sof-test"), "");
            File.CreateSymbolicLink(Path.Combine(bin, "dotnet-sof-test"), Path.Combine(dotnet, "dotnet-sof-test"));
            string[] commands = ["dotnet-sof-test build"];

            Assert.Equal([("dotnet-sof-test", dotnet)], CommandDetection.Unseen(commands, dotnet, []));
            Assert.Empty(CommandDetection.Unseen(commands, dotnet, [dotnet]));
            Assert.Equal([("dotnet-sof-test", dotnet)], CommandDetection.Unseen(commands, bin, []));
            Assert.Equal([("dotnet-sof-test", dotnet)], CommandDetection.Unseen(commands, bin, [bin])); // the link leads out of what the sandbox sees
            Assert.Empty(CommandDetection.Unseen(commands, "/usr/bin", [])); // nothing to run
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // CFG-17: plain sof with no sof.json points to sof init; in a session, /init is refused, as the session holds the console.
    [Fact]
    public async Task Plain_sof_suggests_init_and_a_session_refuses_it()
    {
        var (_, _, missing) = await sof.EndedAsync(sof.RunAsync());
        Assert.Contains("There is no sof.json here to chat with: run sof init", missing, StringComparison.Ordinal);

        sof.Write("sof.json", """{ "providers": { "claude": { "prices": { "claude-opus-5-5": { "input": 1 } } } }, "agents": { "dev": { "instructions": "Work." } } }""");
        sof.Providers["claude"] = new ScriptedModelProvider();
        sof.In.Type("/init");
        sof.In.Type("/quit");
        var (exitCode, output, _) = await sof.EndedAsync(sof.RunAsync());

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("error: run sof init in the shell, outside the session.", output, StringComparison.Ordinal);
        Assert.Contains("\"dev\"", File.ReadAllText(Path.Combine(sof.Directory, "sof.json")), StringComparison.Ordinal);
    }

    private OfficinaOptions Load()
    {
        var configuration = SofConfiguration.Load(sof.Directory, null, sof.Variables, []);
        Assert.Empty(configuration.Errors);
        return configuration.Options;
    }

    private (string Build, string Test) Commands()
    {
        var values = Load().Project.Values;
        return (values["buildCommand"], values["testCommand"]);
    }

    private static string Content(string file) => Path.GetFileName(file) switch
    {
        "package.json" => """{ "name": "app", "scripts": { "build": "tsc", "test": "vitest" } }""",
        "pyproject.toml" => "[project]\nname = \"calc\"\n",
        "pytest.ini" => "[pytest]\n",
        "Makefile" => "CC := cc\nbuild:\n\tcc main.c\n\ntest: build\n\t./a.out\n",
        _ => "",
    };
}
