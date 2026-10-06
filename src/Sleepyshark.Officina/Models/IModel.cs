namespace Sleepyshark.Officina;

/// <summary>
/// A provider's model with fixed settings: the only way the core reaches a model. It retries transient failures itself;
/// a failure that remains is thrown, and the run fails.
/// </summary>
public interface IModel
{
    /// <summary>
    /// The model and every setting that shapes its requests, as text that changes when any of them does: it is part of the
    /// prefix fingerprint.
    /// </summary>
    string Settings { get; }

    /// <summary>The provider, as telemetry names it (<c>gen_ai.provider.name</c>), such as <c>anthropic</c>.</summary>
    string Provider { get; }

    /// <summary>The model's identifier, as telemetry names it (<c>gen_ai.request.model</c>).</summary>
    string Name { get; }

    /// <summary>What the model's tokens cost; null when unknown, and then they cost nothing in results and budgets.</summary>
    ModelPrice? Price { get; }

    /// <summary>What the provider supports beyond the basic contract; none unless the model declares it.</summary>
    ModelCapabilities Capabilities => ModelCapabilities.None;

    /// <summary>
    /// Sends one request and streams the reply: text deltas and complete blocks as they arrive, usage, and last a
    /// <see cref="ModelStopped"/>. Each retry is announced by a <see cref="ModelRetried"/>.
    /// </summary>
    IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken);
}
