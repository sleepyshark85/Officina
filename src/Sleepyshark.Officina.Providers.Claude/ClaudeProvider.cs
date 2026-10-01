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
    private readonly ProviderOptions options;
    private readonly ISecretSource secrets;
    private readonly HttpClient? http;
    private AnthropicClient? client;

    /// <param name="options">The provider's configuration.</param>
    /// <param name="secrets">
    /// Where the API key is read, at the first call. Pass the runner's <see cref="Core.Tools.KnownSecrets"/>, so the key is
    /// removed from whatever tools return (INV-06). Without a configured key, calls fail as unauthenticated.
    /// </param>
    /// <param name="http">Sends the requests, and is disposed with the provider; the SDK's own when null. Tests pass one that replays recorded responses.</param>
    public ClaudeProvider(ProviderOptions options, ISecretSource secrets, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);
        this.options = options;
        this.secrets = secrets;
        this.http = http;
    }

    /// <summary>Claude allows four cache markers per request, and runs its server tools (CTX-11, TOOL-13).</summary>
    public ProviderCapabilities Capabilities { get; } = new()
    {
        CacheBoundaries = 4, TurnScopedMessages = true, ProviderTools = ClaudeRequest.ProviderTools.Keys.ToHashSet(StringComparer.Ordinal),
    };

    public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parameters = ClaudeRequest.From(request);
        var reply = new ClaudeReply(request.Tools);
        var stream = (await ClientAsync(ct).ConfigureAwait(false)).Beta.Messages.CreateStreaming(parameters, ct).GetAsyncEnumerator(ct);
        await using var _ = stream.ConfigureAwait(false);
        var inputTooLong = false;
        while (true)
        {
            try
            {
                if (!await stream.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (AnthropicBadRequestException exception) when (exception.ResponseBody.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase))
            {
                // The SDK has no type of its own for it, so the API's message tells (DESIGN.md §9); the turn then shortens the history.
                inputTooLong = true;
                break;
            }
            catch (AnthropicException exception)
            {
                throw new ModelCallException(Classify(exception), exception);
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

    public void Dispose() => client?.Dispose();

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

    private async ValueTask<AnthropicClient> ClientAsync(CancellationToken ct)
    {
        if (client is null)
        {
            var key = options.ApiKey is { } reference ? await secrets.GetAsync(reference.Secret, ct).ConfigureAwait(false) : null;
            client = http is null ? new AnthropicClient { ApiKey = key, MaxRetries = 0 } : new AnthropicClient { ApiKey = key, MaxRetries = 0, HttpClient = http };
        }

        return client;
    }
}
