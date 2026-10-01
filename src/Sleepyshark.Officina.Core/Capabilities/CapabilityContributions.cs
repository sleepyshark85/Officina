namespace Sleepyshark.Officina.Core.Capabilities;

/// <summary>
/// What the capabilities that are on add to an application. Tools and stores are named here; the tool
/// pipeline (S03) and storage (S08) give them their types.
/// </summary>
public sealed class CapabilityContributions
{
    private readonly SortedSet<string> tools = new(StringComparer.Ordinal);
    private readonly SortedSet<string> stores = new(StringComparer.Ordinal);

    /// <summary>The tools offered, such as <c>builtin:workspace.read_file</c>, sorted by name.</summary>
    public IReadOnlyCollection<string> Tools => tools;

    /// <summary>The storage the capabilities need, such as the conversation store, sorted by name.</summary>
    public IReadOnlyCollection<string> Stores => stores;

    public void AddTool(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        tools.Add(name);
    }

    public void AddStore(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        stores.Add(name);
    }
}
