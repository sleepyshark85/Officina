using System.Text.RegularExpressions;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// A secret, by name. Configuration holds only the name; the value is read from the secret source at
/// the moment it is used (CFG-09). In files it is written <c>{ "secret": "NAME" }</c>.
/// </summary>
public sealed partial record SecretReference
{
    public SecretReference(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }

    /// <summary>Whether a name can be a secret name: letters, digits and underscores, not starting with a digit.</summary>
    public static bool IsValidName(string name) => ValidName().IsMatch(name);

    public override string ToString() => $"{{ \"secret\": \"{Name}\" }}";

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ValidName();
}
