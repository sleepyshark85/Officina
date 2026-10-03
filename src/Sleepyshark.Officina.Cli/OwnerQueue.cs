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

    /// <summary>The numbers of the requests shown since the owner's last answer; under its own lock.</summary>
    private readonly HashSet<int> announced = [];

    /// <summary>What the owner types before a command.</summary>
    public string Prefix => prefix;

    /// <summary>Where requests and everything else for the owner are shown.</summary>
    public TextWriter Output => output;

    /// <summary>Called with each request the owner answers, and the answer, as the answer is given.</summary>
    public Action<HumanRequest, HumanAnswer>? Answered { get; set; }

    /// <summary>Called with the asking agent just before a request is shown, such as to show what the agent wrote that was folded.</summary>
    public Action<string>? Asking { get; set; }

    /// <summary>What waits for the owner now, by number.</summary>
    public IEnumerable<(int Number, HumanRequest Request)> Waiting =>
        waiting.OrderBy(entry => entry.Key).Select(entry => (entry.Key, entry.Value.Request));

    public async ValueTask<HumanAnswer> AskAsync(HumanRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var number = Interlocked.Increment(ref numbered);
        var answer = new TaskCompletionSource<HumanAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (announced)
        {
            announced.Add(number);
        }

        waiting[number] = (request, answer);
        Asking?.Invoke(request.Agent);
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
    /// Answers a waiting request with a command (approve, deny, change or answer); returns what to tell the owner: what was
    /// answered, or what was wrong. A command of the wrong kind is refused and the request keeps waiting, so a reply never
    /// approves an approval. Without a <paramref name="number"/>, the request that waits is answered only when it is the one
    /// the owner has seen: the only one that waits, with no other shown since the owner's last answer, so a request that
    /// arrived as another went away is refused. The first request to arrive alone after an answer can still be answered by
    /// a recalled or repeated line; the reply names it, and an irreversible call always needs its number.
    /// </summary>
    public string Answer(int? number, string command, HumanAnswer answer)
    {
        if (number is null)
        {
            var all = Waiting.ToList();
            List<int> gone;
            lock (announced)
            {
                gone = [.. announced.Where(shown => all.All(entry => entry.Number != shown)).Order()];
            }

            switch (all)
            {
                case []:
                    return "error: nothing waits for you.";
                case [var only] when gone.Count == 0 && !only.Request.Irreversible:
                    number = only.Number;
                    break;
                case [var only] when gone.Count == 0:
                    return $"error: #{only.Number} is irreversible, so type its number: {prefix}{command} {only.Number}.";
                case [var only]:
                    return $"error: {Numbers(gone)} {(gone.Count == 1 ? "is" : "are")} no longer waiting; #{only.Number} is new: {What(only.Request)}. Type {prefix}{command} {only.Number}.";
                default:
                    return $"error: {all.Count} requests wait for you; type {prefix}{command} with the number of one:\n"
                        + string.Join("\n", all.Select(entry => $"  #{entry.Number} {Kind(entry.Request.Kind)}, {What(entry.Request)}"));
            }
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

        if (!entry.Answer.TrySetResult(answer))
        {
            return NotWaiting();
        }

        waiting.TryRemove(number.Value, out _); // at once, as the asker's own removal may come after the owner's next command
        lock (announced)
        {
            announced.Clear();
        }

        Answered?.Invoke(entry.Request, answer);
        var done = command switch { "approve" => "approved", "deny" => "denied", "change" => "approved with your change", _ => "answered" };
        return $"{done} #{number}: {What(entry.Request)}";
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

    /// <summary>A request in a few words: the agent and the call it asks to make, or the first line of what it asks.</summary>
    private static string What(HumanRequest request) => request.Kind == HumanRequestKind.Approval
        ? Short($"{request.Agent} {request.Tool} {request.Arguments?.GetRawText()}".TrimEnd())
        : Short($"{request.Agent}: {request.Summary.Split('\n')[0]}");

    private static string Short(string text) => text.Length > 100 ? $"{text[..100]}…" : text;

    /// <summary>That nothing waits with the number typed, and which numbers do.</summary>
    private string NotWaiting() => Waiting.Select(entry => entry.Number).ToList() switch
    {
        [] => "error: nothing waits for you.",
        [var one] => $"error: nothing waits for you with that number; #{one} does.",
        var numbers => $"error: nothing waits for you with that number; {Numbers(numbers)} do.",
    };

    private static string Numbers(IEnumerable<int> numbers) => string.Join(", ", numbers.Select(number => $"#{number}"));

    private static string Kind(HumanRequestKind kind) => kind switch
    {
        HumanRequestKind.Approval => "approval",
        HumanRequestKind.Question => "question",
        _ => "sign-off",
    };

    /// <summary>
    /// A request and how to answer it, on one line; a request of several lines, such as a plan, has its lines indented
    /// under the first and the instruction on a line of its own.
    /// </summary>
    private static string Lines(string request, string instruction) =>
        request.Contains('\n', StringComparison.Ordinal) ? $"{request.ReplaceLineEndings("\n  ")}\n{instruction}" : $"{request} {instruction}";
}
