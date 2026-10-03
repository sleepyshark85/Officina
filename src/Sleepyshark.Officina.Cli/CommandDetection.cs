using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sleepyshark.Officina.Sandbox;

namespace Sleepyshark.Officina.Cli;

/// <summary>A command <c>sof init</c> suggests, and the commands its rules allow, each as itself and with arguments after it.</summary>
internal sealed record Suggestion(string Line, IReadOnlyList<string> Rules);

/// <summary>A toolchain <c>sof init</c> found in a project, the file that shows it, and its build and test commands, where it has them.</summary>
internal sealed record Detection(string Toolchain, string Why, Suggestion? Build, Suggestion? Test);

/// <summary>
/// What <c>sof init</c> knows of toolchains (CFG-17): a small table of detectors, in order of confidence, that find a project's
/// build and test commands in its top folder, with the commands their rules allow; and what a command's program needs in the
/// sandbox: the hosts of its package registry, whether its rule would let an agent run any code, and whether the sandbox shows it.
/// </summary>
internal static partial class CommandDetection
{
    private static readonly Func<string, List<string>, Detection?>[] Detectors = [DotNet, Node, Python, Rust, Go, Make];
    private static readonly string[] PythonProjects = ["pyproject.toml", "setup.cfg", "tox.ini", "setup.py"];
    private static readonly string[] Makefiles = ["GNUmakefile", "makefile", "Makefile"]; // in the order make reads them

    /// <summary>The hosts of each program's package registry.</summary>
    private static readonly Dictionary<string, string[]> Registries = new(StringComparer.Ordinal)
    {
        ["dotnet"] = ["api.nuget.org", "*.nuget.org"],
        ["npm"] = ["registry.npmjs.org"],
        ["pnpm"] = ["registry.npmjs.org"],
        ["yarn"] = ["registry.yarnpkg.com", "registry.npmjs.org"],
        ["cargo"] = ["crates.io", "index.crates.io", "static.crates.io"],
        ["go"] = ["proxy.golang.org", "sum.golang.org"],
    };

    /// <summary>Programs that run whatever code their options give them, unless a script or a module follows.</summary>
    private static readonly HashSet<string> Interpreters =
        ["python", "python3", "py", "node", "deno", "bun", "ruby", "perl", "php", "sh", "bash", "zsh", "dash", "pwsh", "powershell", "cmd"];

    /// <summary>Commands that install or run any package named after them, by their first one, two or three words.</summary>
    private static readonly HashSet<string> Runners =
        ["npx", "pnpx", "bunx", "uvx", "dnx", "npm exec", "npm install", "npm i", "npm add", "pnpm dlx", "pnpm exec", "pnpm add", "yarn dlx", "yarn exec",
         "yarn add", "bun x", "deno run", "dotnet dnx", "dotnet tool run", "pip install", "pip3 install", "uv run"];

    /// <summary>Programs that run any program named after them.</summary>
    private static readonly HashSet<string> Wrappers = ["env", "xargs"];

    /// <summary>Every toolchain found in <paramref name="directory"/>, the most certain first; <paramref name="notes"/> gets why one was skipped.</summary>
    public static IReadOnlyList<Detection> Detect(string directory, List<string> notes) =>
        [.. Detectors.Select(detect => detect(directory, notes)).OfType<Detection>()];

    /// <summary>The hosts the commands' programs need to reach.</summary>
    public static IReadOnlyList<string> Hosts(IEnumerable<string> lines) =>
        [.. Parts(lines).Select(Program).SelectMany(program => Registries.GetValueOrDefault(program) ?? []).Distinct()];

    /// <summary>Each command of the lines, split where the command rules split them.</summary>
    public static IEnumerable<string> Parts(IEnumerable<string> lines) =>
        lines.SelectMany(line => Separators().Split(line)).Select(command => command.Trim()).Where(command => command.Length > 0);

