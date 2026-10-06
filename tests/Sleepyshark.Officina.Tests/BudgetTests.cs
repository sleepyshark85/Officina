using System.Runtime.CompilerServices;
using System.Text.Json;
using CsCheck;
using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Tests;

/// <summary>Prices, usage in results and budgets (BUD-01…03), with the overshoot property of TEST-07.</summary>
public class BudgetTests
{
    /// <summary>Opus 5.5's list price: $4 in, $20 out, $0.20 cache read; writes 1.25× input for five minutes, 2× for an hour.</summary>
    private static readonly ModelPrice Price = new(4m, 20m, 0.2m, 5m, 8m);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ModelEvent[] CallTool(string id, Usage usage) =>
        [new BlockReceived(ScriptedModel.ToolCallBlock(new ToolCall(id, "search", """{"query":"x"}"""))), new UsageReceived(usage), new ModelStopped(ModelStopReason.ToolUse)];

    private static AgentDefinition Agent(ScriptedModel model, TimeProvider? time = null, Tool? tool = null) =>
        Agents.With(model, tools: tool ?? Agents.SearchTool()) with { Time = time ?? TimeProvider.System };

    [Fact]
    public void Cost_prices_each_kind_of_token_and_cache_writes_by_how_long_they_are_kept()
    {
        Assert.Equal(0.0001m + 0.0002m + 0.00002m + 0.003m + 0.0032m, Price.Cost(new Usage(25, 10, 100, 1_000, CacheWriteHour: 400)));
    }

    [Fact]
    public async Task BUD_03_a_result_reports_tokens_cost_model_calls_tool_calls_and_duration_and_each_usage_event_its_cost()
    {
        var time = new FakeTimeProvider();
        var slow = Agents.Tool("search", schema: Agents.SearchSchema, handler: (_, _) =>
        {
            time.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult(new ToolOutput("ok"));
        });
        var model = new ScriptedModel { Price = Price }
            .Reply([.. CallTool("c1", new Usage(100, 20, 0, 1_000, 1_000))[..^1], .. CallTool("c2", new Usage(0, 5, 0, 0))[..1], new ModelStopped(ModelStopReason.ToolUse)])
            .Reply(new BlockReceived(ScriptedModel.TextBlock("Done.")), new UsageReceived(new Usage(10, 5, 1_100, 0)), new ModelStopped(ModelStopReason.End));

        var events = await Agents.CollectAsync(Agent(model, time, slow).StreamAsync(new Conversation(), "Go.", cancellationToken: Ct));

        var result = Assert.IsType<Completed>(Assert.IsType<RunEnded>(events[^1]).Result);
        Assert.Equal(new Usage(110, 25, 1_100, 1_000, 1_000), result.Usage);
        Assert.Equal((0.00044m + 0.0005m + 0.00022m + 0.008m, 2, 2, TimeSpan.FromSeconds(4)), (result.Cost, result.ModelCalls, result.ToolCalls, result.Duration));
        Assert.Equal(result.Cost, events.OfType<UsageReported>().Sum(reported => reported.Cost));
    }

    [Fact]
    public async Task BUD_01_a_cost_budget_used_up_stops_the_run_before_the_next_model_call_with_its_reason()
    {
        var model = new ScriptedModel { Price = Price }.Reply(CallTool("c1", new Usage(100, 50, 0, 0))).Reply("Unused.");
        var conversation = new Conversation();

        var result = await Agent(model).RunAsync(conversation, "Go.", new() { Budget = new Budget { Cost = 0.001m } }, Ct);

        var stopped = Assert.IsType<Stopped>(result);
        Assert.Equal((StopReason.Budget, "The cost budget is used up: $0.0014 of $0.001."), (stopped.Reason, stopped.Detail));
        Assert.Equal(0.0014m, stopped.Cost);
        Assert.Single(model.Requests);
        Assert.Null(RoleSequence.Problem(conversation.Messages));
        Assert.Equal([Role.User, Role.Assistant, Role.User], conversation.Messages.Select(message => message.Role));
    }

