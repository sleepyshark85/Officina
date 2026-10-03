using System.Collections.Concurrent;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// The CLI's human channel: each approval, question and sign-off is numbered and waits in the queue until the owner
/// answers it by its number. Only the asking agent waits, so with nobody at the console the run goes on and requests
/// queue up until their deadline (HITL-05).
/// </summary>
/// <param name="output">Where each request is shown as it arrives.</param>
/// <param name="prefix">What the owner types before a command: nothing in <c>sof run</c>, a slash in <c>sof chat</c>.</param>
internal sealed class OwnerQueue(TextWriter output, string prefix = "") : IHumanChannel
{
    private readonly ConcurrentDictionary<int, (HumanRequest Request, TaskCompletionSource<HumanAnswer> Answer)> waiting = new();
    private int numbered;

    /// <summary>What the owner types before a command.</summary>
    public string Prefix => prefix;

    /// <summary>Where requests and everything else for the owner are shown.</summary>
    public TextWriter Output => output;

    /// <summary>What waits for the owner now, by number.</summary>
    public IEnumerable<(int Number, HumanRequest Request)> Waiting =>
        waiting.OrderBy(entry => entry.Key).Select(entry => (entry.Key, entry.Value.Request));

    public async ValueTask<HumanAnswer> AskAsync(HumanRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var number = Interlocked.Increment(ref numbered);
        var answer = new TaskCompletionSource<HumanAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting[number] = (request, answer);
        output.WriteLine($"#{number} {Describe(number, request)}");
        try
        {
            return await answer.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            waiting.TryRemove(number, out _);
        }
    }

    /// <summary>
    /// Answers a waiting request with a command (approve, deny, change or answer); returns what to tell the owner, if anything.
    /// Without a <paramref name="number"/>, the only request that waits is answered, and the owner is told which; with several,
    /// none is, and they are listed. A command of the wrong kind is refused and the request keeps waiting, so a reply never
    /// approves an approval.
    /// </summary>
    public string? Answer(int? number, string command, HumanAnswer answer)
    {
        if (number is null)
        {
            var all = Waiting.ToList();
            if (all.Count != 1)
            {
                return all.Count == 0
                    ? "error: nothing waits for you."
                    : $"error: {all.Count} requests wait for you; type {prefix}{command} with the number of one:\n"
                        + string.Join("\n", all.Select(entry => $"  #{entry.Number} {Kind(entry.Request.Kind)} from {entry.Request.Agent}: {entry.Request.Summary.Split('\n')[0]}"));
            }

            var only = all[0].Number;
            return Answer(only, command, answer) ?? Done(command, only);
        }

        if (!waiting.TryGetValue(number.Value, out var entry))
        {
            return NotWaiting();
        }

        var kind = entry.Request.Kind;
        var fits = command switch
        {
            "approve" => kind != HumanRequestKind.Question,
            "deny" => true, // on a question, it declines
            "change" => kind == HumanRequestKind.Approval,
            _ => kind == HumanRequestKind.Question,
        };
        if (!fits)
        {
            var needs = command switch { "answer" => "a question", "change" => "an approval", _ => "an approval or sign-off" };
            return $"error: #{number} is not {needs}.";
        }

        return entry.Answer.TrySetResult(answer) ? null : NotWaiting();
    }

    /// <summary>A request, and how to answer it by its number.</summary>
    public string Describe(int number, HumanRequest request) => request.Kind switch
    {
        HumanRequestKind.Approval => Lines(
            $"{request.Agent} asks to run {request.Tool} {request.Arguments?.GetRawText()}: {request.Summary}.",
            $"Answer with {prefix}approve {number}, {prefix}deny {number} or {prefix}change {number} <json>."),
        HumanRequestKind.Question => Lines($"{request.Agent} asks: {request.Summary}", $"Answer with {prefix}answer {number} <text> or {prefix}deny {number}."),
        _ => Lines($"{request.Agent} needs your sign-off: {request.Summary}", $"Answer with {prefix}approve {number} or {prefix}deny {number}."),
    };

    /// <summary>That nothing waits with the number typed, and which numbers do.</summary>
    private string NotWaiting() => Waiting.Select(entry => $"#{entry.Number}").ToList() switch
    {
        [] => "error: nothing waits for you.",
        [var one] => $"error: nothing waits for you with that number; {one} does.",
        var numbers => $"error: nothing waits for you with that number; {string.Join(", ", numbers)} do.",
    };

    private static string Kind(HumanRequestKind kind) => kind switch
    {
        HumanRequestKind.Approval => "approval",
        HumanRequestKind.Question => "question",
        _ => "sign-off",
    };

    /// <summary>What was done to the only request that waited, as the owner did not name it.</summary>
    private static string Done(string command, int number) => command switch
    {
        "approve" => $"approved #{number}.",
        "deny" => $"denied #{number}.",
        "change" => $"approved #{number} with your change.",
        _ => $"answered #{number}.",
    };

    /// <summary>
    /// A request and how to answer it, on one line; a request of several lines, such as a plan, has its lines indented
    /// under the first and the instruction on a line of its own.
    /// </summary>
    private static string Lines(string request, string instruction) =>
        request.Contains('\n', StringComparison.Ordinal) ? $"{request.ReplaceLineEndings("\n  ")}\n{instruction}" : $"{request} {instruction}";
}
