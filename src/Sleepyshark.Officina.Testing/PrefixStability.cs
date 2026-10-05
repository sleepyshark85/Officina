using System.Text.Json;

namespace Sleepyshark.Officina.Testing;

/// <summary>
/// The prefix stability check of TEST-02: each request's tools, instructions, output schema and earlier messages are
/// byte-identical to the previous request's. Pass the requests of a scripted run, or of several runs, saves and resumes,
/// in the order they were sent.
/// </summary>
public static class PrefixStability
{
    /// <summary>What breaks the prefix, one line per difference; empty when the prefix is stable.</summary>
    public static IReadOnlyList<string> Problems(IEnumerable<ModelRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var sent = requests.ToList();
        var problems = new List<string>();
        for (var index = 1; index < sent.Count; index++)
        {
            var (previous, next, number) = (sent[index - 1], sent[index], index + 1);
            if (JsonSerializer.Serialize(previous.Tools) != JsonSerializer.Serialize(next.Tools))
            {
                problems.Add($"Request {number}: the tools differ from request {index}'s.");
            }

            if (previous.Instructions != next.Instructions)
            {
                problems.Add($"Request {number}: the instructions differ from request {index}'s.");
            }

            if (previous.OutputSchema != next.OutputSchema)
            {
                problems.Add($"Request {number}: the output schema differs from request {index}'s.");
            }

            if (next.Messages.Length < previous.Messages.Length)
            {
                problems.Add($"Request {number}: has {next.Messages.Length} messages, fewer than request {index}'s {previous.Messages.Length}.");
                continue;
            }

            for (var message = 0; message < previous.Messages.Length; message++)
            {
                if (JsonSerializer.Serialize(previous.Messages[message]) != JsonSerializer.Serialize(next.Messages[message]))
                {
                    problems.Add($"Request {number}: message {message + 1} differs from request {index}'s.");
                    break;
                }
            }
        }

        return problems;
    }
}
