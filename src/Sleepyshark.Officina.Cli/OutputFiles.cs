using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Tasks;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// Output saved as a file in the project, the owner's choice (<c>sof chat</c>'s offer and <c>/save</c>): which output is a
/// document, where it goes by default, which paths are refused, and the write. A file is written only inside the project, into
/// its checked-out working tree, and never committed.
/// </summary>
internal static partial class OutputFiles
{
    /// <summary>Output of this many lines, or characters, is a document.</summary>
    private const int DocumentLines = 40;

    private const int DocumentCharacters = 2000;

    /// <summary>Output with a Markdown heading is a document from this many lines, or characters: a heading alone is not enough.</summary>
    private const int HeadedLines = 15;

    private const int HeadedCharacters = 600;

    private const int SlugLength = 60;

    /// <summary>
    /// Whether the output is a document, which the session offers to save: long, Markdown with a heading that is not short, or a
    /// structured plan, a JSON object with a list of <c>steps</c> as a plan-and-execute planner writes.
    /// </summary>
    public static bool IsDocument(string output)
    {
        var text = output.Trim();
        var lines = text.Split('\n').Length;
        return text.Length >= DocumentCharacters || lines >= DocumentLines
            || (Heading(text) is not null && (text.Length >= HeadedCharacters || lines >= HeadedLines)) || IsPlan(Json(text));
    }

    /// <summary>
    /// The default path of the output, relative to the project: <c>docs/plans/</c> for a structured plan and <c>docs/</c> for
    /// anything else, named by the first Markdown heading, or else by the owner's message, with <c>.json</c> for JSON and
    /// <c>.md</c> otherwise.
    /// </summary>
    public static string Suggest(string output, string message)
    {
        var text = output.Trim();
        var json = Json(text);
        return IsPlan(json) ? $"docs/plans/{Slug(message)}.json" : $"docs/{Slug(Heading(text) ?? message)}{(json is null ? ".md" : ".json")}";
    }

    /// <summary>Where a team's plan goes by default: <c>docs/plans/&lt;date&gt;-&lt;slug of the goal&gt;.md</c>.</summary>
    public static string PlanPath(string goal, DateTimeOffset now) => $"docs/plans/{now:yyyy-MM-dd}-{Slug(goal)}.md";

