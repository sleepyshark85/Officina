using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// The chat session's side of saving output as a file (<see cref="OutputFiles"/>): the offer after a document, and <c>/save</c>.
/// Only at a terminal is the owner asked anything; with piped input nothing is offered, and <c>/save</c> never overwrites a file.
/// </summary>
/// <param name="root">The project folder.</param>
/// <param name="options">The configuration as it is now, for its protected paths and masking.</param>
/// <param name="status">Where the session prints.</param>
/// <param name="read">The owner's next line, as the session reads it, never from the console directly; null at the end of the input.</param>
/// <param name="interactive">Whether the owner is at a terminal, so may be asked.</param>
internal sealed class OutputSaver(string root, Func<OfficinaOptions> options, StatusView status, Func<CancellationToken, Task<string?>> read, bool interactive)
{
    private const string Later = "Not saved; /save saves it later.";

    /// <summary>
    /// At a terminal, asks the owner whether to save the output at <paramref name="suggestion"/>, at another path, or not at all.
    /// An answer is Enter, y, yes, n or no in any case, or a path: one word with a slash or a file extension. Anything else is
    /// not an answer: a command or a message the owner went on with, which is returned for the session to take as typed, with the
    /// path saved at, if the output was saved. A cancelled question, by Ctrl+C, ends at once.
    /// </summary>
    public async Task<(string? Saved, string? Leftover)> OfferAsync(string content, string suggestion, CancellationToken ct)
    {
        if (!interactive)
        {
            return (null, null);
        }

        var configuration = options();
        var proposed = OutputFiles.Free(root, suggestion);
        if (MaskingNote(content, configuration) is { } note)
        {
            status.WriteLine(note);
        }

        while (true)
        {
            status.WriteLine($"Save this as {proposed}? [Enter = yes, n = no, or type another path]");
            var line = await read(ct);
            var answer = line?.Trim();
            string path;
            if (answer is null)
            {
                status.WriteLine(Later);
                return (null, null);
            }
            else if (Is(answer, "n", "no"))
            {
                status.WriteLine(Later);
                return (null, null);
            }
            else if (answer.Length == 0 || Is(answer, "y", "yes"))
            {
                path = proposed;
            }
            else if (IsPath(answer))
            {
                path = answer;
            }
            else
            {
                status.WriteLine(Later);
                return (null, line);
            }

            var (saved, leftover) = await SaveAsync(content, path, Path.GetFileName(suggestion), configuration, ct);
            if (saved is not null || leftover is not null || ct.IsCancellationRequested)
            {
                return (saved, leftover);
            }
        }
    }

    /// <summary>
    /// <c>/save [path]</c>: saves the output at the path, or at the suggestion, numbered so no file is overwritten. Returns the path
    /// saved at, if it was, and a line typed at the question whether to overwrite that is not an answer, for the session to take
    /// as typed.
    /// </summary>
    public async Task<(string? Saved, string? Leftover)> SaveAsync(string content, string suggestion, string? path, CancellationToken ct)
    {
        var configuration = options();
        if (configuration.Policies.Masking.HoldsToken(content))
        {
            status.WriteLine(TokensNote);
        }

        return await SaveAsync(content, path ?? OutputFiles.Free(root, suggestion), Path.GetFileName(suggestion), configuration, ct);
    }

    /// <summary>
    /// Whether an answer is a path rather than a word: no spaces, and a slash or a file extension, such as <c>specs/</c> or
    /// <c>login.md</c> (not <c>1.5</c>), and not a command, which starts with a slash. A path with spaces is given to <c>/save</c> in quotes.
    /// </summary>
    internal static bool IsPath(string answer) =>
        !answer.StartsWith('/') && !answer.Any(char.IsWhiteSpace) && (answer.Contains('/', StringComparison.Ordinal) || answer.Contains('\\', StringComparison.Ordinal) || IsExtension(Path.GetExtension(answer)));

    /// <summary>A file extension such as <c>.md</c> or <c>.json</c>: a letter, then letters or digits, so <c>1.5</c> or <c>main.cs?</c> is a word.</summary>
    private static bool IsExtension(string extension) =>
        extension.Length > 1 && char.IsAsciiLetter(extension[1]) && extension.Skip(1).All(char.IsAsciiLetterOrDigit);

    private static bool Is(string answer, params string[] words) => words.Any(word => string.Equals(answer, word, StringComparison.OrdinalIgnoreCase));

    /// <returns>The path of the file written, relative to the project, if it was, and a line typed at the question whether to overwrite that is not an answer.</returns>
    private async Task<(string? Saved, string? Leftover)> SaveAsync(string content, string path, string fileName, OfficinaOptions configuration, CancellationToken ct)
    {
        if (OutputFiles.Resolve(root, path, fileName, configuration, out var refusal) is not { } target)
        {
            status.WriteLine($"error: {refusal}");
            return (null, null);
        }

        var (full, relative) = target;
        var existed = File.Exists(full);
        if (existed)
        {
            if (!interactive)
            {
                status.WriteLine($"error: {relative} exists, and is not overwritten without asking; give another path, such as {OutputFiles.Free(root, relative)}.");
                return (null, null);
            }

            status.WriteLine($"{relative} exists. Overwrite it? [y = yes, Enter = no]");
            var line = await read(ct);
            var answer = line?.Trim();
            if (answer is null || !Is(answer, "y", "yes"))
            {
                status.WriteLine($"{relative} is left as it was.");
                return (null, answer is null || answer.Length == 0 || Is(answer, "n", "no") ? null : line);
            }
        }

        // The folders may have changed while the owner was asked: the path is checked again just before it is written.
        if (OutputFiles.Resolve(root, path, fileName, configuration, out refusal) != target || File.Exists(full) != existed)
        {
            status.WriteLine($"error: {relative} changed while you were asked, so it is not saved{(refusal.Length > 0 ? $": {refusal}" : ".")}");
            return (null, null);
        }

        try
        {
            OutputFiles.Write(full, content);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            status.WriteLine($"error: {relative} was not saved: {exception.Message}");
            return (null, null);
        }

        status.WriteLine($"Saved {relative}. It is not committed.");
        return (relative, null);
    }

    private const string TokensNote = "note: this holds masking tokens, such as [email-1], in place of the values they mask; the file gets the tokens.";

    /// <summary>For the offer: when masking is on, or the output holds masking tokens, what the file gets: the tokens, never the values they mask.</summary>
    private static string? MaskingNote(string content, OfficinaOptions configuration)
    {
        var masking = configuration.Policies.Masking;
        return masking.HoldsToken(content) ? TokensNote
            : masking.Enabled ? "note: masking is on, so personal data the agent saw shows as tokens such as [email-1]; this output holds none." : null;
    }
}
