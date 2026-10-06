using System.Globalization;
using System.Text.Json;

namespace Sleepyshark.Officina;

/// <summary>A memory file of a scope: its path within the scope, its parts separated by <c>/</c>, and its size in bytes.</summary>
public sealed record MemoryFile(string Path, long Size);

/// <summary>
/// Where memory files are kept (MEM-02, ARCHITECTURE §4.4). Every operation is within one scope, and a scope never sees
/// another's files. Scopes and paths are those <see cref="MemoryPath"/> accepts; a store refuses any other with an
/// <see cref="ArgumentException"/>, so no path leaves its scope (MEM-03). Directories are implied by the files' paths.
/// </summary>
public interface IMemoryStore
{
    /// <summary>Every file of the scope, in any order; none for a scope never written to.</summary>
    Task<IReadOnlyList<MemoryFile>> ListAsync(string scope, CancellationToken cancellationToken);

    /// <summary>The file's text; null when there is no such file.</summary>
    Task<string?> ReadAsync(string scope, string path, CancellationToken cancellationToken);

    /// <summary>Creates the file, or replaces its text.</summary>
    Task WriteAsync(string scope, string path, string content, CancellationToken cancellationToken);

    /// <summary>Deletes the file; does nothing when there is none.</summary>
    Task DeleteAsync(string scope, string path, CancellationToken cancellationToken);

    /// <summary>Moves the file to <paramref name="newPath"/>; throws when there is no such file, or one at <paramref name="newPath"/>.</summary>
    Task RenameAsync(string scope, string path, string newPath, CancellationToken cancellationToken);
}

/// <summary>
/// The scopes and paths a memory store accepts (MEM-03): a scope is one path part, and a path is parts separated by
/// <c>/</c>. A part is never empty, <c>.</c> or <c>..</c>, never ends with a dot or a space, holds no control character
/// and none of <c>\ / : * ? " &lt; &gt; | %</c>, and is no device name Windows reserves (<c>CON</c>, <c>COM1</c>, <c>CONIN$</c>…). So a path is relative, has one
/// separator on every system, cannot climb out, and has no encoded form: it names the same file in every store.
/// </summary>
public static class MemoryPath
{
    /// <summary>The longest path, in characters.</summary>
    public const int MaxLength = 1_024;

    private const string Forbidden = "\\/:*?\"<>|%";

    private static readonly HashSet<string> Devices = new(
        ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", .. "0123456789\u00b9\u00b2\u00b3".SelectMany(digit => new[] { $"COM{digit}", $"LPT{digit}" })],
        StringComparer.OrdinalIgnoreCase);

    public static bool IsValid(string? path) => path is { Length: > 0 and <= MaxLength } && path.Split('/').All(IsPart);

    public static bool IsValidScope(string? scope) => scope is not null && IsPart(scope);

    /// <summary>Throws <see cref="ArgumentException"/> unless <paramref name="scope"/>, and <paramref name="path"/> if given, are valid.</summary>
    public static void Check(string scope, string? path = null)
    {
        if (!IsValidScope(scope) || (path is not null && !IsValid(path)))
        {
            throw new ArgumentException(IsValidScope(scope) ? $"'{path}' is not a valid memory path." : $"'{scope}' is not a valid memory scope.");
        }
    }

    private static bool IsPart(string part) =>
        part.Length is > 0 and <= 255 && part[^1] is not ('.' or ' ') && !part.Any(c => char.IsControl(c) || Forbidden.Contains(c))
        && !Devices.Contains(part.Split('.')[0].TrimEnd());
}

/// <summary>
/// The memory service (MEM-01, ARCHITECTURE §7): memory as a tool with the commands of Claude's memory tool (view,
/// create, str_replace, insert, delete, rename) over files under <c>/memories</c>, which map to the run's memory scope in
/// a store. It is a write tool, so every call is audited before it runs (MEM-04); approval, when asked for, is asked
/// for the commands that change memory. Memory is never put in the instructions: the model reads it on demand (MEM-05).
/// </summary>
public static class MemoryTool
{
    public const string Name = "memory";

    /// <summary>The memory directory as the model sees it.</summary>
    public const string Root = "/memories";

    /// <summary>The most characters a memory file may hold; a command that would make it longer is refused.</summary>
    public const int MaxFileLength = 50_000;

    /// <summary>The most characters a file's <c>view</c> shows, as the model's tool description says; ranges show the rest.</summary>
    internal const int MaxViewLength = 16_000;

