using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Configuration.Validation;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Core.Tests.Running;

public class AgentRunnerTests
{
    private const string Instructions = "Extract the invoice number, date and total. Reply as JSON.";

    [Fact]
    public async Task An_agent_with_only_instructions_completes_with_the_model_output()
    {
        var kit = new TestKit();
        kit.Model.Reply("""{"number":"A-17"}""");

        var result = await kit.RunAsync(new AgentDefinition { Instructions = Instructions }, "Invoice A-17", TestContext.Current.CancellationToken);

        Assert.Equal(AgentResult.Completed("""{"number":"A-17"}"""), result);
    }

    [Fact]
    public async Task An_agent_with_only_instructions_runs_on_the_default_profile()
    {
        var kit = new TestKit();
        kit.Model.Reply("done");

        await kit.RunAsync(new AgentDefinition { Instructions = Instructions }, "Invoice A-17", TestContext.Current.CancellationToken);

        var request = Assert.Single(kit.Model.Requests);
        Assert.Equal(new ModelProfile(), request.Profile);
        Assert.Equal(Instructions, request.Instructions);
        Assert.Equal([Message.User("Invoice A-17")], request.Messages);
    }

    [Fact]
    public async Task Agents_in_one_application_can_use_different_providers()
    {
        var first = new ScriptedModelProvider().Reply("from first");
        var second = new ScriptedModelProvider().Reply("from second");
        var options = new OfficinaOptions
        {
            Providers = new Dictionary<string, ProviderOptions> { ["first"] = new() { Type = "scripted" }, ["second"] = new() { Type = "scripted" } },
            Models = new Dictionary<string, ModelProfile>
            {
                ["cheap"] = new() { Provider = "first", Model = "small" },
                ["strong"] = new() { Provider = "second", Model = "large" },
            },
        };
        var runner = new AgentRunner(options, new Dictionary<string, IModelProvider> { ["first"] = first, ["second"] = second });
        var ct = TestContext.Current.CancellationToken;

        var cheap = await runner.RunAsync(new AgentDefinition { Instructions = "Classify.", Model = "cheap" }, "input", ct);
        var strong = await runner.RunAsync(new AgentDefinition { Instructions = "Answer.", Model = "strong" }, "input", ct);

        Assert.Equal("from first", cheap.Output);
        Assert.Equal("from second", strong.Output);
        Assert.Equal("small", Assert.Single(first.Requests).Profile.Model);
        Assert.Equal("large", Assert.Single(second.Requests).Profile.Model);
    }

    [Fact]
    public async Task A_model_slot_can_have_its_profile_inline()
    {
        var kit = new TestKit();
        kit.Model.Reply("done");
        var profile = new ModelProfile { Model = "claude-haiku-4-5", Effort = "low" };

        await kit.RunAsync(new AgentDefinition { Instructions = Instructions, Model = profile }, "Invoice A-17", TestContext.Current.CancellationToken);

        Assert.Equal(profile, Assert.Single(kit.Model.Requests).Profile);
    }

    [Fact]
    public async Task An_unknown_profile_is_reported_by_name()
    {
        var kit = new TestKit();

        var error = await Assert.ThrowsAsync<ConfigurationException>(
            () => kit.RunAsync(new AgentDefinition { Instructions = Instructions, Model = "missing" }, "input", TestContext.Current.CancellationToken));

        Assert.Contains("\"missing\"", error.Message, StringComparison.Ordinal);
    }
}
