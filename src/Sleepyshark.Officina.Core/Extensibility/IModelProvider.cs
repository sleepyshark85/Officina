namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>A model provider. The core knows providers only through this interface (MDL-01).</summary>
public interface IModelProvider
{
    /// <summary>What the provider supports for this model; models of one provider can differ (MDL-06).</summary>
    ProviderCapabilities CapabilitiesOf(string model);

    IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken ct);
}
