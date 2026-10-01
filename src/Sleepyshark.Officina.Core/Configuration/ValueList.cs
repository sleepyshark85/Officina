using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>An immutable list that compares by its items, so Options records that hold lists compare by value.</summary>
[CollectionBuilder(typeof(ValueList), nameof(ValueList.Create))]
public sealed class ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
    private readonly ImmutableArray<T> items;

    public ValueList(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        this.items = [.. items];
    }

    [SuppressMessage("Design", "CA1000", Justification = "Like ImmutableArray<T>.Empty.")]
    public static ValueList<T> Empty { get; } = new([]);

    public int Count => items.Length;

    public T this[int index] => items[index];

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(ValueList<T>? other) => other is not null && items.SequenceEqual(other.items);

    public override bool Equals(object? obj) => Equals(obj as ValueList<T>);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => "[" + string.Join(", ", items) + "]";

    public static implicit operator ValueList<T>(T[] items) => new(items);

    public static implicit operator ValueList<T>(List<T> items) => new(items);
}

public static class ValueList
{
    public static ValueList<T> Create<T>(ReadOnlySpan<T> items) => new(items.ToArray());
}
