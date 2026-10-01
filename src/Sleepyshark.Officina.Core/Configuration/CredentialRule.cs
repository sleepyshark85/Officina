using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// Configuration never holds secrets (CFG-09, INV-06): a value shaped like a key or token is rejected, and so is a
/// literal under a name that says it is a credential. Messages never repeat the value.
/// </summary>
internal static partial class CredentialRule
{
    public static IEnumerable<ConfigurationError> Check(IEnumerable<SettingVisit> visits) =>
        visits.SelectMany(visit => visit.Value switch
        {
            string text => Text(visit.Path, visit.EntryName, text),
            JsonElement json => Json(visit.Path, visit.EntryName, json),
            _ => [],
        });

    private static IEnumerable<ConfigurationError> Text(string path, string? name, string text)
    {
        if (TokenShaped().IsMatch(text))
        {
            yield return Error(path, "looks like a credential.");
        }
        else if (name is not null && text.Length > 0 && CredentialName().IsMatch(name))
        {
            yield return Error(path, $"holds a literal value, but \"{name}\" names a credential.");
        }
    }

    private static IEnumerable<ConfigurationError> Json(string path, string? name, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Text(path, name, value.GetString()!),
        JsonValueKind.Object => value.EnumerateObject().SelectMany(property => Json($"{path}.{property.Name}", property.Name, property.Value)),
        JsonValueKind.Array => value.EnumerateArray().SelectMany((item, index) => Json($"{path}[{index}]", null, item)),
        _ => [],
    };

    private static ConfigurationError Error(string path, string problem) => new(ValidationPhase.Shape, path, problem,
        "Configuration never holds secrets: store the value in the secret source and refer to it as { \"secret\": \"NAME\" } (CFG-09).");

    // Anthropic, OpenAI-style, GitHub, Slack and AWS keys; JWTs; PEM private keys; bearer tokens.
    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_-]{8,}|\bsk-[A-Za-z0-9_-]{20,}|\bgh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|\bxox[abprs]-[A-Za-z0-9-]{10,}|\bAKIA[0-9A-Z]{16}\b|\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.|-----BEGIN [A-Z ]*PRIVATE KEY-----|\bBearer\s+[A-Za-z0-9._~+/-]{20,}")]
    private static partial Regex TokenShaped();

    [GeneratedRegex(@"(^|[a-z_-])(api[-_]?key|apikey|password|passwd|secret|token|credentials?|private[-_]?key)$", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialName();
}
