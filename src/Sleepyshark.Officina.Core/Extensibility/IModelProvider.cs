namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>A model provider. The core knows providers only through this interface (MDL-01).</summary>
public interface IModelProvider
{
    ProviderCapabilities Capabilities { get; }

    IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken ct);
}
