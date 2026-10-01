using System.Text.Json.Serialization;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// A secret, by name, written <c>{ "secret": "NAME" }</c>. The value is read from the secret source when it is used
/// (CFG-09); configuration should not hold secret values.
/// </summary>
public sealed record SecretReference
{
    public SecretReference(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    [JsonPropertyName("secret")]
    [Setting("The name of the secret, such as the environment variable that holds it.", Example = "\"ANTHROPIC_API_KEY\"")]
    public string Name { get; }
}
