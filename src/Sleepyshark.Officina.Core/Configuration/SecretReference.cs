using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// A secret, by name, written <c>{ "secret": "NAME" }</c>. Configuration holds only the name; the value is read
/// from the secret source when it is used (CFG-09).
/// </summary>
public sealed partial record SecretReference(
    [property: JsonPropertyName("secret")]
    [property: Setting("The secret's name: letters, digits and underscores.", Example = "\"ANTHROPIC_API_KEY\"")]
    string Name)
{
    /// <summary>Letters, digits and underscores, not starting with a digit.</summary>
    public static bool IsValidName(string name) => ValidName().IsMatch(name);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ValidName();
}
