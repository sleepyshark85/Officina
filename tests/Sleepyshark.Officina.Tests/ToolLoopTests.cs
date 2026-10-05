using System.Collections.Concurrent;
using System.Text.Json;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Tests;

/// <summary>The tool loop (AGT-02, CTX-06, TOOL-02…06, GEN-04, AGT-05): S05's acceptance criteria.</summary>
public class ToolLoopTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ToolCall Search(string id, string query = "x") => new(id, "search", JsonSerializer.Serialize(new { query }));

    private static ToolResult[] Results(Conversation conversation, int message) =>
        [.. conversation.Messages[message].Blocks.Select(block => block.ToolResult!)];

    [Fact]
    public async Task Tool_calls_run_their_results_go_back_and_the_run_continues_to_its_answer()
    {
        var model = new ScriptedModel().CallTools(Search("c1", "Gaudy Night")).Reply("It is in stock.");
        var tool = Agents.Tool("search", schema: Agents.SearchSchema, handler: (input, _) => Task.FromResult(new ToolOutput($"found {input.GetProperty("query")}")));
        var conversation = new Conversation();

        var result = await Agents.With(model, tools: tool).RunAsync(conversation, "Is Gaudy Night in stock?", "Date: 2026-10-05.", Ct);

        Assert.Equal("It is in stock.", Assert.IsType<Completed>(result).Text);
        Assert.Equal([new ToolResult("c1", "found Gaudy Night", false)], Results(conversation, 3));
        Assert.Equal(conversation.Messages.Take(4), model.Requests[1].Messages);
        Assert.Null(RoleSequence.Problem(conversation.Messages));
        Assert.Empty(PrefixStability.Problems(model.Requests));
    }

    [Fact]
    public async Task Invalid_input_a_thrown_handler_a_denial_and_an_unknown_tool_each_come_back_as_error_results_and_the_run_continues()
    {
        var model = new ScriptedModel()
            .CallTools(new ToolCall("c1", "search", """{"query":5}"""), new ToolCall("c2", "broken", "{}"), new ToolCall("c3", "order", "{}"), new ToolCall("c4", "missing", "{}"))
            .Reply("Sorry, none of that worked.");
        var approver = new ScriptedApprover().Answer(Approval.Denied("not today"));
        var agent = Agents.With(
            model,
            tools: [
                Agents.SearchTool(),
                Agents.Tool("broken", handler: (_, _) => throw new InvalidOperationException("The database is down.")),
                Agents.Tool("order", kind: ToolKind.Write, needsApproval: true),
            ]) with
        { Approver = approver };
        var conversation = new Conversation();

        var result = await agent.RunAsync(conversation, "Do it all.", cancellationToken: Ct);

        Assert.Equal("Sorry, none of that worked.", Assert.IsType<Completed>(result).Text);
        var results = Results(conversation, 2);
        Assert.All(results, toolResult => Assert.True(toolResult.IsError));
        Assert.Equal(["c1", "c2", "c3", "c4"], results.Select(toolResult => toolResult.CallId));
        Assert.Equal("The input does not match the tool's schema:\n/query: must be string", results[0].Content);
        Assert.Equal("The database is down.", results[1].Content);
        Assert.Equal("The call was denied: not today", results[2].Content);
        Assert.Equal("There is no tool named 'missing'.", results[3].Content);
        Assert.Equal(["c3"], approver.Asked.Select(call => call.Id));
    }

    [Fact]
    public async Task An_approved_call_runs()
    {
        var ran = false;
        var model = new ScriptedModel().CallTools(new ToolCall("c1", "order", "{}")).Reply("Ordered.");
        var order = Agents.Tool("order", kind: ToolKind.Write, needsApproval: true, handler: (_, _) => Task.FromResult(new ToolOutput($"done {ran = true}")));
        var agent = Agents.With(model, tools: order) with { Approver = new ScriptedApprover().Answer(Approval.Granted) };

        var result = await agent.RunAsync(new Conversation(), "Order it.", cancellationToken: Ct);

        Assert.IsType<Completed>(result);
        Assert.True(ran);
    }

    [Fact]
    public async Task An_unattended_run_denies_calls_that_need_approval_and_tells_the_model_why()
    {
        var ran = false;
        var model = new ScriptedModel().CallTools(new ToolCall("c1", "order", "{}")).Reply("I could not order it.");
        var order = Agents.Tool("order", kind: ToolKind.Write, needsApproval: true, handler: (_, _) => Task.FromResult(new ToolOutput($"{ran = true}")));
        var conversation = new Conversation();

        var result = await Agents.With(model, tools: order).RunAsync(conversation, "Order it.", cancellationToken: Ct);

        Assert.IsType<Completed>(result);
        Assert.False(ran);
        Assert.Equal([new ToolResult("c1", "The call needs approval, and this run is unattended, so it was denied.", true)], Results(conversation, 2));
    }

    [Fact]
    public async Task Reads_overlap_and_writes_run_alone_in_call_order()
    {
        var log = new ConcurrentQueue<string>();
        var bothReading = new TaskCompletionSource();
        var reading = 0;
        async Task<ToolOutput> Read(JsonElement input, CancellationToken cancellationToken)
        {
            var name = input.GetProperty("query").GetString()!;
            log.Enqueue($"start {name}");
            if (Interlocked.Increment(ref reading) == 2)
            {
                bothReading.SetResult();
            }

            // Returns only once both reads have started: they can only finish by running at the same time.
            await bothReading.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            log.Enqueue($"end {name}");
            return new ToolOutput(name);
        }

        async Task<ToolOutput> Write(JsonElement input, CancellationToken cancellationToken)
        {
            var name = input.GetProperty("query").GetString()!;
            log.Enqueue($"start {name}");
            await Task.Yield();
            log.Enqueue($"end {name}");
            return new ToolOutput(name);
        }

        var model = new ScriptedModel()
            .CallTools(Search("c1", "r1"), Search("c2", "r2"), new("c3", "save", """{"query":"w1"}"""), new("c4", "save", """{"query":"w2"}"""))
            .Reply("Done.");
        var agent = Agents.With(model, tools: [Agents.Tool("search", schema: Agents.SearchSchema, handler: Read), Agents.Tool("save", kind: ToolKind.Write, handler: Write)]);
        var conversation = new Conversation();

        var result = await agent.RunAsync(conversation, "Go.", cancellationToken: Ct);

        Assert.IsType<Completed>(result);
        Assert.Equal(["r1", "r2", "w1", "w2"], Results(conversation, 2).Select(toolResult => toolResult.Content));
        var order = log.ToList();
        Assert.Equal(["start w1", "end w1", "start w2", "end w2"], order.Skip(4));
        Assert.Equal(["start r1", "start r2"], order.Take(2).Order());
    }

    [Fact]
    public async Task Results_of_one_reply_return_in_one_message_in_call_order_whichever_finishes_first()
    {
        var firstMayFinish = new TaskCompletionSource();
        var model = new ScriptedModel().CallTools(Search("c1", "slow"), Search("c2", "fast")).Reply("Both.");
        var tool = Agents.Tool("search", schema: Agents.SearchSchema, handler: async (input, cancellationToken) =>
        {
            var query = input.GetProperty("query").GetString()!;
            if (query == "slow")
            {
                await firstMayFinish.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            else
            {
                firstMayFinish.SetResult();
            }

            return new ToolOutput(query);
        });
        var conversation = new Conversation();

        var events = await Agents.CollectAsync(Agents.With(model, tools: tool).StreamAsync(conversation, "Go.", cancellationToken: Ct));

        Assert.Equal(["slow", "fast"], Results(conversation, 2).Select(toolResult => toolResult.Content));
        Assert.Equal([Role.User, Role.Assistant, Role.User, Role.Assistant], events.OfType<ConversationAppended>().Select(appended => appended.Message.Role));
    }

    [Fact]
    public async Task A_result_over_the_size_limit_is_truncated_with_a_note()
    {
        var model = new ScriptedModel().CallTools(Search("c1")).Reply("Long.");
        var tool = Agents.Tool("search", schema: Agents.SearchSchema, handler: (_, _) => Task.FromResult(new ToolOutput(new string('a', 70_000))));
        var conversation = new Conversation();

        await Agents.With(model, tools: tool).RunAsync(conversation, "Go.", cancellationToken: Ct);

        var content = Results(conversation, 2)[0].Content;
        Assert.StartsWith(new string('a', ToolPipeline.MaxResultLength) + "\n[Truncated: the result had 70000 characters", content, StringComparison.Ordinal);
        Assert.False(Results(conversation, 2)[0].IsError);
    }

    [Fact]
    public async Task A_reply_that_reaches_the_output_limit_while_calling_tools_stops_without_running_them_or_appending_anything()
    {
        // As the SDK stores a tool input cut short: empty.
        var ran = false;
        var model = new ScriptedModel()
            .Reply(new TextDelta("Let me look."), new BlockReceived(ScriptedModel.TextBlock("Let me look.")), new BlockReceived(ScriptedModel.ToolCallBlock(new ToolCall("c1", "search", "{}"))), new ModelStopped(ModelStopReason.MaxTokens))
            .Reply("Hello.");
        var tool = Agents.Tool("search", handler: (_, _) => Task.FromResult(new ToolOutput($"{ran = true}")));
        var agent = Agents.With(model, tools: tool);
        var conversation = new Conversation();

        var result = await agent.RunAsync(conversation, "Find it.", "Date: 2026-10-05.", Ct);

        Assert.Equal(new Stopped(StopReason.OutputLimit, null, default), result);
        Assert.False(ran);
        Assert.Empty(conversation.Messages);
        Assert.IsType<Completed>(await agent.RunAsync(conversation, "Hi.", cancellationToken: Ct));
        Assert.Null(RoleSequence.Problem(conversation.Messages));
    }

    public static TheoryData<ModelStopped, RunResult> StopsWithTools => new()
    {
        { new ModelStopped(ModelStopReason.ContextFull), new Stopped(StopReason.ContextFull, null, default) },
        { new ModelStopped(ModelStopReason.Refusal, "cyber"), new Stopped(StopReason.Refusal, "cyber", default) },
        { new ModelStopped(ModelStopReason.End), new Completed("Let me look.", default) },
        { new ModelStopped(ModelStopReason.Unknown, "pause_turn"), new Failed(FailureReason.UnexpectedStop, "The model stopped for a reason the run cannot act on: pause_turn.", default) },
    };

    [Theory]
    [MemberData(nameof(StopsWithTools))]
    public async Task A_reply_with_tool_calls_that_stops_for_another_reason_is_held_back_like_the_output_limit(ModelStopped stop, RunResult expected)
    {
        var ran = false;
        var model = new ScriptedModel()
            .Reply(new BlockReceived(ScriptedModel.TextBlock("Let me look.")), new BlockReceived(ScriptedModel.ToolCallBlock(Search("c1"))), stop)
            .Reply("Hello.");
        var tool = Agents.Tool("search", schema: Agents.SearchSchema, handler: (_, _) => Task.FromResult(new ToolOutput($"{ran = true}")));
        var agent = Agents.With(model, tools: tool);
        var conversation = new Conversation();

        var result = await agent.RunAsync(conversation, "Find it.", "Date: 2026-10-05.", Ct);

        Assert.Equal(expected, result);
        Assert.False(ran);
        Assert.Empty(conversation.Messages);
        Assert.IsType<Completed>(await agent.RunAsync(conversation, "Hi.", cancellationToken: Ct));
    }

    [Fact]
    public async Task A_pattern_that_takes_too_long_and_a_null_output_come_back_as_error_results()
    {
        var model = new ScriptedModel()
            .CallTools(new ToolCall("c1", "match", JsonSerializer.Serialize(new { text = new string('a', 40) + "!" })), new ToolCall("c2", "empty", "{}"))
            .Reply("Neither worked.");
        var agent = Agents.With(
            model,
            tools: [
                Agents.Tool("match", schema: """{"type":"object","properties":{"text":{"type":"string","pattern":"^(a+)+$"}}}"""),
                Agents.Tool("empty", handler: (_, _) => Task.FromResult(new ToolOutput(null!))),
            ]);
        var conversation = new Conversation();

        var result = await agent.RunAsync(conversation, "Go.", cancellationToken: Ct);

        Assert.Equal("Neither worked.", Assert.IsType<Completed>(result).Text);
        var results = Results(conversation, 2);
        Assert.All(results, toolResult => Assert.True(toolResult.IsError));
        Assert.StartsWith("The input could not be validated:", results[0].Content, StringComparison.Ordinal);
        Assert.Contains("Content", results[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_call_approved_after_the_host_cancelled_does_not_start()
    {
        using var cancellation = new CancellationTokenSource();
        var ran = false;
        var model = new ScriptedModel().CallTools(new ToolCall("c1", "order", "{}"));
        var order = Agents.Tool("order", kind: ToolKind.Write, needsApproval: true, handler: (_, _) => Task.FromResult(new ToolOutput($"{ran = true}")));
        var agent = Agents.With(model, tools: order) with { Approver = new CancellingApprover(cancellation) };
        var conversation = new Conversation();

        var result = await agent.RunAsync(conversation, "Order it.", cancellationToken: cancellation.Token);

        Assert.Equal(StopReason.Cancelled, Assert.IsType<Stopped>(result).Reason);
        Assert.False(ran);
        Assert.Equal([new ToolResult("c1", "The call was cancelled before it started.", true)], Results(conversation, 2));
    }

    [Fact]
    public async Task A_write_waiting_for_reads_when_the_host_cancels_does_not_start()
    {
        using var cancellation = new CancellationTokenSource();
        var wrote = false;
        var model = new ScriptedModel().CallTools(Search("c1"), new ToolCall("c2", "save", "{}"));
        var search = Agents.Tool("search", schema: Agents.SearchSchema, handler: async (_, _) =>
        {
            await cancellation.CancelAsync();
            return new ToolOutput("found");
        });
        var save = Agents.Tool("save", kind: ToolKind.Write, handler: (_, _) => Task.FromResult(new ToolOutput($"{wrote = true}")));
        var conversation = new Conversation();

        var result = await Agents.With(model, tools: [search, save]).RunAsync(conversation, "Find and save.", cancellationToken: cancellation.Token);

        Assert.Equal(StopReason.Cancelled, Assert.IsType<Stopped>(result).Reason);
        Assert.False(wrote);
        Assert.Equal(
            [new ToolResult("c1", "found", false), new ToolResult("c2", "The call was cancelled before it started.", true)],
            Results(conversation, 2));
    }

    /// <summary>Approves, while the host cancels the run: a cancel that lands during approval.</summary>
    private sealed class CancellingApprover(CancellationTokenSource cancellation) : IApprover
    {
        public async Task<Approval> ApproveAsync(Tool tool, ToolCall toolCall, CancellationToken cancellationToken)
        {
            await cancellation.CancelAsync();
            return Approval.Granted;
        }
    }

    [Fact]
    public async Task A_run_that_keeps_calling_tools_stops_at_the_iteration_limit_with_the_last_results_kept()
    {
        var model = new ScriptedModel();
        for (var call = 1; call <= RunEngine.MaxModelCalls; call++)
        {
            model.CallTools(Search($"c{call}"));
        }

        var conversation = new Conversation();

        var result = await Agents.With(model, tools: Agents.SearchTool()).RunAsync(conversation, "Loop.", cancellationToken: Ct);

        Assert.Equal(StopReason.IterationLimit, Assert.IsType<Stopped>(result).Reason);
        Assert.Equal(RunEngine.MaxModelCalls, model.Requests.Count);
        Assert.Equal(1 + (2 * RunEngine.MaxModelCalls), conversation.Messages.Length);
        Assert.Null(RoleSequence.Problem(conversation.Messages));
    }

    [Fact]
    public async Task Cancelling_during_tools_keeps_finished_results_marks_unstarted_calls_cancelled_and_calls_the_model_no_more()
    {
        using var cancellation = new CancellationTokenSource();
        var model = new ScriptedModel()
            .CallTools(Search("c1"), new ToolCall("c2", "save", "{}"), new ToolCall("c3", "save", "{}"))
            .Reply("Hello again.");
        var save = Agents.Tool("save", kind: ToolKind.Write, handler: async (_, _) =>
        {
            await cancellation.CancelAsync();
            return new ToolOutput("saved");
        });
        var agent = Agents.With(model, tools: [Agents.SearchTool(), save]);
        var conversation = new Conversation();

        var result = await agent.RunAsync(conversation, "Save twice.", cancellationToken: cancellation.Token);

        Assert.Equal(StopReason.Cancelled, Assert.IsType<Stopped>(result).Reason);
        Assert.Single(model.Requests);
        Assert.Equal(
            [new ToolResult("c1", "ok", false), new ToolResult("c2", "saved", false), new ToolResult("c3", "The call was cancelled before it started.", true)],
            Results(conversation, 2));

        // The next run's message follows the results, which the API joins into one user turn.
        Assert.Equal("Hello again.", Assert.IsType<Completed>(await agent.RunAsync(conversation, "Hi.", "Date: 2026-10-05.", Ct)).Text);
        Assert.Null(RoleSequence.Problem(conversation.Messages));
    }

    [Fact]
    public async Task A_call_running_when_the_run_is_cancelled_gets_a_cancelled_result()
    {
        using var cancellation = new CancellationTokenSource();
        var model = new ScriptedModel().CallTools(new ToolCall("c1", "wait", "{}"));
        var wait = Agents.Tool("wait", handler: async (_, cancellationToken) =>
        {
            await cancellation.CancelAsync();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new ToolOutput("never");
        });
        var conversation = new Conversation();

        var result = await Agents.With(model, tools: wait).RunAsync(conversation, "Wait.", cancellationToken: cancellation.Token);

        Assert.Equal(StopReason.Cancelled, Assert.IsType<Stopped>(result).Reason);
        Assert.Equal([new ToolResult("c1", "The call was cancelled while it ran.", true)], Results(conversation, 2));
    }

    [Fact]
    public void The_role_sequence_check_wants_one_result_per_call_in_call_order_right_after_the_calls()
    {
        var user = Message.Of(Role.User, "Go.");
        var calls = new Message(Role.Assistant, [ScriptedModel.ToolCallBlock(Search("c1")), ScriptedModel.ToolCallBlock(Search("c2"))]);
        Message Answers(params string[] ids) => new(Role.User, [.. ids.Select(id => new ContentBlock(new ToolResult(id, "ok", false)))]);

        Assert.Null(RoleSequence.Problem([user, calls, Answers("c1", "c2"), user]));
        Assert.NotNull(RoleSequence.Problem([user, calls]));
        Assert.NotNull(RoleSequence.Problem([user, calls, user]));
        Assert.NotNull(RoleSequence.Problem([user, calls, Answers("c2", "c1")]));
        Assert.NotNull(RoleSequence.Problem([user, calls, Answers("c1")]));
        Assert.NotNull(RoleSequence.Problem([user, Answers("c1")]));
        Assert.NotNull(RoleSequence.Problem([user, calls, Answers("c1", "c2"), Message.Of(Role.Operator, "Date.")]));
    }

    [Fact]
    public async Task A_conversation_with_tool_calls_and_results_round_trips_through_JSON()
    {
        var model = new ScriptedModel().CallTools(Search("c1")).Reply("Done.").Reply("Again.");
        var agent = Agents.With(model, tools: Agents.SearchTool());
        var conversation = new Conversation();
        await agent.RunAsync(conversation, "Go.", cancellationToken: Ct);

        var restored = JsonSerializer.Deserialize<Conversation>(JsonSerializer.Serialize(conversation))!;
        await agent.RunAsync(restored, "Again.", cancellationToken: Ct);

        Assert.Equal(conversation.Messages, restored.Messages.Take(conversation.Messages.Length));
        Assert.Equal(conversation.Id, restored.Id);
        Assert.Empty(PrefixStability.Problems(model.Requests));
    }
}
