namespace BookshopAssistant.Tests;

/// <summary>The application's settings files: the one it ships, a local file over it, and the mistakes it refuses.</summary>
public sealed class SettingsTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("bookshop-settings-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public void The_shipped_settings_reach_the_compose_services()
    {
        // Only the shipped file: a developer's local file is copied to the output too.
        File.Copy(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), Path.Combine(folder, "appsettings.json"));

        var settings = BookshopSettings.Load(folder);

        Assert.Contains("Port=5432", settings.Database, StringComparison.Ordinal);
        Assert.Equal(new Uri("http://localhost:18800/mcp"), settings.ExportsUrl);
        Assert.False(settings.Demo);
        Assert.Null(settings.ReplyBudget);
    }

    [Fact]
    public void The_local_file_overrides_the_shipped_one_setting_by_setting()
    {
        Write("appsettings.json", """{ "Database": "Host=a", "DataFolder": "shared" }""");
        Write("appsettings.Local.json", """{ "AnthropicApiKey": "key", "DataFolder": "mine", "ReplyBudget": 0.01 }""");

        var settings = BookshopSettings.Load(folder);

        Assert.Equal(("Host=a", "key", "mine", 0.01m), (settings.Database, settings.AnthropicApiKey, settings.DataFolder, settings.ReplyBudget));
    }

    [Theory]
    [InlineData("""{ "DataFolder": "data" }""", "Set Database")]
    [InlineData("""{ "Database": "Host=a", "ReplyBudget": 0 }""", "ReplyBudget must be a positive amount")]
    [InlineData("""{ "Database": "Host=a", "DashbordUrl": "http://localhost:1" }""", "DashbordUrl")]
    [InlineData("""{ "Database": "Host=a", "Demo": "sometimes" }""", "not valid")]
    [InlineData("""{ "Database": "Host=a", "ExportsUrl": "localhost:18800" }""", "ExportsUrl must be an absolute http URL")]
    [InlineData("""{ "Database": """, "Failed to load configuration")]
    public void Settings_that_are_missing_misspelled_or_malformed_are_refused_with_the_reason(string json, string reason)
    {
        Write("appsettings.json", json);

        var exception = Assert.Throws<InvalidDataException>(() => BookshopSettings.Load(folder));

        Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_settings_are_refused()
    {
        Assert.Contains("not valid", Assert.Throws<InvalidDataException>(() => BookshopSettings.Load(folder)).Message, StringComparison.Ordinal);
    }

    private void Write(string name, string json) => File.WriteAllText(Path.Combine(folder, name), json);
}
