using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>A check of output (OUT-03). Only its result decides whether the output is accepted (INV-09).</summary>
public sealed record CheckOptions
{
    [Setting("The check the application registers, as `extension:<id>`.", Example = "\"extension:Acme.NoSecretsCheck\"")]
    [Required(ErrorMessage = Messages.Required)]
    public required string Use { get; init; }

    /// <summary>The id of the application's check.</summary>
    public string? ExtensionId() => ToolOptions.After(Use, "extension:");
}
