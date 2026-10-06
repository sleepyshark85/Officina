namespace Sleepyshark.Officina.Dependencies.Tests;

/// <summary>The dependency check catches each kind of violation, and allows what the design allows.</summary>
public sealed class DependencyRulesTests : IDisposable
{
    private const string Core = "Sleepyshark.Officina";
    private const string Claude = "Sleepyshark.Officina.Claude";
    private const string Mcp = "Sleepyshark.Officina.Mcp";
    private const string Testing = "Sleepyshark.Officina.Testing";
    private const string App = "BookshopAssistant";
    private const string ExtensionsAi = "Microsoft.Extensions.AI.Abstractions";

    private readonly FixtureRepository repository = new();

    public void Dispose() => repository.Dispose();

    [Fact]
    public void A_package_other_than_Claude_referencing_the_Anthropic_SDK_fails()
    {
        repository.AddProject(Mcp, [Core, "Anthropic"]);

        Assert.Equal([$"{Mcp} references the Anthropic SDK (Anthropic). Only {Claude} may."], repository.Check());
    }

    [Fact]
    public void A_package_other_than_Claude_referencing_the_Anthropic_SDK_is_reported_once()
    {
        repository.AddProject(Mcp, [Core, "Anthropic"], new() { ["Anthropic"] = [ExtensionsAi] });

        Assert.Equal([$"{Mcp} references the Anthropic SDK (Anthropic). Only {Claude} may."], repository.Check());
    }

    [Fact]
    public void The_core_referencing_the_Anthropic_SDK_fails()
    {
        repository.AddProject(Core, ["Anthropic"]);

        Assert.Contains($"{Core} references the Anthropic SDK (Anthropic). Only {Claude} may.", repository.Check());
    }

    [Fact]
    public void Getting_the_Anthropic_SDK_through_another_package_fails()
    {
        repository.AddProject(Testing, [Core, "Some.Wrapper"], new() { ["Some.Wrapper"] = ["Anthropic"] });

        Assert.Equal([$"{Testing} references the Anthropic SDK (Anthropic). Only {Claude} may."], repository.Check());
    }

    [Fact]
    public void Claude_may_reference_the_Anthropic_SDK_and_its_Microsoft_Extensions_AI_dependency()
    {
        repository.AddProject(Claude, [Core, "Anthropic"], new() { ["Anthropic"] = [ExtensionsAi] });

        Assert.Empty(repository.Check());
    }

    [Fact]
    public void An_application_may_get_the_SDK_through_the_Claude_package()
    {
        repository.AddProject(App, [Core, Claude], new() { [Claude] = [Core, "Anthropic"], ["Anthropic"] = [ExtensionsAi] });

        Assert.Empty(repository.Check());
    }

    [Fact]
    public void The_core_may_reference_the_dependency_injection_abstractions()
    {
        repository.AddProject(Core, [DependencyRules.DependencyInjection]);

        Assert.Empty(repository.Check());
    }

    [Fact]
    public void The_core_referencing_any_other_package_fails()
    {
        repository.AddProject(Core, [DependencyRules.DependencyInjection, "Some.Library"]);

        Assert.Equal([$"{Core} must depend on the .NET base library and {DependencyRules.DependencyInjection} only, but references Some.Library."], repository.Check());
    }

    [Fact]
    public void The_core_referencing_another_project_fails()
    {
        repository.AddProject(Core, [Testing]);

        Assert.Equal([$"{Core} must depend on the .NET base library and {DependencyRules.DependencyInjection} only, but references {Testing}."], repository.Check());
    }

    [Fact]
    public void Claude_referencing_Microsoft_Extensions_AI_itself_fails()
    {
        repository.AddProject(Claude, [Core, "Anthropic", "Microsoft.Extensions.AI"]);

        Assert.Equal([$"{Claude} gets Microsoft.Extensions.AI other than through the Anthropic SDK in {Claude}."], repository.Check());
    }

    [Fact]
    public void Another_project_getting_Microsoft_Extensions_AI_fails()
    {
        repository.AddProject(Mcp, [Core, "Some.Library"], new() { ["Some.Library"] = [ExtensionsAi] });

        Assert.Equal([$"{Mcp} gets {ExtensionsAi} other than through the Anthropic SDK in {Claude}."], repository.Check());
    }

    [Fact]
    public void Referencing_Microsoft_Agent_Framework_fails()
    {
        repository.AddProject(App, [Core, "Microsoft.Agents.AI"]);

        Assert.Equal([$"{App} references Microsoft Agent Framework (Microsoft.Agents.AI)."], repository.Check());
    }

    [Fact]
    public void Code_using_Microsoft_Extensions_AI_types_fails()
    {
        repository.AddProject(Claude, [Core]).AddSource(Claude, "Mapper.cs", "using Microsoft.Extensions.AI;\n");

        Assert.Contains(repository.Check(), violation => violation.EndsWith("Mapper.cs:1 uses Microsoft.Extensions.AI types.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_project_that_was_not_restored_fails()
    {
        repository.AddUnrestoredProject(Mcp);

        Assert.Contains(repository.Check(), violation => violation.StartsWith($"{Mcp} has not been restored", StringComparison.Ordinal));
    }
}
