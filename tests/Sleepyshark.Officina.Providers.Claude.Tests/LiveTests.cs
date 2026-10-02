using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Tools;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Providers.Claude.Tests.Recordings;

namespace Sleepyshark.Officina.Providers.Claude.Tests;

/// <summary>
/// A real turn on Claude: the agent reads a file, then answers, so the turn makes two model calls, and the second reads
/// from the cache what the first sent (COST-01). The live test is opt-in and records the turn, which the offline test
/// replays (TEST-02). To record it again: OFFICINA_RECORD_LIVE=1 with ANTHROPIC_API_KEY set.
/// </summary>
public sealed class LiveTests
{
    private const string Agent = "reader";

    private static readonly string Recording = Named("live-turn.json");

    /// <summary>Long enough to pass Claude's smallest cacheable prefix.</summary>
    private static readonly string Instructions = "You read files and answer questions about them. "
        + string.Join(' ', Enumerable.Range(1, 60).Select(rule => $"Rule {rule}: when a question touches topic {rule}, answer tersely and precisely."));

    [Fact]
    public async Task A_live_turn_is_recorded_and_reads_the_cache_on_its_second_call()
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("OFFICINA_RECORD_LIVE") == "1" && !string.IsNullOrEmpty(key),
            "Calls the Claude API: set OFFICINA_RECORD_LIVE=1 and ANTHROPIC_API_KEY.");

        await AssertTurnAsync(HttpRecording.Record(Recording), key!);
    }

    [Fact]
    public Task The_recorded_turn_replays_offline_exactly() => AssertTurnAsync(HttpRecording.Replay(Recording), "test-key");

    private static async Task AssertTurnAsync(HttpMessageHandler http, string key)
    {
        var ct = TestContext.Current.CancellationToken;
        var secrets = new KnownSecrets(new InMemorySecretSource(new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = key }));
        using var provider = new ClaudeProvider(ProviderOptions.Claude, secrets, http);
        var storage = new InMemoryStorage();
        var options = new OfficinaOptions
        {
            Models = new Dictionary<string, ModelProfile> { [ModelProfile.DefaultName] = new() { Effort = "low" } },
            Tools = new Dictionary<string, ToolOptions> { ["read_file"] = new() { Source = "extension:read_file" } },
            ToolSets = new Dictionary<string, IReadOnlyList<string>> { ["files"] = ["read_file"] },
            Agents = new Dictionary<string, AgentDefinition>
            {
                [Agent] = new() { Instructions = Instructions, Tools = ["files"], Context = new() { OperatingFacts = ["The project is Officina."] } },
            },
        };
        var file = new FakeTool(
            ToolKind.Read,
            """{ "type": "object", "properties": { "path": { "type": "string" } }, "required": ["path"] }""",
            run: (_, _) => ValueTask.FromResult(ToolResult.Success("Pelican is the first word of this file.")));
        var runner = new AgentRunner(
            options, new Dictionary<string, IModelProvider> { [ProviderOptions.ClaudeName] = provider }, storage, new Dictionary<string, ITool> { ["read_file"] = file },
            new Dictionary<string, IGate>(), new Dictionary<string, ICheck>(), new Dictionary<string, IKnowledgeSource>(), new ScriptedHuman(), secrets,
            new FakeTimeProvider());

        var result = await runner.RunAsync(Agent, "Read notes.txt with the tool, then reply with its first word only.", ct: ct);

        Assert.Equal(AgentOutcome.Completed, result.Outcome);
        Assert.Contains("Pelican", result.Output, StringComparison.Ordinal);
        var calls = (await storage.Events.ReadAsync(null, storage.Runs.Runs.Single().RunId, 0, ct)).Select(read => read.Payload).OfType<ModelCallEnded>().ToList();
        Assert.Equal(2, calls.Count);
        Assert.True(calls[1].Usage.CacheRead > 0, $"The second call read nothing from the cache: {calls[1].Usage}.");
    }
}
