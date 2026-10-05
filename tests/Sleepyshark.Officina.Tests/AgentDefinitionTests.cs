using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Tests;

public class AgentDefinitionTests
{
    [Fact]
    public async Task Hundred_concurrent_runs_of_one_definition_each_keep_their_own_conversation()
    {
        const int Runs = 100;
        var model = new ScriptedModel();
        for (var reply = 0; reply < Runs; reply++)
        {
            model.Reply($"reply {reply}");
        }

        var agent = Agents.With(model, tools: Agents.SearchTool());
        var conversations = Enumerable.Range(0, Runs).Select(_ => new Conversation()).ToArray();

        var results = await Task.WhenAll(conversations.Select((conversation, index) =>
            Task.Run(() => agent.RunAsync(conversation, $"message {index}", cancellationToken: TestContext.Current.CancellationToken))));

        Assert.All(results, result => Assert.IsType<Completed>(result));
        for (var index = 0; index < Runs; index++)
        {
            var messages = conversations[index].Messages;
            Assert.Equal([(Role.User, $"message {index}"), (Role.Assistant, ((Completed)results[index]).Text)], messages.Select(m => (m.Role, m.Text)));
        }

        // Every scripted reply went to exactly one run, and every request held only its own run's message.
        Assert.Equal(Enumerable.Range(0, Runs).Select(reply => $"reply {reply}").Order(), results.Select(result => ((Completed)result).Text).Order());
        Assert.All(model.Requests, request => Assert.Single(request.Messages));
        Assert.Single(conversations.Select(conversation => conversation.Fingerprint).Distinct());
    }

    [Fact]
    public void Tools_are_sorted_by_name_and_names_are_unique()
    {
        var agent = Agents.With(new ScriptedModel(), tools: [Agents.SearchTool(), Agents.Tool("cancel", "Cancels an order.")]);

        Assert.Equal(["cancel", "search"], agent.Tools.Select(tool => tool.Name));
        Assert.Throws<ArgumentException>(() => Agents.With(new ScriptedModel(), tools: [Agents.SearchTool(), Agents.SearchTool("Again.")]));
    }

    [Fact]
    public void A_definition_needs_a_model_and_instructions()
    {
        Assert.Throws<ArgumentNullException>(() => new AgentDefinition { Model = null!, Instructions = "Help." });
        Assert.Throws<ArgumentException>(() => new AgentDefinition { Model = new ScriptedModel(), Instructions = " " });
        Assert.Throws<ArgumentException>(() => Agents.Tool("search", "Searches.", "[]"));
    }
}
