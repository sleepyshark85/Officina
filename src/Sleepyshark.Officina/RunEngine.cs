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
        var fingerprint = agent.Fingerprint();
        if (conversation.Fingerprint is { } bound && bound != fingerprint)
        {
            yield return new RunEnded(new Failed(
                FailureReason.PrefixMismatch,
                "The agent's tools, instructions or model settings differ from those this conversation was started with. Start a new conversation.",
                default));
            yield break;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            yield return new RunEnded(new Stopped(StopReason.Cancelled, null, default));
            yield break;
        }

        conversation.Bind(fingerprint);
        yield return Append(conversation, Message.Of(Role.User, message));
        if (context is not null)
        {
            yield return Append(conversation, Message.Of(Role.Operator, context));
        }

        var request = new ModelRequest(agent.Tools, agent.Instructions, conversation.Messages);
        var blocks = ImmutableArray.CreateBuilder<ContentBlock>();
        var usage = default(Usage);
        ModelStopped? stop = null;
        string? error = null;
        await using (var reply = agent.Model.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken))
        {
            while (stop is null && error is null && !cancellationToken.IsCancellationRequested)
            {
                ModelEvent? next;
                try
                {
                    next = await reply.MoveNextAsync().ConfigureAwait(false) ? reply.Current : null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
#pragma warning disable CA1031 // Any failure of the model ends the run as failed (principle 8).
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    error = exception.Message;
                    break;
                }

                switch (next)
                {
                    case null:
                        error = "The model's reply ended without a stop reason.";
                        break;
                    case TextDelta delta:
                        yield return new TextStreamed(delta.Text);
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
        }

        // A reply cut off before its stop reason is not appended (AGT-05).
        if (stop is null)
        {
            yield return new RunEnded(error is null
                ? new Stopped(StopReason.Cancelled, null, usage)
                : new Failed(FailureReason.ModelError, error, usage));
            yield break;
        }

        if (blocks.Count > 0)
        {
            yield return Append(conversation, new Message(Role.Assistant, blocks.ToImmutable()));
        }

        yield return new RunEnded(stop.Reason switch
        {
            ModelStopReason.End => new Completed(string.Concat(blocks.Select(block => block.Text)), usage),
            ModelStopReason.MaxTokens => new Stopped(StopReason.OutputLimit, null, usage),
            ModelStopReason.Refusal => new Stopped(StopReason.Refusal, stop.Detail, usage),
            ModelStopReason.ContextFull => new Stopped(StopReason.ContextFull, null, usage),
            _ => new Failed(FailureReason.UnexpectedStop, $"The model stopped for a reason the run cannot act on: {stop.Detail ?? stop.Reason.ToString()}.", usage),
        });
    }

    private static ConversationAppended Append(Conversation conversation, Message message)
    {
        conversation.Append(message);
        return new ConversationAppended(conversation, message);
    }
}