    /// <summary>The memory tool over <paramref name="store"/>; each run that has it must give a memory scope.</summary>
    public static Tool Create(IMemoryStore store, bool needsApproval = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new Tool(Name, Description, Schema, ToolKind.Write, HandleAsync, needsApproval)
        {
            IsMemory = true,
            ExemptFromApproval = Views,
        };

        Task<ToolOutput> HandleAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken)
        {
            var scope = context.MemoryScope ?? throw new InvalidOperationException("The run has no memory scope.");
            return RunAsync(store, scope, input, cancellationToken);
        }
    }

    /// <summary>For providers without a native memory tool: what the model reads instead of its trained description.</summary>
    private const string Description =
        "Your memory: a directory of text files under /memories that persists across conversations. Commands: view (a file, " +
        "or a directory's listing), create (create or overwrite a file), str_replace, insert, delete and rename.";

    private const string Schema = """
        {"type":"object","properties":{"command":{"type":"string","enum":["view","create","str_replace","insert","delete","rename"]},
        "path":{"type":"string"},"view_range":{"type":"array","items":{"type":"integer"}},"file_text":{"type":"string"},
        "old_str":{"type":"string"},"new_str":{"type":"string"},"insert_line":{"type":"integer"},"insert_text":{"type":"string"},
        "old_path":{"type":"string"},"new_path":{"type":"string"}},"required":["command"]}
        """;

    /// <summary>Whether the call only views memory, and so is never asked approval for.</summary>
    private static bool Views(JsonElement input) => Text(input, "command") == "view";

    /// <summary>Runs one command in <paramref name="scope"/>: every outcome but the store's failures is a result.</summary>
    private static async Task<ToolOutput> RunAsync(IMemoryStore store, string scope, JsonElement input, CancellationToken cancellationToken)
    {
        var command = Text(input, "command");
        var paths = command == "rename" ? new[] { Text(input, "old_path"), Text(input, "new_path") } : [Text(input, "path")];
        foreach (var outside in paths.Where(path => Within(path) is null))
        {
            return Error($"Error: The path {outside ?? "(none)"} is not a valid path under {Root}.");
        }

        var memory = new Scope(store, scope, await store.ListAsync(scope, cancellationToken).ConfigureAwait(false), cancellationToken);
        var (path, at) = (paths[0]!, Within(paths[0])!);
        return command switch
        {
            "view" => await memory.ViewAsync(path, at, input).ConfigureAwait(false),
            "create" => await memory.CreateAsync(path, at, Text(input, "file_text")).ConfigureAwait(false),
            "str_replace" => await memory.ReplaceAsync(path, at, Text(input, "old_str"), Text(input, "new_str") ?? "").ConfigureAwait(false),
            "insert" => await memory.InsertAsync(path, at, Number(input, "insert_line"), Text(input, "insert_text")).ConfigureAwait(false),
            "delete" => await memory.DeleteAsync(path, at).ConfigureAwait(false),
            "rename" => await memory.RenameAsync(path, at, paths[1]!, Within(paths[1])!).ConfigureAwait(false),
            _ => Error($"Error: Unknown command {command}."),
        };
    }

    /// <summary>The path within the scope that the model's <paramref name="path"/> names: empty for the root, null for none.</summary>
    private static string? Within(string? path)
    {
        path = path is [.., '/'] && path.Length > 1 ? path[..^1] : path;
        return path == Root ? "" : path is not null && path.StartsWith(Root + "/", StringComparison.Ordinal) && MemoryPath.IsValid(path[(Root.Length + 1)..])
            ? path[(Root.Length + 1)..]
            : null;
    }

    private static string? Text(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Number(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static ToolOutput Error(string message) => new(message, IsError: true);

    private static string Missing(string path) => $"The path {path} does not exist. Please provide a valid path.";

    private static string[] Lines(string content) => content.Split('\n');

    /// <summary>Lines <paramref name="from"/> to <paramref name="to"/>, numbered from 1, six wide.</summary>
    private static string Numbered(string[] lines, int from, int to) => string.Join(
        '\n', Enumerable.Range(from, to - from + 1).Select(line => string.Create(CultureInfo.InvariantCulture, $"{line,6}\t{lines[line - 1]}")));

    /// <summary>The scope's files as they were when the command started; commands run one at a time.</summary>
    private sealed class Scope(IMemoryStore store, string scope, IReadOnlyList<MemoryFile> files, CancellationToken cancellationToken)
    {
        private bool IsFile(string at) => files.Any(file => file.Path == at);

        private bool IsDirectory(string at) => at.Length == 0 || Under(at).Any();

        private IEnumerable<MemoryFile> Under(string at) => files.Where(file => at.Length == 0 || file.Path.StartsWith(at + "/", StringComparison.Ordinal));

        private bool Exists(string at) => IsFile(at) || IsDirectory(at);

        /// <summary>The first of <paramref name="at"/>'s directories that is a file, if any.</summary>
        private string? FileAbove(string at)
        {
            var parts = at.Split('/');
            return Enumerable.Range(1, parts.Length - 1).Select(count => string.Join('/', parts[..count])).FirstOrDefault(IsFile);
        }

        public async Task<ToolOutput> ViewAsync(string path, string at, JsonElement input)
        {
            if (IsFile(at) && await store.ReadAsync(scope, at, cancellationToken).ConfigureAwait(false) is { } content)
            {
                var lines = Lines(content.EndsWith('\n') ? content[..^1] : content);
                var (from, to) = (1, lines.Length);
                if (input.TryGetProperty("view_range", out var range))
                {
                    if (range.ValueKind != JsonValueKind.Array || range.GetArrayLength() != 2 || !range[0].TryGetInt32(out from) || !range[1].TryGetInt32(out to)
                        || from < 1 || from > lines.Length || (to != -1 && (to < from || to > lines.Length)))
                    {
                        return Error($"Error: Invalid `view_range`: it should be [start, end] with 1 <= start <= end <= {lines.Length}, or end -1 for the end of the file.");
                    }

                    to = to == -1 ? lines.Length : to;
                }

                // A long view ends at a line break in its second half, or else at the limit.
                var numbered = Numbered(lines, from, to);
                var view = numbered.Length <= MaxViewLength ? numbered
                    : numbered.LastIndexOf('\n', MaxViewLength) is var end and >= MaxViewLength / 2 ? numbered[..end]
                    : AgentDefinition.Cut(numbered, MaxViewLength);
                return new($"Here's the content of {path} with line numbers:\n{(view.Length == numbered.Length ? view
                    : $"{view}\n[Truncated at {MaxViewLength} characters: view the rest with view_range.]")}");
            }

            if (!IsDirectory(at))
            {
                return Error(Missing(path));
            }

            // Up to two levels below the directory, without hidden items, each with the size of everything in it.
            var shown = at.Length == 0 ? Root : $"{Root}/{at}";
            var entries = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var file in Under(at))
            {
                var parts = file.Path[(at.Length == 0 ? 0 : at.Length + 1)..].Split('/');
                for (var depth = 1; depth <= Math.Min(2, parts.Length) && !parts[depth - 1].StartsWith('.') && parts[depth - 1] != "node_modules"; depth++)
                {
                    var entry = $"{shown}/{string.Join('/', parts[..depth])}";
                    entries[entry] = entries.GetValueOrDefault(entry) + file.Size;
                }
            }

            var listing = entries.Select(entry => $"{Size(entry.Value)}\t{entry.Key}").Prepend($"{Size(Under(at).Sum(file => file.Size))}\t{shown}");
            return new($"Here're the files and directories up to 2 levels deep in {shown}, excluding hidden items and node_modules:\n{string.Join('\n', listing)}");
        }

        public async Task<ToolOutput> CreateAsync(string path, string at, string? text)
        {
            if (text is null)
            {
                return Error("Error: Parameter `file_text` is required for command: create");
            }

            if (IsDirectory(at) || FileAbove(at) is not null)
            {
                return Error($"Error: Cannot create {path}: {(IsDirectory(at) ? "it is a directory" : $"{Root}/{FileAbove(at)} is a file")}.");
            }

            return await WriteAsync(path, at, text).ConfigureAwait(false) ?? new($"File created successfully at: {path}");
        }

        public async Task<ToolOutput> ReplaceAsync(string path, string at, string? old, string replacement)
        {
            if (string.IsNullOrEmpty(old))
            {
                return Error("Error: Parameter `old_str` is required for command: str_replace");
            }

            if (!IsFile(at) || await store.ReadAsync(scope, at, cancellationToken).ConfigureAwait(false) is not { } content)
            {
                return Error($"Error: {Missing(path)}");
            }

            var found = new List<int>();
            for (var index = content.IndexOf(old, StringComparison.Ordinal); index >= 0; index = content.IndexOf(old, index + old.Length, StringComparison.Ordinal))
            {
                found.Add(index);
            }

            if (found.Count != 1)
            {
                return Error(found.Count == 0
                    ? $"No replacement was performed, old_str `{old}` did not appear verbatim in {path}."
                    : $"No replacement was performed. Multiple occurrences of old_str `{old}` in lines: {string.Join(", ", found.Select(index => content[..index].Count(c => c == '\n') + 1).Distinct())}. Please ensure it is unique");
            }

            var edited = string.Concat(content.AsSpan(0, found[0]), replacement, content.AsSpan(found[0] + old.Length));
            if (await WriteAsync(path, at, edited).ConfigureAwait(false) is { } refused)
            {
                return refused;
            }

            // A snippet of the edited lines, with four lines of context on either side.
            var (lines, first) = (Lines(edited), content[..found[0]].Count(c => c == '\n') + 1);
            var last = Math.Min(lines.Length, first + replacement.Count(c => c == '\n') + 4);
            return new($"The memory file has been edited. A snippet of {path} with line numbers:\n{Numbered(lines, Math.Max(1, first - 4), last)}");
        }

        public async Task<ToolOutput> InsertAsync(string path, string at, int? line, string? text)
        {
            if (line is null || text is null)
            {
                return Error("Error: Parameters `insert_line` and `insert_text` are required for command: insert");
            }

            if (!IsFile(at) || await store.ReadAsync(scope, at, cancellationToken).ConfigureAwait(false) is not { } content)
            {
                return Error($"Error: The path {path} does not exist");
            }

            var lines = Lines(content).ToList();
            if (line < 0 || line > lines.Count)
            {
                return Error($"Error: Invalid `insert_line` parameter: {line}. It should be within the range of lines of the file: [0, {lines.Count}]");
            }

            lines.Insert(line.Value, text.EndsWith('\n') ? text[..^1] : text);
            return await WriteAsync(path, at, string.Join('\n', lines)).ConfigureAwait(false) ?? new($"The file {path} has been edited.");
        }

        public async Task<ToolOutput> DeleteAsync(string path, string at)
        {
            if (at.Length == 0)
            {
                return Error($"Error: The memory directory {Root} itself cannot be deleted.");
            }

            if (!Exists(at))
            {
                return Error($"Error: The path {path} does not exist");
            }

            foreach (var file in IsFile(at) ? [new MemoryFile(at, 0)] : Under(at).ToList())
            {
                await store.DeleteAsync(scope, file.Path, cancellationToken).ConfigureAwait(false);
            }

            return new($"Successfully deleted {path}");
        }

        public async Task<ToolOutput> RenameAsync(string path, string at, string newPath, string to)
        {
            var problem = at.Length == 0 ? $"Error: The memory directory {Root} itself cannot be renamed."
                : !Exists(at) ? $"Error: The path {path} does not exist"
                : Exists(to) ? $"Error: The destination {newPath} already exists"
                : to.StartsWith(at + "/", StringComparison.Ordinal) ? $"Error: Cannot move {path} into itself."
                : FileAbove(to) is { } file ? $"Error: Cannot move to {newPath}: {Root}/{file} is a file."
                : null;
            if (problem is not null)
            {
                return Error(problem);
            }

            foreach (var moved in IsFile(at) ? [new MemoryFile(at, 0)] : Under(at).ToList())
            {
                await store.RenameAsync(scope, moved.Path, to + moved.Path[at.Length..], cancellationToken).ConfigureAwait(false);
            }

            return new($"Successfully renamed {path} to {newPath}");
        }

        /// <summary>Writes the file; returns the refusal when its text is longer than <see cref="MaxFileLength"/>.</summary>
        private async Task<ToolOutput?> WriteAsync(string path, string at, string text)
        {
            if (text.Length > MaxFileLength)
            {
                return Error($"Error: {path} would hold {text.Length} characters; a memory file holds at most {MaxFileLength}. Keep it shorter, or split it.");
            }

            await store.WriteAsync(scope, at, text, cancellationToken).ConfigureAwait(false);
            return null;
        }

        private static string Size(long bytes) => bytes switch
        {
            < 1024 => FormattableString.Invariant($"{bytes}B"),
            < 1024 * 1024 => FormattableString.Invariant($"{bytes / 1024.0:0.0}K"),
            _ => FormattableString.Invariant($"{bytes / (1024.0 * 1024):0.0}M"),
        };
    }
}
