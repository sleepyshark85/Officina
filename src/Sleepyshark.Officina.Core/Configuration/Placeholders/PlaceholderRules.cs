using Sleepyshark.Officina.Core.Configuration.Validation;

namespace Sleepyshark.Officina.Core.Configuration.Placeholders;

/// <summary>Where text with placeholders ends up, which decides the namespaces it may use (CFG-14).</summary>
public enum PlaceholderScope
{
    /// <summary>Instructions, tool descriptions, policies and memory: the same for every caller, work item and time.</summary>
    StablePrefix,

    /// <summary>The volatile context, rebuilt for every call, such as <c>context.operatingFacts</c>.</summary>
    Volatile,
}

/// <summary>The placeholder rules of CFG-14: which namespaces each scope may use, and how stable placeholders are filled.</summary>
public static class PlaceholderRules
{
    /// <summary>Namespaces whose values come from the definition or the project, so they may be in the stable prefix.</summary>
    public static IReadOnlyList<string> StableNamespaces { get; } = ["project", "agent"];

    /// <summary>Namespaces whose values depend on the caller, the work or the time, so they belong in the volatile context only.</summary>
    public static IReadOnlyList<string> VolatileNamespaces { get; } = ["caller", "work", "now"];

    private static readonly string[] SecretNamespaces = ["secret", "secrets", "env"];

    public static IEnumerable<ConfigurationError> Check(string text, string path, PlaceholderScope scope, StablePrefixValues values)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(values);
        foreach (var placeholder in Placeholder.FindAll(text))
        {
            if (Problem(placeholder, scope, values) is { } error)
            {
                yield return error with { Path = path };
            }
        }
    }

    /// <summary>Fills the placeholders of text in the stable prefix. Validation has already rejected any that cannot be filled.</summary>
    public static string FillStablePrefix(string text, StablePrefixValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Placeholder.Replace(text, placeholder => values.Resolve(placeholder)
            ?? throw new InvalidOperationException(Problem(placeholder, PlaceholderScope.StablePrefix, values)?.ToString() ?? $"Placeholder {placeholder.Text} cannot be filled."));
    }

    private static ConfigurationError? Problem(Placeholder placeholder, PlaceholderScope scope, StablePrefixValues values)
    {
        if (SecretNamespaces.Contains(placeholder.Namespace, StringComparer.OrdinalIgnoreCase))
        {
            return new ConfigurationError(ValidationPhase.Invariants, "",
                $"placeholder {placeholder.Text} would put a secret into text the model reads.",
                "Secrets are never placeholders; give the secret to the provider, tool or tool server that needs it (INV-06).");
        }

        if (VolatileNamespaces.Contains(placeholder.Namespace))
        {
            return scope == PlaceholderScope.StablePrefix
                ? new ConfigurationError(ValidationPhase.Prefix, "",
                    $"placeholder {placeholder.Text} is not allowed in the stable prefix.",
                    "Move it to context.operatingFacts (CTX-02, CFG-14).")
                : null;
        }

        if (!StableNamespaces.Contains(placeholder.Namespace))
        {
            return new ConfigurationError(ValidationPhase.References, "",
                $"placeholder {placeholder.Text} has the unknown namespace \"{placeholder.Namespace}\".",
                "Use project.* or agent.* here, or caller.*, work.* and now in context.operatingFacts." + Suggestions.DidYouMean(placeholder.Namespace, [.. StableNamespaces, .. VolatileNamespaces]));
        }

        if (placeholder.Format is not null)
        {
            return new ConfigurationError(ValidationPhase.References, "",
                $"placeholder {placeholder.Text} has a format, but only {{{{now}}}} takes one.",
                $"Write {{{{{placeholder.FullName}}}}}.");
        }

        if (values.Resolve(placeholder) is null)
        {
            return new ConfigurationError(ValidationPhase.References, "",
                $"placeholder {placeholder.Text} refers to {placeholder.FullName}, which is not set. A placeholder is never filled with an empty string.",
                values.FixFor(placeholder));
        }

        return null;
    }
}
