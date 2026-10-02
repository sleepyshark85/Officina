using System.Text.Json;
using System.Text.Json.Nodes;
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

    // CLD-06: the tool call of the model that was declined is withdrawn, so it is not run; it, and its server tool call without a
    // result, are not sent back, while its text and the fallback block are. Each attempt's usage keeps its 1-hour cache writes.
    [Fact]
    public async Task The_declining_models_tool_calls_are_withdrawn_and_not_sent_back()
    {
        using var provider = Replay(Named("fallback-tools.json"), ProviderOptions.Claude with { Features = new() { RefusalFallback = true } });
        var request = new ModelRequest(Profile, [], "Extract.", [Message.User("Invoice A-17.")], []);

        var events = await StreamAsync(provider, request);

        var call = new ToolUseContent("toolu_1", "read", Json("""{ "path": "a.cs" }"""));
        var server = new ProviderContent(Json("""{ "type": "server_tool_use", "id": "srvtoolu_1", "name": "web_search", "input": { "query": "A-17" } }"""));
        var fallback = new ProviderContent(Json("""{ "type": "fallback", "from": { "model": "claude-opus-5-5" }, "to": { "model": "claude-opus-4-8" } }"""));
        Assert.Equal(
            [
                new TextDelta("Looking."), new ContentReceived(call), new ContentReceived(server), new ToolCallsWithdrawn(), new ContentReceived(fallback),
                new TextDelta("A-17."), new UsageReported(new Usage(30, 9, 0, 0)),
                new FallbackUsed("claude-opus-4-8", Profile with { Model = "claude-opus-4-8" }, ModelFailure.Refused), new UsageReported(new Usage(40, 12, 0, 6, 4)),
                new Stopped(StopReason.Finished),
            ],
            events);

        var next = await StreamAsync(provider, request with
        {
            History =
            [
                Message.User("Invoice A-17."),
                new(Role.Assistant, [new TextContent("Looking."), call, server, fallback, new TextContent("A-17.")]),
                Message.User("And the date?"),
            ],
        });
        Assert.Contains(new TextDelta("None."), next);
    }

    // HIST-01, CLD-06: a request to summarize is Claude's compaction on demand. The streamed block, its summary, opaque content and
    // signature, goes back first, as an assistant message of its own, and every request carrying it asks for compaction; the summary
    // call's tokens are reported from its compaction iteration.
    [Fact]
    public async Task A_request_to_summarize_streams_Claudes_compaction_block_which_goes_back_whole()
    {
        using var provider = Replay(Named("compaction.json"));
        var earlier = new ModelRequest(Profile, [], "Design.", [Message.User("Name the entities."), Message.Assistant("Recipe, Ingredient and Step.")], [])
        {
            Summarize = true,
        };

        var events = await StreamAsync(provider, earlier);

        var block = new ProviderContent(Json(
            """{ "type": "compaction", "content": "The entities agreed are Recipe, Ingredient and Step.", "encrypted_content": "Eo8Qx", "signature": "EuYB" }"""));
        Assert.Equal([new ContentReceived(block), new UsageReported(new Usage(144, 276, 0, 0)), new Stopped(StopReason.Unknown)], events);

        var next = await StreamAsync(provider, earlier with
        {
            History = [new Message(Role.Assistant, [block]), Message.User("Now the fields of Recipe.")],
            Summarize = false,
            TurnStart = 1,
        });
        Assert.Contains(new TextDelta("title and servings."), next);
    }

    // A recorded exchange that names no beta feature expects none, so a request that asks for one does not replay.
    [Fact]
    public async Task A_recording_without_beta_features_does_not_answer_a_request_with_them()
    {
        var recorded = JsonNode.Parse(await File.ReadAllTextAsync(Named("compaction.json"), TestContext.Current.CancellationToken))!.AsArray()[1]!.AsObject();
        recorded.Remove("beta");
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, new JsonArray(recorded.DeepClone()).ToJsonString(), TestContext.Current.CancellationToken);
            using var provider = Replay(path);
            var block = new ProviderContent(Json(
                """{ "type": "compaction", "content": "The entities agreed are Recipe, Ingredient and Step.", "encrypted_content": "Eo8Qx", "signature": "EuYB" }"""));

            var error = await Assert.ThrowsAnyAsync<Exception>(() => StreamAsync(provider, new ModelRequest(
                Profile, [], "Design.", [new Message(Role.Assistant, [block]), Message.User("Now the fields of Recipe.")], [])));

            Assert.Contains("asks for beta features \"compact-2026-09-04\", and the recording for \"\"", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // HIST-01: Haiku cannot summarize a conversation, so an agent that has the provider shorten its history on Haiku is a
    // configuration error, before anything runs.
    [Fact]
    public void Shortening_by_a_model_that_cannot_summarize_is_a_configuration_error()
    {
        using var provider = new ClaudeProvider(ProviderOptions.Claude, new InMemorySecretSource(new Dictionary<string, string>()));
        Assert.True(provider.CapabilitiesOf("claude-opus-5-5").Summarizes);
        var options = new OfficinaOptions
        {
            Models = new Dictionary<string, ModelProfile> { [ModelProfile.DefaultName] = new() { Model = "claude-haiku-4-5" } },
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["a"] = new() { Instructions = "Answer.", Context = new() { History = new() { Strategy = HistoryStrategy.Shortened } } },
            },
            Capabilities = new() { ConversationStore = new() { Enabled = true } },
        };

        var error = Assert.Throws<ConfigurationException>(() => Runner(options, provider));

        Assert.Equal("provider \"claude\" cannot shorten history with model \"claude-haiku-4-5\".", Assert.Single(error.Errors).Problem);
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

        var error = Assert.Throws<ConfigurationException>(() => Runner(options, provider));

        Assert.Equal(["providers.claude.features.taskBudget", "providers.claude.features.refusalFallback"], error.Errors.Select(problem => problem.Path));
    }

    private static AgentRunner Runner(OfficinaOptions options, ClaudeProvider provider) => new(
        options, new Dictionary<string, IModelProvider> { [ProviderOptions.ClaudeName] = provider }, new InMemoryStorage(), new Dictionary<string, ITool>(),
        new Dictionary<string, IGate>(), new Dictionary<string, ICheck>(), new Dictionary<string, IKnowledgeSource>(), new ScriptedHuman(),
        new InMemorySecretSource(new Dictionary<string, string>()), TimeProvider.System);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
