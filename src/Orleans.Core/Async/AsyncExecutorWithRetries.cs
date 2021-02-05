using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Internal
{
    /// <summary>
    /// This class is a convenient utility class to execute a certain asynchronous function with retries,
    /// allowing to specify custom retry filters and policies.
    /// </summary>
    public static class AsyncExecutorWithRetries
    {
        public static readonly int INFINITE_RETRIES = -1;

        /// <summary>
        /// Execute a given function a number of times, based on retry configuration parameters.
        /// </summary>
        public static Task ExecuteWithRetries(
            Func<int, Task> action,
            int maxNumErrorTries,
            Func<Exception, int, bool> retryExceptionFilter,
            TimeSpan maxExecutionTime,
            IBackoffProvider onErrorBackOff)
        {
            Func<int, Task<bool>> function = async (int i) => { await action(i); return true; };
            return ExecuteWithRetriesHelper<bool>(
                function,
                0,
                0,
                maxNumErrorTries,
                maxExecutionTime,
                DateTime.UtcNow,
                null,
                retryExceptionFilter,
                null,
                onErrorBackOff);
        }

        /// <summary>
        /// Execute a given function a number of times, based on retry configuration parameters.
        /// </summary>
        public static Task<T> ExecuteWithRetries<T>(
            Func<int, Task<T>> function,
            int maxNumErrorTries,
            Func<Exception, int, bool> retryExceptionFilter,
            TimeSpan maxExecutionTime,
            IBackoffProvider onErrorBackOff)
        {
            return ExecuteWithRetries<T>(
                function,
                0,
                maxNumErrorTries,
                null,
                retryExceptionFilter,
                maxExecutionTime,
                null,
                onErrorBackOff);
        }

        /// <summary>
        /// Execute a given function a number of times, based on retry configuration parameters.
        /// </summary>
        /// <param name="function">Function to execute</param>
        /// <param name="maxNumSuccessTries">Maximal number of successful execution attempts.
        /// ExecuteWithRetries will try to re-execute the given function again if directed so by retryValueFilter.
        /// Set to -1 for unlimited number of success retries, until retryValueFilter is satisfied.
        /// Set to 0 for only one success attempt, which will cause retryValueFilter to be ignored and the given function executed only once until first success.</param>
        /// <param name="maxNumErrorTries">Maximal number of execution attempts due to errors.
        /// Set to -1 for unlimited number of error retries, until retryExceptionFilter is satisfied.</param>
        /// <param name="retryValueFilter">Filter function to indicate if successful execution should be retied.
        /// Set to null to disable successful retries.</param>
        /// <param name="retryExceptionFilter">Filter function to indicate if error execution should be retied.
        /// Set to null to disable error retries.</param>
        /// <param name="maxExecutionTime">The maximal execution time of the ExecuteWithRetries function.</param>
        /// <param name="onSuccessBackOff">The backoff provider object, which determines how much to wait between success retries.</param>
        /// <param name="onErrorBackOff">The backoff provider object, which determines how much to wait between error retries</param>
        /// <returns></returns>
        public static Task<T> ExecuteWithRetries<T>(
            Func<int, Task<T>> function,
            int maxNumSuccessTries,
            int maxNumErrorTries,
            Func<T, int, bool> retryValueFilter,
            Func<Exception, int, bool> retryExceptionFilter,
            TimeSpan maxExecutionTime = default(TimeSpan),
            IBackoffProvider onSuccessBackOff = null,
            IBackoffProvider onErrorBackOff = null)
        {
            return ExecuteWithRetriesHelper<T>(
                function,
                0,
                maxNumSuccessTries,
                maxNumErrorTries,
                maxExecutionTime,
                DateTime.UtcNow,
                retryValueFilter,
                retryExceptionFilter,
                onSuccessBackOff,
                onErrorBackOff);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private static async Task<T> ExecuteWithRetriesHelper<T>(
            Func<int, Task<T>> function,
            int callCounter,
            int maxNumSuccessTries,
            int maxNumErrorTries,
            TimeSpan maxExecutionTime,
            DateTime startExecutionTime,
            Func<T, int, bool> retryValueFilter = null,
            Func<Exception, int, bool> retryExceptionFilter = null,
            IBackoffProvider onSuccessBackOff = null,
            IBackoffProvider onErrorBackOff = null)
        {
            T result = default(T);
            ExceptionDispatchInfo lastExceptionInfo = null;
            bool retry;

            do
            {
                retry = false;

                if (maxExecutionTime != Constants.INFINITE_TIMESPAN && maxExecutionTime != default(TimeSpan))
                {
                    DateTime now = DateTime.UtcNow;
                    if (now - startExecutionTime > maxExecutionTime)
                    {
                        if (lastExceptionInfo == null)
                        {
                            throw new TimeoutException(
                                $"ExecuteWithRetries has exceeded its max execution time of {maxExecutionTime}. Now is {LogFormatter.PrintDate(now)}, started at {LogFormatter.PrintDate(startExecutionTime)}, passed {now - startExecutionTime}");
                        }

                        lastExceptionInfo.Throw();
                    }
                }

                int counter = callCounter;

                try
                {
                    callCounter++;
                    result = await function(counter);
                    lastExceptionInfo = null;

                    if (callCounter < maxNumSuccessTries || maxNumSuccessTries == INFINITE_RETRIES) // -1 for infinite retries
                    {
                        if (retryValueFilter != null)
                            retry = retryValueFilter(result, counter);
                    }

                    if (retry)
                    {
                        TimeSpan? delay = onSuccessBackOff?.Next(counter);

                        if (delay.HasValue)
                        {
                            await Task.Delay(delay.Value);
                        }
                    }
                }
                catch (Exception exc)
                {
                    retry = false;

                    if (callCounter < maxNumErrorTries || maxNumErrorTries == INFINITE_RETRIES)
                    {
                        if (retryExceptionFilter != null)
                            retry = retryExceptionFilter(exc, counter);
                    }

                    if (!retry)
                    {
                        throw;
                    }

                    lastExceptionInfo = ExceptionDispatchInfo.Capture(exc);

                    TimeSpan? delay = onErrorBackOff?.Next(counter);

                    if (delay.HasValue)
                    {
                        await Task.Delay(delay.Value);
                    }
                }
            } while (retry);

            return result;
        }
    }

    // Allow multiple implementations of the backoff algorithm.
    // For instance, ConstantBackoff variation that always waits for a fixed timespan,
    // or a RateLimitingBackoff that keeps makes sure that some minimum time period occurs between calls to some API
    // (especially useful if you use the same instance for multiple potentially simultaneous calls to ExecuteWithRetries).
    // Implementations should be imutable.
    // If mutable state is needed, extend the next function to pass the state from the caller.
    // example: TimeSpan Next(int attempt, object state, out object newState);
    public interface IBackoffProvider
    {
        TimeSpan Next(int attempt);
    }

    public class FixedBackoff : IBackoffProvider
    {
        private readonly TimeSpan fixedDelay;

        public FixedBackoff(TimeSpan delay)
        {
            fixedDelay = delay;
        }

        public TimeSpan Next(int attempt)
        {
            return fixedDelay;
        }
    }

    internal class ExponentialBackoff : IBackoffProvider
    {
        private readonly TimeSpan minDelay;
        private readonly TimeSpan maxDelay;
        private readonly TimeSpan step;

        public ExponentialBackoff(TimeSpan minDelay, TimeSpan maxDelay, TimeSpan step)
        {
            if (minDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("minDelay", minDelay, "ExponentialBackoff min delay must be a positive number.");
            if (maxDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("maxDelay", maxDelay, "ExponentialBackoff max delay must be a positive number.");
            if (step <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("step", step, "ExponentialBackoff step must be a positive number.");
            if (minDelay >= maxDelay) throw new ArgumentOutOfRangeException("minDelay", minDelay, "ExponentialBackoff min delay must be greater than max delay.");

            this.minDelay = minDelay;
            this.maxDelay = maxDelay;
            this.step = step;
        }

        public TimeSpan Next(int attempt)
        {
            TimeSpan currMax;
            try
            {
                long multiple = checked(1 << attempt);
                currMax = minDelay + step.Multiply(multiple); // may throw OverflowException
                if (currMax <= TimeSpan.Zero)
                    throw new OverflowException();
            }
            catch (OverflowException)
            {
                currMax = maxDelay;
            }
            currMax = StandardExtensions.Min(currMax, maxDelay);

            if (minDelay >= currMax) throw new ArgumentOutOfRangeException($"minDelay {minDelay}, currMax = {currMax}");
            return ThreadSafeRandom.NextTimeSpan(minDelay, currMax);
        }
    }
}