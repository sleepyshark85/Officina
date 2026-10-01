namespace Sleepyshark.Officina.Testing;

/// <summary>Items kept in memory with their tenant, in the order added; reads are limited to one tenant (SEC-02).</summary>
internal sealed class TenantRows<T>
{
    private readonly Lock gate = new();
    private readonly List<(string? Tenant, T Item)> rows = [];

    public IReadOnlyList<T> All
    {
        get
        {
            lock (gate)
            {
                return [.. rows.Select(row => row.Item)];
            }
        }
    }

    public void Add(string? tenant, T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (gate)
        {
            rows.Add((tenant, item));
        }
    }

    /// <summary>Adds an item unless a row of its tenant matches <paramref name="taken"/>, in one step.</summary>
    public bool TryAdd(string? tenant, T item, Func<T, bool> taken)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (gate)
        {
            if (rows.Any(row => row.Tenant == tenant && taken(row.Item)))
            {
                return false;
            }

            rows.Add((tenant, item));
            return true;
        }
    }

    public IReadOnlyList<T> Where(string? tenant, Func<T, bool> match)
    {
        lock (gate)
        {
            return [.. rows.Where(row => row.Tenant == tenant && match(row.Item)).Select(row => row.Item)];
        }
    }

    public void RemoveAll(Func<string?, T, bool> match)
    {
        lock (gate)
        {
            rows.RemoveAll(row => match(row.Tenant, row.Item));
        }
    }
}
