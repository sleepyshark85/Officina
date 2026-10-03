using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sleepyshark.Officina.Sandbox;

namespace Sleepyshark.Officina.Cli;

/// <summary>A toolchain <c>sof init</c> found in a project, the file that shows it, and its build and test commands, where it has them.</summary>
internal sealed record Detection(string Toolchain, string Why, string? Build, string? Test);

/// <summary>
/// What <c>sof init</c> knows of toolchains (CFG-17): a small table of detectors, in order of confidence, that find a project's
/// build and test commands in its top folder; and what a command's program needs in the sandbox: the hosts of its package
/// registry, the commands that install the locked packages, and its folder when it lives outside the system folders.
/// </summary>
internal static partial class CommandDetection
{
    private static readonly Func<string, Detection?>[] Detectors = [DotNet, Node, Python, Rust, Go, Make];
    private static readonly string[] PythonProjects = ["pyproject.toml", "setup.cfg", "tox.ini", "setup.py"];
    private static readonly string[] Makefiles = ["GNUmakefile", "makefile", "Makefile"]; // in the order make reads them

    /// <summary>The hosts a program's package registry needs, and the commands that install what its lock file holds.</summary>
    private static readonly Dictionary<string, (string[] Hosts, string[] Install)> Programs = new(StringComparer.Ordinal)
    {
        ["dotnet"] = (["api.nuget.org", "*.nuget.org"], []),
        ["npm"] = (["registry.npmjs.org"], ["npm ci"]),
        ["pnpm"] = (["registry.npmjs.org"], ["pnpm install --frozen-lockfile"]),
        ["yarn"] = (["registry.yarnpkg.com", "registry.npmjs.org"], ["yarn install --frozen-lockfile"]),
        ["cargo"] = (["crates.io", "index.crates.io", "static.crates.io"], []),
        ["go"] = (["proxy.golang.org", "sum.golang.org"], []),
    };

    /// <summary>Every toolchain found in <paramref name="directory"/>, the most certain first.</summary>
    public static IReadOnlyList<Detection> Detect(string directory) => [.. Detectors.Select(detect => detect(directory)).OfType<Detection>()];

    /// <summary>The hosts the commands' programs need to reach.</summary>
    public static IReadOnlyList<string> Hosts(IEnumerable<string> commands) =>
        [.. ProgramsOf(commands).SelectMany(program => (Programs.TryGetValue(program, out var needs) ? needs.Hosts : [])).Distinct()];

    /// <summary>The command rules that allow the commands, and the installs of their programs, each command of a line on its own.</summary>
    public static IReadOnlyList<string> Allowed(IEnumerable<string> commands)
    {
        var list = commands.ToList();
        return [.. list.SelectMany(Parts).Concat(ProgramsOf(list).SelectMany(program => (Programs.TryGetValue(program, out var needs) ? needs.Install : []))).Select(command => $"{command}*").Distinct()];
    }

    /// <summary>
    /// The folders of the commands' programs that the Linux sandbox does not show: a program found on <paramref name="path"/>
    /// outside the system folders, such as <c>~/.dotnet/dotnet</c>, is the folder it is in, or the one above a <c>bin</c> folder.
    /// </summary>
    public static IReadOnlyList<string> Toolchains(IEnumerable<string> commands, string? path)
    {
        var folders = (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return [.. ProgramsOf(commands)
            .Select(program => folders.FirstOrDefault(folder => File.Exists(Path.Combine(folder, program))))
            .OfType<string>()
            .Select(folder => Path.TrimEndingDirectorySeparator(folder))
            .Where(folder => !LinuxSandbox.SystemPaths.Any(system => folder == system || folder.StartsWith($"{system}/", StringComparison.Ordinal)))
            .Select(folder => Path.GetFileName(folder) == "bin" ? Path.GetDirectoryName(folder)! : folder)
            .Distinct()];
    }

    private static Detection? DotNet(string directory)
    {
        var solutions = Files(directory, "*.sln", "*.slnx");
        var found = solutions.Count > 0 ? solutions : Files(directory, "*.csproj");
        if (found.Count == 0)
        {
            return null;
        }

        // dotnet build refuses a folder with more than one solution or project, so then one is named.
        var target = found.Count == 1 ? "" : $" {found[0]}";
        return new(".NET", string.Join(", ", found), $"dotnet build{target}", $"dotnet test{target}");
    }

    private static Detection? Node(string directory)
    {
        var file = Path.Combine(directory, "package.json");
        if (!File.Exists(file))
        {
            return null;
        }

        var scripts = (JsonNode.Parse(File.ReadAllText(file), documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })?["scripts"] as JsonObject) ?? [];
        var manager = File.Exists(Path.Combine(directory, "pnpm-lock.yaml")) ? "pnpm" : File.Exists(Path.Combine(directory, "yarn.lock")) ? "yarn" : "npm";
        var test = scripts["test"]?.ToString() is { } script && !script.Contains("no test specified", StringComparison.Ordinal) ? $"{manager} test" : null;
        var build = scripts["build"] is not null ? $"{manager} run build" : null;
        return new("Node", manager == "npm" ? "package.json" : $"package.json, {(manager == "pnpm" ? "pnpm-lock.yaml" : "yarn.lock")}", build, test);
    }

