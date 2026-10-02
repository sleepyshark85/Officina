using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Observability;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Running;

/// <summary>
/// The model gateway (S12): retries, fallbacks, and one provider shared by all agents. The agent <c>dev</c> runs on the
/// profile <c>default</c> (Opus), whose fallback is <c>fast</c> (Haiku).
/// </summary>
public class ModelGatewayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // MDL-05, REL-01.
    [Fact]
    public async Task A_transient_or_rate_limited_failure_is_retried_and_the_turn_never_sees_it()
    {
        var kit = Kit(maxAttempts: 3);
        kit.Model.Fail(ModelFailure.Transient).Fail(ModelFailure.RateLimited).Reply("Done.");

        var (result, _) = await RunAsync(kit);

        Assert.Equal((AgentOutcome.Completed, "Done.", 3), (result.Outcome, result.Output, kit.Model.Requests.Count));
        Assert.All(kit.Model.Requests, request => Assert.Equal("claude-opus-5-5", request.Profile.Model));
    }

    // REL-01.
    [Fact]
    public async Task Retries_wait_twice_as_long_each_time_up_to_the_longest_wait()
    {
        var kit = Kit(maxAttempts: 5, maxDelay: TimeSpan.FromSeconds(3));
        kit.Model.Fail(ModelFailure.Transient).Fail(ModelFailure.Transient).Fail(ModelFailure.Transient).Fail(ModelFailure.Transient).Reply("Done.");

        var (result, waited) = await RunAsync(kit);

        Assert.Equal(AgentOutcome.Completed, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(1 + 2 + 3 + 3), waited);
    }

    // REL-01.
    [Fact]
    public async Task A_wait_the_provider_asks_for_is_respected_when_it_is_longer()
    {
        var kit = Kit(maxAttempts: 3);
        kit.Model.Fail(ModelFailure.RateLimited, TimeSpan.FromSeconds(5)).Fail(ModelFailure.Transient, TimeSpan.FromMilliseconds(500)).Reply("Done.");

        var (result, waited) = await RunAsync(kit);

        Assert.Equal(AgentOutcome.Completed, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(5 + 2), waited);
    }

    // REL-01.
    [Fact]
    public async Task Retries_stop_after_the_configured_attempts_and_the_turn_is_handed_off()
    {
        var kit = Kit(maxAttempts: 2);
        kit.Model.Fail(ModelFailure.RateLimited).Fail(ModelFailure.RateLimited).Reply("Never asked for.");

        var (result, waited) = await RunAsync(kit);

        Assert.Equal((HandoffReason.ProviderFailure, 2, TimeSpan.FromSeconds(1)), (result.Handoff!.Reason, kit.Model.Requests.Count, waited));
        Assert.Contains("RateLimited", result.Handoff.Detail, StringComparison.Ordinal);
    }

    // MDL-05: the other categories are not helped by trying again, or by another model.
    [Theory]
    [InlineData(ModelFailure.InvalidRequest)]
    [InlineData(ModelFailure.Authentication)]
    public async Task A_failure_that_trying_again_cannot_fix_is_not_retried_and_does_not_use_a_fallback(ModelFailure failure)
    {
        var kit = Kit(fallback: true);
        kit.Model.Fail(failure).Reply("Never asked for.");

        var (result, _) = await RunAsync(kit);

        Assert.Equal((HandoffReason.ProviderFailure, 1), (result.Handoff!.Reason, kit.Model.Requests.Count));
    }

    // REL-01: an error that arrives mid-stream voids the reply so far.
    [Fact]
    public async Task A_failure_in_the_middle_of_a_reply_starts_the_reply_over()
    {
        var kit = Kit(maxAttempts: 2);
        kit.Model.Fail(ModelFailure.Transient, null, new TextDelta("A half "), new UsageReported(new Usage(10, 1, 0, 0)))
            .Reply(new TextDelta("A whole answer."), new Stopped(StopReason.Finished));

        var (result, _) = await RunAsync(kit);

        Assert.Equal("A whole answer.", result.Output);
        Assert.Equal(new Usage(10, 1, 0, 0), result.Statistics.Usage); // spent, so counted
        Assert.Equal("A whole answer.", Assert.Single(result.Transcript, message => message.Role == Role.Assistant).Content.OfType<TextContent>().Single().Text);
    }

    // REL-01, EVT-01: each retry is an event of its own, so a reader of the events knows the text before it in the call is void.
    [Fact]
    public async Task Each_retry_is_published_so_readers_know_the_text_before_it_is_void()
    {
        var kit = Kit(maxAttempts: 3);
        kit.Model.Fail(ModelFailure.Transient).Fail(ModelFailure.RateLimited, null, new TextDelta("A half ")).Reply("Done.");
        var work = new Work(Agent, "work");

        await RunAsync(kit, work);

        Assert.Equal(
            [new ModelCallRetried(ModelFailure.Transient), new ModelCallRetried(ModelFailure.RateLimited)],
            (await kit.Storage.Events.ReadAsync(null, work.RunId, 0, Ct)).Select(coreEvent => coreEvent.Payload).OfType<ModelCallRetried>());
    }

    // MDL-04, TEST-16, OBS-02.
    [Fact]
    public async Task A_fallback_serves_the_call_when_the_primary_stays_unavailable_and_the_switch_is_recorded()
    {
        var fallbacks = new List<(string Model, string? Reason)>();
        using var metering = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == Telemetry.Name && instrument.Name == "officina.fallbacks")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        metering.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            lock (fallbacks)
            {
                fallbacks.Add(((string)tags.ToArray().Single(tag => tag.Key == "gen_ai.request.model").Value!, (string?)tags.ToArray().Single(tag => tag.Key == "officina.fallback.reason").Value));
            }
        });
        metering.Start();
        var kit = Kit(maxAttempts: 2, fallback: true);
        kit.Model.Fail(ModelFailure.Transient).Fail(ModelFailure.Transient)
            .Reply(new TextDelta("Done."), new UsageReported(new Usage(1_000_000, 0, 0, 0)), new Stopped(StopReason.Finished));

        var (result, _) = await RunAsync(kit);

        Assert.Equal(AgentOutcome.Completed, result.Outcome);
        Assert.Equal(["claude-opus-5-5", "claude-opus-5-5", "claude-haiku-4-5"], kit.Model.Requests.Select(request => request.Profile.Model));
        Assert.Equal(1m, result.Statistics.Cost); // the fallback's price: $1 per million input tokens, not Opus's $4
        var events = await kit.Storage.Events.ReadAsync(null, kit.Storage.Runs.Runs.Single().RunId, 0, Ct);
        Assert.Equal(new ModelFallback("fast", "claude", "claude-haiku-4-5", ModelFailure.Transient), events.Select(read => read.Payload).OfType<ModelFallback>().Single());
        Assert.Equal([("claude-haiku-4-5", "Transient")], fallbacks);
    }

    // MDL-04: the primary is tried again by the next call.
    [Fact]
    public async Task The_next_call_tries_the_primary_again()
    {
        var kit = Kit(maxAttempts: 1, fallback: true);
        kit.Model.Fail(ModelFailure.Transient).CallTools(("echo", "{}")).Reply("Done.");

        var (result, _) = await RunAsync(kit);

        Assert.Equal(["claude-opus-5-5", "claude-haiku-4-5", "claude-opus-5-5"], kit.Model.Requests.Select(request => request.Profile.Model));
        Assert.Equal(AgentOutcome.Completed, result.Outcome);
    }

    // MDL-04: when the fallback fails as well, the turn is handed off.
    [Fact]
    public async Task When_the_last_fallback_fails_too_the_turn_is_handed_off()
    {
        var kit = Kit(maxAttempts: 1, fallback: true);
        kit.Model.Fail(ModelFailure.Transient).Fail(ModelFailure.RateLimited);

        var (result, _) = await RunAsync(kit);

        Assert.Equal(HandoffReason.ProviderFailure, result.Handoff!.Reason);
        Assert.Contains("RateLimited", result.Handoff.Detail, StringComparison.Ordinal);
    }

    // MDL-04: a fallback must exist.
    [Fact]
    public void A_fallback_that_is_not_a_profile_or_is_the_profile_itself_fails_validation()
    {
        var missing = Options() with { Models = new Dictionary<string, ModelProfile> { ["default"] = new() { Fallbacks = ["nowhere", "default"] } } };

        var errors = missing.Validate().Select(error => (error.Path, error.Problem)).ToList();

        Assert.Equal(
            [("models.default.fallbacks", "model profile \"nowhere\" does not exist."), ("models.default.fallbacks", "a profile cannot be its own fallback.")],
            errors);
    }

    // MDL-04, MDL-06: a fallback is offered what the slot is offered, so the model must support it.
    [Fact]
    public void A_fallback_whose_model_cannot_serve_the_slot_fails_validation()
    {
        var options = Options() with
        {
            Models = new Dictionary<string, ModelProfile>
            {
                ["default"] = new() { Fallbacks = ["fast"] },
                ["fast"] = new() { Model = "claude-haiku-4-5" },
            },
        };
        var providers = new Dictionary<string, IModelProvider> { ["claude"] = new PerModelCapabilities() };

        var failure = Assert.Throws<ConfigurationException>(() => Runner(options, providers));

        var error = Assert.Single(failure.Errors);
        Assert.Equal(("models.default.fallbacks", "fallback \"fast\" of agent \"dev\": model \"claude-haiku-4-5\" does not take turn-scoped system messages, which the agent's model uses."),
            (error.Path, error.Problem));

        // The other way round is fine: a primary without them can fall back to a model with them.
        var reversed = options with { Models = new Dictionary<string, ModelProfile> { ["default"] = new() { Model = "claude-haiku-4-5", Fallbacks = ["fast"] }, ["fast"] = new() } };
        Runner(reversed, providers);
    }

    // MDL-06: the same check for a provider tool.
    [Fact]
    public void A_fallback_whose_model_does_not_run_a_provider_tool_the_slot_uses_fails_validation()
    {
        var options = Options() with
        {
            Models = new Dictionary<string, ModelProfile> { ["default"] = new() { Fallbacks = ["fast"] }, ["fast"] = new() { Model = "claude-haiku-4-5" } },
            Tools = new Dictionary<string, ToolOptions> { ["search"] = new() { Source = "provider:web_search", Reason = "Tests only." } },
            ToolSets = new Dictionary<string, IReadOnlyList<string>> { ["all"] = ["search"] },
        };
        var providers = new Dictionary<string, IModelProvider> { ["claude"] = new PerModelCapabilities(noSearchFor: "claude-haiku-4-5") };

        var failure = Assert.Throws<ConfigurationException>(() => Runner(options, providers));

        Assert.Contains(
            "fallback \"fast\" of agent \"dev\": provider \"claude\" does not run the tool \"web_search\" with model \"claude-haiku-4-5\".", failure.Errors.Select(error => error.Problem));
    }

    // MDL-08, CLD-10: calls over the account's share wait their turn, the lead's first and the rest in the order they came.
    [Fact]
    public async Task Agents_share_the_providers_calls_in_turn_with_the_lead_first()
    {
        var provider = new HeldProvider();
        var gateway = Gateway(provider, maxConcurrentCalls: 1);

        var holder = Call(gateway, "holder");
        var queued = new[] { Call(gateway, "worker-1"), Call(gateway, "worker-2"), Call(gateway, "lead"), Call(gateway, "worker-3") };
        await Eventually(() => provider.Started.Count == 1);
        Assert.Equal(["holder"], provider.Started);

        provider.Release.SetResult();
        await Task.WhenAll(queued.Append(holder));

        Assert.Equal(["holder", "lead", "worker-1", "worker-2", "worker-3"], provider.Started);
    }

    // MDL-08: with no limit configured, calls do not wait for each other.
    [Fact]
    public async Task Without_a_limit_calls_run_together()
    {
        var provider = new HeldProvider();
        var gateway = Gateway(provider, maxConcurrentCalls: null);

        var calls = new[] { Call(gateway, "holder"), Call(gateway, "worker-1"), Call(gateway, "worker-2") };
        await Eventually(() => provider.Started.Count == 3);
        provider.Release.SetResult();
        await Task.WhenAll(calls);
    }

    // CLD-10, REL-01: a wait the provider asks for holds back every agent, not only the one that was told.
    [Fact]
    public async Task A_wait_the_provider_asks_for_holds_back_all_agents()
    {
        var time = new RecordingTimeProvider();
        var provider = new HeldProvider { FirstFailure = new ModelCallException(ModelFailure.RateLimited, retryAfter: TimeSpan.FromSeconds(30)) };
        provider.Release.SetResult();
        var gateway = Gateway(provider, maxConcurrentCalls: null, time);

        var first = Call(gateway, "worker-1"); // told to wait 30 seconds; it would retry after 1, but the wait holds it back
        var second = Call(gateway, "worker-2");
        await Eventually(() => time.Pending.Count == 2);
        time.Advance(TimeSpan.FromSeconds(29));
        await Eventually(() => time.Pending.Count(wait => wait == TimeSpan.FromSeconds(1)) == 2); // both now wait for the last second
        Assert.Equal(["worker-1"], provider.Started);

        time.Advance(TimeSpan.FromSeconds(1));
        await Task.WhenAll(first, second);
        Assert.Equal(["worker-1", "worker-1", "worker-2"], provider.Started.Order());
    }

    private static Task Call(ModelGateway gateway, string agent) => Drain(gateway.StreamAsync(agent, Request(agent), Ct));

    private static async Task Drain(IAsyncEnumerable<ModelEvent> events)
    {
        await foreach (var _ in events)
        {
        }
    }

    /// <summary>The instructions say who calls, so the provider can tell.</summary>
    private static ModelRequest Request(string agent) => new(new ModelProfile(), [], agent, [Message.User("Go.")], []);

    private static ModelGateway Gateway(IModelProvider provider, int? maxConcurrentCalls, TimeProvider? time = null)
    {
        var agents = new Dictionary<string, AgentDefinition>
        {
            ["lead"] = new() { Instructions = "Lead.", Pattern = new() { Type = PatternOptions.Team, Lead = "lead" } },
        };
        var options = new OfficinaOptions
        {
            Agents = agents,
            Providers = new Dictionary<string, ProviderOptions> { ["claude"] = ProviderOptions.Claude with { MaxConcurrentCalls = maxConcurrentCalls } },
        };
        return new ModelGateway(options, new Dictionary<string, IModelProvider> { ["claude"] = provider }, time ?? new FakeTimeProvider());
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var tries = 0; !condition() && tries < 400; tries++)
        {
            await Task.Delay(5, Ct);
        }

        Assert.True(condition(), "The condition did not become true.");
    }

    /// <summary>The kit for agent <c>dev</c>, whose profile <c>default</c> has the fallback <c>fast</c> when asked.</summary>
    private static TestKit Kit(int maxAttempts = 4, TimeSpan? maxDelay = null, bool fallback = false)
    {
        var options = Options(("echo", Extension("echo"))) with
        {
            Providers = new Dictionary<string, ProviderOptions>
            {
                ["claude"] = ProviderOptions.Claude with { Retry = new() { MaxAttempts = maxAttempts, InitialDelay = TimeSpan.FromSeconds(1), MaxDelay = maxDelay ?? TimeSpan.FromSeconds(30) } },
            },
            Models = new Dictionary<string, ModelProfile>
            {
                ["default"] = new() { Fallbacks = fallback ? ["fast"] : [] },
                ["fast"] = new() { Model = "claude-haiku-4-5" },
            },
        };
        return new TestKit(options, new Dictionary<string, ITool> { ["echo"] = new FakeTool(ToolKind.Read) });
    }

    /// <summary>Runs the agent, moving the clock on to each wait as it starts; says how long it waited.</summary>
    private static async Task<(AgentResult Result, TimeSpan Waited)> RunAsync(TestKit kit, Work? work = null)
    {
        var start = kit.Time.GetUtcNow();
        var run = work is null ? kit.RunAsync(Agent, "work", Ct) : kit.Runner.RunAsync(work, Ct);
        while (true)
        {
            await Eventually(() => run.IsCompleted || kit.Time.Pending.Count > 0);
            if (run.IsCompleted)
            {
                break;
            }

            kit.Time.Advance(kit.Time.Pending.Min()); // exactly to the next wait
        }

        return (await run, kit.Time.GetUtcNow() - start);
    }

    private static AgentRunner Runner(OfficinaOptions options, IReadOnlyDictionary<string, IModelProvider> providers) =>
        new(options, providers, new InMemoryStorage(), new Dictionary<string, ITool>(), new Dictionary<string, IGate>(), new Dictionary<string, ICheck>(),
            new Dictionary<string, IKnowledgeSource>(), new ScriptedHuman(), new InMemorySecretSource(new Dictionary<string, string>()), new FakeTimeProvider());

    /// <summary>A provider that, like Claude, differs between its models.</summary>
    private sealed class PerModelCapabilities(string? noSearchFor = null) : IModelProvider
    {
        public ProviderCapabilities CapabilitiesOf(string model) => new()
        {
            TurnScopedMessages = model != "claude-haiku-4-5",
            ProviderTools = model == noSearchFor ? new HashSet<string>() : new HashSet<string> { "web_search" },
        };

        public IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>A provider whose calls end when released, and which notes the order they started in.</summary>
    private sealed class HeldProvider : IModelProvider
    {
        private readonly ConcurrentQueue<string> started = new();
        private int failed;

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>What the first call fails with, if anything.</summary>
        public ModelCallException? FirstFailure { get; init; }

        public IReadOnlyList<string> Started => [.. started];

        public ProviderCapabilities CapabilitiesOf(string model) => ProviderCapabilities.None;

        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            started.Enqueue(Who(request));
            if (FirstFailure is not null && Interlocked.Exchange(ref failed, 1) == 0)
            {
                throw FirstFailure;
            }

            await Release.Task.WaitAsync(ct);
            yield return new Stopped(StopReason.Finished);
        }

        private static string Who(ModelRequest request) => request.Instructions;
    }
}
