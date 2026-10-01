namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>
/// Who a run acts for (ING-05). The host supplies it; it never comes from model output, tool arguments, agent messages
/// or content (INV-03). It has no business-specific fields: those go in <see cref="Attributes"/>.
/// </summary>
/// <param name="Id">The caller's identifier, or null for an anonymous caller, who holds the permissions configuration grants anonymous callers.</param>
/// <param name="Tenant">The caller's tenant, or null.</param>
/// <param name="Permissions">The permissions the caller holds.</param>
/// <param name="Attributes">Anything else the application knows about the caller.</param>
public sealed record Caller(string? Id, string? Tenant, IReadOnlySet<string> Permissions, IReadOnlyDictionary<string, string> Attributes)
{
    public static Caller Anonymous { get; } = new(null, null, new HashSet<string>(), new Dictionary<string, string>());
}
