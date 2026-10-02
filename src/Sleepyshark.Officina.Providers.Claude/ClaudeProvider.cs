using System.Net.Http;
using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models;
using Anthropic.Models.Beta.Messages;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;

namespace Sleepyshark.Officina.Providers.Claude;

/// <summary>
/// The Claude API (CLD-01), through the official Anthropic SDK (CLD-12), mapped as DESIGN.md §9 describes. Every call
/// streams (CLD-09). It uses the SDK's beta API, where turn-scoped system messages are typed, and turns the SDK's own
/// retries off, so the model gateway is the only retry policy. It shortens history with Claude's compaction (HIST-01), and
/// uses the features its configuration switches on (CLD-06).
/// </summary>
public sealed class ClaudeProvider : IModelProvider, IHistoryShortener, IDisposable
{
    /// <summary>The models that summarize a conversation on demand, as the API's documentation lists them: all current ones but Haiku.</summary>
    private static readonly string[] Compacts =
        ["claude-fable-5", "claude-mythos", "claude-opus-5", "claude-opus-4-8", "claude-opus-4-7", "claude-opus-4-6", "claude-sonnet-5", "claude-sonnet-4-6"];

    private static readonly string[] TaskBudgets = ["claude-opus-5", "claude-opus-4-8", "claude-opus-4-7", "claude-fable-5", "claude-sonnet-5-5"];

    private static readonly string[] RefusalFallbacks = ["claude-fable-5", "claude-opus-5", "claude-sonnet-5-5"];

    private static readonly string[] MidConversationSystem = ["claude-opus-5", "claude-opus-4-8", "claude-fable-5", "claude-mythos-5", "claude-sonnet-5-5"];

    private readonly ProviderOptions options;
    private readonly ISecretSource secrets;
    private readonly SemaphoreSlim clientGate = new(1, 1);
    private readonly RetryAfterHandler retryAfter;
    private readonly TimeProvider time;
    private AnthropicClient? client;

