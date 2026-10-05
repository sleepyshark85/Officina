using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Sleepyshark.Officina;

/// <summary>One run of an agent (ARCHITECTURE §5). Every run ends in a <see cref="RunEnded"/>; model behaviour never throws.</summary>
internal static class RunEngine
{
    /// <summary>The most model calls one run makes; a run that needs more ends as <see cref="StopReason.IterationLimit"/>.</summary>
    internal const int MaxModelCalls = 25;

    public static async IAsyncEnumerable<RunEvent> StreamAsync(
        AgentDefinition agent, Conversation conversation, string message, string? context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        conversation.StartRun();
        var span = Telemetry.StartRun(agent, conversation, message);
        try
        {
            using var audit = new AuditRecorder(agent, conversation, span);
            span?.SetTag("officina.run.id", audit.Run);
            await audit.RecordAsync(AuditKind.RunStarted).ConfigureAwait(false);
            var result = new StrongBox<RunResult>();
            await foreach (var runEvent in LoopAsync(agent, conversation, message, context, audit, span, result, cancellationToken).ConfigureAwait(false))
            {
                yield return runEvent;
            }

            var (outcome, detail) = result.Value switch
            {
                Stopped stopped => ($"Stopped: {stopped.Reason}", stopped.Detail),
                Failed failed => ($"Failed: {failed.Reason}", failed.Error),
                _ => ("Completed", null),
            };
            await audit.RecordAsync(AuditKind.RunEnded, outcome: outcome, detail: detail, usage: result.Value!.Usage).ConfigureAwait(false);
            Telemetry.EndRun(span, agent, result.Value);
            yield return new RunEnded(result.Value);
        }
        finally
        {
            // A run the host abandoned still ends its span.
            span?.Dispose();
            conversation.EndRun();
        }
    }

