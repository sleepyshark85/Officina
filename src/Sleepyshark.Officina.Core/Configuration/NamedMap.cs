using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// An immutable map of named items, such as agents or model profiles. Names are case-sensitive and
/// enumerated in ordinal order, so output built from a map never depends on the order it was written in.
/// </summary>
[CollectionBuilder(typeof(NamedMap), nameof(NamedMap.Create))]
[SuppressMessage("Naming", "CA1710", Justification = "\"Named map\" is the configuration reference's term.")]
public sealed class NamedMap<T> : IReadOnlyDictionary<string, T>, IEquatable<NamedMap<T>>
{
    private readonly ImmutableSortedDictionary<string, T> items;

    public NamedMap(IEnumerable<KeyValuePair<string, T>> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var builder = ImmutableSortedDictionary.CreateBuilder<string, T>(StringComparer.Ordinal);
        foreach (var (name, value) in items)
        {
            builder[name] = value;
        }

        this.items = builder.ToImmutable();
    }

    [SuppressMessage("Design", "CA1000", Justification = "Like ImmutableArray<T>.Empty.")]
    public static NamedMap<T> Empty { get; } = new([]);

    public int Count => items.Count;

    public IEnumerable<string> Keys => items.Keys;

    public IEnumerable<T> Values => items.Values;

    public T this[string key] => items[key];

    public bool ContainsKey(string key) => items.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out T value) => items.TryGetValue(key, out value);

    /// <summary>A copy with <paramref name="name"/> set to <paramref name="value"/>.</summary>
    public NamedMap<T> With(string name, T value) => new(items.SetItem(name, value));

    public IEnumerator<KeyValuePair<string, T>> GetEnumerator() => items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(NamedMap<T>? other) =>
        other is not null
        && items.Count == other.items.Count
        && items.All(pair => other.items.TryGetValue(pair.Key, out var value) && EqualityComparer<T>.Default.Equals(pair.Value, value));

    public override bool Equals(object? obj) => Equals(obj as NamedMap<T>);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (name, value) in items)
        {
            hash.Add(name, StringComparer.Ordinal);
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => "{" + string.Join(", ", items.Select(pair => $"{pair.Key}: {pair.Value}")) + "}";

    public static implicit operator NamedMap<T>(Dictionary<string, T> items) => new(items);
}

public static class NamedMap
{
    public static NamedMap<T> Create<T>(ReadOnlySpan<KeyValuePair<string, T>> items) => new(items.ToArray());

    public static NamedMap<T> Of<T>(params (string Name, T Value)[] items) =>
        new(items.Select(item => KeyValuePair.Create(item.Name, item.Value)));
}
