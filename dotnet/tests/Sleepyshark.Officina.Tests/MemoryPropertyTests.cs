using System.Text.Json;
using CsCheck;

namespace Sleepyshark.Officina.Tests;

/// <summary>
/// Generated paths (climbing, absolute, encoded, through a link, either separator) never reach a file outside the run's
/// scope, in either store, via the memory tool or the store directly. Each case starts with a file in the scope, one in
/// another scope, and a secret outside the stores, linked from inside the scope where links can be created.
/// </summary>
public class MemoryPropertyTests
{
    private const string Secret = "TOP-SECRET";

    /// <summary>The file store's directory for scope <c>sam</c>: the hex of its UTF-8 bytes.</summary>
    private const string Sam = "73616d";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Pieces =
    [
        "..", ".", "", "a", "b.md", "link", "memories", "/memories", "other", "outside", "secret.txt", "%2e%2e", "%2f", "..%2f", "%252e%252e",
        "C:", "c:\\", "\\", "/", "..\\..", "\\\\server\\share", "~", "...", ".. ", "a.", "NUL", "con.txt", "x\0", "\u2215", "\uff0e\uff0e", "/etc/passwd",
    ];

    /// <summary>Ways out of the scope's directory, each followed by a separator and a target.</summary>
    private static readonly string[] Routes =
    [
        "link", "./link", "a/../link", "..", "../..", "a/../..", "../../outside", "%2e%2e", "..%2f..", "\uff0e\uff0e", "", "C:", "\\\\server\\share",
        "x\0/..", "... ", "..\\..",
    ];

    private static readonly string[] Targets = ["secret.txt", "outside/secret.txt", "other/o.md", "o.md", "memory/other/o.md", "6f74686572/o.md", "memory/6f74686572/o.md", "etc/passwd"];

    private static readonly string[] Commands = ["view", "create", "str_replace", "insert", "delete", "rename", "rename back"];

    /// <summary>One case: a command, its path, and a second path for renames; run against both stores.</summary>
    public sealed record Attempt(string Command, string Path, string Other);

    private static readonly Gen<string> Separator = Gen.Frequency((3, Gen.Const("/")), (1, Gen.Const("\\")));

    /// <summary>Mostly a route to a target; otherwise any pieces, joined by either separator or none.</summary>
    private static readonly Gen<string> Path = Gen.Select(
        Gen.Bool,
        Gen.Frequency(
            (4, Gen.Select(Gen.OneOfConst(Routes), Separator, Gen.OneOfConst(Targets), (route, separator, target) => route + separator + target)),
            (1, Gen.Select(Gen.OneOfConst(Pieces).Array[1, 4], Gen.OneOfConst("/", "\\", "").Array[4], (parts, separators) =>
                string.Concat(parts.Select((part, index) => index == 0 ? part : separators[index] + part))))),
        (prefixed, path) => (prefixed ? "/memories/" : "") + path);

    private static readonly Gen<Attempt> Cases = Gen.Select(Gen.OneOfConst(Commands), Path, Path, (command, path, other) => new Attempt(command, path, other));

    [Fact]
    public async Task Memory_paths_never_leave_their_scope()
    {
        await Property.CheckAsync(Cases, async test => { await CheckAsync(test, files: false); await CheckAsync(test, files: true); }, test => JsonSerializer.Serialize(test), cases: 500);
    }

    private static async Task CheckAsync(Attempt test, bool files)
    {
        var temp = Directory.CreateTempSubdirectory("officina-memory-property-");
        try
        {
            var (store, outside) = await ArrangeAsync(files, temp.FullName);
            var before = Snapshot(temp.FullName);
            var (path, other) = test.Command == "rename back" ? (test.Other, "/memories/a/b.md") : (test.Path, test.Other);
            var input = JsonSerializer.SerializeToElement(new
            {
                command = test.Command.Split(' ')[0],
                path,
                old_path = path,
                new_path = other,
                file_text = "x",
                old_str = Secret,
                new_str = "x",
                insert_line = 0,
                insert_text = "x",
            });

            var outputs = new List<string>();
            await Try(async () => outputs.Add((await MemoryTool.Create(store).Handler(input, new ToolContext("sam"), Ct)).Content));
            var relative = path.StartsWith("/memories/", StringComparison.Ordinal) ? path["/memories/".Length..] : path;
            await Try(async () => outputs.Add(await store.ReadAsync("sam", relative, Ct) ?? ""));
            await Try(() => store.WriteAsync("sam", relative, "x", Ct));
            await Try(() => store.RenameAsync("sam", "a/b.md", relative, Ct));
            await Try(() => store.DeleteAsync("sam", relative, Ct));
            await Try(async () => outputs.AddRange((await store.ListAsync("sam", Ct)).Select(file => file.Path)));

            Assert.All(outputs, output => Assert.DoesNotContain(Secret, output, StringComparison.Ordinal));
            Assert.All(outputs, output => Assert.DoesNotContain("other scope", output, StringComparison.Ordinal));
            Assert.Equal("other scope", await store.ReadAsync("other", "o.md", Ct));
            Assert.Single(await store.ListAsync("other", Ct));
            Assert.All(await store.ListAsync("sam", Ct), file => Assert.True(MemoryPath.IsValid(file.Path), file.Path));
            Assert.Equal(Secret, await File.ReadAllTextAsync(outside, Ct));

            // On disk, nothing changed outside the scope's directory.
            var scope = System.IO.Path.Combine(temp.FullName, "memory", Sam) + System.IO.Path.DirectorySeparatorChar;
            Assert.Equal(
                before.Where(file => !file.Key.StartsWith(scope, StringComparison.Ordinal)),
                Snapshot(temp.FullName).Where(file => !file.Key.StartsWith(scope, StringComparison.Ordinal)));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    /// <summary>The stores' files, and the path of the secret outside them.</summary>
    private static async Task<(IMemoryStore Store, string Outside)> ArrangeAsync(bool files, string root)
    {
        var outside = System.IO.Path.Combine(root, "outside", "secret.txt");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outside)!);
        await File.WriteAllTextAsync(outside, Secret, Ct);
        var memory = System.IO.Path.Combine(root, "memory");
        IMemoryStore store = files ? new FileMemoryStore(memory) : new InMemoryMemoryStore();
        await store.WriteAsync("sam", "a/b.md", "inside", Ct);
        await store.WriteAsync("other", "o.md", "other scope", Ct);
        if (files)
        {
            try
            {
                Directory.CreateSymbolicLink(System.IO.Path.Combine(memory, Sam, "link"), System.IO.Path.GetDirectoryName(outside)!);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                // Windows without the privilege to create links: there is no link to follow.
            }
        }

        return (store, outside);
    }

    /// <summary>Every file under <paramref name="root"/> and its text, without following links.</summary>
    private static SortedDictionary<string, string> Snapshot(string root) => new(
        new DirectoryInfo(root)
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .ToDictionary(file => file.FullName, file => File.ReadAllText(file.FullName)),
        StringComparer.Ordinal);

    /// <summary>Runs a step a store may refuse: refusing is fine; reaching outside the scope is what the checks catch.</summary>
    private static async Task Try(Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
        }
    }
}