    /// <summary>
    /// A team's plan as Markdown: the goal, then each task's title, description, acceptance criteria and dependencies. A cancelled
    /// task is not part of the plan.
    /// </summary>
    public static string PlanMarkdown(string goal, IReadOnlyList<BoardTask> tasks, DateTimeOffset now)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"# Plan: {FirstLine(goal)}\n\nThe team lead's plan, approved on {now:yyyy-MM-dd}.\n\n## Goal\n\n{goal.Trim()}\n");
        foreach (var task in tasks.Where(task => task.State != TaskState.Cancelled))
        {
            text.Append(CultureInfo.InvariantCulture, $"\n## {task.Title} (`{task.Id}`)\n");
            if (task.Description.Trim() is { Length: > 0 } description)
            {
                text.Append(CultureInfo.InvariantCulture, $"\n{description}\n");
            }

            if (task.AcceptanceCriteria.Count > 0)
            {
                text.Append("\nAcceptance criteria:\n\n").AppendJoin("", task.AcceptanceCriteria.Select(criterion => $"- {criterion}\n"));
            }

            if (task.DependsOn.Count > 0)
            {
                text.Append("\nDepends on: ").AppendJoin(", ", task.DependsOn.Select(id => $"`{id}`")).Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>The path, numbered if needed (<c>name-2.md</c>, <c>name-3.md</c>, …), so no file there is overwritten.</summary>
    public static string Free(string root, string relative)
    {
        var (folder, stem, extension) = (Path.GetDirectoryName(relative) ?? "", Path.GetFileNameWithoutExtension(relative), Path.GetExtension(relative));
        var path = relative;
        for (var number = 2; File.Exists(Path.Combine(root, path)) || Directory.Exists(Path.Combine(root, path)); number++)
        {
            path = Path.Combine(folder, $"{stem}-{number}{extension}").Replace('\\', '/');
        }

        return path;
    }

    /// <summary>
    /// The file a path the owner gave names: the project-relative path, or a folder to put <paramref name="fileName"/> in when it
    /// ends with a slash or is a folder. Refused, with why: a path outside the project (<c>..</c> is resolved), one through a
    /// symbolic link, one under <c>.sof/</c> or <c>.git</c>, the configuration (<c>sof.json</c>, <c>sof.*.json</c> and the files
    /// it extends), and the workspace's protected and hidden paths.
    /// </summary>
    public static (string Full, string Relative)? Resolve(string root, string path, string fileName, OfficinaOptions options, out string refusal)
    {
        refusal = "";
        root = Path.GetFullPath(root);
        if (path.EndsWith('/') || path.EndsWith('\\') || Directory.Exists(Path.Combine(root, path)))
        {
            path = Path.Combine(path, fileName);
        }

        var full = Path.GetFullPath(Path.Combine(root, path));
        var relative = Path.GetRelativePath(root, full);
        if (relative == "." || Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0] == "..")
        {
            refusal = $"{path} is outside the project, and only files in it are saved.";
            return null;
        }

        relative = relative.Replace('\\', '/');
        if (relative.Contains(':', StringComparison.Ordinal))
        {
            // On Windows, name:stream is a hidden stream of the file name.
            refusal = $"{relative} has a colon, which a saved file's path may not have.";
            return null;
        }

        var protectedPaths = new Matcher();
        protectedPaths.AddIncludePatterns(WorkspaceOptions.FixedProtectedPaths.Concat(options.Capabilities.Workspace.ProtectedPaths).Select(protectedPath => protectedPath.Path));
        if (protectedPaths.Match(relative).HasMatches)
        {
            refusal = $"{relative} is Officina's state, its configuration, or a protected path, which is not saved over.";
            return null;
        }

        // A link could lead out of the project, or to a protected path, so no path through one is followed.
        var at = root;
        foreach (var part in relative.Split('/'))
        {
            at = Path.Combine(at, part);
            var info = new FileInfo(at);
            if (info.LinkTarget is not null || ((info.Exists || Directory.Exists(at)) && info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            {
                refusal = $"{relative} goes through a symbolic link, which is not followed.";
                return null;
            }
        }

        return (full, relative);
    }

    /// <summary>
    /// Writes the file atomically: a temporary file next to it, then a rename. Its folders are created as needed, and a file it
    /// replaces keeps its permissions.
    /// </summary>
    public static void Write(string full, string content)
    {
        var folder = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, content.EndsWith('\n') ? content : content + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (!OperatingSystem.IsWindows() && File.Exists(full))
            {
                File.SetUnixFileMode(temporary, File.GetUnixFileMode(full)); // an overwritten file keeps its permissions
            }

            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>A file name from text: its first line, lower case, letters and digits with dashes between words; <c>output</c> if none.</summary>
    internal static string Slug(string text)
    {
        var slug = NotWord().Replace(FirstLine(text).ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > SlugLength)
        {
            slug = slug[..SlugLength] is var cut && cut.LastIndexOf('-') is > 0 and var dash ? cut[..dash] : cut;
        }

        return slug.Length > 0 ? slug : "output";
    }

    private static string FirstLine(string text) => text.Trim().Split('\n')[0].Trim();

    /// <summary>The text of the first Markdown heading, such as <c># Login requirements</c>; null if there is none.</summary>
    internal static string? Heading(string text) => MarkdownHeading().Match(text) is { Success: true } heading ? heading.Groups["text"].Value : null;

    private static JsonElement? Json(string text)
    {
        if (!text.StartsWith('{') && !text.StartsWith('['))
        {
            return null;
        }

        try
        {
            return JsonElement.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsPlan(JsonElement? json) =>
        json is { ValueKind: JsonValueKind.Object } plan && plan.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array;

    [GeneratedRegex(@"^#{1,6}[ \t]+(?<text>\S.*?)[ \t#]*\r?$", RegexOptions.Multiline)]
    private static partial Regex MarkdownHeading();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotWord();
}