    [Fact]
    public async Task BUD_01_each_call_s_output_limit_is_lowered_to_what_the_remaining_cost_and_tokens_allow()
    {
        var model = new ScriptedModel { Price = Price }.Reply(CallTool("c1", new Usage(200, 100, 0, 0))).Reply("Done.").Reply("Free.");

        await Agent(model).RunAsync(new Conversation(), "Go.", new() { Budget = new Budget { Cost = 0.01m, Tokens = 1_000 } }, Ct);
        await Agent(model).RunAsync(new Conversation(), "Go.", cancellationToken: Ct);

        // $0.01 buys 500 output tokens, and 1,000 tokens are left; then $0.0072 buys 360, and 700 tokens are left.
        Assert.Equal([500, 360, null], model.Requests.Select(request => request.MaxOutputTokens));
    }

    [Fact]
    public async Task BUD_01_a_reply_cut_short_by_the_lowered_limit_stops_for_the_budget_and_one_cut_by_the_model_s_own_limit_does_not()
    {
        ModelEvent[] Cut(long output) => [new TextDelta("Long"), new BlockReceived(ScriptedModel.TextBlock("Long")), new UsageReceived(new Usage(10, output, 0, 0)), new ModelStopped(ModelStopReason.MaxTokens)];
        var model = new ScriptedModel { Price = Price }.Reply(Cut(50)).Reply(Cut(30));

        var cut = await Agent(model).RunAsync(new Conversation(), "Go.", new() { Budget = new Budget { Cost = 0.001m } }, Ct);
        var own = await Agent(model).RunAsync(new Conversation(), "Go.", new() { Budget = new Budget { Cost = 0.01m } }, Ct);

        Assert.Equal((StopReason.Budget, "The cost budget is used up: $0.00104 of $0.001."), (Assert.IsType<Stopped>(cut).Reason, ((Stopped)cut).Detail));
        Assert.Equal(StopReason.OutputLimit, Assert.IsType<Stopped>(own).Reason);
    }

    [Fact]
    public async Task BUD_01_model_call_and_time_limits_stop_the_run_before_the_next_call()
    {
        var time = new FakeTimeProvider();
        var slow = Agents.Tool("search", schema: Agents.SearchSchema, handler: (_, _) =>
        {
            time.Advance(TimeSpan.FromSeconds(10));
            return Task.FromResult(new ToolOutput("ok"));
        });
        var model = new ScriptedModel().CallTools(new ToolCall("c1", "search", """{"query":"x"}""")).CallTools(new ToolCall("c2", "search", """{"query":"x"}"""));

        var calls = await Agent(model).RunAsync(new Conversation(), "Go.", new() { Budget = new Budget { ModelCalls = 1 } }, Ct);
        var timed = await Agent(model, time, slow).RunAsync(new Conversation(), "Go.", new() { Budget = new Budget { Time = TimeSpan.FromSeconds(5) } }, Ct);

        Assert.Equal("The model call budget is used up: 1 of 1.", Assert.IsType<Stopped>(calls).Detail);
        Assert.Equal("The time budget is used up: 10 s of 5 s.", Assert.IsType<Stopped>(timed).Detail);
        Assert.Equal(2, model.Requests.Count);
    }

    [Fact]
    public async Task BUD_01_a_used_up_budget_stops_before_the_first_call_and_appends_nothing()
    {
        var model = new ScriptedModel().Reply("Unused.");
        var conversation = new Conversation();

        var result = await Agent(model).RunAsync(conversation, "Go.", new() { Budget = new Budget { Tokens = 0 } }, Ct);

        Assert.Equal(StopReason.Budget, Assert.IsType<Stopped>(result).Reason);
        Assert.Empty(model.Requests);
        Assert.Empty(conversation.Messages);
    }

    [Fact]
    public async Task BUD_01_a_cost_budget_is_used_up_when_what_is_left_buys_less_than_one_output_token()
    {
        // The first call costs $0.0014; the $0.00001 left would buy half an output token at $20 per million.
        var model = new ScriptedModel { Price = Price }.Reply(CallTool("c1", new Usage(100, 50, 0, 0))).Reply("Unused.");

        var result = await Agent(model).RunAsync(new Conversation(), "Go.", new() { Budget = new Budget { Cost = 0.00141m } }, Ct);

        Assert.Equal((StopReason.Budget, "The cost budget is used up: $0.0014 of $0.00141."), (Assert.IsType<Stopped>(result).Reason, ((Stopped)result).Detail));
        Assert.Single(model.Requests);
    }

    [Fact]
    public void A_cost_budget_needs_a_model_with_a_price()
    {
        var agent = Agent(new ScriptedModel());

        Assert.Throws<InvalidOperationException>(() => agent.StreamAsync(new Conversation(), "Go.", new() { Budget = new Budget { Cost = 1m } }, Ct));
    }

