using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// A knowledge source (CTX-04). Agents search it before each turn (<c>context.retrieval.beforeTurn</c>), through a tool
/// whose source is <c>knowledge:&lt;name&gt;</c>, or both.
/// </summary>
public sealed record KnowledgeOptions
{
    [Setting("The source the application registers, as `extension:<id>`.", Example = "\"extension:Acme.HandbookIndex\"")]
    [Required(ErrorMessage = Messages.Required)]
    public required string Use { get; init; }

    [Setting("Whether personal data in the passages is masked before the model sees them, when masking is on.", Example = "true")]
    public bool Mask { get; init; }

    /// <summary>The id of the application's knowledge source.</summary>
    public string? ExtensionId() => ToolOptions.After(Use, "extension:");
}
