using Sleepyshark.Officina.Core.Capabilities;
using Sleepyshark.Officina.Core.Configuration.Validation;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Hosting.Tests;

/// <summary>Capabilities in files (CAP-01…03, TEST-08): off by default, <c>true</c> as shorthand, and nothing added while off.</summary>
public sealed class CapabilityConfigurationTests : IDisposable
{
    private readonly FakeCapability fake = new();
    private readonly ConfigurationFolder folder;

    public CapabilityConfigurationTests()
    {
        folder = new ConfigurationFolder { Capabilities = new CapabilityRegistry([fake]) };
    }

    public void Dispose() => folder.Dispose();

    [Fact]
    public void A_capability_that_is_off_adds_no_tools_storage_or_settings()
    {
        folder.Write("sof.json", """{ "capabilities": { "fake": { "enabled": false, "size": 7 } } }""");

        var configuration = folder.Load();

        Assert.True(configuration.IsValid, string.Join('\n', configuration.Errors));
        var contributions = new CapabilityRegistry([fake]).Compose(configuration.Options);
        Assert.Empty(contributions.Tools);
        Assert.Empty(contributions.Stores);
        Assert.Equal(0, fake.Contributions);
        Assert.Equal(["capabilities.fake.enabled"], configuration.Settings().Where(setting => setting.Path.StartsWith("capabilities", StringComparison.Ordinal)).Select(setting => setting.Path));
    }

    [Fact]
    public void A_capability_nobody_mentions_is_off()
    {
        var setting = Assert.Single(folder.Load().Settings(), setting => setting.Path.StartsWith("capabilities", StringComparison.Ordinal));

        Assert.Equal(("capabilities.fake.enabled", "false"), (setting.Path, setting.Value));
    }

    [Fact]
    public void True_turns_a_capability_on_and_its_required_settings_with_it()
    {
        folder.Write("sof.json", """{ "capabilities": { "fake": true } }""");

        var configuration = folder.Load();

        var error = Assert.Single(configuration.Errors);
        Assert.Equal((ValidationPhase.Shape, "capabilities.fake.endpoint", "is required but not set."), (error.Phase, error.Path, error.Problem));
        Assert.True(configuration.Options.IsEnabled("fake"));
    }

    [Fact]
    public void A_capability_that_is_on_adds_its_tools_storage_and_settings()
    {
        folder.Write("sof.json", """{ "capabilities": { "fake": { "enabled": true, "endpoint": "https://fake" } } }""");

        var configuration = folder.Load();

        Assert.True(configuration.IsValid, string.Join('\n', configuration.Errors));
        var contributions = new CapabilityRegistry([fake]).Compose(configuration.Options);
        Assert.Equal(["builtin:fake.ping"], contributions.Tools);
        Assert.Equal(["fake-store"], contributions.Stores);
        Assert.Equal(
            ["capabilities.fake.enabled", "capabilities.fake.endpoint", "capabilities.fake.size"],
            configuration.Settings().Where(setting => setting.Path.StartsWith("capabilities", StringComparison.Ordinal)).Select(setting => setting.Path));
    }

    [Fact]
    public void An_environment_variable_can_switch_a_capability_off()
    {
        folder.Write("sof.json", """{ "capabilities": { "fake": { "enabled": true, "endpoint": "https://fake" } } }""");

        var configuration = folder.Load(variables: new() { ["SOF__capabilities__FAKE"] = "false" });

        Assert.True(configuration.IsValid, string.Join('\n', configuration.Errors));
        Assert.False(configuration.Options.IsEnabled("fake"));
        Assert.Equal("environment variable SOF__capabilities__FAKE", configuration.OriginOf("capabilities.fake.enabled").ToString());
    }
}
