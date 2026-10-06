// Hello: a live chat with Claude through Officina. Needs ANTHROPIC_API_KEY. Type a message per line; an empty line or
// end of input quits. After each reply a status line shows the call's tokens and cache reads: from the second message
// on, the instructions and earlier turns are read from the cache.
using System.Globalization;
using Sleepyshark.Officina;
using Sleepyshark.Officina.Claude;

using var model = new ClaudeModel
{
    Model = "claude-opus-5-5",
    Effort = ClaudeEffort.Low,
    MaxOutputTokens = 2_000,
    CacheLifetime = CacheLifetime.OneHour,
};
var agent = new Agent { Model = model, Instructions = Instructions() };
var conversation = new Conversation();

Console.WriteLine("Hello: chat with Claude. An empty line quits.");
while (Console.ReadLine() is { Length: > 0 } line)
{
    Console.WriteLine($"> {line}");

    // The date is run context, sent after the message; the instructions never change.
    var context = $"Today is {DateTime.Now.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)}.";
    await foreach (var runEvent in agent.StreamAsync(conversation, line, new() { Context = context }))
    {
        switch (runEvent)
        {
            case TextStreamed text:
                Console.Write(text.Text);
                break;
            case ReplyRestarted:
                Console.WriteLine(" [the reply was interrupted and starts again]");
                break;
            case RunEnded { Result: var result }:
                Console.WriteLine();
                Console.WriteLine(Status(result));
                break;
        }
    }
}

static string Status(RunResult result)
{
    var usage = result.Usage;
    var outcome = result switch
    {
        Completed => "completed",
        Stopped stopped => $"stopped: {stopped.Reason}",
        Failed failed => $"failed: {failed.Error}",
        _ => "?",
    };
    return $"[{outcome} · input {usage.Input} · cache read {usage.CacheRead} · cache write {usage.CacheWrite} · output {usage.Output}]";
}

// Frozen instructions, long enough to pass the model's minimum cacheable prefix (512 tokens on Opus 5.5).
static string Instructions() =>
    """
    You are Hello, a friendly assistant in a small demonstration of the Officina library. Answer in one to three short
    sentences unless the user asks for more. Be warm, concrete and plain-spoken.

    House rules:
    """ + "\n" + string.Join("\n", Enumerable.Range(1, 40).Select(rule =>
        $"{rule}. When a question touches topic number {rule}, answer from general knowledge, say so when you are unsure, " +
        "never invent facts, figures or quotations, and offer one useful next step if it helps."));
