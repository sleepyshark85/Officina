using System.Text.Json;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>
/// The owner's queue (HITL-05): a command without a number acts only on the request the owner has seen, and each answer says
/// what it answered; one with a number that waits for nothing says which numbers do.
/// </summary>
public sealed class OwnerQueueTests : IDisposable
{
    private readonly StringWriter output = new() { NewLine = "\n" };
    private readonly OwnerQueue queue;
    private readonly CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

    public OwnerQueueTests() => queue = new OwnerQueue(output, "/");

    public void Dispose()
    {
        cancel.Cancel();
        cancel.Dispose();
        output.Dispose();
    }

    [Fact]
    public void Without_a_number_and_nothing_waiting_it_says_nothing_waits()
    {
        Assert.Equal("error: nothing waits for you.", queue.Answer(null, "approve", HumanAnswer.Approve));
        Assert.Equal("error: nothing waits for you.", queue.Answer(3, "approve", HumanAnswer.Approve));
    }

    [Fact]
    public async Task Without_a_number_the_only_request_that_waits_is_answered_and_named()
    {
        var plan = Ask(new HumanRequest(HumanRequestKind.SignOff, "lead", "Approve the lead's plan before work starts?", DateTimeOffset.MaxValue));

        var said = queue.Answer(null, "approve", HumanAnswer.Approve);

        Assert.Equal("approved #1: lead: Approve the lead's plan before work starts?", said);
        Assert.True((await plan).Approved);
        Assert.Equal("#1 lead needs your sign-off: Approve the lead's plan before work starts? Answer with /approve 1 or /deny 1.\n", output.ToString());
    }

    // Each answer names what it answered: an approval by its agent, tool and arguments.
    [Fact]
    public async Task An_answer_by_number_names_the_call_it_approved()
    {
        var call = Ask(Approval("developer[1]", "run_command", """{"command":"dotnet new console"}"""));

        Assert.Equal("""approved #1: developer[1] run_command {"command":"dotnet new console"}""", queue.Answer(1, "approve", HumanAnswer.Approve));
        Assert.True((await call).Approved);
    }

    [Fact]
    public void Without_a_number_and_several_waiting_none_is_answered_and_they_are_listed()
    {
        _ = Ask(new HumanRequest(HumanRequestKind.SignOff, "lead", "Approve the plan?\n  a Parse", DateTimeOffset.MaxValue));
        _ = Ask(new HumanRequest(HumanRequestKind.Question, "developer[1]", "Which database?", DateTimeOffset.MaxValue));

        var said = queue.Answer(null, "deny", HumanAnswer.Deny);
        var wrong = queue.Answer(5, "deny", HumanAnswer.Deny);

        Assert.Equal(
            "error: 2 requests wait for you; type /deny with the number of one:\n  #1 sign-off, lead: Approve the plan?\n  #2 question, developer[1]: Which database?",
            said);
        Assert.Equal("error: nothing waits for you with that number; #1, #2 do.", wrong);
        Assert.Equal(2, queue.Waiting.Count());
    }

    // The hints that name a question's number say the answer follows it.
    [Fact]
    public void A_hint_to_answer_a_question_by_its_number_includes_the_answer()
    {
        _ = Ask(new HumanRequest(HumanRequestKind.Question, "developer[2]", "Which database?", DateTimeOffset.MaxValue));
        _ = Ask(new HumanRequest(HumanRequestKind.Question, "lead", "Which test framework?", DateTimeOffset.MaxValue));

        Assert.Equal(
            "error: 2 requests wait for you; type /answer <n> <your answer> with the number of one:\n  #1 question, developer[2]: Which database?\n  #2 question, lead: Which test framework?",
            queue.Answer(null, "answer", HumanAnswer.Reply("Postgres.")));
        Assert.StartsWith("#1 developer[2] asks: Which database? Answer with /answer 1 <your answer> or /deny 1.\n", output.ToString(), StringComparison.Ordinal);
    }

