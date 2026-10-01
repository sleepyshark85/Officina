using System.Reflection;
using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Core.Tests.Configuration;

public class OptionsTests
{
    private static readonly Type[] OptionsTypes = [.. typeof(OfficinaOptions).Assembly.GetExportedTypes()
        .Where(type => type.Namespace == typeof(OfficinaOptions).Namespace && type.GetProperties().Any(property => property.GetCustomAttribute<SettingAttribute>() is not null))];

    [Fact]
    public void Every_setting_is_documented_with_a_description_and_an_example_that_is_JSON()
    {
        var settings = OptionsTypes.SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)).ToArray();

        Assert.Equal(8, OptionsTypes.Length);
        Assert.All(settings, property =>
        {
            var setting = property.GetCustomAttribute<SettingAttribute>();
            Assert.True(setting is not null, $"{property.DeclaringType!.Name}.{property.Name} has no [Setting].");
            Assert.False(string.IsNullOrWhiteSpace(setting.Description));
            JsonDocument.Parse(setting.Example).Dispose();
        });
    }

    [Fact]
    public void The_defaults_are_the_safe_choice()
    {
        var options = new OfficinaOptions();

        Assert.Equal(PermissionMode.Ask, options.Run.PermissionMode);
        Assert.Empty(options.Agents);
        Assert.Equal(25m, options.Run.Budget.Cost);
        Assert.Equal(TimeSpan.FromHours(8), options.Run.Budget.Time);
        Assert.Equal("claude-opus-5-5", options.Models[ModelProfile.DefaultName].Model);
        Assert.Equal(new SecretReference("ANTHROPIC_API_KEY"), options.Providers["claude"].ApiKey);
    }

    [Fact]
    public void Options_are_written_in_the_file_format_with_every_default()
    {
        var json = OfficinaJson.Write(new OfficinaOptions());

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
        Assert.True(OfficinaJson.TryParseDuration(text, out var duration));
        Assert.Equal(seconds, duration.TotalSeconds);
        Assert.True(OfficinaJson.TryParseDuration(OfficinaJson.FormatDuration(duration), out var again));
        Assert.Equal(duration, again);
    }

    [Theory]
    [InlineData("30")]
    [InlineData("1 h")]
    [InlineData("-1h")]
    [InlineData("unlimited")]
    public void A_duration_without_a_number_and_unit_is_rejected(string text) => Assert.False(OfficinaJson.TryParseDuration(text, out _));
}
