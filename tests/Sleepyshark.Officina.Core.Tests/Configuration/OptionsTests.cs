using System.Text.Json;
using Sleepyshark.Officina.Core.Capabilities;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Configuration.Model;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Core.Tests.Configuration;

public class OptionsTests
{
    private static readonly SettingsModel Model = SettingsModel.For(new CapabilityRegistry([new FakeCapability()]));

    [Fact]
    public void Every_setting_is_documented_with_a_description_and_an_example_that_is_JSON()
    {
        var settings = AllShapes().SelectMany(shape => shape.Properties).ToArray();

        Assert.NotEmpty(settings);
        Assert.All(settings, setting =>
        {
            Assert.False(string.IsNullOrWhiteSpace(setting.Info.Description), $"{setting.Name} has no description.");
            Assert.NotNull(setting.Info.Example);
            JsonDocument.Parse(setting.Info.Example).Dispose();
        });
    }

    [Fact]
    public void The_defaults_are_the_safe_choice()
    {
        var options = new OfficinaOptions();

        Assert.Equal(PermissionMode.Ask, options.Run.PermissionMode);
        Assert.Empty(options.Capabilities);
        Assert.Empty(options.Agents);
        Assert.Equal(25m, options.Run.Budget.Cost);
        Assert.Equal(TimeSpan.FromHours(8), options.Run.Budget.Time);
        Assert.Equal("claude-opus-5-5", options.Models[ModelProfile.DefaultName].Model);
        Assert.Equal(new SecretReference("ANTHROPIC_API_KEY"), options.Providers["claude"].ApiKey);
    }

    [Fact]
    public void Only_instructions_have_no_default()
    {
        var required = AllShapes().SelectMany(shape => shape.Properties.Where(property => property.Required).Select(property => $"{shape.Type.Name}.{property.Name}"));

        Assert.Equal(["AgentDefinition.instructions", "FakeCapabilitySettings.endpoint"], required.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Options_compare_by_value_whatever_order_named_items_were_added_in()
    {
        var first = new OfficinaOptions
        {
            Models = new Dictionary<string, ModelProfile> { ["a"] = new() { Fallbacks = ["b"] }, ["b"] = new() },
        };
        var second = new OfficinaOptions
        {
            Models = new Dictionary<string, ModelProfile> { ["b"] = new(), ["a"] = new() { Fallbacks = ["b"] } },
        };

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, second with { Run = new RunDefaults { PermissionMode = PermissionMode.Auto } });
    }

    [Fact]
    public void Options_are_written_in_canonical_order_with_every_default()
    {
        var unordered = new OfficinaOptions { Project = new ProjectOptions { Values = new Dictionary<string, string> { ["z"] = "1", ["a"] = "2" } } };

        var json = OptionsWriter.WriteText(unordered, Model);

        Assert.True(json.IndexOf("\"a\"", StringComparison.Ordinal) < json.IndexOf("\"z\"", StringComparison.Ordinal));
        Assert.Contains("\"permissionMode\": \"ask\"", json, StringComparison.Ordinal);
        Assert.Contains("\"time\": \"8h\"", json, StringComparison.Ordinal);
        Assert.Contains("\"secret\": \"ANTHROPIC_API_KEY\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("30m", 1800)]
    [InlineData("8h", 28800)]
    [InlineData("1.5s", 1.5)]
    [InlineData("250ms", 0.25)]
    [InlineData("2d", 172800)]
    public void Durations_are_numbers_with_a_unit(string text, double seconds)
    {
        Assert.True(Durations.TryParse(text, out var duration));
        Assert.Equal(seconds, duration.TotalSeconds);
        Assert.True(Durations.TryParse(Durations.Format(duration), out var again));
        Assert.Equal(duration, again);
    }

    [Theory]
    [InlineData("30")]
    [InlineData("m")]
    [InlineData("1 h")]
    [InlineData("-1h")]
    [InlineData("unlimited")]
    public void A_duration_without_a_number_and_unit_is_rejected(string text) => Assert.False(Durations.TryParse(text, out _));

    [Theory]
    [InlineData("agents", "dev", "agents.dev")]
    [InlineData("agents", "my agent", "agents[\"my agent\"]")]
    [InlineData("", "run", "run")]
    public void Setting_paths_quote_names_that_are_not_identifiers(string parent, string name, string expected) =>
        Assert.Equal(expected, SettingPath.Child(parent, name));

    [Theory]
    [InlineData("agents.dev.model", "agents.dev")]
    [InlineData("models.a.fallbacks[2]", "models.a.fallbacks")]
    [InlineData("agents[\"my.agent\"]", "agents")]
    [InlineData("run", null)]
    public void A_setting_path_has_a_parent(string path, string? parent) => Assert.Equal(parent, SettingPath.Parent(path));

    private static IEnumerable<ObjectShape> AllShapes()
    {
        var seen = new HashSet<Type>();
        var pending = new Stack<ObjectShape>([Model.Root, Model.CapabilityShape("fake")!]);
        while (pending.TryPop(out var shape))
        {
            if (!seen.Add(shape.Type))
            {
                continue;
            }

            yield return shape;
            foreach (var property in shape.Properties)
            {
                for (var type = property.Type; type is not null; type = type.Element)
                {
                    if (type.Shape is { } child)
                    {
                        pending.Push(child);
                    }
                }
            }
        }
    }
}
