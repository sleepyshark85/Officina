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
    /// Returns a line the owner typed that is not an answer, a command or a message, for the session to act on.
    /// </summary>
    public async Task<string?> OfferAsync(string content, string suggestion, CancellationToken ct)
    {
        if (!interactive)
        {
            return null;
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
            var answer = (await read(ct))?.Trim();
            switch (answer)
            {
                case null:
                    return null;
                case "n" or "no":
                    status.WriteLine(Later);
                    return null;
                case var _ when answer.StartsWith('/') || answer.Any(char.IsWhiteSpace):
                    // A command, or a message: the owner went on without answering.
                    status.WriteLine(Later);
                    return answer;
                case "" or "y" or "yes":
                    answer = proposed;
                    break;
            }

            if (await SaveAsync(content, answer, Path.GetFileName(suggestion), configuration, ct))
            {
                return null;
            }
        }
    }

    /// <summary><c>/save [path]</c>: saves the output at the path, or at the suggestion, numbered so no file is overwritten.</summary>
    public async Task SaveAsync(string content, string suggestion, string? path, CancellationToken ct)
    {
        var configuration = options();
        if (configuration.Policies.Masking.HoldsToken(content))
        {
            status.WriteLine(TokensNote);
        }

        await SaveAsync(content, path ?? OutputFiles.Free(root, suggestion), Path.GetFileName(suggestion), configuration, ct);
    }

    /// <returns>Whether the file was written.</returns>
    private async Task<bool> SaveAsync(string content, string path, string fileName, OfficinaOptions configuration, CancellationToken ct)
    {
        if (OutputFiles.Resolve(root, path, fileName, configuration, out var refusal) is not var (full, relative))
        {
            status.WriteLine($"error: {refusal}");
            return false;
        }

        if (File.Exists(full))
        {
            if (!interactive)
            {
                status.WriteLine($"error: {relative} exists, and is not overwritten without asking; give another path, such as {OutputFiles.Free(root, relative)}.");
                return false;
            }

            status.WriteLine($"{relative} exists. Overwrite it? [y = yes, Enter = no]");
            if ((await read(ct))?.Trim() is not ("y" or "yes"))
            {
                status.WriteLine($"{relative} is left as it was.");
                return false;
            }
        }

        try
        {
            OutputFiles.Write(full, content);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            status.WriteLine($"error: {relative} was not saved: {exception.Message}");
            return false;
        }

        status.WriteLine($"Saved {relative}. It is not committed.");
        return true;
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