    /// <summary>Calls the model and runs the tools it asks for, until a stop that ends the run; leaves the run's result in <paramref name="result"/>.</summary>
    private static async IAsyncEnumerable<RunEvent> LoopAsync(
        AgentDefinition agent, Conversation conversation, string message, string? context, AuditRecorder audit, Activity? span,
        StrongBox<RunResult> result, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var fingerprint = agent.Fingerprint();
        if (conversation.Fingerprint is { } bound && bound != fingerprint)
        {
            result.Value = new Failed(
                FailureReason.PrefixMismatch,
                "The agent's tools, instructions or model settings differ from those this conversation was started with. Start a new conversation.",
                default);
            yield break;
        }

        // The user message and run context enter the conversation only with the reply that answers them, so a run that
        // gets no reply leaves the conversation as it was, and valid for the next request.
        ImmutableArray<Message> pending = context is null
            ? [Message.Of(Role.User, message)]
            : [Message.Of(Role.User, message), Message.Of(Role.Operator, context)];
        var usage = default(Usage);
        for (var calls = 1; ; calls++)
        {
            if (calls > MaxModelCalls)
            {
                result.Value = new Stopped(StopReason.IterationLimit, null, usage);
                yield break;
            }

            var reply = new Reply();
            await foreach (var runEvent in CallModelAsync(agent, conversation.Messages.AddRange(pending), span, reply, cancellationToken).ConfigureAwait(false))
            {
                yield return runEvent;
            }

            usage += reply.Usage;
            var toolCalls = reply.Blocks.Select(block => block.ToolCall).OfType<ToolCall>().ToList();

            // A reply cut off before its stop reason is not appended (AGT-05), nor is one without content. Nor is one that
            // requested tools but stopped for another reason (output limit, context full, refusal, end, unknown): its calls
            // do not run, as its last tool input may be cut short (the SDK keeps an empty one), and appending it would leave
            // calls without results, which the provider rejects. The run ends with the result its stop reason maps to; an end with calls fails, as a completed
            // run would report an answer the history does not hold.
            if (reply.Error is not null || reply.Stop is null || reply.Blocks.Count == 0 || (toolCalls.Count > 0 && reply.Stop.Reason != ModelStopReason.ToolUse))
            {
                result.Value = reply.Error is not null ? new Failed(FailureReason.ModelError, agent.Redact(reply.Error), usage)
                    : reply.Stop is null ? new Stopped(StopReason.Cancelled, null, usage)
                    : toolCalls.Count > 0 && reply.Stop.Reason == ModelStopReason.End ? new Failed(FailureReason.UnexpectedStop, "The model's reply asked for tools but did not stop for them.", usage)
                    : Result(reply.Stop, reply.Blocks, usage);
                yield break;
            }

            conversation.Bind(fingerprint);
            foreach (var appended in pending.Add(new Message(Role.Assistant, reply.Blocks.ToImmutable())))
            {
                conversation.Append(appended);
                yield return new ConversationAppended(conversation, appended);
            }

            pending = [];
            if (reply.Stop.Reason != ModelStopReason.ToolUse || toolCalls.Count == 0)
            {
                result.Value = Result(reply.Stop, reply.Blocks, usage);
                yield break;
            }

            // A reply's calls all get results, in one message, even when the run is cancelled meanwhile (CTX-06, AGT-05).
            // The pipeline's events stream while it runs. A host that stops reading them abandons the run: the tools are
            // cancelled, and the run waits for them, so none runs on after it, nor on a conversation another run may take.
            var events = Channel.CreateUnbounded<RunEvent>(new UnboundedChannelOptions { SingleReader = true });
            using var tools = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var running = RunToolsAsync(new ToolPipeline(agent, audit, span, events.Writer), toolCalls, events.Writer, tools.Token);
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

    /// <summary>What one model call returned.</summary>
    private sealed class Reply
    {
        public ImmutableArray<ContentBlock>.Builder Blocks { get; } = ImmutableArray.CreateBuilder<ContentBlock>();

        public Usage Usage { get; set; }

        public ModelStopped? Stop { get; set; }

        public string? Error { get; set; }

        public int Retries { get; set; }

        /// <summary>How long the first text took to arrive, if any did.</summary>
        public TimeSpan? FirstText { get; set; }
    }

    /// <summary>Makes one model call, streaming its events, and leaves what it returned in <paramref name="reply"/>.</summary>
    private static async IAsyncEnumerable<RunEvent> CallModelAsync(
        AgentDefinition agent, ImmutableArray<Message> messages, Activity? run, Reply reply, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new ModelRequest(agent.Tools, agent.Instructions, messages);
        var (started, span) = (agent.Time.GetTimestamp(), Telemetry.StartModelCall(agent, run));
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
                        yield return new UsageReported(received.Usage);
                        break;
                    case ModelStopped stopped:
                        reply.Stop = stopped;
                        break;
                }
            }

            if (stream is not null)
            {
                var disposing = stream;
                stream = null;
                var disposed = await TryAsync(async () => await disposing.DisposeAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

                // Once the stop reason has arrived the reply is complete, and a failure to close the call does not lose it.
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

            Telemetry.EndModelCall(
                span, agent, started, reply.Usage, reply.Stop, reply.Error, reply.Retries, reply.FirstText, string.Concat(reply.Blocks.Select(block => block.Text)));
        }
    }

    private static RunResult Result(ModelStopped stop, IEnumerable<ContentBlock> blocks, Usage usage) => stop.Reason switch
    {
        ModelStopReason.End => new Completed(string.Concat(blocks.Select(block => block.Text)), usage),
        ModelStopReason.MaxTokens => new Stopped(StopReason.OutputLimit, null, usage),
        ModelStopReason.Refusal => new Stopped(StopReason.Refusal, stop.Detail, usage),
        ModelStopReason.ContextFull => new Stopped(StopReason.ContextFull, null, usage),
        ModelStopReason.ToolUse => new Failed(FailureReason.UnexpectedStop, "The model stopped to use tools but called none.", usage),
        _ => new Failed(FailureReason.UnexpectedStop, $"The model stopped for a reason the run cannot act on: {stop.Detail ?? stop.Reason.ToString()}.", usage),
    };

    /// <summary>Runs a step of the model call; returns the failure's message, or null when it succeeded or the run was cancelled.</summary>
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
#pragma warning disable CA1031 // Any failure of the model ends the run as failed (principle 8).
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return exception.Message;
        }
    }
}
