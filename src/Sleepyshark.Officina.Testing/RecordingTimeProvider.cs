using Microsoft.Extensions.Time.Testing;

namespace Sleepyshark.Officina.Testing;

/// <summary>
/// A fake clock that knows which waits are pending, so a test can move it exactly to the next one, once the code under test
/// has started it, instead of guessing how long that takes.
/// </summary>
public sealed class RecordingTimeProvider : FakeTimeProvider
{
    private readonly Lock gate = new();
    private readonly List<Timer> timers = [];

    /// <summary>How long each running timer still has to go, such as a <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>.</summary>
    public IReadOnlyList<TimeSpan> Pending
    {
        get
        {
            lock (gate)
            {
                var now = GetUtcNow();
                return [.. timers.Select(timer => timer.Due - now).Where(remaining => remaining > TimeSpan.Zero && remaining < TimeSpan.MaxValue / 2)];
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, base.CreateTimer(callback, state, dueTime, period), DueAt(dueTime));
        lock (gate)
        {
            timers.Add(timer);
        }

        return timer;
    }

    private DateTimeOffset DueAt(TimeSpan dueTime) => dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : GetUtcNow() + dueTime;

    private sealed class Timer(RecordingTimeProvider owner, ITimer inner, DateTimeOffset due) : ITimer
    {
        public DateTimeOffset Due { get; private set; } = due;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                Due = owner.DueAt(dueTime);
            }

            return inner.Change(dueTime, period);
        }

        public void Dispose()
        {
            lock (owner.gate)
            {
                owner.timers.Remove(this);
            }

            inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
