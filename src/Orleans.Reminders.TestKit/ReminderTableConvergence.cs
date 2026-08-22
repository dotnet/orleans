using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;

namespace Orleans.Reminders.TestKit;

internal static class ReminderTableConvergence
{
    public static async Task<T> ReadUntilAsync<T>(
        Func<Task<T>> read,
        Func<T, bool> hasConverged,
        ReminderTableCapabilities capabilities,
        string guarantee,
        string operation,
        string expected,
        Func<T, string> describe)
    {
        if (capabilities.ReadConvergenceTimeout <= TimeSpan.Zero)
        {
            return await read();
        }

        if (capabilities.ReadConvergenceDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capabilities),
                capabilities.ReadConvergenceDelay,
                $"{nameof(ReminderTableCapabilities.ReadConvergenceDelay)} must be positive when convergence retries are enabled.");
        }

        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        var lastObservation = "<no completed read>";
        while (true)
        {
            var remaining = capabilities.ReadConvergenceTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                ThrowTimeout();
            }

            T value;
            try
            {
                value = await read().WaitAsync(remaining);
            }
            catch (TimeoutException)
            {
                ThrowTimeout();
                throw;
            }

            attempts++;
            lastObservation = describe(value);
            if (hasConverged(value))
            {
                return value;
            }

            remaining = capabilities.ReadConvergenceTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                ThrowTimeout();
            }

            await Task.Delay(remaining < capabilities.ReadConvergenceDelay ? remaining : capabilities.ReadConvergenceDelay);
        }

        void ThrowTimeout() => throw new ReminderConformanceException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Reminder read convergence timed out [provider={capabilities.ProviderName}, guarantee={guarantee}, operation={operation}, timeout={capabilities.ReadConvergenceTimeout}, delay={capabilities.ReadConvergenceDelay}, attempts={attempts}]. Expected {expected}. Last observation: {lastObservation}."));
    }
}
