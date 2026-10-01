using Sleepyshark.Officina.Core.Capabilities;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Configuration.Validation;

namespace Sleepyshark.Officina.Testing;

/// <summary>The settings of <see cref="FakeCapability"/>.</summary>
public sealed record FakeCapabilitySettings : CapabilitySettings
{
    [Setting("Where the fake capability connects. Required while it is on.", Example = "\"https://fake.example.com\"", Required = true)]
    public string? Endpoint { get; init; }

    [Setting("How many items it keeps.", Example = "3", Minimum = 1)]
    public int Size { get; init; } = 3;
}

/// <summary>
/// A capability for tests: it offers one tool and one store, has a required setting, a rule and,
/// optionally, a dependency, and counts how often it was asked to contribute (CAP-02, TEST-08).
/// </summary>
public sealed class FakeCapability(string name = "fake", params string[] requires) : Capability<FakeCapabilitySettings>
{
    private int contributions;

    public override string Name { get; } = name;

    public override string Description => "A capability for tests.";

    /// <summary>How many times <see cref="ICapability.Contribute"/> was called.</summary>
    public int Contributions => contributions;

    /// <summary>The rule the capability adds; it reports an error when <see cref="FakeCapabilitySettings.Size"/> is 13.</summary>
    public override IEnumerable<IConfigurationRule> Rules => [new UnluckySizeRule(Name)];

    protected override IEnumerable<string> Requires(FakeCapabilitySettings settings) => requires;

    protected override void Contribute(FakeCapabilitySettings settings, CapabilityContributions contributions)
    {
        Interlocked.Increment(ref this.contributions);
        contributions.AddTool($"builtin:{Name}.ping");
        contributions.AddStore($"{Name}-store");
    }

    private sealed class UnluckySizeRule(string capability) : IConfigurationRule
    {
        public ValidationPhase Phase => ValidationPhase.Tools;

        public IEnumerable<ConfigurationError> Check(ValidationContext context)
        {
            if (context.Options.Capabilities.TryGetValue(capability, out var settings) && settings is FakeCapabilitySettings { Size: 13 })
            {
                yield return new ConfigurationError(Phase, $"capabilities.{capability}.size", "is 13.", "Use another size.");
            }
        }
    }
}
