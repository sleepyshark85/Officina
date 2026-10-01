using Sleepyshark.Officina.Core.Capabilities;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Configuration.Model;
using Sleepyshark.Officina.Core.Configuration.Validation;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Core.Tests.Capabilities;

/// <summary>Capabilities on and off (CAP-01…03, TEST-08), with a fake capability.</summary>
public class CapabilityTests
{
    private readonly FakeCapability fake = new();

    [Fact]
    public void A_capability_is_off_by_default()
    {
        var options = new OfficinaOptions();

        Assert.False(options.IsEnabled("fake"));
        Assert.False(CapabilityRegistry.DefaultSettings(fake).Enabled);
    }

    [Fact]
    public void A_capability_that_is_off_adds_no_tools_storage_rules_or_required_settings()
    {
        var registry = new CapabilityRegistry([fake]);
        var options = With(new FakeCapabilitySettings { Enabled = false, Endpoint = null, Size = 13 });

        var contributions = registry.Compose(options);
        var errors = Validate(registry, options);

        Assert.Empty(contributions.Tools);
        Assert.Empty(contributions.Stores);
        Assert.Equal(0, fake.Contributions);
        Assert.Empty(errors);
        Assert.Equal("""{"enabled":false}""", OptionsWriter.WriteObject(options.Capabilities["fake"], SettingsModel.For(registry).CapabilityShape("fake")!, SettingsModel.For(registry)).ToJsonString());
    }

    [Fact]
    public void A_capability_that_is_on_adds_its_tools_storage_rules_and_required_settings()
    {
        var registry = new CapabilityRegistry([fake]);
        var options = With(new FakeCapabilitySettings { Enabled = true, Size = 13 });

        var contributions = registry.Compose(options);
        var errors = Validate(registry, options);

        Assert.Equal(["builtin:fake.ping"], contributions.Tools);
        Assert.Equal(["fake-store"], contributions.Stores);
        Assert.Equal(1, fake.Contributions);
        Assert.Equal(["capabilities.fake.endpoint", "capabilities.fake.size"], errors.Select(error => error.Path));
        Assert.Equal([ValidationPhase.Shape, ValidationPhase.Tools], errors.Select(error => error.Phase));
    }

    [Fact]
    public void A_dependency_that_is_off_is_reported()
    {
        var team = new FakeCapability("team", "taskBoard");
        var registry = new CapabilityRegistry([team, new FakeCapability("taskBoard")]);
        var options = new OfficinaOptions
        {
            Capabilities = new Dictionary<string, CapabilitySettings> { ["team"] = new FakeCapabilitySettings { Enabled = true, Endpoint = "e" } },
        };

        var error = Assert.Single(Validate(registry, options));

        Assert.Equal(ValidationPhase.Capabilities, error.Phase);
        Assert.Equal("capabilities.team", error.Path);
        Assert.Equal("team needs taskBoard, which is off.", error.Problem);
        Assert.Equal("Turn on capabilities.taskBoard, or turn team off (CAP-03).", error.Fix);
    }

    [Fact]
    public void An_agent_can_use_only_capabilities_the_application_enabled()
    {
        var registry = new CapabilityRegistry([fake, new FakeCapability("other")]);
        var options = With(new FakeCapabilitySettings { Enabled = true, Endpoint = "e" }) with
        {
            Agents = new Dictionary<string, AgentDefinition> { ["a"] = new() { Instructions = "x", Capabilities = ["fake", "other", "sandbox"] } },
        };

        var errors = Validate(registry, options);

        Assert.Equal(["agents.a.capabilities[1]", "agents.a.capabilities[2]"], errors.Select(error => error.Path));
        Assert.Equal("Turn on capabilities.other, or remove it here (CAP-03).", errors[0].Fix);
        Assert.Equal("Available capabilities: fake, other.", errors[1].Fix);
    }

    [Fact]
    public void An_unregistered_capability_is_reported()
    {
        var options = With(new FakeCapabilitySettings { Enabled = true });

        var error = Assert.Single(Validate(CapabilityRegistry.Empty, options));

        Assert.Equal("capability \"fake\" is not available in this application.", error.Problem);
    }

    [Theory]
    [InlineData("Team")]
    [InlineData("task-board")]
    public void Capability_names_are_camel_case_identifiers(string name)
    {
        Assert.Throws<ArgumentException>(() => new CapabilityRegistry([new FakeCapability(name)]));
    }

    [Fact]
    public void A_capability_is_registered_once()
    {
        Assert.Throws<ArgumentException>(() => new CapabilityRegistry([new FakeCapability(), new FakeCapability()]));
    }

    private static OfficinaOptions With(FakeCapabilitySettings settings) =>
        new() { Capabilities = new Dictionary<string, CapabilitySettings> { ["fake"] = settings } };

    private static IReadOnlyList<ConfigurationError> Validate(CapabilityRegistry registry, OfficinaOptions options) =>
        new ConfigurationValidator(SettingsModel.For(registry)).Validate(options);
}
