using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Sleepyshark.Officina;

/// <summary>One run of an agent. Every run ends in a <see cref="RunEnded"/>; model behaviour never throws.</summary>
internal static class RunEngine
{
    /// <summary>The most model calls a run makes; one that needs more ends as <see cref="StopReason.IterationLimit"/>.</summary>
    internal const int MaxModelCalls = 25;

    public static async IAsyncEnumerable<RunEvent> StreamAsync(
        Agent agent, Conversation conversation, string message, RunOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        conversation.StartRun();
        var span = Telemetry.StartRun(agent, conversation, message);
        var ended = false;
        try
        {
            using var run = new RunScope(agent, conversation, span, options);
            span?.SetTag("officina.run.id", run.Audit.Run);
            span?.SetTag("officina.memory.scope", options.MemoryScope);
            await run.Audit.RecordAsync(AuditKind.RunStarted).ConfigureAwait(false);
            var result = new StrongBox<RunResult>();
            await foreach (var runEvent in LoopAsync(run, message, options.Context, result, cancellationToken).ConfigureAwait(false))
            {
                yield return runEvent;
            }

            result.Value = run.Spending.Report(result.Value!);

            var (outcome, detail) = result.Value switch
            {
                Stopped stopped => ($"Stopped: {stopped.Reason}", stopped.Detail),
                Failed failed => ($"Failed: {failed.Reason}", failed.Error),
                _ => ("Completed", null),
            };
            var ending = result.Value;
            await run.Audit.RecordAsync(AuditKind.RunEnded, entry => entry with { Outcome = outcome, Detail = detail, Usage = ending.Usage, Cost = ending.Cost })
                .ConfigureAwait(false);
            Telemetry.EndRun(span, agent, result.Value);
            ended = true;
            yield return new RunEnded(result.Value);
        }
        finally
        {
            // A run the host abandoned still ends its span, and is counted.
            if (!ended)
            {
                Telemetry.EndRun(span, agent, null);
            }

            conversation.EndRun();
        }
    }

    /// <summary>Calls the model and runs its tools until a stop ends the run; leaves the result in <paramref name="result"/>.</summary>
    private static async IAsyncEnumerable<RunEvent> LoopAsync(
        RunScope run, string message, string? context, StrongBox<RunResult> result, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (agent, conversation, audit, spending) = (run.Agent, run.Conversation, run.Audit, run.Spending);
        var fingerprint = run.Prefix.Fingerprint;
        if (conversation.Fingerprint is { } bound && bound != fingerprint)
        {
            result.Value = new Failed(
                FailureReason.PrefixMismatch,
                "The agent's tools, instructions or model settings differ from those this conversation was started with. Start a new conversation.",
                default);
            yield break;
        }

        var unavailable = await ToolSources.ConnectAsync(agent, audit, cancellationToken).ConfigureAwait(false);
        if (unavailable is not null || cancellationToken.IsCancellationRequested)
        {
            result.Value = unavailable is null ? new Stopped(StopReason.Cancelled, null, default) : new Failed(FailureReason.ToolSourceUnavailable, unavailable, default);
            yield break;
        }

        if (await AnswerInterruptedAsync(conversation, audit).ConfigureAwait(false) is { } interrupted)
        {
            yield return new ConversationAppended(conversation, interrupted);
        }

        // The message and context enter the conversation only with the reply that answers them, so a run that gets no reply
        // leaves the conversation valid for the next request.
        ImmutableArray<Message> pending = context is null
            ? [Message.Of(Role.User, message)]
            : [Message.Of(Role.User, message), Message.Of(Role.Operator, context)];
        for (var calls = 1; ; calls++)
        {
            var reached = spending.Reached();
            if (calls > MaxModelCalls || reached is not null)
            {
                result.Value = calls > MaxModelCalls
                    ? new Stopped(StopReason.IterationLimit, null, spending.Usage)
                    : new Stopped(StopReason.Budget, reached, spending.Usage);
                yield break;
            }

            var reply = new ModelReply();
            var limit = spending.OutputLimit();
            await foreach (var runEvent in CallModelAsync(run, conversation.Messages.AddRange(pending), limit, reply, cancellationToken).ConfigureAwait(false))
            {
                yield return runEvent;
            }

            spending.AddCall(reply.Usage);
            foreach (var (kind, detail) in reply.ContextEdits)
            {
                await audit.RecordAsync(kind, entry => entry with { Detail = detail }).ConfigureAwait(false);
            }

            var usage = spending.Usage;

            // A reply cut short by the lowered output limit stopped for the budget, once the budget allows no more.
            var budgetCut = limit is not null && reply.Stop?.Reason == ModelStopReason.MaxTokens ? spending.Reached() : null;
            var toolCalls = reply.Blocks.Select(block => block.ToolCall).OfType<ToolCall>().ToList();
            var (append, end) = Decide(agent, reply, toolCalls.Count > 0, usage, budgetCut);
            if (append)
            {
                conversation.Bind(fingerprint);
                foreach (var appended in pending.Add(new Message(Role.Assistant, reply.Blocks.ToImmutable())))
                {
                    conversation.Append(appended);
                    yield return new ConversationAppended(conversation, appended);
                }

                pending = [];
            }

            if (end is not null)
            {
                result.Value = end;
                yield break;
            }

            spending.ToolCalls += toolCalls.Count;

            // A reply's calls all get results, in one message, even if the run is cancelled meanwhile. If the host stops reading
            // the events, the tools are cancelled and awaited, so none outlives the run or touches a conversation another run takes.
            var events = Channel.CreateUnbounded<RunEvent>(new UnboundedChannelOptions { SingleReader = true });
            using var tools = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var running = RunToolsAsync(new ToolPipeline(run, events.Writer), toolCalls, events.Writer, tools.Token);
            var relayed = false;
            try
            {
                await foreach (var runEvent in events.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    yield return runEvent;
                }

                relayed = true;
            }
            finally
            {
                if (!relayed)
                {
                    await tools.CancelAsync().ConfigureAwait(false);
                    await running.ConfigureAwait(false);
                }
            }

            var results = await running.ConfigureAwait(false);
            var answer = new Message(Role.User, [.. results.Select(toolResult => new ContentBlock(toolResult))]);
            conversation.Append(answer);
            yield return new ConversationAppended(conversation, answer);
            if (cancellationToken.IsCancellationRequested)
            {
                result.Value = new Stopped(StopReason.Cancelled, null, usage);
                yield break;
            }
        }
    }

