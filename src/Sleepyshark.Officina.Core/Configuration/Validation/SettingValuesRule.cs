using System.Globalization;
using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration.Model;

namespace Sleepyshark.Officina.Core.Configuration.Validation;

/// <summary>
/// Checks every setting's value against its declaration: required settings are set, numbers and
/// durations are in range, and nothing looks like a credential (phase 2). A value outside the range of
/// a setting that protects an invariant is reported as an attempt to weaken it (phase 10).
/// </summary>
internal sealed class SettingValuesRule : IConfigurationRule
{
    public ValidationPhase Phase => ValidationPhase.Shape;

    public IEnumerable<ConfigurationError> Check(ValidationContext context)
    {
        foreach (var visit in SettingsWalker.Walk(context.Options, context.Model))
        {
            var errors = visit.Property is { } property ? CheckProperty(visit, property) : [];
            foreach (var error in errors.Concat(CheckCredentials(visit)))
            {
                yield return error;
            }
        }
    }

    private static IEnumerable<ConfigurationError> CheckProperty(SettingVisit visit, SettingProperty property)
    {
        var phase = property.Info.Invariant is null ? ValidationPhase.Shape : ValidationPhase.Invariants;
        if (visit.Value is null || (visit.Value is string text && string.IsNullOrWhiteSpace(text) && property.Required))
        {
            if (property.Required)
            {
                yield return new ConfigurationError(ValidationPhase.Shape, visit.Path, "is required but not set.",
                    $"Add \"{property.Name}\" to {Parent(visit.Path)}. {property.Info.Description}");
            }
            else if (!property.Nullable)
            {
                yield return new ConfigurationError(phase, visit.Path, "cannot be removed.",
                    property.Info.Invariant is { } invariant
                        ? $"Set a value; it may be high, but it always exists ({invariant})."
                        : "Set a value, or leave the setting out to use its default.");
            }

            yield break;
        }

        if (Number(visit.Value) is not { } number)
        {
            yield break;
        }

        var info = property.Info;
        var below = !double.IsNaN(info.Minimum) && (info.ExclusiveMinimum ? number <= info.Minimum : number < info.Minimum);
        var above = !double.IsNaN(info.Maximum) && number > info.Maximum;
        if (!below && !above)
        {
            yield break;
        }

        var limit = below
            ? (info.ExclusiveMinimum ? "greater than " : "at least ") + Format(info.Minimum, visit.Value)
            : "at most " + Format(info.Maximum, visit.Value);
        yield return new ConfigurationError(phase, visit.Path, $"is {Format(number, visit.Value)}, but must be {limit}.",
            info.Invariant is { } protects
                ? $"A limit can be high, but never zero, negative or unlimited ({protects})."
                : $"Use a value {limit}.");
    }

    private static IEnumerable<ConfigurationError> CheckCredentials(SettingVisit visit)
    {
        if (visit.Value is string text && visit.Type.Kind == SettingKind.Text)
        {
            if (CredentialDetector.LooksLikeCredential(text))
            {
                yield return Credential(visit.Path, "looks like a credential.");
            }
            else if (visit.EntryName is { } name && CredentialDetector.IsCredentialName(name) && text.Length > 0)
            {
                yield return Credential(visit.Path, $"holds a literal value, but \"{name}\" names a credential.");
            }
        }
        else if (visit.Value is SettingValue value)
        {
            foreach (var path in CredentialsIn(value.Element, visit.Path, visit.EntryName))
            {
                yield return Credential(path, "looks like a credential.");
            }
        }
    }

    private static IEnumerable<string> CredentialsIn(JsonElement value, string path, string? name)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString()!;
                if (CredentialDetector.LooksLikeCredential(text) || (name is not null && CredentialDetector.IsCredentialName(name) && text.Length > 0))
                {
                    yield return path;
                }

                break;
            case JsonValueKind.Object:
                foreach (var child in value.EnumerateObject().SelectMany(property => CredentialsIn(property.Value, SettingPath.Child(path, property.Name), property.Name)))
                {
                    yield return child;
                }

                break;
            case JsonValueKind.Array:
                foreach (var child in value.EnumerateArray().SelectMany((item, index) => CredentialsIn(item, SettingPath.Item(path, index), null)))
                {
                    yield return child;
                }

                break;
        }
    }

    private static ConfigurationError Credential(string path, string problem) => new(
        ValidationPhase.Shape, path, problem,
        "Configuration never holds secrets: store the value in the secret source and refer to it as { \"secret\": \"NAME\" } (CFG-09).");

    private static double? Number(object value) => value switch
    {
        int number => number,
        long number => number,
        decimal number => (double)number,
        double number => number,
        TimeSpan duration => duration.TotalSeconds,
        _ => null,
    };

    private static string Format(double number, object kind) =>
        kind is TimeSpan ? Durations.Format(TimeSpan.FromSeconds(number)) : number.ToString(CultureInfo.InvariantCulture);

    private static string Parent(string path)
    {
        var cut = Math.Max(path.LastIndexOf('.'), path.LastIndexOf('['));
        return cut <= 0 ? "the configuration" : path[..cut];
    }
}
