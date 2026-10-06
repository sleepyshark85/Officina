using System.Collections.Concurrent;

namespace Sleepyshark.Officina.Claude.Tests;

/// <summary>
/// A clock whose waits end at once, recording what each asked for, so retry tests never sleep; a held clock's waits never
/// end. Each wait is kept with the <see cref="Caller"/> that started it.
/// </summary>
internal sealed class InstantTime(bool held = false) : TimeProvider
{
    public static readonly AsyncLocal<string?> Caller = new();

    public ConcurrentQueue<TimeSpan> Waits { get; } = new();

    public ConcurrentQueue<(string? Caller, TimeSpan Wait)> WaitsByCaller { get; } = new();

    /// <summary>Completes when the first wait starts.</summary>
    public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Waits.Enqueue(dueTime);
        WaitsByCaller.Enqueue((Caller.Value, dueTime));
        Waiting.TrySetResult();
        return System.CreateTimer(callback, state, held ? Timeout.InfiniteTimeSpan : TimeSpan.Zero, period);
    }
}
