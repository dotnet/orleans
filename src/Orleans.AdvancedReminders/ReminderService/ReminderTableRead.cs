namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal static class ReminderTableRead
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(1);

    internal static Task<T> WaitAsync<T>(Task<T> read, TimeProvider timeProvider, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return read.WaitAsync(ReadTimeout, timeProvider, cancellationToken);
    }
}
