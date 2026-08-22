using Orleans.Reminders.TestKit;

namespace Orleans.Reminders.TestKit.Tests;

internal static class XunitReminderTableTestAdapter
{
    public static Task RunAsync(
        ReminderTableTestRunner runner,
        string guarantee,
        Func<Task> execute)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrWhiteSpace(guarantee);
        ArgumentNullException.ThrowIfNull(execute);

        if (runner.SkippedGuarantees.TryGetValue(guarantee, out var reason))
        {
            throw Xunit.Sdk.SkipException.ForSkip(reason);
        }

        return execute();
    }
}
