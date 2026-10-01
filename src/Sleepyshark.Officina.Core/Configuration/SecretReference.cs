using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// A secret, by name, written <c>{ "secret": "NAME" }</c>. The value is read from the secret source when it is used
/// (CFG-09); configuration should not hold secret values.
/// </summary>
public sealed record SecretReference
{
    /// <param name="secret">The secret's name.</param>
    public SecretReference(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        Secret = secret;
    }

    [Setting("The name of the secret, such as the environment variable that holds it.", Example = "\"ANTHROPIC_API_KEY\"")]
    [Required(ErrorMessage = Messages.Required)]
    public string Secret { get; }
}
