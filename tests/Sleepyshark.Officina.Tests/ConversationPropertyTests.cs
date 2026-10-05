using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsCheck;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Tests;

/// <summary>
/// TEST-07: over generated sequences of runs on one conversation, with cancels, model failures and tool calls (reads and
/// writes, errors, invalid input, denials, unknown tools, writes whose audit fails), each saved and resumed, the
/// conversation always passes the role-sequence check, the prefix stays byte-identical, every tool call has exactly one
/// result, no write runs before its audit entry, and no secret reaches the events or the audit trail.
/// </summary>
public class ConversationPropertyTests
{
    public enum Target
    {
        Read,
        Write,
        Guarded,
        Missing,
    }

    public enum Behaviour
    {
        Ok,
        Throws,
        Invalid,
        Unaudited,
    }

    public enum Ending
    {
        Text,
        ModelFailure,
        OutputLimitWithTools,
        ContextFullWithTools,
        RefusalWithTools,
        EndWithTools,
    }

    /// <summary>One tool call: which tool, how it goes, and whether the approver approves it.</summary>
    public sealed record CallSpec(Target Tool, Behaviour Behaviour, bool Approve);

    /// <summary>
    /// One run: the model's tool-calling replies, how it ends, and when the host cancels: 0 never, 1–4 when that tool call
    /// of the run starts, 5 mid-stream, 6 before the run starts.
    /// </summary>
    public sealed record RunSpec(CallSpec[][] ToolReplies, Ending Ending, int CancelAt, bool Attended);

    private static readonly Gen<CallSpec> Call = Gen.Select(
        Gen.OneOfConst(Enum.GetValues<Target>()), Gen.OneOfConst(Enum.GetValues<Behaviour>()), Gen.Bool, (tool, behaviour, approve) => new CallSpec(tool, behaviour, approve));

    private static readonly Gen<RunSpec> Run = Gen.Select(
        Call.Array[1, 4].Array[0, 3], Gen.OneOfConst(Enum.GetValues<Ending>()), Gen.Int[0, 6], Gen.Bool,
        (replies, ending, cancelAt, attended) => new RunSpec(replies, ending, cancelAt, attended));

    private const string Secret = "hunter2";