    /// <summary>The result of a call whose reply was never recorded, as the application stopped mid-reply.</summary>
    internal const string Interrupted = "The call was interrupted: the application stopped before its result was recorded, so it may or may not have taken effect.";

    private static async Task<ImmutableArray<ToolResult>> RunToolsAsync(
        ToolPipeline pipeline, List<ToolCall> calls, ChannelWriter<RunEvent> events, CancellationToken cancellationToken)
    {
        try
        {
            return await pipeline.RunAsync(calls, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            events.Complete();
        }
    }

    /// <summary>Makes one model call, streaming its events, and leaves what it returned in <paramref name="reply"/>.</summary>
    private static async IAsyncEnumerable<RunEvent> CallModelAsync(
        RunScope run, ImmutableArray<Message> messages, int? limit, ModelReply reply, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var agent = run.Agent;
        var request = new ModelRequest(run.Prefix, messages, limit);
        var (started, span) = (agent.Time.GetTimestamp(), Telemetry.StartModelCall(agent, run.Span));
        var streamed = false;
        IAsyncEnumerator<ModelEvent>? stream = null;
        try
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                reply.Error = await TryAsync(
                    () =>
                    {
                        stream = agent.Model.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
                        return ValueTask.CompletedTask;
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            while (stream is not null && reply.Stop is null && reply.Error is null && !cancellationToken.IsCancellationRequested)
            {
                ModelEvent? next = null;
                reply.Error = await TryAsync(async () => next = await stream.MoveNextAsync().ConfigureAwait(false) ? stream.Current : null, cancellationToken)
                    .ConfigureAwait(false);
                switch (next)
                {
                    case null when reply.Error is null && !cancellationToken.IsCancellationRequested:
                        reply.Error = "The model's reply ended without a stop reason.";
                        break;
                    case TextDelta delta:
                        reply.FirstText ??= agent.Time.GetElapsedTime(started);
                        streamed = true;
                        yield return new TextStreamed(delta.Text);
                        break;
                    case ModelRetried:
                        reply.Blocks.Clear();
                        reply.FirstText = null;
                        reply.Retries++;
                        Telemetry.Retried(agent);
                        if (streamed)
                        {
                            streamed = false;
                            yield return new ReplyRestarted();
                        }

                        break;
                    case BlockReceived received:
                        reply.Blocks.Add(received.Block);
                        break;
                    case UsageReceived received:
                        reply.Usage += received.Usage;
                        yield return new UsageReported(received.Usage, agent.Model.Price?.Cost(received.Usage) ?? 0);
                        break;
                    case ModelStopped stopped:
                        reply.Stop = stopped;
                        break;
                    case CompactionReported compacted:
                        reply.ContextEdits.Add((AuditKind.Compacted, Say($"{compacted.Tokens:N0} tokens summarized into {compacted.SummaryTokens:N0}.")));
                        Telemetry.Compacted(span, agent, compacted);
                        yield return new ConversationCompacted(compacted.Tokens, compacted.SummaryTokens);
                        break;
                    case ClearingReported cleared:
                        reply.ContextEdits.Add((AuditKind.Cleared, Say($"Results of {cleared.ToolCalls:N0} tool calls cleared: {cleared.Tokens:N0} tokens.")));
                        Telemetry.Cleared(span, agent, cleared);
                        yield return new ToolResultsCleared(cleared.Tokens, cleared.ToolCalls);
                        break;
                }
            }

            if (stream is not null)
            {
                var disposing = stream;
                stream = null;
                var disposed = await TryAsync(async () => await disposing.DisposeAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

                // Once the stop reason has arrived the reply is complete; failing to close the call does not lose it.
                reply.Error ??= reply.Stop is null ? disposed : null;
            }
        }
        finally
        {
            // Reached with the reply still open only when the host stopped reading the run's events.
            if (stream is not null)
            {
                await TryAsync(async () => await stream.DisposeAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            }

            Telemetry.EndModelCall(span, agent, started, reply);
        }
    }

    /// <summary>
    /// What a reply means: whether it is appended, with the messages it answers, and the result that ends the run, or null
    /// to run its tool calls and call the model again.
    /// </summary>
    /// <remarks>
    /// A reply cut off before its stop reason is not appended, nor is one without content, nor one that asked for tools but
    /// stopped for another reason: its calls do not run, as its last input may be cut short, and calls without results are
/// rejected by the provider.
    /// An end with calls fails, as a completed run would report an answer the history does not hold.
    /// </remarks>
    private static (bool Append, RunResult? End) Decide(Agent agent, ModelReply reply, bool hasCalls, Usage usage, string? budgetCut) =>
        (reply.Error, reply.Stop, reply.Blocks.Count > 0, hasCalls) switch
        {
            ({ } error, _, _, _) => (false, new Failed(FailureReason.ModelError, agent.Redact(error), usage)),
            (_, null, _, _) => (false, new Stopped(StopReason.Cancelled, null, usage)),
            (_, { } stop, false, _) => (false, Result(agent, stop, reply.Blocks, usage, budgetCut)),
            (_, { Reason: ModelStopReason.ToolUse }, _, true) => (true, null),
            (_, { Reason: ModelStopReason.End }, _, true) => (false, new Failed(FailureReason.UnexpectedStop, "The model's reply asked for tools but did not stop for them.", usage)),
            (_, { } stop, _, true) => (false, Result(agent, stop, reply.Blocks, usage, budgetCut)),
            (_, { } stop, _, false) => (true, Result(agent, stop, reply.Blocks, usage, budgetCut)),
        };

    /// <summary>The result a reply's stop reason maps to, when the run ends with it.</summary>
    private static RunResult Result(Agent agent, ModelStopped stop, IEnumerable<ContentBlock> blocks, Usage usage, string? budgetCut) => stop.Reason switch
    {
        ModelStopReason.End => Completed(agent, agent.Redact(string.Concat(blocks.Select(block => block.Text))), usage),
        ModelStopReason.MaxTokens when budgetCut is not null => new Stopped(StopReason.Budget, budgetCut, usage),
        ModelStopReason.MaxTokens => new Stopped(StopReason.OutputLimit, null, usage),
        ModelStopReason.Refusal => new Stopped(StopReason.Refusal, stop.Detail, usage),
        ModelStopReason.ContextFull => new Stopped(StopReason.ContextFull, null, usage),
        ModelStopReason.ToolUse => new Failed(FailureReason.UnexpectedStop, "The model stopped to use tools but called none.", usage),
        _ => new Failed(FailureReason.UnexpectedStop, $"The model stopped for a reason the run cannot act on: {stop.Detail ?? stop.Reason.ToString()}.", usage),
    };

    /// <summary>
    /// Gives error results to calls left unanswered when the application stopped mid-reply, so the conversation can resume:
    /// the provider rejects calls without results. Returns the message appended, or null when none was needed.
    /// </summary>
    private static async Task<Message?> AnswerInterruptedAsync(Conversation conversation, AuditRecorder audit)
    {
        if (conversation.Messages is not [.., { Role: Role.Assistant } last]
            || last.Blocks.Select(block => block.ToolCall).OfType<ToolCall>().ToList() is not { Count: > 0 } unanswered)
        {
            return null;
        }

        foreach (var call in unanswered)
        {
            await audit.RecordAsync(
                AuditKind.ToolEnded, entry => entry with { Tool = call.Name, CallId = call.Id, Input = call.Input, Outcome = "interrupted", Detail = Interrupted })
                .ConfigureAwait(false);
        }

        var interrupted = new Message(Role.User, [.. unanswered.Select(call => new ContentBlock(new ToolResult(call.Id, Interrupted, IsError: true)))]);
        conversation.Append(interrupted);
        return interrupted;
    }

    /// <summary>A completed run's result; with typed output, a reply that cannot be read fails the run, with no retry.</summary>
    private static RunResult Completed(Agent agent, string text, Usage usage) => agent.Output?.Read(text) switch
    {
        null => new Completed(text, usage),
        (_, { } error) => new Failed(FailureReason.InvalidOutput, error, usage),
        var (value, _) => new Completed(text, usage) { Output = value },
    };

    private static string Say(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>Runs a step of the model call; returns the failure's message, or null on success or cancellation.</summary>
    private static async ValueTask<string?> TryAsync(Func<ValueTask> step, CancellationToken cancellationToken)
    {
        try
        {
            await step().ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
#pragma warning disable CA1031 // Any model failure fails the run.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return exception.Message;
        }
    }
}