    private static Detection? Python(string directory)
    {
        var markers = PythonProjects.Where(name => File.Exists(Path.Combine(directory, name))).ToList();
        markers.AddRange(Files(directory, "test_*.py"));
        if (markers.Count == 0)
        {
            return null;
        }

        var pytest = File.Exists(Path.Combine(directory, "pytest.ini")) || File.Exists(Path.Combine(directory, "conftest.py"))
            || Contains(directory, "pyproject.toml", "[tool.pytest") || Contains(directory, "setup.cfg", "[tool:pytest]") || Contains(directory, "tox.ini", "[pytest]");
        var python = OperatingSystem.IsWindows() ? "python" : "python3";
        return new("Python", string.Join(", ", markers.Take(3)), $"{python} -m compileall -q .", pytest ? $"{python} -m pytest" : $"{python} -m unittest");
    }

    private static Detection? Rust(string directory) =>
        File.Exists(Path.Combine(directory, "Cargo.toml")) ? new("Rust", "Cargo.toml", "cargo build", "cargo test") : null;

    private static Detection? Go(string directory) =>
        File.Exists(Path.Combine(directory, "go.mod")) ? new("Go", "go.mod", "go build ./...", "go test ./...") : null;

    private static Detection? Make(string directory)
    {
        if (Makefiles.FirstOrDefault(name => File.Exists(Path.Combine(directory, name))) is not { } name)
        {
            return null;
        }

        var text = File.ReadAllText(Path.Combine(directory, name));
        var build = Target("build").IsMatch(text) ? "make build" : null;
        var test = Target("test").IsMatch(text) ? "make test" : null;
        return build is null && test is null ? null : new("make", $"{name} with {string.Join(" and ", new[] { build, test }.OfType<string>().Select(command => command[5..]))} targets", build, test);
    }

    private static Regex Target(string name) => new($@"^{name}\s*:(?!=)", RegexOptions.Multiline);

    private static List<string> Files(string directory, params string[] patterns) =>
        [.. patterns.SelectMany(pattern => Directory.EnumerateFiles(directory, pattern)).Select(Path.GetFileName).OfType<string>().Distinct().Order(StringComparer.Ordinal)];

    private static bool Contains(string directory, string file, string text) =>
        File.Exists(Path.Combine(directory, file)) && File.ReadAllText(Path.Combine(directory, file)).Contains(text, StringComparison.Ordinal);

    /// <summary>Each command of a command line, split where the command rules split it.</summary>
    private static IEnumerable<string> Parts(string line) =>
        Separators().Split(line).Select(command => command.Trim()).Where(command => command.Length > 0);

    /// <summary>The program each command of the lines runs: its first word.</summary>
    private static IEnumerable<string> ProgramsOf(IEnumerable<string> lines) =>
        lines.SelectMany(Parts).Select(command => command.Split(' ')[0]).Distinct();

    [GeneratedRegex(@"[;&|\n]")]
    private static partial Regex Separators();
}
