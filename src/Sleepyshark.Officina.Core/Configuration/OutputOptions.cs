using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// What an agent's output must be before the turn completes with it (OUT-01 to OUT-04). There is no setting that
/// accepts output a check failed (INV-09).
/// </summary>
public sealed record OutputOptions
{
    [Setting("`text`, or `structured`: JSON that must match `schema`.", Example = "\"structured\"")]
    public OutputFormat Format { get; init; } = OutputFormat.Text;

    [Setting("The JSON Schema that structured output must match, as JSON text.",
        Example = """ "{ \"type\": \"object\", \"required\": [\"total\"] }" """)]
    public string? Schema { get; init; }

    [Setting("How many times output goes back to the model with its problems before the turn is handed off: structured output that does not match the schema and, with `onCheckFailure: revise`, output that fails a check.",
        Example = "3")]
    [Range(0, int.MaxValue, ErrorMessage = "must be zero or more.")]
    public int Attempts { get; init; } = 2;

    [Setting("Checks, by name in `checks`, that the output must pass, run in this order. The first that fails decides.",
        Example = """["no-secrets", "style"]""")]
    public IReadOnlyList<string> Checks { get; init; } = [];

    [Setting("What a failed check, or citation rule, does: `handoff` the turn, or `revise`: its findings, masked, go back to the model, which replies again, within `attempts`.",
        Example = "\"revise\"")]
    public CheckFailure OnCheckFailure { get; init; }

    [Setting("`off`; `resolve`: every id the output cites as `[cite:<id>]` must be a citation in the run record; or `required`: as `resolve`, and the output must cite at least one. Output that fails hands the turn off. Unset means `resolve` when the knowledge capability is on, otherwise `off`.",
        Example = "\"required\"")]
    public CitationRule? Citations { get; init; }
}

public enum OutputFormat
{
    Text,
    Structured,
}

public enum CheckFailure
{
    Handoff,
    Revise,
}

public enum CitationRule
{
    Off,
    Resolve,
    Required,
}
