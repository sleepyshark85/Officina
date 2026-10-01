namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>The model profile of a model slot: either the name of a profile in <c>models</c>, or a profile written inline (MDL-03).</summary>
public sealed record ModelReference
{
    private ModelReference(string? name, ModelProfile? profile)
    {
        Name = name;
        Profile = profile;
    }

    /// <summary>The profile's name, or null for an inline profile.</summary>
    public string? Name { get; }

    /// <summary>The inline profile, or null for a named one.</summary>
    public ModelProfile? Profile { get; }

    public static ModelReference Named(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(name, null);
    }

    public static ModelReference Inline(ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new(null, profile);
    }

    public static implicit operator ModelReference(string name) => Named(name);

    public static implicit operator ModelReference(ModelProfile profile) => Inline(profile);

    public override string ToString() => Name ?? Profile!.ToString();
}
