namespace Orleans.Internal;

internal static class TimeProviderExtensions
{
    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(0xfffffffe);

    public static async Task DelayAsync(
        this TimeProvider timeProvider,
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (delay == Timeout.InfiniteTimeSpan)
        {
            await Task.Delay(delay, timeProvider, cancellationToken);
            return;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        var startTimestamp = timeProvider.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = timeProvider.GetElapsedTime(startTimestamp);
            if (elapsed >= delay)
            {
                return;
            }

            var remaining = delay - elapsed;
            await Task.Delay(
                remaining > MaximumTimerDelay ? MaximumTimerDelay : remaining,
                timeProvider,
                cancellationToken);
        }
    }

    public static async Task DelayUntilAsync(
        this TimeProvider timeProvider,
        DateTimeOffset dueTime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = dueTime - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            // Task.Delay cannot represent delays beyond approximately 49.7 days. Waiting in bounded
            // chunks preserves the absolute deadline without imposing that limit on callers.
            await Task.Delay(
                remaining > MaximumTimerDelay ? MaximumTimerDelay : remaining,
                timeProvider,
                cancellationToken);
        }
    }
}