    /// <summary>One model call of the property: the prompt's tokens, how much output it wants, and whether it compacts.</summary>
    public sealed record PlannedCall(int Input, int CacheRead, int CacheWrite, int Output, bool Compacts);

    /// <summary>A budget, and the calls the model would make if nothing stopped it; each but the last asks for a tool.</summary>
    public sealed record BudgetCase(decimal? Cost, long? Tokens, int? ModelCalls, PlannedCall[] Calls);

    private static readonly Gen<BudgetCase> Cases = Gen.Select(
        Gen.Select(Gen.Bool, Gen.Int[1, 500], (limited, cents) => limited ? cents / 10_000m : (decimal?)null),
        Gen.Select(Gen.Bool, Gen.Long[1, 60_000], (limited, tokens) => limited ? tokens : (long?)null),
        Gen.Select(Gen.Bool, Gen.Int[1, 8], (limited, calls) => limited ? calls : (int?)null),
        Gen.Select(Gen.Int[0, 3_000], Gen.Int[0, 20_000], Gen.Int[0, 5_000], Gen.Int[0, 8_000], Gen.Bool, (input, read, write, output, compacts) => new PlannedCall(input, read, write, output, compacts)).Array[1, 10],
        (cost, tokens, calls, plan) => new BudgetCase(cost, tokens, calls, plan));

    [Fact]
    public async Task TEST_07_a_budget_is_overshot_by_at_most_one_call_s_input_or_twice_that_when_it_compacts()
    {
        await Property.CheckAsync(Cases, CheckOvershootAsync, @case => JsonSerializer.Serialize(@case));
    }

    private static async Task CheckOvershootAsync(BudgetCase @case)
    {
        var model = new PlannedModel(@case.Calls);
        var budget = new Budget { Cost = @case.Cost, Tokens = @case.Tokens, ModelCalls = @case.ModelCalls };
        var agent = new AgentDefinition { Model = model, Instructions = Agents.Instructions, Tools = [Agents.SearchTool()] };

        var result = await agent.RunAsync(new Conversation(), "Go.", new() { Budget = budget }, Ct);

        Assert.True(result is Completed or Stopped { Reason: StopReason.Budget }, $"The run ended {result}.");
        // Only the last call can cross a limit, as each call starts below all of them; it overshoots by its input at most.
        var last = model.Made == 0 ? new PlannedCall(0, 0, 0, 0, false) : @case.Calls[model.Made - 1];
        var factor = last.Compacts ? 2 : 1;
        var tokenOvershoot = (long)(last.Input + last.CacheRead + last.CacheWrite) * factor;
        var costOvershoot = Price.Cost(new Usage(last.Input, 0, last.CacheRead, last.CacheWrite)) * factor;
        Assert.True(result.Usage.Total <= @case.Tokens + tokenOvershoot || @case.Tokens is null, $"{result.Usage.Total} tokens used of {@case.Tokens}.");
        Assert.True(result.Cost <= @case.Cost + costOvershoot || @case.Cost is null, $"${result.Cost} spent of ${@case.Cost}.");
        Assert.True(result.ModelCalls <= @case.ModelCalls || @case.ModelCalls is null);
        Assert.Equal(model.Made, result.ModelCalls);
    }

    /// <summary>
    /// A model that makes the planned calls in order, keeping each reply within the request's output limit as a real one
    /// does; a call that compacts reads its prompt twice, as server-side compaction does.
    /// </summary>
    private sealed class PlannedModel(PlannedCall[] plan) : IModel
    {
        public int Made { get; private set; }

        public string Settings => "planned";

        public string Provider => "test";

        public string Name => "planned";

        public ModelPrice? Price => BudgetTests.Price;

        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            var call = plan[Made++];
            var factor = call.Compacts ? 2 : 1;
            var output = Math.Min(call.Output, request.MaxOutputTokens ?? int.MaxValue);
            var last = Made == plan.Length;
            yield return new BlockReceived(last ? ScriptedModel.TextBlock("Done.") : ScriptedModel.ToolCallBlock(new ToolCall($"c{Made}", "search", """{"query":"x"}""")));
            yield return new UsageReceived(new Usage(call.Input * factor, output, call.CacheRead * factor, call.CacheWrite * factor));
            yield return new ModelStopped(last ? ModelStopReason.End : ModelStopReason.ToolUse);
        }
    }
}