    // The question the owner read goes away, and another arrives: a numberless answer is refused, with how to answer the new one.
    [Fact]
    public async Task Without_a_number_an_answer_meant_for_a_question_that_went_away_says_how_to_answer_the_new_one()
    {
        using var first = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
        var gone = Ask(new HumanRequest(HumanRequestKind.Question, "developer[1]", "Tabs?", DateTimeOffset.MaxValue), first.Token);
        await first.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await gone);
        _ = Ask(new HumanRequest(HumanRequestKind.Question, "lead", "Which database?", DateTimeOffset.MaxValue));

        Assert.Equal(
            "error: #1 is no longer waiting; #2 is new: lead: Which database?. Type /answer 2 <your answer>.",
            queue.Answer(null, "answer", HumanAnswer.Reply("Tabs.")));
    }

    // The owner reads #1; it goes away, as its agent is cancelled, and #2 arrives: a numberless approve, meant for #1, is refused.
    [Fact]
    public async Task Without_a_number_a_request_that_came_after_the_one_the_owner_read_went_away_is_refused()
    {
        using var first = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
        var test = Ask(Approval("developer[1]", "run_command", """{"command":"dotnet test"}"""), first.Token);
        await first.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await test);
        _ = Ask(Approval("developer[2]", "run_command", """{"command":"rm -rf src"}"""));

        var said = queue.Answer(null, "approve", HumanAnswer.Approve);

        Assert.Equal("""error: #1 is no longer waiting; #2 is new: developer[2] run_command {"command":"rm -rf src"}. Type /approve 2.""", said);
        Assert.Equal([2], queue.Waiting.Select(entry => entry.Number));
        Assert.StartsWith("approved #2:", queue.Answer(2, "approve", HumanAnswer.Approve), StringComparison.Ordinal);
    }

    // A request shown before the owner's last answer was seen, so once it is the only one, it is answered without its number.
    [Fact]
    public async Task Without_a_number_a_request_left_waiting_after_the_last_answer_is_answered()
    {
        _ = Ask(Approval("developer[1]", "run_command", """{"command":"dotnet build"}"""));
        var test = Ask(Approval("developer[2]", "run_command", """{"command":"dotnet test"}"""));
        queue.Answer(1, "deny", HumanAnswer.Deny);

        Assert.StartsWith("approved #2:", queue.Answer(null, "approve", HumanAnswer.Approve), StringComparison.Ordinal);
        Assert.True((await test).Approved);
    }

    // TOOL-10: an irreversible call is never approved without its number.
    [Fact]
    public void An_irreversible_call_needs_its_number()
    {
        _ = Ask(Approval("developer[1]", "push", """{"branch":"main"}""") with { Irreversible = true });

        Assert.Equal("error: #1 is irreversible, so type its number: /approve 1.", queue.Answer(null, "approve", HumanAnswer.Approve));
        Assert.Single(queue.Waiting);
        Assert.StartsWith("approved #1:", queue.Answer(1, "approve", HumanAnswer.Approve), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_number_a_command_of_the_wrong_kind_is_refused_and_the_request_keeps_waiting()
    {
        var question = Ask(new HumanRequest(HumanRequestKind.Question, "lead", "Which database?", DateTimeOffset.MaxValue));

        Assert.Equal("error: #1 is not an approval or sign-off.", queue.Answer(null, "approve", HumanAnswer.Approve));
        Assert.Equal("answered #1: lead: Which database?", queue.Answer(null, "answer", HumanAnswer.Reply("Postgres.")));
        Assert.Equal("Postgres.", (await question).Text);
    }

    private static HumanRequest Approval(string agent, string tool, string arguments) =>
        new(HumanRequestKind.Approval, agent, "the tool needs approval", DateTimeOffset.MaxValue, tool, JsonDocument.Parse(arguments).RootElement.Clone());

    private Task<HumanAnswer> Ask(HumanRequest request, CancellationToken? ct = null) => queue.AskAsync(request, ct ?? cancel.Token).AsTask();
}
