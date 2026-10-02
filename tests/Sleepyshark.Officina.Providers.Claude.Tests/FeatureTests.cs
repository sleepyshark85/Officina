using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Providers.Claude.Tests.Recordings;

namespace Sleepyshark.Officina.Providers.Claude.Tests;

/// <summary>
/// The Claude API's features the configuration switches on (CLD-06), and compaction as the provider's shortening (HIST-01),
/// checked offline against recorded exchanges written from the API's documentation: each request must equal the recorded one,
/// beta header included, and each reply must read as the expected events.
/// </summary>
public sealed class FeatureTests
{
    private static readonly ModelProfile Profile = new() { Model = "claude-opus-5-5" };

    private static readonly JsonElement Schema =
        Json("""{ "type": "object", "properties": { "number": { "type": "string" } }, "required": ["number"], "additionalProperties": false }""");

    private static readonly ProviderOptions AllOn = ProviderOptions.Claude with
    {
        Features = new() { StructuredOutput = true, ClearToolResults = true, TaskBudget = 64_000, RefusalFallback = true },
    };

    // CLD-06: structured output, clearing old tool results, a task budget and the server-side refusal fallback, each with its beta
    // header. A call declined partway is served by the fallback model: each attempt is priced by its own model, and the fallback
    // block stays where it came.
    [Fact]
    public async Task Switched_on_features_are_sent_and_a_refusal_fallback_is_priced_by_each_model()
    {
        using var provider = Replay(Named("features.json"), AllOn);
        var request = new ModelRequest(Profile, [], "Extract.", [Message.User("Invoice A-17.")], []) { OutputSchema = Schema };

        var events = await StreamAsync(provider, request);

        var fallback = new ProviderContent(Json("""{ "type": "fallback", "from": { "model": "claude-opus-5-5" }, "to": { "model": "claude-opus-4-8" } }"""));
        Assert.Equal(
            [
                new TextDelta("{\"num"), new ContentReceived(fallback), new TextDelta("ber\": \"A-17\"}"),
                new UsageReported(new Usage(30, 3, 0, 0)),
                new FallbackUsed("claude-opus-4-8", Profile with { Model = "claude-opus-4-8" }, ModelFailure.Refused),
                new UsageReported(new Usage(40, 12, 0, 0)),
                new Stopped(StopReason.Finished),
            ],
            events);

        // The next call sends the reply back with the declining model's reasoning before the fallback block left out, as the API
        // asks; a later call that sticky routing sends to the fallback model is priced by that model too.
        var next = request with
        {
            History =
            [
                Message.User("Invoice A-17."),
                new(Role.Assistant, [
                    new ReasoningContent("Dropped: before the fallback.", "sig-3"), new TextContent("{\"num"), fallback,
                    new ReasoningContent("Kept: after the fallback.", "sig-4"), new TextContent("ber\": \"A-17\"}"),
                ]),
                Message.User("And the date?"),
            ],
        };
        var sticky = await StreamAsync(provider, next);

        Assert.Equal(
            [new FallbackUsed("claude-opus-4-8", Profile with { Model = "claude-opus-4-8" }, ModelFailure.Refused), new UsageReported(new Usage(60, 2, 0, 0))],
            sticky.Where(modelEvent => modelEvent is FallbackUsed or UsageReported));
    }

    // HIST-01, HIST-04, CLD-06: Claude summarizes the earlier turns, and its signed block takes their place, first, with the current
    // turn kept; the summary call's tokens are reported, and every later request carrying the block asks for compaction.
    [Fact]
    public async Task Compaction_replaces_the_earlier_turns_with_Claudes_summary_and_reports_its_tokens()
    {
        using var provider = Replay(Named("compaction.json"));
        var current = Message.User("Now the fields of Recipe.");
        var request = new ModelRequest(Profile, [], "Design.", [Message.User("Name the entities."), Message.Assistant("Recipe, Ingredient and Step."), current], [])
        {
            TurnStart = 2,
        };

        var shortened = await ((IHistoryShortener)provider).ShortenAsync(request, TestContext.Current.CancellationToken);

        var block = new ProviderContent(Json("""{ "type": "compaction", "content": "The entities agreed are Recipe, Ingredient and Step.", "signature": "EuYB" }"""));
        Assert.Equal([new Message(Role.User, [block]), current], shortened.History);
        Assert.Equal(new Usage(144, 276, 0, 0), shortened.Usage);

        var events = await StreamAsync(provider, request with { History = shortened.History, TurnStart = 1 });
        Assert.Contains(new TextDelta("title and servings."), events);
    }

    // HIST-02: the current turn is never summarized, so a conversation with no earlier turn cannot be shortened.
    [Fact]
    public async Task A_conversation_with_no_earlier_turn_is_not_shortened()
    {
        using var provider = Replay(Named("compaction.json"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ((IHistoryShortener)provider).ShortenAsync(new ModelRequest(Profile, [], "Design.", [Message.User("Hi.")], []), TestContext.Current.CancellationToken));
    }

    // MDL-06: a model without mid-conversation system messages reads the operator's as a user message, labelled as the operator's.
    [Fact]
    public async Task A_model_without_mid_conversation_system_messages_gets_the_operators_as_a_user_message()
    {
        using var provider = Replay(Named("operator.json"));
        var request = new ModelRequest(new() { Model = "claude-haiku-4-5" }, [], "Design.",
            [Message.User("Name the entities."), Message.System("Project memory changed: use singular names.")], []);

        var events = await StreamAsync(provider, request);

        Assert.Equal(new Stopped(StopReason.Finished), events[^1]);
    }

    // MDL-06, CLD-06: a feature switched on for a model that does not have it is a configuration error, before anything runs.
    [Fact]
    public void A_feature_the_model_does_not_have_is_a_configuration_error()
    {
        var options = new OfficinaOptions
        {
            Providers = new Dictionary<string, ProviderOptions> { [ProviderOptions.ClaudeName] = ProviderOptions.Claude with { Features = new() { RefusalFallback = true, TaskBudget = 20_000 } } },
            Models = new Dictionary<string, ModelProfile> { [ModelProfile.DefaultName] = new() { Model = "claude-haiku-4-5" } },
            Agents = new Dictionary<string, AgentDefinition> { ["a"] = new() { Instructions = "Answer." } },
        };
        using var provider = new ClaudeProvider(options.Providers[ProviderOptions.ClaudeName], new InMemorySecretSource(new Dictionary<string, string>()));

        var error = Assert.Throws<ConfigurationException>(() => new AgentRunner(
            options, new Dictionary<string, IModelProvider> { [ProviderOptions.ClaudeName] = provider }, new InMemoryStorage(), new Dictionary<string, ITool>(),
            new Dictionary<string, IGate>(), new Dictionary<string, ICheck>(), new Dictionary<string, IKnowledgeSource>(), new ScriptedHuman(),
            new InMemorySecretSource(new Dictionary<string, string>()), TimeProvider.System));

        Assert.Equal(["providers.claude.features.taskBudget", "providers.claude.features.refusalFallback"], error.Errors.Select(problem => problem.Path));
    }

    // HIST-01: Haiku cannot summarize a conversation, so its history is not shortened that way.
    [Fact]
    public async Task A_model_that_cannot_summarize_is_not_asked_to()
    {
        using var provider = Replay(Named("compaction.json"));
        var request = new ModelRequest(new() { Model = "claude-haiku-4-5" }, [], "Design.", [Message.User("1."), Message.Assistant("one"), Message.User("2.")], []) { TurnStart = 2 };

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await ((IHistoryShortener)provider).ShortenAsync(request, TestContext.Current.CancellationToken));
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
