using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>
/// The owner's queue (HITL-05): a command without a number acts on the only request that waits, and one with a number that
/// waits for nothing says which numbers do.
/// </summary>
public sealed class OwnerQueueTests : IDisposable
{
    private readonly StringWriter output = new() { NewLine = "\n" };
    private readonly OwnerQueue queue;

    public OwnerQueueTests() => queue = new OwnerQueue(output, "/");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => output.Dispose();

    [Fact]
    public void Without_a_number_and_nothing_waiting_it_says_nothing_waits()
    {
        Assert.Equal("error: nothing waits for you.", queue.Answer(null, "approve", HumanAnswer.Approve));
        Assert.Equal("error: nothing waits for you.", queue.Answer(3, "approve", HumanAnswer.Approve));
    }

    [Fact]
    public async Task Without_a_number_the_only_request_that_waits_is_answered_and_named()
    {
        var plan = queue.AskAsync(new HumanRequest(HumanRequestKind.SignOff, "lead", "Approve the lead's plan before work starts?", DateTimeOffset.MaxValue), Ct);

        var said = queue.Answer(null, "approve", HumanAnswer.Approve);

        Assert.Equal("approved #1.", said);
        Assert.True((await plan).Approved);
        Assert.Equal("#1 lead needs your sign-off: Approve the lead's plan before work starts? Answer with /approve 1 or /deny 1.\n", output.ToString());
    }

    [Fact]
    public async Task Without_a_number_and_several_waiting_none_is_answered_and_they_are_listed()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var plan = queue.AskAsync(new HumanRequest(HumanRequestKind.SignOff, "lead", "Approve the plan?\n  a Parse", DateTimeOffset.MaxValue), cancel.Token);
        var question = queue.AskAsync(new HumanRequest(HumanRequestKind.Question, "developer[1]", "Which database?", DateTimeOffset.MaxValue), cancel.Token);

        var said = queue.Answer(null, "deny", HumanAnswer.Deny);
        var wrong = queue.Answer(5, "deny", HumanAnswer.Deny);

        Assert.Equal(
            "error: 2 requests wait for you; type /deny with the number of one:\n  #1 sign-off from lead: Approve the plan?\n  #2 question from developer[1]: Which database?",
            said);
        Assert.Equal("error: nothing waits for you with that number; #1, #2 do.", wrong);
        Assert.Equal(2, queue.Waiting.Count());
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await plan);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await question);
    }

    [Fact]
    public async Task Without_a_number_a_command_of_the_wrong_kind_is_refused_and_the_request_keeps_waiting()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var question = queue.AskAsync(new HumanRequest(HumanRequestKind.Question, "lead", "Which database?", DateTimeOffset.MaxValue), cancel.Token);

        Assert.Equal("error: #1 is not an approval or sign-off.", queue.Answer(null, "approve", HumanAnswer.Approve));
        Assert.Equal("answered #1.", queue.Answer(null, "answer", HumanAnswer.Reply("Postgres.")));
        Assert.Equal("Postgres.", (await question).Text);
    }
}
