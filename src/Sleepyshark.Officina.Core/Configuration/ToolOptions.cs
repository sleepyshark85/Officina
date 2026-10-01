using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// One tool, by the name the model sees (TOOL-04). A tool's own declaration sets its kind and parallel safety;
/// configuration can narrow them but never loosen them.
/// </summary>
public sealed record ToolOptions
{
    [Setting("Where the tool comes from: `extension:<id>` for a tool the application registers, `mcp:<server>/<tool>` for a tool of a server in `toolServers`, `knowledge:<name>` to search a source in `knowledge`, `provider:<name>` for a tool the model provider runs itself, or `builtin:<name>` for a built-in tool: `record.propose_fact`, `record.propose_finding`, `record.propose_decision` and `record.cite` propose changes to the run record, and `artifact.page` reads an artifact, such as a trimmed result in full.",
        Example = "\"extension:Acme.CreateIssue\"")]
    [Required(ErrorMessage = Messages.Required)]
    public required string Source { get; init; }

    [Setting("`write` for a tool that changes something. Unset uses the tool's declaration, and `write` for a tool server's tools; a tool that declares itself `write` stays `write`.",
        Example = "\"write\"")]
    public ToolKind? Kind { get; init; }

    [Setting("Permissions the caller must hold to call the tool.", Example = """["issues:write"]""")]
    public IReadOnlyList<string> Permissions { get; init; } = [];

    [Setting("The tool's own gates, by name in `gates`. They run after the gates for all tools.", Example = """["issue-dedupe"]""")]
    public IReadOnlyList<string> Gates { get; init; } = [];

    [Setting("Why a write tool needs no gate of its own. Every write tool needs gates or this reason.", Example = "\"Writes only to a scratch folder.\"")]
    public string? GateExemption { get; init; }

    [Setting("Whether each call needs a human's approval. Unset means `always` for irreversible tools, otherwise `never`. For approval by rule, give the tool a `builtin:require-approval` gate with a condition.",
        Example = "\"always\"")]
    public Approval? Approval { get; init; }

    [Setting("The longest one call may take, as `hh:mm:ss`.", Example = "\"00:15:00\"")]
    [Range(typeof(TimeSpan), "00:00:00", "10675199.02:48:05.4775807", MinimumIsExclusive = true, ErrorMessage = "must be greater than zero.")]
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    [Setting("How many times a call is tried. Only timeouts and unavailable errors are retried, and irreversible tools never are.", Example = "3")]
    [Range(1, int.MaxValue, ErrorMessage = "must be at least 1.")]
    public int MaxAttempts { get; init; } = 1;

    [Setting("The most characters of a result that enter the conversation. The full result is kept as an artifact, which a `builtin:artifact.page` tool reads.", Example = "8000")]
    [Range(1, int.MaxValue, ErrorMessage = "must be at least 1.")]
    public int MaxResultLength { get; init; } = 32_000;

    [Setting("Whether calls may run at the same time as other calls. Unset uses the tool's declaration; `true` cannot loosen a tool that declares itself unsafe. Irreversible tools never run in parallel.",
        Example = "false")]
    public bool? ParallelSafe { get; init; }

    [Setting("Whether the tool's effects cannot be undone. Such a tool is a write tool, is carried out at most once for the same run and arguments, and needs approval unless `approval` says otherwise.",
        Example = "true")]
    public bool Irreversible { get; init; }

    [Setting("Whether personal data in the tool's results is masked before the model sees them, when masking is on.", Example = "true")]
    public bool MaskResults { get; init; }

    [Setting("Whether masked values are restored in the tool's arguments, so it receives the real ones, such as a tool that sends an email. Its results are masked again.",
        Example = "true")]
    public bool ReceivesMaskedValues { get; init; }

    [Setting("Whether the tool's results are untrusted content, such as fetched web pages. An agent that has read them is marked, and gates can act on the mark.",
        Example = "true")]
    public bool Untrusted { get; init; }

    [Setting("Why a provider tool is enabled. Required for `provider:` tools.", Example = "\"The lead researches unfamiliar libraries.\"")]
    public string? Reason { get; init; }

    /// <summary>The id of the application's tool, for an <c>extension:</c> source.</summary>
    public string? ExtensionId() => After(Source, "extension:");

    /// <summary>The server and the tool's name on it, as <c>&lt;server&gt;/&lt;tool&gt;</c>, for an <c>mcp:</c> source.</summary>
    public string? McpTool() => After(Source, "mcp:");

    /// <summary>The knowledge source the tool searches, for a <c>knowledge:</c> source.</summary>
    public string? KnowledgeSource() => After(Source, "knowledge:");

    /// <summary>The built-in tool, for a <c>builtin:</c> source.</summary>
    public string? BuiltinTool() => After(Source, "builtin:");

    /// <summary>The provider's name for its own tool, for a <c>provider:</c> source (TOOL-13).</summary>
    public string? ProviderTool() => After(Source, "provider:");

    internal static string? After(string? source, string prefix) =>
        source?.StartsWith(prefix, StringComparison.Ordinal) == true ? source[prefix.Length..] : null;
}
