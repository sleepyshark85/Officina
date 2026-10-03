namespace Sleepyshark.Officina.Sandbox;

/// <summary>
/// Retries a cleanup step that fails because a handle is still closing, such as a process that has ended but whose files
/// Windows, a console host or a virus scanner has not yet let go of. The last failure is thrown.
/// </summary>
internal static class RetryOnIo
{
    /// <summary>Waits 100, 200, 400 and 800 ms between five attempts: about 1.5 seconds in all.</summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff = [.. new[] { 100, 200, 400, 800 }.Select(ms => TimeSpan.FromMilliseconds(ms))];

    public static void Run(Action step, IReadOnlyList<TimeSpan> backoff, Action<TimeSpan> wait)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                step();
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < backoff.Count)
            {
                wait(backoff[attempt]);
            }
        }
    }
}
