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
/// retries off, so the model gateway is the only retry policy.
/// </summary>
public sealed class ClaudeProvider : IModelProvider, IDisposable
{
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
            TurnScopedMessages = MidConversationSystem.Any(prefix => model.StartsWith(prefix, StringComparison.Ordinal)),
            ProviderTools = ClaudeRequest.ProviderTools.Keys.ToHashSet(StringComparer.Ordinal),
        };
    }

    public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parameters = ClaudeRequest.From(request);
        var reply = new ClaudeReply(request.Tools, request.History);

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
