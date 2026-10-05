using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace Sleepyshark.Officina.Claude;

/// <summary>How hard Claude thinks and how much it spends (MDL-03); always set, never left to the model's default.</summary>
public enum ClaudeEffort
{
    Low,
    Medium,
    High,
    XHigh,
    Max,
}

/// <summary>How long Claude keeps the cached prefix (CTX-03): five minutes, or an hour for users who reply slowly.</summary>
public enum CacheLifetime
{
    FiveMinutes,
    OneHour,
}

/// <summary>
/// Claude through the official Anthropic SDK's beta messages API (MDL-02, ARCHITECTURE §10). Every request streams, with
/// adaptive thinking and no refusal fallback (D10). Its settings are fixed once created (MDL-03). Transient failures are
/// retried here (MDL-04); the SDK's own retries are off.
/// </summary>
public sealed class ClaudeModel : IModel, IDisposable
{
    private readonly AnthropicClient client;
    private readonly TimeProvider time;

    /// <param name="apiKey">The API key; when null, the SDK finds credentials as it does by default (<c>ANTHROPIC_API_KEY</c>).</param>
    /// <param name="http">Sends the HTTP requests; tests pass one that answers with recorded responses.</param>
    /// <param name="time">The clock retries wait on; tests pass one that does not sleep.</param>
    public ClaudeModel(string? apiKey = null, HttpMessageHandler? http = null, TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        var httpClient = new HttpClient(new RetryAfterHandler(http ?? new HttpClientHandler(), this.time)) { Timeout = Timeout.InfiniteTimeSpan };
        client = apiKey is null
            ? new AnthropicClient { HttpClient = httpClient, MaxRetries = 0 }
            : new AnthropicClient { HttpClient = httpClient, MaxRetries = 0, ApiKey = apiKey };
    }

    /// <summary>The model's identifier, such as <c>claude-opus-5-5</c>.</summary>
    public required string Model { get; init; }

    public required ClaudeEffort Effort { get; init; }

    /// <summary>The longest reply, in tokens. Every request streams, so a long reply does not time out.</summary>
    public int MaxOutputTokens { get; init; } = 64_000;

    /// <summary>The lifetime of both cache points: the instructions' and the conversation tail's.</summary>
    public CacheLifetime CacheLifetime { get; init; } = CacheLifetime.FiveMinutes;

    /// <summary>What the model's tokens cost; by default its row of <see cref="ClaudePrices.Table"/>, if it has one.</summary>
    public ModelPrice? Price
    {
        get => field ?? ClaudePrices.Table.GetValueOrDefault(Model);
        init;
    }

    /// <summary>Server-side compaction and tool-result clearing, through the beta context management (ARCHITECTURE §10).</summary>
    public ModelCapabilities Capabilities => ModelCapabilities.Compaction | ModelCapabilities.ContextEditing;

    public string Provider => "anthropic";

    public string Name => Model;

    public string Settings =>
        $"claude model={Model} effort={ClaudeRequest.EffortWord(Effort)} max_tokens={MaxOutputTokens} cache={(CacheLifetime == CacheLifetime.OneHour ? "1h" : "5m")} thinking=adaptive";

    public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parameters = ClaudeRequest.Build(this, request);
        for (var attempt = 1; ; attempt++)
        {
            var events = new List<BetaRawMessageStreamEvent>();
            var asked = new StrongBox<TimeSpan?>();
            var (retry, tooLong) = (false, false);
            ClaudeException? failed = null;
            var stream = client.Beta.Messages.CreateStreaming(parameters, cancellationToken).GetAsyncEnumerator(cancellationToken);
            await using (stream.ConfigureAwait(false))
            {
                while (true)
                {
                    try
                    {
                        if (!await RetryAfterHandler.MoveNextAsync(stream, asked).ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    catch (Exception exception) when (ClaudeErrors.IsTransient(exception, cancellationToken) && attempt < ClaudeErrors.MaxAttempts)
                    {
                        retry = true;
                        break;
                    }
                    catch (Exception exception) when (ClaudeErrors.IsPromptTooLong(exception))
                    {
                        tooLong = true;
                        break;
                    }
                    catch (Exception exception) when (ClaudeErrors.Classify(exception, cancellationToken) is { } classified)
                    {
                        failed = classified;
                        break;
                    }

                    events.Add(stream.Current);
                    if (stream.Current.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                    {
                        yield return new TextDelta(text.Text);
                    }
                }
            }

            if (tooLong)
            {
                yield return new ModelStopped(ModelStopReason.ContextFull);
                yield break;
            }

            // The tokens of an attempt that failed mid-stream are counted too.
            if ((retry || failed is not null) && ClaudeReply.Partial(events) is var partial && partial != default)
            {
                yield return new UsageReceived(partial);
            }

            if (failed is not null)
            {
                throw failed;
            }

            if (!retry)
            {
                foreach (var modelEvent in await ClaudeReply.ReadAsync(events).ConfigureAwait(false))
                {
                    yield return modelEvent;
                }

                yield break;
            }

            yield return new ModelRetried();

            await Task.Delay(ClaudeErrors.Backoff(attempt, asked.Value), time, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => client.Dispose();
}