    /// <param name="options">The provider's configuration.</param>
    /// <param name="secrets">
    /// Where the API key is read, at the first call. Pass the runner's <see cref="Core.Tools.KnownSecrets"/>, so the key is
    /// removed from whatever tools return (INV-06). Without a configured key, calls fail as unauthenticated.
    /// </param>
    /// <param name="http">Sends the requests, and is disposed with the provider; the default one when null. Tests pass one that replays recorded responses.</param>
    /// <param name="time">The clock for the idle timeout; tests pass a fake one.</param>
    public ClaudeProvider(ProviderOptions options, ISecretSource secrets, HttpMessageHandler? http = null, TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);
        this.options = options;
        this.secrets = secrets;
        retryAfter = new RetryAfterHandler(http ?? new HttpClientHandler());
    }

    /// <summary>
    /// Claude allows four cache markers per request, and runs its server tools (CTX-11, TOOL-13). Only the Opus 5 and 4.8, Fable 5,
    /// Mythos 5 and Sonnet 5.5 models take a system message in the middle of a conversation, so a turn-scoped one; the
    /// others get the volatile context in the history (MDL-06).
    /// </summary>
    public ProviderCapabilities CapabilitiesOf(string model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new()
        {
            CacheBoundaries = 4,
            TurnScopedMessages = SystemMessages(model),
            Features = Features(model),
            ProviderTools = ClaudeRequest.ProviderTools.Keys.ToHashSet(StringComparer.Ordinal),
        };
    }

    public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await foreach (var modelEvent in CallAsync(ClaudeRequest.From(request, options.Features, SystemMessages(request.Profile.Model)), request, ct).ConfigureAwait(false))
        {
            yield return modelEvent;
        }
    }

    /// <summary>
    /// HIST-01, HIST-04: Claude summarizes the earlier turns (compaction on demand), and its signed summary takes their place, in a
    /// user message of its own before the current turn, which is kept unchanged. The summary call is paid for like any other.
    /// </summary>
    /// <exception cref="InvalidOperationException">There are no earlier turns, or Claude returned no summary.</exception>
    public async ValueTask<ShortenedHistory> ShortenAsync(ModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Compacts.Any(prefix => request.Profile.Model.StartsWith(prefix, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Model {request.Profile.Model} cannot summarize a conversation (compaction).");
        }

        if (request.TurnStart == 0)
        {
            throw new InvalidOperationException("There are no earlier turns to summarize, and the current turn is never shortened.");
        }

        var earlier = request with { History = request.History[..request.TurnStart] };
        var (summary, usage, stop) = ((Core.Messages.Content?)null, Usage.None, StopReason.Unknown);
        await foreach (var modelEvent in CallAsync(ClaudeRequest.Compaction(earlier, options.Features, SystemMessages(request.Profile.Model)), earlier, ct).ConfigureAwait(false))
        {
            switch (modelEvent)
            {
                case ContentReceived { Content: var content } when ClaudeRequest.TypeOf(content) == "compaction":
                    summary = content;
                    break;
                case UsageReported reported:
                    usage += reported.Usage;
                    break;
                case Stopped stopped:
                    stop = stopped.Reason;
                    break;
            }
        }

        return summary is null
            ? throw new InvalidOperationException($"Claude returned no summary of the earlier turns (stop reason {stop}).")
            : new ShortenedHistory([new Message(Core.Messages.Role.User, [summary]), .. request.History[request.TurnStart..]]) { Usage = usage };
    }

    /// <summary>
    /// The features each model has, as the API's documentation lists them: structured output and context editing on all current
    /// models; task budgets on Opus 5, 4.8 and 4.7, Fable 5 and Sonnet 5.5; the server-side refusal fallback on the models whose
    /// safety classifiers decline, Fable 5, Opus 5 and Sonnet 5.5.
    /// </summary>
    private static HashSet<string> Features(string model)
    {
        var features = new HashSet<string>(StringComparer.Ordinal) { "structuredOutput", "clearToolResults" };
        if (TaskBudgets.Any(prefix => model.StartsWith(prefix, StringComparison.Ordinal)))
        {
            features.Add("taskBudget");
        }

        if (RefusalFallbacks.Any(prefix => model.StartsWith(prefix, StringComparison.Ordinal)))
        {
            features.Add("refusalFallback");
        }

        return features;
    }

    private static bool SystemMessages(string model) => MidConversationSystem.Any(prefix => model.StartsWith(prefix, StringComparison.Ordinal));

    private async IAsyncEnumerable<ModelEvent> CallAsync(MessageCreateParams parameters, ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var reply = new ClaudeReply(request.Tools, request.History, request.Profile);

        // A call that goes quiet for longer than the timeout is given up on, so it cannot hold its place for ever.
        using var quiet = new CancellationTokenSource(System.Threading.Timeout.InfiniteTimeSpan, time);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct, quiet.Token);
        var stream = (await ClientAsync(ct).ConfigureAwait(false)).Beta.Messages.CreateStreaming(parameters, idle.Token).GetAsyncEnumerator(idle.Token);
        await using var _ = stream.ConfigureAwait(false);
        var inputTooLong = false;
        while (true)
        {
            try
            {
                quiet.CancelAfter(options.Timeout);
                if (!await stream.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                quiet.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);
            }
            catch (AnthropicBadRequestException exception) when (exception.ResponseBody.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase))
            {
                // The SDK has no type of its own for it, so the API's message tells (DESIGN.md §9); the turn then shortens the history.
                inputTooLong = true;
                break;
            }
            catch (AnthropicException exception)
            {
                throw new ModelCallException(Classify(exception), exception, retryAfter.Take());
            }
            catch (Exception exception) when (exception is HttpIOException or HttpRequestException || (exception is OperationCanceledException && !ct.IsCancellationRequested))
            {
                // A connection dropped mid-stream, or a call that timed out (ours or the SDK's), is not the caller's cancellation.
                throw new ModelCallException(ModelFailure.Transient, exception, retryAfter.Take());
            }

            foreach (var modelEvent in reply.Read(stream.Current))
            {
                yield return modelEvent;
            }
        }

        if (inputTooLong)
        {
            yield return new Stopped(StopReason.InputTooLong);
        }
    }

    public void Dispose()
    {
        client?.Dispose();
        retryAfter.Dispose();
        clientGate.Dispose();
    }

    /// <summary>
    /// CLD-08: by the SDK's exception type, then by the error type, which is all an error that arrives mid-stream has.
    /// What is left, such as 5xx statuses, connection errors and an overload mid-stream, may pass if tried again.
    /// </summary>
    private static ModelFailure Classify(AnthropicException exception) => exception switch
    {
        AnthropicRateLimitException or AnthropicServiceException { ErrorType: ErrorType.RateLimitError } => ModelFailure.RateLimited,
        AnthropicUnauthorizedException or AnthropicForbiddenException
            or AnthropicServiceException { ErrorType: ErrorType.AuthenticationError or ErrorType.PermissionError } => ModelFailure.Authentication,
        Anthropic4xxException
            or AnthropicServiceException { ErrorType: ErrorType.InvalidRequestError or ErrorType.NotFoundError or ErrorType.BillingError } => ModelFailure.InvalidRequest,
        _ => ModelFailure.Transient,
    };

    /// <summary>The client, created once by the first call, even when calls start together.</summary>
    private async ValueTask<AnthropicClient> ClientAsync(CancellationToken ct)
    {
        if (client is { } existing)
        {
            return existing;
        }

        await clientGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (client is null)
            {
                var key = options.ApiKey is { } reference ? await secrets.GetAsync(reference.Secret, ct).ConfigureAwait(false) : null;
                client = new AnthropicClient { ApiKey = key, MaxRetries = 0, Timeout = options.Timeout, HttpClient = new HttpClient(retryAfter, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan } };
            }

            return client;
        }
        finally
        {
            clientGate.Release();
        }
    }
}