    private static readonly JsonSerializerOptions Print = new() { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Any_sequence_of_runs_keeps_the_conversation_valid_and_every_write_audited_first()
    {
        await Property.CheckAsync(Run.Array[1, 4], CheckAsync, runs => JsonSerializer.Serialize(runs, Print));
    }

    private static async Task CheckAsync(RunSpec[] runs)
    {
        var conversation = new Conversation();
        ModelRequest? first = null;
        var sink = new RecordingSink(entry => entry.Kind == AuditKind.ToolStarted && entry.Input!.Contains("Unaudited", StringComparison.Ordinal));
        var violations = new ConcurrentQueue<string>();
        var ids = 0;
        foreach (var run in runs)
        {
            using var cancellation = new CancellationTokenSource();
            var started = 0;
            Tool Make(string name, ToolKind kind, bool needsApproval) => new(
                name, "A tool.", Agents.SearchSchema, kind,
                (input, _) =>
                {
                    var (id, behaviour) = (input.GetProperty("query").GetString()!.Split(' ')[0], input.GetProperty("query").GetString()!);
                    if (Interlocked.Increment(ref started) == run.CancelAt)
                    {
                        cancellation.Cancel();
                    }

                    if (kind == ToolKind.Write && !sink.Entries.Any(entry => entry.Kind == AuditKind.ToolStarted && entry.CallId == id))
                    {
                        violations.Enqueue($"Write {id} ran before its attempt was audited.");
                    }

                    return behaviour.Contains("Throws", StringComparison.Ordinal)
                        ? throw new InvalidOperationException($"Login {Secret} refused.")
                        : Task.FromResult(new ToolOutput("ok"));
                },
                needsApproval);

            var model = new ScriptedModel();
            foreach (var reply in run.ToolReplies)
            {
                model.Reply([new TextDelta("Working."), .. reply.Select(call => new BlockReceived(ScriptedModel.ToolCallBlock(ToolCall(call, ++ids)))), new ModelStopped(ModelStopReason.ToolUse)]);
            }

            _ = run.Ending switch
            {
                Ending.Text => model.Reply("Done."),
                Ending.ModelFailure => model.Fail(new InvalidOperationException("boom"), new TextDelta("Do")),
                _ => model.Reply(
                    new TextDelta("Let me"),
                    new BlockReceived(ScriptedModel.ToolCallBlock(new ToolCall($"cut{++ids}", "read", "{}"))),
                    new ModelStopped(run.Ending switch
                    {
                        Ending.ContextFullWithTools => ModelStopReason.ContextFull,
                        Ending.RefusalWithTools => ModelStopReason.Refusal,
                        Ending.EndWithTools => ModelStopReason.End,
                        _ => ModelStopReason.MaxTokens,
                    })),
            };
            var agent = new AgentDefinition
            {
                Model = model,
                Instructions = Agents.Instructions,
                AuditSink = sink,
                Secrets = [Secret],
                Tools = [Make("read", ToolKind.Read, false), Make("write", ToolKind.Write, false), Make("guarded", ToolKind.Write, true)],
                Approver = run.Attended ? new InputApprover() : null,
            };

            if (run.CancelAt == 6)
            {
                await cancellation.CancelAsync();
            }

            var before = conversation.Messages.Select(message => JsonSerializer.Serialize(message)).ToList();
            var events = new List<RunEvent>();
            await foreach (var runEvent in agent.StreamAsync(conversation, "Go.", "Date: 2026-10-05.", cancellation.Token))
            {
                events.Add(runEvent);
                if (runEvent is TextStreamed && run.CancelAt == 5)
                {
                    await cancellation.CancelAsync();
                }
            }

            var result = Assert.IsType<RunEnded>(events[^1]).Result;
            Assert.Single(events.OfType<RunEnded>());
            Assert.True(result is not Failed failed || failed.Error == "boom", $"The run failed: {result}");
            Assert.Null(RoleSequence.Problem(conversation.Messages));
            var calls = conversation.Messages.SelectMany(message => message.Blocks).Select(block => block.ToolCall?.Id).OfType<string>().ToList();
            var results = conversation.Messages.SelectMany(message => message.Blocks).Select(block => block.ToolResult?.CallId).OfType<string>().ToList();
            Assert.Equal(calls, results);
            Assert.Equal(calls.Count, calls.Distinct().Count());

            // TEST-02: every request repeats the conversation as it was before the run, byte for byte, after the same tools and instructions.
            foreach (var request in model.Requests)
            {
                Assert.Equal(before, request.Messages.Take(before.Count).Select(message => JsonSerializer.Serialize(message)));
                first ??= request;
                Assert.Empty(PrefixStability.Problems([first with { Messages = [] }, request with { Messages = [] }]));
            }

            Assert.Empty(PrefixStability.Problems(model.Requests));
            Assert.DoesNotContain(events, runEvent => JsonSerializer.Serialize(runEvent, runEvent.GetType()).Contains(Secret, StringComparison.Ordinal));

            // Saved and resumed between runs, as a host does.
            conversation = JsonSerializer.Deserialize<Conversation>(JsonSerializer.Serialize(conversation))!;
        }

        Assert.Empty(violations);
        Assert.DoesNotContain(sink.Entries, entry => JsonSerializer.Serialize(entry).Contains(Secret, StringComparison.Ordinal));
    }

    private static ToolCall ToolCall(CallSpec call, int number)
    {
        var id = $"call{number}";
        var input = call.Behaviour == Behaviour.Invalid ? """{"query":5}""" : JsonSerializer.Serialize(new { query = $"{id} {call.Behaviour} {(call.Approve ? "approve" : "deny")}" });
        return new ToolCall(id, call.Tool.ToString().ToLowerInvariant(), input);
    }

    /// <summary>Approves a call whose input asks for it: a policy, standing in for the human.</summary>
    private sealed class InputApprover : IApprover
    {
        public Task<Approval> ApproveAsync(Tool tool, ToolCall toolCall, CancellationToken cancellationToken) =>
            Task.FromResult(toolCall.Input.Contains(" approve", StringComparison.Ordinal) ? Approval.Granted : Approval.Denied("no"));
    }
}