    /// <summary>Why a rule that allows <paramref name="command"/> with any arguments lets an agent run any code, if it does.</summary>
    public static string? Broad(string command)
    {
        var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var program = Program(command);
        if (Interpreters.Contains(program) && !(words.Length > 2 && words[1] == "-m") && !(words.Length > 1 && !words[1].StartsWith('-')))
        {
            return $"\"{command}\" with any arguments lets an agent run any code with {program}. Name a script or a module after it instead.";
        }

        if (Enumerable.Range(1, Math.Min(3, words.Length)).Any(count => Runners.Contains(string.Join(' ', words.Take(count).Skip(1).Prepend(program)))))
        {
            return $"\"{command}\" with any arguments lets an agent install or run any package. Use the lock file's install, such as npm ci, and a script of the project.";
        }

        if (Wrappers.Contains(program))
        {
            return $"\"{command}\" with any arguments lets an agent run any program with {program}. Name the program instead.";
        }

        // Only options or variables, such as make -j4: no target is named, so any can follow.
        return program == "make" && words.Skip(1).All(word => word.StartsWith('-') || word.Contains('=', StringComparison.Ordinal))
            ? $"\"{command}\" with any arguments lets an agent run any target. Name the target, such as make build."
            : null;
    }

    /// <summary>
    /// Each program of the commands that <paramref name="path"/> finds and the Linux sandbox doesn't: the sandbox searches only
    /// the toolchains and its own path, and sees only its system folders and the toolchains. With the folder that holds the program,
    /// after any links. A program given with a folder, such as <c>./gradlew</c>, is not looked up.
    /// </summary>
    public static IEnumerable<(string Program, string Folder)> Unseen(IEnumerable<string> lines, string? path, IReadOnlyList<string> toolchains)
    {
        var folders = (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(Path.TrimEndingDirectorySeparator).ToList();
        var visible = LinuxSandbox.SystemPaths.Concat(toolchains).Select(Path.TrimEndingDirectorySeparator).ToList();
        foreach (var program in Parts(lines).Select(command => command.Split(' ')[0]).Distinct().Where(program => !program.Contains('/', StringComparison.Ordinal)))
        {
            if (Find(program, folders) is { } yours
                && Find(program, toolchains.Concat(LinuxSandbox.SearchPath)) is var sandboxes
                && (sandboxes != yours || !visible.Any(folder => yours == folder || yours.StartsWith($"{folder}/", StringComparison.Ordinal))))
            {
                yield return (program, Path.GetDirectoryName(yours)!);
            }
        }

        // The program's file, after any links, in the first folder that has it.
        static string? Find(string program, IEnumerable<string> folders) =>
            folders.Select(folder => new FileInfo(Path.Combine(folder, program))).Where(file => file.Exists)
                .Select(file => file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file.FullName).FirstOrDefault();
    }

    private static Detection? DotNet(string directory, List<string> notes)
    {
        var solutions = Files(directory, "*.sln", "*.slnx");
        var projects = Files(directory, "*.csproj");
        if (solutions.Count == 0 && projects.Count > 1)
        {
            // dotnet build refuses a folder with several projects, and building one of them may leave the others out.
            return new(".NET", $"{string.Join(", ", projects)}: several projects and no solution, so name one in each command", null, null);
        }

        var found = solutions.Count > 0 ? solutions : projects;
        if (found.Count == 0)
        {
            return null;
        }

        var target = found.Count == 1 ? "" : $" {found[0]}"; // several solutions: dotnet build needs one named
        return new(".NET", string.Join(", ", found), new($"dotnet build{target}", ["dotnet build"]), new($"dotnet test{target}", ["dotnet test"]));
    }

    private static Detection? Node(string directory, List<string> notes)
    {
        var file = Path.Combine(directory, "package.json");
        if (!File.Exists(file))
        {
            return null;
        }

        JsonObject? package;
        try
        {
            package = JsonNode.Parse(File.ReadAllText(file), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch (JsonException exception)
        {
            notes.Add($"package.json cannot be read ({exception.Message}), so sof init does not look at its scripts.");
            return null;
        }

        if (package is null)
        {
            notes.Add("package.json is not a JSON object, so sof init does not look at its scripts.");
            return null;
        }

        var scripts = package["scripts"] as JsonObject ?? [];
        var (manager, lockFile, install) = Exists(directory, "pnpm-lock.yaml") ? ("pnpm", "pnpm-lock.yaml", "pnpm install --frozen-lockfile")
            : Exists(directory, "yarn.lock") ? ("yarn", "yarn.lock", Exists(directory, ".yarnrc.yml") ? "yarn install --immutable" : "yarn install --frozen-lockfile")
            : Exists(directory, "package-lock.json") ? ("npm", "package-lock.json", "npm ci")
            : ("npm", null, null);
        var hasBuild = Script(scripts, "build") is not null;
        var hasTest = Script(scripts, "test") is { } test && !test.Contains("no test specified", StringComparison.Ordinal);

        // A working copy has only what git tracks, so the packages are installed first: before the build, or the tests if there is none.
        Suggestion? Command(string line, bool installs) =>
            install is not null && installs ? new($"{install} && {line}", [install, line]) : new(line, [line]);
        return new(
            "Node",
            lockFile is null ? "package.json" : $"package.json, {lockFile}",
            hasBuild ? Command($"{manager} run build", installs: true) : null,
            hasTest ? Command($"{manager} test", installs: !hasBuild) : null);
    }

    private static Detection? Python(string directory, List<string> notes)
    {
        var markers = PythonProjects.Where(name => Exists(directory, name)).ToList();
        markers.AddRange(Files(directory, "test_*.py"));
        if (markers.Count == 0)
        {
            return null;
        }

        var pytest = Exists(directory, "pytest.ini") || Exists(directory, "conftest.py")
            || Contains(directory, "pyproject.toml", "[tool.pytest") || Contains(directory, "setup.cfg", "[tool:pytest]") || Contains(directory, "tox.ini", "[pytest]");
        var python = OperatingSystem.IsWindows() ? "python" : "python3";
        var test = pytest ? $"{python} -m pytest" : $"{python} -m unittest";
        return new("Python", string.Join(", ", markers.Take(3)), new($"{python} -m compileall -q .", [$"{python} -m compileall"]), new(test, [test]));
    }

    private static Detection? Rust(string directory, List<string> notes) =>
        Exists(directory, "Cargo.toml") ? new("Rust", "Cargo.toml", new("cargo build", ["cargo build"]), new("cargo test", ["cargo test"])) : null;

    private static Detection? Go(string directory, List<string> notes) =>
        Exists(directory, "go.mod") ? new("Go", "go.mod", new("go build ./...", ["go build"]), new("go test ./...", ["go test"])) : null;

    private static Detection? Make(string directory, List<string> notes)
    {
        // By the name on disk: a case-insensitive file system finds Makefile as makefile too.
        if (Makefiles.SelectMany(name => Files(directory, name)).FirstOrDefault() is not { } name)
        {
            return null;
        }

        var text = File.ReadAllText(Path.Combine(directory, name));
        Suggestion? Target(string target) => new Regex($@"^{target}\s*:(?!=)", RegexOptions.Multiline).IsMatch(text) ? new($"make {target}", [$"make {target}"]) : null;
        var (build, test) = (Target("build"), Target("test"));
        var targets = string.Join(" and ", new[] { build is null ? null : "build", test is null ? null : "test" }.OfType<string>());
        return build is null && test is null ? null : new("make", $"{name} with {targets} targets", build, test);
    }

    private static string? Script(JsonObject scripts, string name) =>
        scripts[name] is JsonValue value && value.TryGetValue<string>(out var script) ? script : null;

    /// <summary>The program a command runs, by its name: <c>python3</c> for <c>/usr/bin/python3.12</c>.</summary>
    private static string Program(string command) => Path.GetFileNameWithoutExtension(command.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);

    private static List<string> Files(string directory, params string[] patterns) =>
        [.. patterns.SelectMany(pattern => Directory.EnumerateFiles(directory, pattern)).Select(Path.GetFileName).OfType<string>().Distinct().Order(StringComparer.Ordinal)];

    private static bool Exists(string directory, string file) => File.Exists(Path.Combine(directory, file));

    private static bool Contains(string directory, string file, string text) =>
        Exists(directory, file) && File.ReadAllText(Path.Combine(directory, file)).Contains(text, StringComparison.Ordinal);

    [GeneratedRegex(@"[;&|\n]")]
    private static partial Regex Separators();
}
