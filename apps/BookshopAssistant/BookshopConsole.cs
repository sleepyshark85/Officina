using System.Collections.Concurrent;
using Sleepyshark.Officina;

namespace BookshopAssistant;

/// <summary>
/// The console (ARCHITECTURE §12.1): asks who is using it, then reads messages and commands, streams each reply with its
/// tool activity, asks approval for changes, and cancels a reply on request (APP-01, APP-03, APP-06, APP-13). It is also
/// the agent's approver: the run announces each approval in its event stream, the console asks the staff member there,
/// in order with everything shown before it, and hands the answer to the waiting run.
/// </summary>
public sealed class BookshopConsole(TextReader input, TextWriter output, TimeProvider time, bool echoInput) : IApprover
{
    private const string Help = """
        Commands:
          /help   Show this help.
          /quit   Leave the assistant.
        Anything else is a message to the assistant. Ctrl+C stops a reply in progress.
        """;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<Approval>> approvals = new();
    private CancellationTokenSource? reply;
    private Task<string?>? pendingRead;
    private bool atLineStart = true;

    /// <summary>Cancels the reply in progress (APP-03); false when there is none.</summary>
    public bool CancelReply()
    {
        var current = Volatile.Read(ref reply);
        if (current is null)
        {
            return false;
        }

        try
        {
            current.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The reply ended between the read and the cancel.
            return false;
        }
    }

    /// <summary>Runs the session until <c>/quit</c> or the end of input. <paramref name="agent"/> must have this console as its approver.</summary>
    public async Task RunAsync(AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        await output.WriteLineAsync("Bookshop Assistant. Type /help for commands.");
        var staffMember = await AskStaffMemberAsync();
        if (staffMember is null)
        {
            return;
        }

        var conversation = new Conversation();
        string? context = null;
        while (await ReadAsync("you> ") is { } line)
        {
            switch (line.Trim())
            {
                case "":
                    continue;
                case "/quit":
                    return;
                case "/help":
                    await output.WriteLineAsync(Help);
                    continue;
                case ['/', ..] command:
                    await output.WriteLineAsync($"Unknown command {command}. Type /help for commands.");
                    continue;
            }

            // The run context is sent at the start of the session and again only when it changes (APP-13).
            var current = BookshopAgent.Context(time.GetLocalNow(), staffMember);
            context = await ReplyAsync(agent, conversation, line, current == context ? null : current) ?? context;
        }
    }

    public async Task<Approval> ApproveAsync(Tool tool, ToolCall toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        var answer = approvals.GetOrAdd(toolCall.Id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            return await answer.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            approvals.TryRemove(toolCall.Id, out _);
        }
    }

    private async Task<string?> AskStaffMemberAsync()
    {
        while (await ReadAsync("Who is using the assistant? Your name: ") is { } name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                await output.WriteLineAsync($"Hello, {name.Trim()}.");
                return name.Trim();
            }
        }

        return null;
    }

    /// <summary>Streams one reply; returns the run context the conversation received, if it received one.</summary>
    private async Task<string?> ReplyAsync(AgentDefinition agent, Conversation conversation, string message, string? context)
    {
        using var cancellation = new CancellationTokenSource();
        Volatile.Write(ref reply, cancellation);
        string? appendedContext = null;
        var labelled = false;
        try
        {
            await foreach (var runEvent in agent.StreamAsync(conversation, message, context, cancellation.Token))
            {
                switch (runEvent)
                {
                    case TextStreamed text:
                        if (!labelled)
                        {
                            await WriteLineAsync("");
                            await WriteAsync("assistant> ");
                            labelled = true;
                        }

                        await WriteAsync(text.Text);
                        break;
                    case ReplyRestarted:
                        await WriteLineAsync("[The reply was interrupted and starts again.]");
                        break;
                    case ToolCallStarted started:
                        await WriteLineAsync($"  > {started.Call.Name} {started.Call.Input}");
                        break;
                    case ApprovalAsked asked:
                        await AskApprovalAsync(asked.Call, cancellation.Token);
                        break;
                    case ToolCallFinished { Result: var result } finished:
                        await WriteLineAsync(result.IsError
                            ? $"  < {finished.Call.Name}: error: {FirstLine(result.Content)}"
                            : $"  < {finished.Call.Name}: ok");
                        break;
                    case ConversationAppended { Message: { Role: Role.Operator } appended }:
                        appendedContext = appended.Text;
                        break;
                    case RunEnded { Result: var result }:
                        await WriteLineAsync(result switch
                        {
                            Completed => "",
                            Stopped { Reason: StopReason.Cancelled } => "[Cancelled.]",
                            Stopped stopped => $"[Stopped: {stopped.Reason}.]",
                            Failed failed => $"[Failed: {failed.Error}]",
                            _ => "",
                        });
                        break;
                }
            }
        }
        finally
        {
            Volatile.Write(ref reply, null);
        }

        await EndLineAsync();
        return appendedContext;
    }

    /// <summary>Shows the call's exact input and asks the staff member (APP-06); the waiting run gets the answer.</summary>
    /// <remarks>
    /// Cancelling the reply at the prompt takes effect at once: the run stops waiting for the answer, and the line being
    /// typed becomes the next message. A cancelled prompt leaves no answer behind.
    /// </remarks>
    private async Task AskApprovalAsync(ToolCall call, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            await WriteLineAsync($"  ? {call.Name} needs your approval. Its exact input:");
            await WriteLineAsync($"    {call.Input}");
            var answer = await ReadAsync("    Approve? [y/N] ", cancellationToken);
            var approval = answer?.Trim().ToUpperInvariant() is "Y" or "YES"
                ? Approval.Granted
                : Approval.Denied("the staff member declined");
            if (!cancellationToken.IsCancellationRequested)
            {
                approvals.GetOrAdd(call.Id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(approval);
            }
        }

        // A cancelled run no longer waits for the answer: drop any entry its wait or this answer left.
        if (cancellationToken.IsCancellationRequested)
        {
            approvals.TryRemove(call.Id, out _);
        }
    }

    /// <summary>Reads a line: null at the end of input, or when <paramref name="cancellationToken"/> is cancelled first.</summary>
    private async Task<string?> ReadAsync(string prompt, CancellationToken cancellationToken = default)
    {
        await EndLineAsync();
        await output.WriteAsync(prompt);
        await output.FlushAsync(CancellationToken.None);

        // The console's reader blocks, so the read runs aside; a read that a cancel abandons serves the next prompt.
        pendingRead ??= Task.Run(async () => await input.ReadLineAsync(), CancellationToken.None);
        string? line;
        try
        {
            line = await pendingRead.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            atLineStart = false;
            return null;
        }

        // The line can finish just as the cancel arrives, and WaitAsync then returns it instead of throwing. Keep it
        // pending so it serves the next prompt rather than being lost.
        if (cancellationToken.IsCancellationRequested)
        {
            atLineStart = false;
            return null;
        }

        pendingRead = null;
        if (echoInput)
        {
            await output.WriteLineAsync(line ?? "");
        }

        atLineStart = true;
        return line;
    }

    private async Task WriteAsync(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        await output.WriteAsync(text);
        await output.FlushAsync();
        atLineStart = text.EndsWith('\n');
    }

    private async Task WriteLineAsync(string text)
    {
        await EndLineAsync();
        if (text.Length > 0)
        {
            await output.WriteLineAsync(text);
        }
    }

    private async Task EndLineAsync()
    {
        if (!atLineStart)
        {
            await output.WriteLineAsync();
            atLineStart = true;
        }
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0];
}
