using System.Reflection;
using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Core.Tests.Configuration;

public class OptionsTests
{
    private static readonly Type[] OptionsTypes = [.. typeof(OfficinaOptions).Assembly.GetExportedTypes()
        .Where(type => type.Namespace == typeof(OfficinaOptions).Namespace)
        .Where(type => type.GetProperties().Any(property => property.GetCustomAttribute<SettingAttribute>() is not null))];

    [Fact]
    public void Every_setting_is_documented_with_a_description_and_an_example_that_is_JSON()
    {
        var settings = OptionsTypes
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .ToArray();

        Assert.Equal(54, OptionsTypes.Length);
        Assert.All(settings, property => Assert.DoesNotMatch(RequirementId, property.GetCustomAttribute<SettingAttribute>()?.Description ?? ""));
        Assert.All(settings, property =>
        {
            var setting = property.GetCustomAttribute<SettingAttribute>();
            Assert.True(setting is not null, $"{property.DeclaringType!.Name}.{property.Name} has no [Setting].");
            Assert.False(string.IsNullOrWhiteSpace(setting.Description));
            JsonDocument.Parse(setting.Example).Dispose();
        });
    }

    // Users see descriptions; plan and requirement IDs such as CFG-17 stay in code comments.
    private const string RequirementId = @"\b[A-Z]{2,5}-[0-9]{2}\b|\bS[0-9]{2}\b";

    [Fact]
    public void The_defaults_are_the_safe_choice()
    {
        var options = new OfficinaOptions();

        Assert.Equal(PermissionMode.Ask, options.Run.PermissionMode);
        Assert.Empty(options.Agents);
        Assert.Empty(new SandboxOptions().AllowedHosts);
        Assert.Equal(25m, options.Run.Budget.Cost);
        Assert.Equal(TimeSpan.FromHours(8), options.Run.Budget.Time);
        Assert.Null(options.Storage.UnstoredEvents); // unset leaves out streamed text only
        Assert.True(options.Policies.Masking.Enabled); // ING-02
        Assert.Equal(TimeSpan.FromDays(365), options.Storage.Retention.Audit); // PRIV-02: deleted owners' audit entries expire
        Assert.Equal("claude-opus-5-5", options.Models[ModelProfile.DefaultName].Model);
        Assert.Equal(new SecretReference("ANTHROPIC_API_KEY"), options.Providers["claude"].ApiKey);

        // REQUIREMENTS.md §13, decision 13.
        var agent = new AgentDefinition { Instructions = "Work." };
        Assert.Equal((50, 3), (agent.Budget.Turn.Iterations, agent.Stall.IterationsWithoutProgress));
    }
}
