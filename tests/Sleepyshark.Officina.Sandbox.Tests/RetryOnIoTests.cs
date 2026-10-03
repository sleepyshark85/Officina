namespace Sleepyshark.Officina.Sandbox.Tests;

/// <summary>Deleting a folder whose last handle is still closing is retried, then reported.</summary>
public sealed class RetryOnIoTests
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)];

    [Fact]
    public void A_step_that_fails_twice_and_then_succeeds_is_retried_without_error()
    {
        var calls = 0;
        var waits = new List<TimeSpan>();

        RetryOnIo.Run(() => { if (++calls < 3) { throw new IOException("being used by another process"); } }, Backoff, waits.Add);

        Assert.Equal(3, calls);
        Assert.Equal(Backoff, waits);
    }

    [Fact]
    public void A_step_that_keeps_failing_throws_its_last_failure_after_the_attempts()
    {
        var calls = 0;

        var failure = Assert.Throws<UnauthorizedAccessException>(() =>
            RetryOnIo.Run(() => throw new UnauthorizedAccessException($"attempt {++calls}"), Backoff, _ => { }));

        Assert.Equal(3, calls);
        Assert.Equal("attempt 3", failure.Message);
    }

    [Fact]
    public void Other_failures_are_not_retried()
    {
        var calls = 0;

        Assert.Throws<InvalidOperationException>(() => RetryOnIo.Run(() => { calls++; throw new InvalidOperationException(); }, Backoff, _ => { }));

        Assert.Equal(1, calls);
    }
}
