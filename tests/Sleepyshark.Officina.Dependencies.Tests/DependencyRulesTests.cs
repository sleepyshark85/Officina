namespace Sleepyshark.Officina.Dependencies.Tests;

/// <summary>Shows that the TEST-32 check catches each kind of violation, and allows what the design allows.</summary>
public sealed class DependencyRulesTests : IDisposable
{
    private const string Core = "Sleepyshark.Officina.Core";
    private const string Team = "Sleepyshark.Officina.Team";
    private const string Testing = "Sleepyshark.Officina.Testing";
    private const string Provider = "Sleepyshark.Officina.Providers.Claude";
    private const string Cli = "Sleepyshark.Officina.Cli";
    private const string ExtensionsAi = "Microsoft.Extensions.AI.Abstractions";

    private readonly FixtureRepository repository = new();

    public void Dispose() => repository.Dispose();

    [Fact]
    public void A_project_other_than_the_provider_referencing_the_Anthropic_SDK_fails()
    {
        repository.AddProject(Team, [Core, "Anthropic"], new() { ["Anthropic"] = [ExtensionsAi] });

        var violations = repository.Check();

        Assert.Contains($"{Team} references the Anthropic SDK (Anthropic). Only {Provider} may.", violations);
    }

    [Fact]
    public void Getting_the_Anthropic_SDK_through_another_package_fails()
    {
        repository.AddProject(Testing, [Core, "Some.Wrapper"], new() { ["Some.Wrapper"] = ["Anthropic"] });

        Assert.Contains(repository.Check(), violation => violation.StartsWith($"{Testing} references the Anthropic SDK", StringComparison.Ordinal));
    }

    [Fact]
    public void The_provider_may_reference_the_Anthropic_SDK_and_its_Microsoft_Extensions_AI_dependency()
    {
        repository.AddProject(Provider, [Core, "Anthropic"], new() { ["Anthropic"] = [ExtensionsAi] });

        Assert.Empty(repository.Check());
    }

    [Fact]
    public void A_project_may_get_the_SDK_through_the_provider_project()
    {
        repository.AddProject(Cli, [Core, Provider], new() { [Provider] = [Core, "Anthropic"], ["Anthropic"] = [ExtensionsAi] });

        Assert.Empty(repository.Check());
    }

    [Fact]
    public void The_provider_referencing_Microsoft_Extensions_AI_itself_fails()
    {
        repository.AddProject(Provider, [Core, "Anthropic", "Microsoft.Extensions.AI"]);

        Assert.Contains(
            $"{Provider} gets Microsoft.Extensions.AI other than through the Anthropic SDK in {Provider}.",
            repository.Check());
    }

    [Fact]
    public void Another_project_getting_Microsoft_Extensions_AI_fails()
    {
        repository.AddProject(Team, [Core, "Some.Library"], new() { ["Some.Library"] = [ExtensionsAi] });

        Assert.Contains($"{Team} gets {ExtensionsAi} other than through the Anthropic SDK in {Provider}.", repository.Check());
    }

    [Theory]
    [InlineData("Microsoft.Agents.AI")]
    [InlineData("Microsoft.SemanticKernel")]
    [InlineData("OpenAI")]
    [InlineData("ModelContextProtocol")]
    public void Any_other_AI_or_agent_framework_fails(string package)
    {
        repository.AddProject(Provider, [Core, package]);

        Assert.Contains($"{Provider} references the AI or agent framework {package}.", repository.Check());
    }

    [Fact]
    public void Core_depending_on_anything_but_the_base_library_and_the_schema_validator_fails()
    {
        repository.AddProject(Core, ["JsonSchema.Net", "Newtonsoft.Json"]);

        Assert.Equal([$"{Core} must depend on the .NET base library and JsonSchema.Net only, but references Newtonsoft.Json."], repository.Check());
    }

    [Fact]
    public void Code_using_Microsoft_Extensions_AI_types_fails()
    {
        repository.AddProject(Provider, [Core]).AddSource(Provider, "Mapper.cs", "using Microsoft.Extensions.AI;\n");

        Assert.Contains(repository.Check(), violation => violation.EndsWith("Mapper.cs:1 uses Microsoft.Extensions.AI types.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_project_that_was_not_restored_fails()
    {
        repository.AddUnrestoredProject(Team);

        Assert.Contains(repository.Check(), violation => violation.StartsWith($"{Team} has not been restored", StringComparison.Ordinal));
    }
}
