using System.Text.Json;
using CsCheck;
using Sleepyshark.Officina;
using Sleepyshark.Officina.Testing;
using Sleepyshark.Officina.Tests;
using static BookshopAssistant.Tests.ConsoleSession;

namespace BookshopAssistant.Tests;

/// <summary>
/// TEST-07 and TEST-02 for the reference application: over generated sequences of replies, each saved to the sessions
/// table's text column after every step and resumed with a freshly built agent, some crashing while their tools run, the
/// prefix stays byte-identical and every request is valid.
/// </summary>
public class SessionPropertyTests(BookshopDatabase database) : IClassFixture<BookshopDatabase>
{
    /// <summary>One reply: its text, whether it carries a reasoning block, calls a tool, sends run context, and crashes while the tool runs.</summary>
    public sealed record ReplySpec(string Text, bool Thinking, bool CallsTool, bool Context, bool Crashes);

    /// <summary>Pieces that JSON escapes, or that a store could normalize: quotes, backslashes, NUL, non-ASCII, a surrogate pair, a line separator.</summary>
    private static readonly string[] Pieces = ["café", "\"quoted\"", "back\\slash", "nul\0", "😀", "line\u2028sep", "<b>&amp;", "+1", "tab\tnew\nline", " "];

    private static readonly Gen<ReplySpec> Steps = Gen.Select(
        Gen.Int[0, Pieces.Length - 1].Array[1, 6].Select(picked => string.Concat(picked.Select(index => Pieces[index]))),
        Gen.Bool, Gen.Bool, Gen.Bool, Gen.Int[0, 3],
        (text, thinking, callsTool, context, crash) => new ReplySpec(text, thinking, callsTool, context, callsTool && crash == 0));

    [DatabaseFact]
    public async Task TEST_07_the_prefix_stays_byte_identical_across_saves_to_the_sessions_table_and_resumes()
    {
        await Property.CheckAsync(Steps.Array[1, 5], CheckAsync, steps => JsonSerializer.Serialize(steps), cases: 40);
    }

    private async Task CheckAsync(ReplySpec[] steps)
    {
        var store = new SessionStore(database.DataSource);
        var id = Guid.NewGuid().ToString("N")[..12];
        var requests = new List<ModelRequest>();
        var created = false;
        foreach (var step in steps)
        {
            // A new start of the application: only the stored text is left, and the agent is built afresh.
            var stored = await store.LoadAsync(id, TestContext.Current.CancellationToken);
            var conversation = stored?.Conversation ?? new Conversation { Id = id };
            if (stored is not null)
            {
                Assert.Equal(await database.ScalarAsync<string>("select conversation from sessions where id = $1", id), JsonSerializer.Serialize(conversation));
            }

            var model = Model();
            if (step.CallsTool)
            {
                model.Reply(SayThenCall(step.Text, Call($"call-{Guid.NewGuid():N}", "get_book", new { bookId = 144 })));
            }

            model.Reply([.. step.Thinking ? [new BlockReceived(new ContentBlock(null, """{ "type" : "thinking", "thinking": "", "signature" : "c2ln+/=" }"""))] : Array.Empty<ModelEvent>(),
                new BlockReceived(ScriptedModel.TextBlock(step.Text)), new ModelStopped(ModelStopReason.End)]);
            var agent = BookshopAgent.Create(model, database.Tools, new ScriptedApprover(), new AuditTable(database.DataSource), [], TimeProvider.System);
            await foreach (var runEvent in agent.StreamAsync(conversation, $"Say {step.Text}", step.Context ? $"Context: {step.Text}" : null, TestContext.Current.CancellationToken))
            {
                if (runEvent is ConversationAppended)
                {
                    await store.SaveAsync(conversation, "Sam", default, 0, created, TestContext.Current.CancellationToken);
                    created = true;
                }

                if (runEvent is RunEnded { Result: var result })
                {
                    Assert.True(result is Completed, $"The run ended {result}.");
                }

                if (step.Crashes && runEvent is ToolCallStarted)
                {
                    break;
                }
            }

            requests.AddRange(model.Requests);
        }

        Assert.All(requests, request => Assert.Null(RoleSequence.Problem(request.Messages)));
        Assert.Empty(PrefixStability.Problems(requests));
    }
}
