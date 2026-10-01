using System.Text.RegularExpressions;

namespace Sleepyshark.Officina.Core.Configuration.Validation;

/// <summary>
/// Recognises values that look like credentials, which configuration never holds (CFG-09, INV-06).
/// It errs on the side of rejecting: a false alarm is fixed by rewording, a leaked key is not.
/// </summary>
public static partial class CredentialDetector
{
    /// <summary>Whether the text contains something shaped like a key, token or private key.</summary>
    public static bool LooksLikeCredential(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return KnownTokenFormat().IsMatch(value);
    }

    /// <summary>Whether a name says its value is a credential, such as <c>apiKey</c> or <c>password</c>.</summary>
    public static bool IsCredentialName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return CredentialName().IsMatch(name);
    }

    // Anthropic, OpenAI-style, GitHub, Slack, AWS, Google keys; JWTs; PEM private keys; bearer tokens.
    [GeneratedRegex(
        @"sk-ant-[A-Za-z0-9_-]{8,}|\bsk-[A-Za-z0-9_-]{20,}|\bgh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|\bxox[abprs]-[A-Za-z0-9-]{10,}|\bAKIA[0-9A-Z]{16}\b|\bAIza[0-9A-Za-z_-]{35}|\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.|-----BEGIN [A-Z ]*PRIVATE KEY-----|\bBearer\s+[A-Za-z0-9._~+/-]{20,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex KnownTokenFormat();

    [GeneratedRegex(@"(^|[a-z_-])(api[-_]?key|apikey|password|passwd|passphrase|secret|token|credentials?|private[-_]?key|access[-_]?key)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialName();
}
