using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Sleepyshark.Officina;

/// <summary>One run of an agent (ARCHITECTURE §5). Every run ends in a <see cref="RunEnded"/>; model behaviour never throws.</summary>
internal static class RunEngine
{
    public static async IAsyncEnumerable<RunEvent> StreamAsync(
        AgentDefinition agent, Conversation conversation, string message, string? context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        conversation.StartRun();
        try
        {
            var fingerprint = agent.Fingerprint();
            if (conversation.Fingerprint is { } bound && bound != fingerprint)
            {
                yield return new RunEnded(new Failed(
                    FailureReason.PrefixMismatch,
                    "The agent's tools, instructions or model settings differ from those this conversation was started with. Start a new conversation.",
                    default));
                yield break;
            }

            // The user message and run context enter the conversation only with the reply that answers them, so a run that
            // gets no reply leaves the conversation as it was, and valid for the next request.
            ImmutableArray<Message> pending = context is null
                ? [Message.Of(Role.User, message)]
                : [Message.Of(Role.User, message), Message.Of(Role.Operator, context)];
            var request = new ModelRequest(agent.Tools, agent.Instructions, conversation.Messages.AddRange(pending));
            var blocks = ImmutableArray.CreateBuilder<ContentBlock>();
            var usage = default(Usage);
            ModelStopped? stop = null;
            string? error = null;
            IAsyncEnumerator<ModelEvent>? reply = null;
            try
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    error = await TryAsync(
                        () =>
                        {
                            reply = agent.Model.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
                            return ValueTask.CompletedTask;
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                while (reply is not null && stop is null && error is null && !cancellationToken.IsCancellationRequested)
                {
                    ModelEvent? next = null;
                    error = await TryAsync(async () => next = await reply.MoveNextAsync().ConfigureAwait(false) ? reply.Current : null, cancellationToken)
                        .ConfigureAwait(false);
                    switch (next)
                    {
                        case null when error is null && !cancellationToken.IsCancellationRequested:
                            error = "The model's reply ended without a stop reason.";
                            break;
                        case TextDelta delta:
                            yield return new TextStreamed(delta.Text);
                            break;
                        case ModelRestarted:
                            blocks.Clear();
                            yield return new ReplyRestarted();
                            break;
                        case BlockReceived received:
                            blocks.Add(received.Block);
                            break;
                        case UsageReceived received:
                            usage += received.Usage;
                            yield return new UsageReported(received.Usage);
                            break;
                        case ModelStopped stopped:
                            stop = stopped;
                            break;
                    }
                }

                if (reply is not null)
                {
                    var disposing = reply;
                    reply = null;
                    var disposed = await TryAsync(async () => await disposing.DisposeAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

                    // Once the stop reason has arrived the reply is complete, and a failure to close the call does not lose it.
                    error ??= stop is null ? disposed : null;
                }
            }
            finally
            {
                // Reached with the reply still open only when the host stopped reading the run's events.
                if (reply is not null)
                {
                    await TryAsync(async () => await reply.DisposeAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                }
            }

            // A reply cut off before its stop reason is not appended (AGT-05), nor is one without content.
            if (error is not null || stop is null || blocks.Count == 0)
            {
                yield return new RunEnded(
                    error is not null ? new Failed(FailureReason.ModelError, error, usage)
                    : stop is null ? new Stopped(StopReason.Cancelled, null, usage)
                    : Result(stop, blocks, usage));
                yield break;
            }

            conversation.Bind(fingerprint);
            foreach (var appended in pending.Add(new Message(Role.Assistant, blocks.ToImmutable())))
            {
                conversation.Append(appended);
                yield return new ConversationAppended(conversation, appended);
            }

            yield return new RunEnded(Result(stop, blocks, usage));
        }
        finally
        {
            conversation.EndRun();
        }
    }

    private static RunResult Result(ModelStopped stop, IEnumerable<ContentBlock> blocks, Usage usage) => stop.Reason switch
    {
        ModelStopReason.End => new Completed(string.Concat(blocks.Select(block => block.Text)), usage),
        ModelStopReason.MaxTokens => new Stopped(StopReason.OutputLimit, null, usage),
        ModelStopReason.Refusal => new Stopped(StopReason.Refusal, stop.Detail, usage),
        ModelStopReason.ContextFull => new Stopped(StopReason.ContextFull, null, usage),
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
