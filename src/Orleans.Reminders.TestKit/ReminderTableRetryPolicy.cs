using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Reminders.TestKit;

internal static class ReminderTableRetryPolicy
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(100);
    private static readonly AsyncLocal<(TimeSpan Timeout, TimeSpan Delay)?> TestTiming = new();

    private static TimeSpan Timeout => TestTiming.Value?.Timeout ?? DefaultTimeout;

    private static TimeSpan Delay => TestTiming.Value?.Delay ?? DefaultDelay;

    internal static IDisposable UseTestTiming(TimeSpan timeout, TimeSpan delay)
    {
        var previous = TestTiming.Value;
        TestTiming.Value = (timeout, delay);
        return new RestoreTiming(previous);
    }

    public static Task<T> ReadUntilAsync<T>(
        Func<Task<T>> read,
        Func<T, bool> hasConverged,
        string providerName,
        string guarantee,
        string operation,
        string expected,
        Func<T, string> describe)
        => ExecuteUntilAsync(
            read,
            hasConverged,
            providerName,
            guarantee,
            operation,
            expected,
            describe,
            "read convergence",
            Timeout,
            Delay);

    public static Task<T> MutateUntilAsync<T>(
        Func<Task<T>> mutation,
        Func<T, bool> succeeded,
        string providerName,
        string guarantee,
        string operation,
        string expected,
        Func<T, string> describe)
        => ExecuteUntilAsync(
            mutation,
            succeeded,
            providerName,
            guarantee,
            operation,
            expected,
            describe,
            "mutation retry",
            Timeout,
            Delay);

    internal static async Task<T> ExecuteUntilAsync<T>(
        Func<Task<T>> operation,
        Func<T, bool> succeeded,
        string providerName,
        string guarantee,
        string operationName,
        string expected,
        Func<T, string> describe,
        string policyName,
        TimeSpan timeout,
        TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(succeeded);
        ArgumentNullException.ThrowIfNull(describe);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The retry timeout must be positive.");
        }

        if (delay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "The retry delay must be positive.");
        }

        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        var lastObservation = "<no completed attempt>";
        Exception? lastException = null;

        while (true)
        {
            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                ThrowTimeout();
            }

            try
            {
                var value = await operation().WaitAsync(remaining);
                attempts++;
                lastException = null;
                lastObservation = describe(value);
                if (succeeded(value))
                {
                    return value;
                }

            }
            catch (Exception exception) when (exception is not ReminderConformanceException)
            {
                attempts++;
                lastException = exception;
            }

            remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                ThrowTimeout();
            }

            await Task.Delay(remaining < delay ? remaining : delay);
        }

        void ThrowTimeout()
        {
            var exception = lastException is null
                ? "<none>"
                : $"{lastException.GetType().FullName}: {lastException.Message}";
            throw new ReminderConformanceException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Reminder {policyName} timed out [provider={providerName}, guarantee={guarantee}, operation={operationName}, timeout={timeout}, delay={delay}, attempts={attempts}]. Expected {expected}. Last observation: {lastObservation}. Last exception: {exception}."));
        }
    }

    private sealed class RestoreTiming((TimeSpan Timeout, TimeSpan Delay)? previous) : IDisposable
    {
        public void Dispose() => TestTiming.Value = previous;
    }
}
